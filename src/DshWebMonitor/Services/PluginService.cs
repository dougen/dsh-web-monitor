using System.Text.Json;
using System.Text.RegularExpressions;
using DshWebMonitor.Models;

namespace DshWebMonitor.Services;

/// <summary>
/// 插件管理：安装 / 删除 / 更新通过 dsh plugin --profile web 转发给 profile 目录中的 pnpm；
/// 插件列表与版本信息来自 CLI 的 --json 输出；启用 / 停用由 <see cref="ProfileConfigService"/>
/// 维护 bundles 列表（dsh CLI 未提供 enable/disable 子命令）。
/// </summary>
internal sealed partial class PluginService
{
    private const string ProfileArgument = "--profile " + AppConstants.PluginProfile;

    public Task<CommandResult> ListJsonAsync(
        Action<string>? onOutput,
        Action<string>? onError,
        CancellationToken cancellationToken = default)
    {
        return ProcessRunner.RunCommandAsync(
            BuildListJsonCommand(),
            onOutput,
            onError,
            cancellationToken,
            AppConstants.QueryTimeout);
    }

    public Task<CommandResult> AddAsync(
        string package,
        string? version,
        Action<string>? onOutput,
        Action<string>? onError,
        CancellationToken cancellationToken = default)
    {
        return ProcessRunner.RunCommandAsync(
            BuildAddCommand(package, version),
            onOutput,
            onError,
            cancellationToken,
            AppConstants.CommandTimeout);
    }

    public Task<CommandResult> RemoveAsync(
        string package,
        Action<string>? onOutput,
        Action<string>? onError,
        CancellationToken cancellationToken = default)
    {
        return ProcessRunner.RunCommandAsync(
            BuildRemoveCommand(package),
            onOutput,
            onError,
            cancellationToken,
            AppConstants.CommandTimeout);
    }

    public Task<CommandResult> UpdateAsync(
        string package,
        string? latestVersion,
        Action<string>? onOutput,
        Action<string>? onError,
        CancellationToken cancellationToken = default)
    {
        return ProcessRunner.RunCommandAsync(
            BuildUpdateCommand(package, latestVersion),
            onOutput,
            onError,
            cancellationToken,
            AppConstants.CommandTimeout);
    }

    public static string BuildListJsonCommand() => $"dsh plugin {ProfileArgument} list --json";

    public static string BuildAddCommand(string package, string? version = null)
    {
        var target = string.IsNullOrWhiteSpace(version)
            ? package
            : $"{package}@{version.Trim()}";
        return $"dsh plugin {ProfileArgument} add {target}";
    }

    public static string BuildRemoveCommand(string package) => $"dsh plugin {ProfileArgument} remove {package}";

    /// <summary>更新到指定版本：dsh plugin --profile web update &lt;包名&gt;@&lt;最新版本&gt;。</summary>
    public static string BuildUpdateCommand(string package, string? latestVersion = null)
    {
        var target = string.IsNullOrWhiteSpace(latestVersion)
            ? package
            : $"{package}@{latestVersion.Trim()}";
        return $"dsh plugin {ProfileArgument} update {target}";
    }

    /// <summary>校验版本号，避免把非版本内容拼进命令行。</summary>
    internal static bool IsValidVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 64 && VersionPattern().IsMatch(trimmed);
    }

    /// <summary>
    /// 解析 dsh plugin ... list --json 的输出，得到插件名与实际安装版本。
    /// 返回 false 表示输出不是可解析的 JSON：此时不能当成「没有插件」。
    /// </summary>
    internal static bool TryParseInstalledPlugins(
        IReadOnlyList<string> standardOutput,
        out IReadOnlyList<(string Name, string Version)> plugins)
    {
        var result = new List<(string, string)>();
        plugins = result;
        using var document = TryParseJson(standardOutput);
        if (document is null)
        {
            return false;
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var version = dependency.Value.TryGetProperty("version", out var versionElement)
                              && versionElement.ValueKind == JsonValueKind.String
                    ? versionElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    result.Add((dependency.Name, version!));
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 校验用户输入的插件包名：只允许 npm 包名形态的字符，禁止空白、引号、分号、
    /// 以 '-' 开头等可被 cmd.exe 解释或当作命令行选项的内容。
    /// 包名本身不允许带版本号（<c>pkg@1.2.3</c>），版本必须单独传入，
    /// 否则会拼出双 @ 的非法命令，也可能把版本串写进 profile bundles。
    /// </summary>
    internal static bool IsValidPackageName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > 214 || trimmed.StartsWith('-'))
        {
            return false;
        }

        return PackageNamePattern().IsMatch(trimmed);
    }

    /// <summary>
    /// 拆分用户输入的 <c>包名[@版本]</c> 形式：包名必须合法，版本可缺省。
    /// 用于安装输入框，避免把 <c>pkg@1.2.3</c> 整串当作包名。
    /// </summary>
    internal static bool TryParsePackageSpec(string? input, out string packageName, out string? version)
    {
        packageName = string.Empty;
        version = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();

        // 作用域包名的 '@' 在开头，版本分隔符是第二个 '@' 之后的最后一段。
        var versionSeparator = text.IndexOf('@', text.StartsWith('@') ? 1 : 0);
        if (versionSeparator > 0)
        {
            var candidateVersion = text[(versionSeparator + 1)..];
            if (!IsValidVersion(candidateVersion))
            {
                return false;
            }

            version = candidateVersion.Trim();
            text = text[..versionSeparator];
        }

        if (!IsValidPackageName(text))
        {
            return false;
        }

        packageName = text.Trim();
        return true;
    }

    private static JsonDocument? TryParseJson(IReadOnlyList<string> lines)
    {
        // 不能按首尾括号粗暴截取：日志噪声里的 [warn]/[notice] 会让起点落在错误位置。
        // 逐行作为起点尝试解析，取第一个能完整解析且根节点为数组的结果（插件列表必为数组）。
        for (var index = 0; index < lines.Count; index++)
        {
            var start = lines[index].TrimStart();
            if (start.Length == 0 || start[0] != '[')
            {
                continue;
            }

            for (var end = lines.Count; end > index; end--)
            {
                var candidate = string.Join('\n', lines.Skip(index).Take(end - index));
                try
                {
                    var document = JsonDocument.Parse(candidate);
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        return document;
                    }

                    document.Dispose();
                }
                catch (JsonException)
                {
                }
            }
        }

        return null;
    }

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+_-]{0,63}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^(@[A-Za-z0-9][A-Za-z0-9._-]*/)?[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex PackageNamePattern();
}
