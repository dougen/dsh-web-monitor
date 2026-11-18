using System.Diagnostics;

namespace DshWebMonitor.Services;

/// <summary>
/// 子进程的临时目录选择。低完整性进程（受限会话 / 沙箱）无法在系统临时目录
/// （<c>%TEMP%</c>）里创建目录，导致 dsh web 的插件（例如 spill-local）在
/// <c>mkdtemp</c> 时抛 EPERM。此处在默认临时目录不可写时，改用应用内可写目录，
/// 并通过 <c>TEMP</c> / <c>TMP</c> / <c>TMPDIR</c> 传给子进程。
/// </summary>
internal static class TempDirectory
{
    private static readonly Lazy<string?> Writable = new(ResolveWritable);
    private static readonly Lazy<bool> DefaultWritable = new(ProbeDefault);

    /// <summary>应覆盖使用的临时目录；null 表示系统临时目录可用，无需改动。</summary>
    public static string? Preferred => DefaultWritable.Value ? null : Writable.Value;

    /// <summary>把可写临时目录写进子进程环境。</summary>
    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        if (Preferred is not { Length: > 0 } directory)
        {
            return;
        }

        startInfo.Environment["TEMP"] = directory;
        startInfo.Environment["TMP"] = directory;
        startInfo.Environment["TMPDIR"] = directory;
    }

    /// <summary>系统临时目录是否可写。</summary>
    private static bool ProbeDefault()
    {
        try
        {
            var probe = Path.Combine(Path.GetTempPath(), "dshwm-temp-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>按「本地应用数据 → 临时目录 → 程序目录」的顺序找可写的临时目录；都不可写时返回 null。</summary>
    private static string? ResolveWritable()
    {
        var candidates = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "DshWebMonitor", "temp"));
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), "DshWebMonitor", "temp"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "temp"));

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
