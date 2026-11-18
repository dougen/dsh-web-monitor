using System.Text.Json;

namespace DshWebMonitor.Services;

/// <summary>
/// npm 全局包检测 / 安装 / 更新。所有命令都执行 npm 的 --json 子命令，
/// 不使用无参数文本输出做字符串匹配；每条命令都有超时。
/// 低完整性 / 受限环境里 npm 默认缓存目录不可写，此时由 <see cref="NpmCache"/> 通过
/// <c>npm_config_cache</c> 指向应用内可写缓存，避免检查更新 / 安装因 EPERM 失败。
/// </summary>
internal sealed class NpmService
{
    public Task<CommandResult> ListGlobalAsync(
        Action<string>? onOutput,
        Action<string>? onError,
        CancellationToken cancellationToken = default)
    {
        return RunNpmAsync(
            $"npm list -g {AppConstants.NpmPackage} --json --depth=0",
            AppConstants.QueryTimeout,
            onOutput,
            onError,
            cancellationToken);
    }

    /// <summary>安装（或按精确版本安装）全局包；version 为空时安装 latest。</summary>
    public Task<CommandResult> InstallAsync(
        string? version,
        Action<string>? onOutput,
        Action<string>? onError,
        CancellationToken cancellationToken = default)
    {
        var target = string.IsNullOrWhiteSpace(version)
            ? AppConstants.NpmPackage
            : $"{AppConstants.NpmPackage}@{version.Trim()}";
        return RunNpmAsync(
            $"npm add -g {target}",
            AppConstants.CommandTimeout,
            onOutput,
            onError,
            cancellationToken);
    }

    /// <summary>查询 dist-tags：npm view &lt;包名&gt; dist-tags --json。</summary>
    public Task<CommandResult> ViewDistTagsAsync(
        string package,
        Action<string>? onOutput,
        Action<string>? onError,
        CancellationToken cancellationToken = default)
    {
        // 包名来自 CLI 的 --json 输出或用户输入，必须校验后才允许拼进命令行。
        if (!PluginService.IsValidPackageName(package))
        {
            throw new ArgumentException($"非法的 npm 包名：{package}", nameof(package));
        }

        return RunNpmAsync(
            $"npm view {package.Trim()} dist-tags --json",
            AppConstants.QueryTimeout,
            onOutput,
            onError,
            cancellationToken);
    }

    /// <summary>
    /// 执行 npm 命令。低完整性进程的缓存目录已由 <see cref="NpmCache"/> 通过环境变量切换；
    /// 其它情况下若命令因写缓存失败，再用应用内缓存重试一次。
    /// </summary>
    private static async Task<CommandResult> RunNpmAsync(
        string command,
        TimeSpan timeout,
        Action<string>? onOutput,
        Action<string>? onError,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunCommandAsync(
            command,
            onOutput,
            onError,
            cancellationToken,
            timeout);

        if (result.IsSuccess
            || !LooksLikeCachePermissionFailure(result)
            || NpmCache.FallbackDirectory is not { } cacheDirectory)
        {
            return result;
        }

        // 只针对「查询类」命令重试：安装/更新是写操作，重复执行会产生副作用。
        // 判断依据：命令里带 --json（list/view），不带 add/install/update。
        if (!LooksLikeReadOnlyQuery(command))
        {
            return result;
        }

        return await ProcessRunner.RunCommandAsync(
            $"{command} --cache \"{cacheDirectory}\"",
            onOutput,
            onError,
            cancellationToken,
            timeout);
    }

    private static bool LooksLikeReadOnlyQuery(string command) =>
        command.Contains("--json", StringComparison.Ordinal)
        && !command.Contains(" add ", StringComparison.Ordinal)
        && !command.Contains(" install ", StringComparison.Ordinal)
        && !command.Contains(" update ", StringComparison.Ordinal);

    /// <summary>
    /// npm 写缓存被系统拒绝时的输出特征。stdout 与 stderr 都要看（npm 会把错误写到 stdout），
    /// 且必须带缓存相关字样，避免全局目录无写权限等其它 EACCES 误触发重试。
    /// </summary>
    private static bool LooksLikeCachePermissionFailure(CommandResult result)
    {
        var text = string.Join('\n', result.StandardOutput) + '\n' + string.Join('\n', result.StandardError);
        if (!(text.Contains("EPERM", StringComparison.OrdinalIgnoreCase)
              || text.Contains("EACCES", StringComparison.OrdinalIgnoreCase)
              || text.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return text.Contains("cache", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析 dist-tags 输出（npm 会返回对象，或「数组包一个对象」的形式）。</summary>
    internal static IReadOnlyDictionary<string, string> ParseDistTags(IReadOnlyList<string> standardOutput)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = TryParseJsonObject(standardOutput);
        if (document is null)
        {
            return result;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result[property.Name] = value!;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 npm list -g &lt;包&gt; --json --depth=0 的输出。
    /// 返回 false 表示输出无法解析（此时不能断言「未安装」）；返回 true 时 <paramref name="installed"/>
    /// 给出 dependencies 里是否真的存在该包。
    /// </summary>
    internal static bool TryParseInstallState(
        IReadOnlyList<string> standardOutput,
        out bool installed,
        out string? version)
    {
        installed = false;
        version = null;
        var document = TryParseJsonObject(standardOutput);
        if (document is null)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            if (!dependencies.TryGetProperty(AppConstants.NpmPackage, out var package)
                || package.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            // npm 对「声明了依赖但实际没装」会给出 { "required": "*", "missing": true }，
            // 这种条目不能当成已安装，否则界面会放行启动。
            if (package.TryGetProperty("missing", out var missing)
                && missing.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            installed = true;
            if (package.TryGetProperty("version", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.String)
            {
                version = versionElement.GetString();
            }

            return true;
        }
    }

    /// <summary>
    /// npm 有时会在 JSON 之前输出提示行，因此截取第一个 '{' 到最后一个 '}'。
    /// </summary>
    private static JsonDocument? TryParseJsonObject(IReadOnlyList<string> lines)
    {
        // npm 可能在 JSON 前后夹带 [warn]/[notice] 等提示行，按首尾 '{'/'}' 截取会取到噪声。
        // 逐行作为起点尝试，取第一个能完整解析且根节点为对象的结果。
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].TrimStart().StartsWith('{'))
            {
                continue;
            }

            for (var end = lines.Count; end > index; end--)
            {
                var candidate = string.Join('\n', lines.Skip(index).Take(end - index));
                try
                {
                    var document = JsonDocument.Parse(candidate);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
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
}
