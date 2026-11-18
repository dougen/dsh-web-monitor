using System.Diagnostics;

namespace DshWebMonitor.Services;

/// <summary>
/// npm 的缓存目录选择。低完整性进程（受限会话 / 沙箱）无法写 npm 的默认缓存
/// （Windows 的 no-write-up 会返回 EPERM），此时改用应用内可写目录，并写入
/// <c>npm_config_cache</c> 环境变量，让所有 npm / npx 子命令共用同一份缓存。
/// </summary>
internal static class NpmCache
{
    private static readonly Lazy<string?> WritableDirectory = new(ResolveWritableDirectory);
    private static readonly Lazy<bool> DefaultCacheWritable = new(ProbeDefaultCacheWritable);

    /// <summary>当前进程是否为低完整性；低完整性下 npm 默认缓存不可写。</summary>
    public static bool IsLowIntegrity => !TokenIntegrity.IsAtLeastMedium;

    /// <summary>npm 默认缓存目录是否可写；受限 ACL / 沙箱下可能为 false。</summary>
    public static bool IsDefaultCacheWritable => DefaultCacheWritable.Value;

    /// <summary>应直接使用的缓存目录；null 表示使用 npm 自己的默认缓存（普通桌面环境）。</summary>
    public static string? PreferredDirectory =>
        IsLowIntegrity || !IsDefaultCacheWritable ? WritableDirectory.Value : null;

    /// <summary>命令因写缓存失败时的兜底目录；都不可写时返回 null。</summary>
    public static string? FallbackDirectory => WritableDirectory.Value;

    /// <summary>把缓存目录写进子进程环境（npm / npx 读取 npm_config_cache）。</summary>
    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        if (PreferredDirectory is { Length: > 0 } directory)
        {
            startInfo.Environment["npm_config_cache"] = directory;
        }
    }

    /// <summary>探测 npm 在 Windows 上的默认缓存目录（%LOCALAPPDATA%\npm-cache）是否可写。</summary>
    private static bool ProbeDefaultCacheWritable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return false;
        }

        var directory = Path.Combine(localAppData, "npm-cache");
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".dshwm-write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>按「本地应用数据 → 临时目录 → 程序目录」的顺序找可写的缓存目录；都不可写时返回 null。</summary>
    private static string? ResolveWritableDirectory()
    {
        var candidates = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "DshWebMonitor", "npm-cache"));
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), "DshWebMonitor", "npm-cache"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "npm-cache"));

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".write-probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return candidate;
            }
            catch (Exception)
            {
                // 该目录不可写，换下一个候选。
            }
        }

        return null;
    }
}
