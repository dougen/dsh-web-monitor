using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DshWebMonitor.Services;

/// <summary>某个 PID 的进程身份信息。</summary>
internal sealed record ProcessIdentity(
    int Pid,
    string ProcessName,
    string? CommandLine,
    bool IsDshWebNode,
    DateTime? StartTimeUtc = null);

/// <summary>端口探测结果：必须能区分「端口空闲」与「探测本身失败」。</summary>
internal readonly record struct PortProbeResult(bool Succeeded, IReadOnlyList<int> Pids, string? Error);

/// <summary>
/// 端口监听进程的发现与身份校验：
/// netstat 找监听 PID，原生接口 / tasklist 取映像名，原生接口 / wmic / Get-CimInstance 取命令行。
/// 只有 node.exe 且命令行命中 dsh 包入口（<c>…/dsh/lib/bin.js</c>）并带 <c>web</c> 子命令的进程
/// 才被认定为 dsh web，避免把命令行里仅仅含有 "dsh" 字样的无关 node 进程当成 dsh web。
/// </summary>
internal static partial class PortProbe
{
    public static async Task<PortProbeResult> FindListeningPidsAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "netstat.exe",
            "-ano",
            timeout: AppConstants.SystemToolTimeout,
            outputEncoding: ProcessRunner.SystemToolEncoding,
            cancellationToken: cancellationToken);

        if (result.TimedOut)
        {
            return new PortProbeResult(false, Array.Empty<int>(), "netstat 超时（端口状态未知）");
        }

        var pids = ParseListeningCandidatePids(result.StandardOutput, port);

        // 解析到监听者就说明输出可信；解析不到时必须用统一的成功语义判断 netstat 本身是否失败，
        // 否则「netstat 报错但退出码为 0」会被当成端口空闲，进而放行第二次启动。
        if (pids.Count == 0 && !result.IsSuccess)
        {
            return new PortProbeResult(
                false,
                Array.Empty<int>(),
                $"netstat 执行失败（{result.DescribeOutcome()}）");
        }

        return new PortProbeResult(true, pids, null);
    }

    /// <summary>
    /// 从 netstat -ano 的文本输出中解析监听指定端口的 PID，按本地地址优先级排序：
    /// 127.0.0.1 → 0.0.0.0 → [::1] → [::] → 其它地址。
    /// </summary>
    internal static IReadOnlyList<int> ParseListeningCandidatePids(IReadOnlyList<string> netstatLines, int port)
    {
        var candidates = new List<(int Score, int Pid)>();
        foreach (var line in netstatLines)
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 5)
            {
                continue;
            }

            if (!string.Equals(columns[3], "LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetPort(columns[1], out var localPort) || localPort != port)
            {
                continue;
            }

            if (!int.TryParse(columns[4], out var pid) || pid <= 0)
            {
                continue;
            }

            if (candidates.Any(candidate => candidate.Pid == pid))
            {
                continue;
            }

            candidates.Add((ScoreLocalAddress(columns[1]), pid));
        }

        return candidates
            .OrderBy(candidate => candidate.Score)
            .Select(candidate => candidate.Pid)
            .ToList();
    }

    internal static int? ParseListeningPid(IReadOnlyList<string> netstatLines, int port)
    {
        var candidates = ParseListeningCandidatePids(netstatLines, port);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static int ScoreLocalAddress(string localAddress)
    {
        var separator = localAddress.LastIndexOf(':');
        var host = separator > 0 ? localAddress[..separator] : localAddress;
        return host switch
        {
            "127.0.0.1" => 0,
            "0.0.0.0" => 1,
            "[::1]" => 2,
            "[::]" => 3,
            _ when host.StartsWith("127.", StringComparison.Ordinal) => 4,
            _ => 5,
        };
    }

    /// <summary>
    /// 读取指定 PID 的身份。映像名与命令行都读不到时返回 null（进程已退出或完全不可访问），
    /// 由调用方当作「本次无法判定」处理，而不是塞进一个「未知进程」参与身份判定。
    /// </summary>
    public static async Task<ProcessIdentity?> IdentifyAsync(int pid, CancellationToken cancellationToken = default)
    {
        var processName = await ReadProcessNameAsync(pid, cancellationToken);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var commandLine = await ReadCommandLineAsync(pid, cancellationToken);
        return new ProcessIdentity(
            pid,
            processName,
            commandLine,
            IsDshWebNode(processName, commandLine),
            TryGetStartTimeUtc(pid));
    }

    /// <summary>
    /// 身份判定：映像名 node.exe，命令行命中 dsh 包入口脚本（<c>…[@deepseek-ai/]dsh/lib/bin.js</c>），
    /// 并且该入口后面紧跟 <c>web</c> 子命令。仅含 "dsh" 字样的命令行不再通过。
    /// </summary>
    internal static bool IsDshWebNode(string? processName, string? commandLine)
    {
        if (!string.Equals(processName, "node.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return DshEntryPattern().IsMatch(commandLine) && DshWebArgumentPattern().IsMatch(commandLine);
    }

    public static DateTime? TryGetStartTimeUtc(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>从 tasklist 的 CSV 输出中解析映像名。</summary>
    internal static string? ParseTaskListName(IReadOnlyList<string> taskListLines)
    {
        foreach (var line in taskListLines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('"'))
            {
                continue;
            }

            var parts = trimmed.Split("\",\"", StringSplitOptions.None);
            return parts[0].Trim('"');
        }

        return null;
    }

    private static async Task<string?> ReadProcessNameAsync(int pid, CancellationToken cancellationToken)
    {
        // 原生接口最快，且对受限令牌/提权进程同样有效。
        var native = NativeProcessInfo.TryGetExecutableName(pid);
        if (!string.IsNullOrWhiteSpace(native))
        {
            return native;
        }

        var result = await ProcessRunner.RunAsync(
            "tasklist.exe",
            $"/FI \"PID eq {pid}\" /FO CSV /NH",
            timeout: AppConstants.SystemToolTimeout,
            outputEncoding: ProcessRunner.SystemToolEncoding,
            cancellationToken: cancellationToken);
        var name = ParseTaskListName(result.StandardOutput);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static async Task<string?> ReadCommandLineAsync(int pid, CancellationToken cancellationToken)
    {
        // 原生接口优先：不依赖 wmic / PowerShell，也不受控制台代码页影响。
        var native = NativeProcessInfo.TryGetCommandLine(pid);
        if (!string.IsNullOrWhiteSpace(native))
        {
            return native;
        }

        if (File.Exists(Path.Combine(Environment.SystemDirectory, "Wbem", "WMIC.exe")))
        {
            var wmic = await ProcessRunner.RunAsync(
                "wmic.exe",
                $"process where processid={pid} get name,commandline /format:list",
                timeout: AppConstants.SystemToolTimeout,
                outputEncoding: ProcessRunner.SystemToolEncoding,
                cancellationToken: cancellationToken);
            var commandLine = ParseWmicCommandLine(wmic.StandardOutput);
            if (!string.IsNullOrWhiteSpace(commandLine))
            {
                return commandLine;
            }
        }

        var cim = await ProcessRunner.RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"",
            timeout: AppConstants.SystemToolTimeout,
            outputEncoding: ProcessRunner.SystemToolEncoding,
            cancellationToken: cancellationToken);
        return cim.StandardOutput.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
    }

    /// <summary>从 wmic /format:list 输出中解析 CommandLine 字段。</summary>
    internal static string? ParseWmicCommandLine(IReadOnlyList<string> lines)
    {
        const string key = "CommandLine=";
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r', '\n');
            if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[key.Length..].Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static bool TryGetPort(string localAddress, out int port)
    {
        port = 0;
        var separator = localAddress.LastIndexOf(':');
        if (separator < 0 || separator == localAddress.Length - 1)
        {
            return false;
        }

        return int.TryParse(localAddress[(separator + 1)..], out port);
    }

    [GeneratedRegex(@"(?:@deepseek-ai[\\/]+dsh[\\/]+lib[\\/]+bin\.js|[\\/]dsh[\\/]+lib[\\/]+bin\.js)", RegexOptions.IgnoreCase)]
    private static partial Regex DshEntryPattern();

    [GeneratedRegex(@"bin\.js""?\s+web(?:\s|""|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DshWebArgumentPattern();
}
