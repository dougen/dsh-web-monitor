using System.ComponentModel;
using System.Diagnostics;

namespace DshWebMonitor.Services;

/// <summary>结束进程的结果：失败时必须带可诊断的原因，调用方不能静默忽略。</summary>
internal readonly record struct KillProcessResult(bool Success, string? Error);

/// <summary>
/// dsh web 子进程的生命周期封装。启动通过 cmd.exe /c 执行固定命令行，
/// 不弹出独立控制台窗口，stdout/stderr 全部回调给日志面板。
/// 退出监控器时结束由本程序启动的进程树（stdout/stderr 是本进程的管道，
/// 让 dsh web 留在后台只会让它在下次写日志时撞 EPIPE 崩溃）。
/// </summary>
internal sealed class ServerService : IDisposable
{
    private const int MaximumAncestorDepth = 8;

    private readonly string _commandLine;
    private Process? _process;

    /// <summary>本程序启动的包装进程（cmd.exe）在启动瞬间记录的启动时间，用于校验根 PID 未被复用。</summary>
    private DateTime? _startedRootStartUtc;

    public ServerService(string commandLine)
    {
        _commandLine = commandLine;
    }

    /// <summary>实际执行的命令行（日志与诊断使用，避免打印常量命令）。</summary>
    public string CommandLine => _commandLine;

    /// <summary>当前由监控器启动的进程（若由外部启动则为 null）。</summary>
    public Process? StartedProcess => _process;

    public void Launch(Action<string> onStandardOutput, Action<string> onStandardError)
    {
        if (!KillStartedProcess() && _process is { } stuck)
        {
            // 旧进程树没能结束：不再追踪它（否则会与本次启动混淆），但释放句柄。
            _process = null;
            _startedRootStartUtc = null;
            stuck.Dispose();
        }

        _process = ProcessRunner.StartLongRunning(
            "cmd.exe",
            "/d /c " + _commandLine,
            onStandardOutput,
            onStandardError);

        // 立即记录根进程的启动时间。父子链只比 PID 会在包装进程退出、PID 被复用后把无关进程
        // 误认作自有进程；遍历到根时用它做二次校验。读不到就保持 null，IsDescendant 将拒绝认领。
        try
        {
            _startedRootStartUtc = _process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            _startedRootStartUtc = null;
        }
    }

    /// <summary>由监控器启动的进程是否已退出；退出时返回退出码。</summary>
    public bool HasStartedProcessExited(out int? exitCode)
    {
        exitCode = null;
        var process = _process;
        if (process is null)
        {
            return false;
        }

        try
        {
            if (!process.HasExited)
            {
                return false;
            }

            exitCode = process.ExitCode;
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    /// <summary>结束本监控器自己启动的进程树，返回是否已确认结束。</summary>
    public bool KillStartedProcess()
    {
        var process = _process;
        if (process is null)
        {
            return true;
        }

        var finished = true;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                finished = process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出。
        }
        catch (Win32Exception)
        {
            // 无权限或进程已消失。
            finished = false;
        }
        catch (AggregateException)
        {
            // 进程树中有结束不掉的后代（提权 / 受保护）。
            finished = false;
        }

        if (!finished)
        {
            // 保留引用：下次停止 / 退出时还能再试，也还能把这些进程认作「本程序启动」。
            return false;
        }

        _process = null;
        _startedRootStartUtc = null;
        process.Dispose();
        return true;
    }

    /// <summary>
    /// 指定 PID 是否为本程序启动进程的后代（用于身份判定的「自有进程」分支）。
    /// 走到根 PID 时校验根进程启动时间，避免包装进程退出、PID 被复用后误认领无关进程。
    /// </summary>
    public bool IsDescendantOfStartedProcess(int pid)
    {
        if (_process is not { } process)
        {
            return false;
        }

        int rootPid;
        try
        {
            rootPid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var current = pid;
        for (var depth = 0; depth < MaximumAncestorDepth && current > 0; depth++)
        {
            if (current == rootPid)
            {
                // 命中根 PID：仅在读到「不同的启动时间」时判定为 PID 复用并拒绝认领。
                // 根包装进程（cmd.exe /c）可能已正常退出、PID 尚未被复用，此时启动时间读不到，
                // 但父子链仍真实指向本程序拉起的进程，应当认领；若 PID 已被占用，读到的启动时间
                // 必然与启动瞬间记录的不同，从而拒绝把无关进程误当自有。
                if (_startedRootStartUtc is { } expected
                    && PortProbe.TryGetStartTimeUtc(rootPid) is { } actual
                    && Math.Abs((actual - expected).TotalSeconds) > 2)
                {
                    return false;
                }

                return true;
            }

            current = NativeProcessInfo.TryGetParentProcessId(current) ?? 0;
        }

        return false;
    }

    /// <summary>
    /// 按 PID 结束整棵进程树（调用方必须先完成身份校验）。
    /// 用 PID + 进程启动时间双重绑定，避免校验与结束之间 PID 被复用而杀错进程。
    /// </summary>
    public static KillProcessResult KillProcessTree(int pid, DateTime? expectedStartTimeUtc = null)
    {
        // fail-closed：拿不到启动时间就无法证明「要结束的进程仍是校验过的那个」。
        // 读不到时拒绝结束，宁可让用户重试，也不要在 PID 复用时误杀无关进程。
        if (expectedStartTimeUtc is not { } expected)
        {
            return new KillProcessResult(
                false,
                $"无法读取 PID {pid} 的启动时间，已拒绝结束该进程（防止 PID 复用误杀）");
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // 进程已经不存在。
            return new KillProcessResult(true, null);
        }
        catch (InvalidOperationException)
        {
            return new KillProcessResult(true, null);
        }

        using (process)
        {
            DateTime actual;
            try
            {
                actual = process.StartTime.ToUniversalTime();
            }
            catch (Win32Exception ex)
            {
                return new KillProcessResult(
                    false,
                    $"无法读取 PID {pid} 的启动时间（{ex.Message}），已拒绝结束该进程");
            }

            if (Math.Abs((actual - expected).TotalSeconds) > 2)
            {
                return new KillProcessResult(
                    false,
                    $"PID {pid} 已被其它进程复用（启动时间与校验时不一致），已拒绝结束该进程");
            }

            try
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    return new KillProcessResult(false, $"已请求结束 PID {pid}，但 5 秒内进程仍未退出");
                }

                return new KillProcessResult(true, null);
            }
            catch (InvalidOperationException)
            {
                // 结束过程中进程自行退出。
                return new KillProcessResult(true, null);
            }
            catch (Win32Exception ex)
            {
                var hint = ex.NativeErrorCode == 5
                    ? "（权限不足：请以管理员身份运行监控器，或手动结束该进程）"
                    : string.Empty;
                return new KillProcessResult(false, $"无法结束 PID {pid}：{ex.Message}{hint}");
            }
            catch (NotSupportedException ex)
            {
                return new KillProcessResult(false, $"无法结束 PID {pid}：{ex.Message}");
            }
            catch (AggregateException)
            {
                return new KillProcessResult(
                    false,
                    $"PID {pid} 的进程树中有结束不掉的子进程（可能提权运行），可手动结束该进程");
            }
        }
    }

    public void Dispose()
    {
        // 退出监控器即结束由本程序启动的 dsh web：它持有本进程提供的 stdout/stderr 管道，
        // 若留在后台会在下一次写日志时因 EPIPE 崩溃，且用户无法归因。
        KillStartedProcess();
    }
}
