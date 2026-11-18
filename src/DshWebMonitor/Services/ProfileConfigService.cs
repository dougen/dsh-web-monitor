using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DshWebMonitor.Services;

/// <summary>
/// 读写 dsh profile 的 package.json：插件的启用 / 停用即维护
/// <c>dsh.profile.bundles</c> 数组（dsh CLI 没有提供 enable / disable 子命令，
/// 该列表就是 profile 启动时加载哪些插件 bundle 的唯一来源）。
/// 写入采用「临时文件 + File.Replace」原子替换，避免中途崩溃把用户的 profile 配置截断。
/// </summary>
internal sealed class ProfileConfigService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private const int ReadRetryCount = 3;
    private const int ReadRetryDelayMs = 60;

    private readonly string _profileName;

    public ProfileConfigService(string profileName)
    {
        _profileName = profileName;
    }

    public string ProfileDirectory => ResolveProfileDirectory(_profileName);

    public string PackageJsonPath => Path.Combine(ProfileDirectory, "package.json");

    /// <summary>
    /// 读取 <c>dsh.profile.bundles</c>。文件不存在返回空集合；
    /// 文件存在但无法解析时抛出异常，由调用方明确报错，而不是静默当成「全部未启用」。
    /// </summary>
    public IReadOnlyList<string> ReadBundles()
    {
        var path = PackageJsonPath;
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        var text = ReadAllTextWithRetry(path);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidOperationException("package.json 不是 JSON 对象");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"无法解析 {path}（文件可能已损坏）", ex);
        }

        if (root["dsh"]?["profile"]?["bundles"] is not JsonArray bundles)
        {
            return Array.Empty<string>();
        }

        return bundles
            .Select(node => node is JsonValue value && value.TryGetValue<string>(out var name) ? name : null)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();
    }

    /// <summary>启用 / 停用一个插件，返回是否发生了变更。</summary>
    public bool SetBundleEnabled(string packageName, bool enabled)
    {
        var path = PackageJsonPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到 profile 的 package.json：" + path);
        }

        var text = ReadAllTextWithRetry(path);
        var root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidOperationException("package.json 不是 JSON 对象");
        var profileNode = root["dsh"]?["profile"] as JsonObject
                          ?? throw new InvalidOperationException("package.json 缺少 dsh.profile 配置");
        if (profileNode["bundles"] is not JsonArray bundles)
        {
            bundles = new JsonArray();
            profileNode["bundles"] = bundles;
        }

        var existing = bundles
            .Select(node => node is JsonValue value && value.TryGetValue<string>(out var name) ? name : null)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();

        var contains = existing.Contains(packageName, StringComparer.OrdinalIgnoreCase);
        if (enabled == contains)
        {
            return false;
        }

        if (enabled)
        {
            bundles.Add(packageName);
        }
        else
        {
            for (var index = bundles.Count - 1; index >= 0; index--)
            {
                if (bundles[index] is JsonValue value
                    && value.TryGetValue<string>(out var name)
                    && string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    bundles.RemoveAt(index);
                }
            }
        }

        var updated = root.ToJsonString(WriteOptions) + Environment.NewLine;

        // 读改写之间如果有别的进程（dsh CLI / pnpm / 编辑器）改写了同一个文件，
        // 用旧快照整体覆写会静默覆盖掉对方写入的字段；发现内容变化就放弃本次写入。
        if (!string.Equals(ReadAllTextWithRetry(path), text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "profile 的 package.json 在本次修改期间被其它程序改写，已放弃写入，请重新执行");
        }

        WriteAtomically(path, updated);
        return true;
    }

    /// <summary>
    /// 原子写：先写同目录 .tmp 并自检 JSON，再用 File.Replace 替换（保留一份 .bak）。
    /// 中途崩溃/断电只会留下临时文件，原文件保持完整。
    /// </summary>
    private static void WriteAtomically(string path, string content)
    {
        // 写前自检：生成的文本必须仍能被解析，否则绝不覆盖原文件。
        try
        {
            using var _ = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("拒绝写入：生成的 package.json 不是合法 JSON", ex);
        }

        // 临时文件名唯一：固定名会让并发写入（多实例，或实例内被绕过的调用）对同一文件交替截断，
        // 使 File.Replace 把另一个进程写了一半的内容落盘，绕过上面的 JSON 自检。
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupPath = path + ".bak";
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
        try
        {
            // 校验真正落盘的文件，而不只是内存里的字符串。
            using (JsonDocument.Parse(File.ReadAllText(temporaryPath, Encoding.UTF8)))
            {
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        catch (Exception)
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static string ReadAllTextWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException) when (attempt < ReadRetryCount)
            {
                // 另一个进程（dsh CLI / 编辑器）可能正在替换文件，短暂重试。
                Thread.Sleep(ReadRetryDelayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < ReadRetryCount)
            {
                Thread.Sleep(ReadRetryDelayMs);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ResolveProfileDirectory(string profileName)
    {
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(dshHome))
        {
            dshHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }

        return Path.Combine(dshHome, "profiles", profileName);
    }
}
