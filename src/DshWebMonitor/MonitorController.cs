using System.Collections.Concurrent;
using System.Diagnostics;
using DshWebMonitor.Models;
using DshWebMonitor.Services;

namespace DshWebMonitor;

/// <summary>
/// 监控器唯一的编排者与状态持有者：端口/进程状态、npm 包状态、忙碌状态都在这里维护，
/// 界面与托盘都只消费同一份 <see cref="AppState"/> 快照。
/// 所有启停失败分支都必须把状态收敛到可恢复的取值，绝不留在 Starting / Stopping。
/// </summary>
internal sealed class MonitorController : IDisposable
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <summary>只读查询（安装态、插件概况）专用闸门：不占用写操作闸门，也不把界面标记为忙碌。</summary>
    private readonly SemaphoreSlim _readGate = new(1, 1);

    /// <summary>端口状态刷新串行化：避免两次并发刷新用陈旧快照互相覆盖。</summary>
    private readonly SemaphoreSlim _serverRefreshGate = new(1, 1);

    /// <summary>插件最新版本查询的并发上限：插件多时不至于同时拉起几十个 npm 进程。</summary>
    private readonly SemaphoreSlim _packageQueryGate = new(AppConstants.MaxConcurrentPackageQueries);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly ServerService _server;
    private readonly NpmService _npm = new();
    private readonly PluginService _plugins = new();
    private readonly ProfileConfigService _profileConfig = new(AppConstants.PluginProfile);
    private readonly int _port;
    private readonly TimeSpan _startTimeout;
    private readonly string _webUiUrl;
    private readonly List<string> _stderrTail = new();
    private AppState _state;
    private volatile ProcessIdentity? _cachedIdentity;
    private DateTime _cachedIdentityAt = DateTime.MinValue;
    private int _refreshTick;
    private int _probeFailureCount;

    /// <summary>最近一次插件最新版本查询中失败的条数（0 表示全部查到；&gt;0 时界面不能声称「已全部最新」）。</summary>
    private int _lastPluginUpdateCheckFailures;

    /// <summary>0 表示没有启停操作在进行；非 0 表示正在启动/停止/重启。</summary>
    private int _serverOperationInProgress;

    private volatile bool _disposed;

    public MonitorController()
        : this(AppConstants.Port, AppConstants.StartTimeout, AppConstants.StartCommand)
    {
    }

    public MonitorController(int port, TimeSpan startTimeout, string startCommand)
    {
        _port = port;
        _startTimeout = startTimeout;
        _webUiUrl = $"http://127.0.0.1:{port}";
        _server = new ServerService(startCommand);
        _state = new AppState { Port = port };
    }

    public event Action<AppState>? StateChanged;

    public event Action<LogEntry>? ServerLog;

    public event Action<LogEntry>? PluginLog;

    public AppState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>被监控的端口。</summary>
    public int Port => _port;

    /// <summary>当前管理的 dsh profile 目录（插件管理的 package.json 所在位置）。</summary>
    public string ProfileDirectory => _profileConfig.ProfileDirectory;

    /// <summary>当前监听是否由本监控器启动的进程持有（外部命令启动则为 false）。</summary>
    public bool IsServerOwnedByApp
    {
        get
        {
            var process = _server.StartedProcess;
            if (process is null)
            {
                return false;
            }

            // 有监听 PID 时以「该 PID 是否在本程序启动的进程树内」为准；包装进程存活并不能
            // 证明占用端口的就是它拉起的进程。启动中尚无监听 PID 时才退回包装进程存活判断。
            if (State.ListenerPid is { } pid)
            {
                return _server.IsDescendantOfStartedProcess(pid);
            }

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>主页概况与插件页共用的更新通道。</summary>
    public UpdateChannel PluginChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>启动后台轮询并做一次初始探测。</summary>
    public async Task StartAsync()
    {
        try
        {
            await Task.WhenAll(RefreshServerStateAsync(), RefreshInstallStateAsync());
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log(LogTarget.Server, $"初始检测失败：{ex.Message}", LogLevel.Error);
        }

        _ = Task.Run(WatchLoopAsync);
    }

    // ---------------------------------------------------------------- 端口 / 进程状态

    public Task RefreshServerStateAsync() => RefreshServerStateCoreAsync(force: false);

    /// <summary>
    /// 端口状态刷新。<paramref name="force"/> 为 true 时忽略「有启停操作正在进行」的跳过条件，
    /// 用于失败路径自我收敛：跳过条件不再是「状态是 Starting/Stopping」，
    /// 否则停止超时的自愈刷新会因为状态是 Stopping 而变成空操作。
    /// </summary>
    private async Task RefreshServerStateCoreAsync(bool force)
    {
        if (!force && Volatile.Read(ref _serverOperationInProgress) != 0)
        {
            return;
        }

        // 强制收敛路径必须拿到闸门：否则跳过刷新后调用方会用 fallback 覆盖可能已被并发刷新
        // 写成正确值的状态。普通轮询仍用 WaitAsync(0)，抢不到就跳过，避免阻塞轮询循环。
        if (force)
        {
            await _serverRefreshGate.WaitAsync(_lifetime.Token);
        }
        else if (!await _serverRefreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await RefreshServerStateBodyAsync();
        }
        finally
        {
            _serverRefreshGate.Release();
        }
    }

    private async Task RefreshServerStateBodyAsync()
    {
        var ct = _lifetime.Token;

        PortProbeResult probe;
        try
        {
            probe = await PortProbe.FindListeningPidsAsync(_port, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            probe = new PortProbeResult(false, Array.Empty<int>(), ex.Message);
        }

        if (!probe.Succeeded)
        {
            _probeFailureCount++;
            Log(LogTarget.Server, $"端口探测失败：{probe.Error}", LogLevel.Error);
            if (State.Server == ServerStatus.Probing && _probeFailureCount >= AppConstants.ProbeFailureThreshold)
            {
                Log(
                    LogTarget.Server,
                    "连续多次无法读取端口状态，暂按「未运行」处理；可点击「启动」重试",
                    LogLevel.Error);
                UpdateState(s => s with
                {
                    Server = ServerStatus.Stopped,
                    ListenerPid = null
                });
            }

            return;
        }

        _probeFailureCount = 0;

        if (probe.Pids.Count == 0)
        {
            _cachedIdentity = null;
            var current = State;
            if (current.Server == ServerStatus.StartFailed)
            {
                // 启动失败的结果保留在状态区，直到下一次启动尝试。
                return;
            }

            if (current.Server is ServerStatus.Running or ServerStatus.PortConflict)
            {
                Log(LogTarget.Server, $"127.0.0.1:{_port} 已无进程监听，dsh web 已退出", LogLevel.System);
            }

            if (current.Server != ServerStatus.Stopped || current.ListenerPid is not null)
            {
                UpdateState(s => s with
                {
                    Server = ServerStatus.Stopped,
                    ListenerPid = null
                });
            }

            return;
        }

        // 多个监听者（例如 IPv4 与 IPv6 双栈）时逐个校验，优先识别真正的 dsh web。
        ProcessIdentity? dshIdentity = null;
        ProcessIdentity? otherIdentity = null;
        foreach (var candidatePid in probe.Pids)
        {
            ProcessIdentity? identity;
            try
            {
                identity = await IdentifyCachedAsync(candidatePid, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(LogTarget.Server, $"进程身份校验失败（PID {candidatePid}）：{ex.Message}", LogLevel.Error);
                continue;
            }

            if (identity is null)
            {
                continue;
            }

            if (identity.IsDshWebNode || _server.IsDescendantOfStartedProcess(candidatePid))
            {
                dshIdentity = identity;
                break;
            }

            otherIdentity ??= identity;
        }

        if (dshIdentity is { } web)
        {
            var current = State;
            if (current.Server != ServerStatus.Running || current.ListenerPid != web.Pid)
            {
                Log(
                    LogTarget.Server,
                    $"检测到 dsh web 运行中：PID {web.Pid}（{web.ProcessName}）",
                    LogLevel.System);
                UpdateState(s => s with
                {
                    Server = ServerStatus.Running,
                    ListenerPid = web.Pid
                });
            }

            return;
        }

        if (otherIdentity is { } other)
        {
            var current = State;
            if (current.Server != ServerStatus.PortConflict || current.ListenerPid != other.Pid)
            {
                Log(
                    LogTarget.Server,
                    $"端口 {_port} 被其他进程占用：PID {other.Pid}（{other.ProcessName}），"
                    + "该进程不是 dsh web，不提供停止 / 重新启动",
                    LogLevel.Error);
                UpdateState(s => s with
                {
                    Server = ServerStatus.PortConflict,
                    ListenerPid = other.Pid
                });
            }
        }
    }

    // ---------------------------------------------------------------- 启动 / 停止 / 重启

    public async Task StartServerAsync()
    {
        if (!State.CanStart)
        {
            return;
        }

        // 先置「启停进行中」标志再抢闸门：端口刷新在入口读这个标志来跳过，
        // 若先抢闸门再置标志，中间窗口里刷新会趁机进入并与状态切换并发。抢不到则回滚。
        Interlocked.Exchange(ref _serverOperationInProgress, 1);
        if (!await TryEnterOperationGateAsync("启动"))
        {
            Interlocked.Exchange(ref _serverOperationInProgress, 0);
            return;
        }

        try
        {
            await StartCoreAsync();
        }
        catch (OperationCanceledException)
        {
            await CleanupStartedServerAsync();
            await ConvergeAsync(ServerStatus.StartFailed, null);
        }
        catch (Exception ex)
        {
            Log(LogTarget.Server, $"启动过程异常：{ex.Message}", LogLevel.Error);
            await CleanupStartedServerAsync();
            await ConvergeAsync(ServerStatus.StartFailed, null);
        }
        finally
        {
            Interlocked.Exchange(ref _serverOperationInProgress, 0);
            _operationGate.Release();
        }
    }

    public async Task StopServerAsync()
    {
        if (!State.CanStop)
        {
            return;
        }

        Interlocked.Exchange(ref _serverOperationInProgress, 1);
        if (!await TryEnterOperationGateAsync("停止"))
        {
            Interlocked.Exchange(ref _serverOperationInProgress, 0);
            return;
        }

        try
        {
            await StopCoreAsync();
        }
        catch (OperationCanceledException)
        {
            await ConvergeAsync(ServerStatus.Running, State.ListenerPid);
        }
        catch (Exception ex)
        {
            Log(LogTarget.Server, $"停止过程异常：{ex.Message}", LogLevel.Error);
            await ConvergeAsync(ServerStatus.Running, State.ListenerPid);
        }
        finally
        {
            Interlocked.Exchange(ref _serverOperationInProgress, 0);
            _operationGate.Release();
        }
    }

    public async Task RestartServerAsync()
    {
        if (!State.CanRestart)
        {
            return;
        }

        Interlocked.Exchange(ref _serverOperationInProgress, 1);
        if (!await TryEnterOperationGateAsync("重新启动"))
        {
            Interlocked.Exchange(ref _serverOperationInProgress, 0);
            return;
        }

        try
        {
            Log(LogTarget.Server, "重新启动：先停止 dsh web…", LogLevel.System);

            // 停止与启动分开处理：停止阶段的异常不能被误报成「启动失败」并多拉起一次进程。
            bool stopped;
            try
            {
                stopped = await StopCoreAsync();
            }
            catch (OperationCanceledException)
            {
                await ConvergeAsync(ServerStatus.Running, State.ListenerPid);
                return;
            }
            catch (Exception ex)
            {
                Log(LogTarget.Server, $"重新启动失败（停止阶段异常）：{ex.Message}", LogLevel.Error);
                await ConvergeAsync(ServerStatus.Running, State.ListenerPid);
                return;
            }

            if (!stopped)
            {
                Log(LogTarget.Server, "重新启动已取消：停止阶段未通过身份校验或端口未释放", LogLevel.Error);
                return;
            }

            try
            {
                await StartCoreAsync();
            }
            catch (OperationCanceledException)
            {
                await CleanupStartedServerAsync();
                await ConvergeAsync(ServerStatus.StartFailed, null);
            }
            catch (Exception ex)
            {
                Log(LogTarget.Server, $"重新启动失败（启动阶段异常）：{ex.Message}", LogLevel.Error);
                await CleanupStartedServerAsync();
                await ConvergeAsync(ServerStatus.StartFailed, null);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _serverOperationInProgress, 0);
            _operationGate.Release();
        }
    }

    /// <summary>打开 Web UI；dsh web 未运行时拒绝。</summary>
    public void OpenWebUi()
    {
        if (!State.CanOpenWebUi)
        {
            Log(LogTarget.Server, "请先启动 dsh web，再打开 Web UI", LogLevel.System);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_webUiUrl) { UseShellExecute = true });
            Log(LogTarget.Server, $"已打开 Web UI：{_webUiUrl}", LogLevel.System);
        }
        catch (Exception ex)
        {
            Log(LogTarget.Server, $"打开 Web UI 失败：{ex.Message}；请手动在浏览器中访问 {_webUiUrl}", LogLevel.Error);
        }
    }

    private async Task StartCoreAsync()
    {
        var ct = _lifetime.Token;
        lock (_stderrTail)
        {
            _stderrTail.Clear();
        }

        _cachedIdentity = null;
        UpdateState(s => s with
        {
            Server = ServerStatus.Starting,
            ListenerPid = null
        });

        Log(LogTarget.Server, $"执行启动命令：{_server.CommandLine}", LogLevel.Command);
        if (NpmCache.PreferredDirectory is { Length: > 0 } cacheDirectory)
        {
            Log(
                LogTarget.Server,
                $"当前环境无法写 npm 默认缓存，改用应用内缓存：{cacheDirectory}",
                LogLevel.System);
        }

        if (TempDirectory.Preferred is { Length: > 0 } tempDirectory)
        {
            Log(
                LogTarget.Server,
                $"当前环境无法写系统临时目录，dsh web 的临时文件改放到：{tempDirectory}",
                LogLevel.System);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            _server.Launch(
                line => Log(LogTarget.Server, line, LogLevel.Output),
                line =>
                {
                    lock (_stderrTail)
                    {
                        _stderrTail.Add(line);
                        if (_stderrTail.Count > AppConstants.MaxStderrTailLines)
                        {
                            _stderrTail.RemoveAt(0);
                        }
                    }

                    Log(LogTarget.Server, line, LogLevel.Error);
                });
        }
        catch (Exception ex)
        {
            Log(LogTarget.Server, $"无法启动 dsh web 进程：{ex.Message}", LogLevel.Error);
            await CleanupStartedServerAsync();
            UpdateState(s => s with { Server = ServerStatus.StartFailed });
            return;
        }

        while (stopwatch.Elapsed < _startTimeout)
        {
            ct.ThrowIfCancellationRequested();

            var probe = await PortProbe.FindListeningPidsAsync(_port, ct);
            if (probe.Succeeded && probe.Pids.Count > 0)
            {
                var identity = await ResolveDshListenerAsync(probe.Pids, ct);
                if (identity is not null)
                {
                    // 端口上已存在 dsh web：若它不是本程序刚拉起的进程树，本次启动实际没拉起服务，
                    // 必须先清理本次 Launch 残留的包装进程/后代，否则会留下孤儿进程并被误标为自有。
                    if (!_server.IsDescendantOfStartedProcess(identity.Pid))
                    {
                        Log(
                            LogTarget.Server,
                            $"检测到外部 dsh web（PID {identity.Pid}）已占用端口，清理本次启动拉起的进程树",
                            LogLevel.System);
                        await CleanupStartedServerAsync();
                    }

                    UpdateState(s => s with
                    {
                        Server = ServerStatus.Running,
                        ListenerPid = identity.Pid
                    });
                    Log(
                        LogTarget.Server,
                        $"端口已由 dsh web 监听：PID {identity.Pid}，耗时 {stopwatch.Elapsed.TotalSeconds:F1} 秒",
                        LogLevel.System);
                    return;
                }

                var other = await IdentifyCachedAsync(probe.Pids[0], ct);
                if (other is null)
                {
                    Log(LogTarget.Server, $"端口 {_port} 被进程占用，但无法读取其身份，启动失败", LogLevel.Error);
                    UpdateState(s => s with { Server = ServerStatus.StartFailed });
                    await CleanupStartedServerAsync();
                    return;
                }

                Log(
                    LogTarget.Server,
                    $"端口 {_port} 被进程 PID {other.Pid}（{other.ProcessName}）占用，启动失败",
                    LogLevel.Error);
                UpdateState(s => s with
                {
                    Server = ServerStatus.PortConflict,
                    ListenerPid = other.Pid
                });
                await CleanupStartedServerAsync();
                return;
            }

            if (_server.HasStartedProcessExited(out var exitCode))
            {
                // cmd.exe 包装进程退出后，node 子进程可能稍晚才开始监听，再确认一次。
                await Task.Delay(400, ct);
                var lateProbe = await PortProbe.FindListeningPidsAsync(_port, ct);
                if (lateProbe.Succeeded && lateProbe.Pids.Count > 0)
                {
                    var lateIdentity = await ResolveDshListenerAsync(lateProbe.Pids, ct);
                    if (lateIdentity is not null)
                    {
                        UpdateState(s => s with
                        {
                            Server = ServerStatus.Running,
                            ListenerPid = lateIdentity.Pid
                        });
                        Log(LogTarget.Server, $"启动成功：PID {lateIdentity.Pid}", LogLevel.System);
                        return;
                    }
                }

                Log(
                    LogTarget.Server,
                    exitCode is { } code
                        ? $"dsh web 进程已退出（退出码 {code}）"
                        : "dsh web 进程已退出",
                    LogLevel.Error);
                ReportStderrTail();
                UpdateState(s => s with { Server = ServerStatus.StartFailed });
                await CleanupStartedServerAsync();
                return;
            }

            await Task.Delay(500, ct);
        }

        Log(
            LogTarget.Server,
            $"启动失败：{_startTimeout.TotalSeconds:F0} 秒内未检测到端口 {_port} 进入 LISTENING 状态",
            LogLevel.Error);
        ReportStderrTail();
        UpdateState(s => s with { Server = ServerStatus.StartFailed });
        await CleanupStartedServerAsync();
    }

    private async Task<bool> StopCoreAsync()
    {
        var ct = _lifetime.Token;
        UpdateState(s => s with { Server = ServerStatus.Stopping });

        // 停止前重新做一次端口 + 身份校验，状态快照可能已经过期。
        var probe = await PortProbe.FindListeningPidsAsync(_port, ct);
        if (!probe.Succeeded)
        {
            Log(LogTarget.Server, $"无法探测端口 {_port}：{probe.Error}", LogLevel.Error);
            await ConvergeAsync(ServerStatus.Running, State.ListenerPid);
            return false;
        }

        if (probe.Pids.Count == 0)
        {
            Log(LogTarget.Server, $"端口 {_port} 已空闲，无需停止", LogLevel.System);
            _cachedIdentity = null;
            if (!_server.KillStartedProcess())
            {
                Log(LogTarget.Server, "端口已释放，但监控器启动的包装进程未能在 5 秒内退出", LogLevel.Error);
            }

            UpdateState(s => s with
            {
                Server = ServerStatus.Stopped,
                ListenerPid = null
            });
            return true;
        }

        // 停止前的身份校验必须用最新数据（含进程启动时间，用于结束前的二次绑定）。
        var identity = await ResolveDshListenerAsync(probe.Pids, ct);
        if (identity is null)
        {
            var other = await IdentifyCachedAsync(probe.Pids[0], ct);
            if (other is null)
            {
                Log(LogTarget.Server, $"无法读取 PID {probe.Pids[0]} 的身份，为保证安全已拒绝停止", LogLevel.Error);
                await ConvergeAsync(ServerStatus.Running, probe.Pids[0]);
                return false;
            }

            Log(
                LogTarget.Server,
                $"已拒绝停止：PID {other.Pid} 的进程名为 {other.ProcessName}，"
                + "身份校验未通过（不是 dsh web），不会结束该进程",
                LogLevel.Error);
            UpdateState(s => s with
            {
                Server = ServerStatus.PortConflict,
                ListenerPid = other.Pid
            });
            return false;
        }

        Log(
            LogTarget.Server,
            $"身份校验通过：PID {identity.Pid}（{identity.ProcessName}），正在结束进程树",
            LogLevel.System);

        var kill = ServerService.KillProcessTree(identity.Pid, identity.StartTimeUtc);
        if (!kill.Success)
        {
            Log(LogTarget.Server, kill.Error ?? $"结束 PID {identity.Pid} 失败", LogLevel.Error);
            Log(
                LogTarget.Server,
                $"停止失败：dsh web（PID {identity.Pid}）仍在运行，可再次点击「停止」或手动结束该进程",
                LogLevel.Error);
            UpdateState(s => s with
            {
                Server = ServerStatus.Running,
                ListenerPid = identity.Pid
            });
            return false;
        }

        if (!await WaitForPortReleasedAsync(_port, AppConstants.PortReleaseTimeout, ct))
        {
            Log(LogTarget.Server, $"停止超时：端口 {_port} 仍在监听", LogLevel.Error);
            // 收敛到「仍在运行」并保留 PID：状态立刻可操作，3 秒后的轮询会再校正一次。
            await ConvergeAsync(ServerStatus.Running, identity.Pid);
            Log(LogTarget.Server, "停止超时：dsh web 可能仍在运行，可重试「停止」或查看日志", LogLevel.Error);
            return false;
        }

        _cachedIdentity = null;
        if (!_server.KillStartedProcess())
        {
            Log(LogTarget.Server, "端口已释放，但监控器启动的包装进程未能在 5 秒内退出", LogLevel.Error);
        }

        UpdateState(s => s with
        {
            Server = ServerStatus.Stopped,
            ListenerPid = null
        });
        Log(LogTarget.Server, "dsh web 已停止，端口已释放", LogLevel.System);
        return true;
    }

    /// <summary>
    /// 把状态收敛到确定值：强制重新探测端口；探测仍然失败时按调用方给出的兜底状态落定。
    /// 任何失败路径都必须调用它，避免状态永久停在 Starting / Stopping。
    /// </summary>
    private async Task ConvergeAsync(ServerStatus fallback, int? fallbackPid)
    {
        try
        {
            await RefreshServerStateCoreAsync(force: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log(LogTarget.Server, $"状态收敛刷新失败：{ex.Message}", LogLevel.Error);
        }

        if (State.Server is not (ServerStatus.Starting or ServerStatus.Stopping))
        {
            return;
        }

        Log(
            LogTarget.Server,
            "无法确认 dsh web 的最终状态，已回到可操作状态；后台轮询会继续校正",
            LogLevel.Error);
        UpdateState(s => s with
        {
            Server = fallback,
            ListenerPid = fallbackPid
        });
    }

    /// <summary>在候选监听者中找出 dsh web（或本程序启动的进程）。用最新身份数据，不走缓存。</summary>
    private async Task<ProcessIdentity?> ResolveDshListenerAsync(
        IReadOnlyList<int> candidatePids,
        CancellationToken cancellationToken)
    {
        foreach (var candidatePid in candidatePids)
        {
            var identity = await PortProbe.IdentifyAsync(candidatePid, cancellationToken);
            RememberIdentity(identity);
            if (identity is not null && (identity.IsDshWebNode || _server.IsDescendantOfStartedProcess(candidatePid)))
            {
                return identity;
            }
        }

        return null;
    }

    /// <summary>
    /// 启动失败后的清理：cmd.exe 包装进程可能已经退出，而真正监听端口的 node 仍在。
    /// 只结束「本程序启动进程的后代」——按 PID 的父子链可以追到已退出的 cmd.exe，
    /// 因此不会误杀本来就存在于端口上的、外部启动的 dsh web。
    /// </summary>
    private async Task CleanupStartedServerAsync()
    {
        var ownedListeners = new List<(int Pid, DateTime? StartTimeUtc)>();
        try
        {
            var probe = await PortProbe.FindListeningPidsAsync(_port, CancellationToken.None);
            if (probe.Succeeded)
            {
                foreach (var pid in probe.Pids)
                {
                    if (!_server.IsDescendantOfStartedProcess(pid))
                    {
                        continue;
                    }

                    var identity = await PortProbe.IdentifyAsync(pid, CancellationToken.None);
                    if (identity is null)
                    {
                        Log(LogTarget.Server, $"无法读取残留进程 PID {pid} 的身份，已跳过清理", LogLevel.Error);
                        continue;
                    }

                    ownedListeners.Add((pid, identity.StartTimeUtc));
                }
            }
        }
        catch (Exception ex)
        {
            Log(LogTarget.Server, $"清理残留监听进程时探测失败：{ex.Message}", LogLevel.Error);
        }

        if (!_server.KillStartedProcess())
        {
            Log(LogTarget.Server, "无法结束监控器启动的包装进程，请检查权限", LogLevel.Error);
        }

        foreach (var (pid, startTimeUtc) in ownedListeners)
        {
            var kill = ServerService.KillProcessTree(pid, startTimeUtc);
            Log(
                LogTarget.Server,
                kill.Success
                    ? $"已清理启动失败残留的进程 PID {pid}"
                    : $"清理残留进程 PID {pid} 失败：{kill.Error}",
                kill.Success ? LogLevel.System : LogLevel.Error);
        }
    }

    // ---------------------------------------------------------------- npm 全局包

    public async Task RefreshInstallStateAsync()
    {
        var ct = _lifetime.Token;
        CommandResult result;
        try
        {
            result = await _npm.ListGlobalAsync(null, null, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(LogTarget.Plugin, $"读取 npm 全局包失败：{ex.Message}", LogLevel.Error);
            return;
        }

        // 命令失败不等于「未安装」：只有成功解析出 JSON 才更新安装态，否则保持现状并明确报错。
        if (!NpmService.TryParseInstallState(result.StandardOutput, out var installed, out var version))
        {
            Log(
                LogTarget.Plugin,
                $"无法解析 npm list 输出（{result.DescribeOutcome()}），保持当前安装状态不变",
                LogLevel.Error);
            if (State.Install == InstallStatus.Probing)
            {
                UpdateState(s => s with { Install = InstallStatus.Unknown });
            }

            return;
        }

        var previous = State;
        UpdateState(s => s with
        {
            Install = installed ? InstallStatus.Installed : InstallStatus.NotInstalled,
            InstalledVersion = installed ? version : null,
            LatestVersion = installed ? s.LatestVersion : null,
            NewerVersionHint = installed ? s.NewerVersionHint : null,
        });

        if (installed && previous.InstalledVersion != version)
        {
            Log(
                LogTarget.Plugin,
                version is { Length: > 0 }
                    ? $"npm 全局包 {AppConstants.NpmPackage} v{version} 已安装"
                    : $"npm 全局包 {AppConstants.NpmPackage} 已安装",
                LogLevel.System);
        }
        else if (!installed && previous.Install == InstallStatus.Installed)
        {
            Log(LogTarget.Plugin, $"npm 全局包 {AppConstants.NpmPackage} 已不可用", LogLevel.Error);
        }
    }

    public async Task InstallPackageAsync()
    {
        if (!State.CanInstallPackage)
        {
            return;
        }

        await RunExclusiveAsync($"正在安装 {AppConstants.NpmPackage}…", BusyKind.InstallNpmPackage, async ct =>
        {
            LogPluginCommand($"npm add -g {AppConstants.NpmPackage}");
            var result = await _npm.InstallAsync(
                null,
                line => Log(LogTarget.Plugin, line, LogLevel.Output),
                line => Log(LogTarget.Plugin, line, LogLevel.Error),
                ct);
            LogExitCode(result);

            if (!result.IsSuccess)
            {
                Log(
                    LogTarget.Plugin,
                    $"安装失败（{result.DescribeOutcome()}），状态恢复为「未安装」；"
                    + "常见原因是 npm 全局目录没有写权限，详见日志",
                    LogLevel.Error);
                UpdateState(s => s with
                {
                    Install = InstallStatus.NotInstalled,
                    InstalledVersion = null,
                    LatestVersion = null,
                    NewerVersionHint = null,
                });
                return;
            }

            Log(LogTarget.Plugin, "安装命令执行完成，刷新安装状态…", LogLevel.System);
            await RefreshInstallStateAsync();
            if (State.Install == InstallStatus.NotInstalled)
            {
                Log(LogTarget.Plugin, "安装命令返回成功但未检测到全局包，请检查 npm 全局目录", LogLevel.Error);
            }
        });
    }

    /// <summary>按更新通道检查 dsh 最新版本（dist-tags 解析）。</summary>
    public async Task CheckNpmUpdatesAsync(UpdateChannel channel)
    {
        if (!State.CanCheckNpmUpdates)
        {
            return;
        }

        var channelLabel = UpdateChannels.ToLabel(channel);
        await RunExclusiveAsync($"正在检查更新（通道 {channelLabel}）…", BusyKind.CheckNpmUpdates, async ct =>
        {
            try
            {
                LogPluginCommand($"npm view {AppConstants.NpmPackage} dist-tags --json");
                var result = await _npm.ViewDistTagsAsync(AppConstants.NpmPackage, null, null, ct);
                LogExitCode(result);
                if (!result.IsSuccess)
                {
                    LogCommandFailure(result, $"检查更新失败：无法读取 {AppConstants.NpmPackage} 的 dist-tags");
                    SetUpdateCheckFailure($"无法读取 npm dist-tags（{result.DescribeOutcome()}），详见日志页");
                    return;
                }

                var distTags = NpmService.ParseDistTags(result.StandardOutput);
                if (!UpdateChannels.TryResolve(distTags, channel, out var target) || target is null)
                {
                    Log(
                        LogTarget.Plugin,
                        $"检查更新失败：通道 {channelLabel} 没有可用的目标版本（不高于当前版本的预发布 tag 不会被当作正式版）",
                        LogLevel.Error);
                    SetUpdateCheckFailure($"通道 {channelLabel} 没有可用的目标版本，详见日志页");
                    return;
                }

                if (target.UsedFallback)
                {
                    Log(LogTarget.Plugin, $"通道 {channelLabel} 无同名 dist-tag，已按 {target.ResolvedTag} 解析目标版本", LogLevel.System);
                }

                var installed = State.InstalledVersion;
                if (string.IsNullOrWhiteSpace(installed))
                {
                    await RefreshInstallStateAsync();
                    installed = State.InstalledVersion;
                }

                var hasUpdate = installed is not null
                                && SemverComparer.Instance.Compare(target.Version, installed) > 0;

                // 当前通道没有更高版本时，记下其它通道里更高的版本，让「已是最新版本」不再掩盖新版本；
                // 只做提示，不改变当前通道的选择与更新按钮。
                string? newerElsewhere = null;
                if (!hasUpdate && installed is not null)
                {
                    var newer = distTags
                        .Where(pair => UpdateChannels.IsVersion(pair.Value))
                        .Select(pair => (pair.Key, pair.Value))
                        .Where(pair => SemverComparer.Instance.Compare(pair.Value, installed) > 0)
                        .OrderByDescending(pair => pair.Value, SemverComparer.Instance)
                        .FirstOrDefault();
                    if (newer.Value is { Length: > 0 } newerVersion)
                    {
                        newerElsewhere = $"{newerVersion}（{newer.Key}）";
                    }
                }

                UpdateState(s => s with
                {
                    InstalledVersion = installed ?? s.InstalledVersion,
                    LatestVersion = hasUpdate ? target.Version : installed,
                    NewerVersionHint = newerElsewhere,
                    UpdateCheckError = null,
                });

                Log(
                    LogTarget.Plugin,
                    hasUpdate
                        ? $"通道 {channelLabel}：发现新版本 v{target.Version}（当前 v{installed}）"
                        : newerElsewhere is not null
                            ? $"通道 {channelLabel}：目标 v{target.Version} 不高于当前 v{installed}，无可用更新；"
                              + $"其它通道有更高版本 v{newerElsewhere}"
                            : $"通道 {channelLabel}：目标 v{target.Version} 不高于当前 v{installed ?? "未知"}，无可用更新",
                    LogLevel.System);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(LogTarget.Plugin, $"检查更新失败：{ex.Message}；请查看日志页获取详细信息", LogLevel.Error);
                SetUpdateCheckFailure($"{ex.Message}；详见日志页");
            }
        });
    }

    /// <summary>把最近一次检查更新失败写进状态，让界面显示失败原因而不是「尚未检查更新」。</summary>
    private void SetUpdateCheckFailure(string reason) =>
        UpdateState(s => s with
        {
            LatestVersion = null,
            NewerVersionHint = null,
            UpdateCheckError = reason,
        });

    /// <summary>把 dsh 全局包更新到检查更新得到的通道目标版本。</summary>
    public async Task UpdatePackageAsync(string targetVersion)
    {
        if (!State.CanUpdatePackage || string.IsNullOrWhiteSpace(targetVersion))
        {
            return;
        }

        var version = targetVersion.Trim();
        if (!PluginService.IsValidVersion(version))
        {
            Log(LogTarget.Plugin, $"目标版本号无效：「{targetVersion}」，已取消更新", LogLevel.Error);
            return;
        }

        await RunExclusiveAsync($"正在更新 {AppConstants.NpmPackage} → v{version}…", BusyKind.UpdateNpmPackage, async ct =>
        {
            LogPluginCommand($"npm add -g {AppConstants.NpmPackage}@{version}");
            var result = await _npm.InstallAsync(
                version,
                line => Log(LogTarget.Plugin, line, LogLevel.Output),
                line => Log(LogTarget.Plugin, line, LogLevel.Error),
                ct);
            LogExitCode(result);

            if (!result.IsSuccess)
            {
                Log(LogTarget.Plugin, $"更新失败（{result.DescribeOutcome()}），版本状态保持不变", LogLevel.Error);
                return;
            }

            Log(LogTarget.Plugin, "更新完成，刷新安装状态…", LogLevel.System);
            await RefreshInstallStateAsync();
            UpdateState(s => s with { LatestVersion = s.InstalledVersion, NewerVersionHint = null });

            if (State.Server == ServerStatus.Running)
            {
                Log(
                    LogTarget.Server,
                    "npm 更新完成；dsh web 正在运行，不会自动重启，请手动点击「重新启动」",
                    LogLevel.System);
            }
        });
    }

    // ---------------------------------------------------------------- 插件管理

    /// <summary>读取插件信息：CLI 的 --json 解析安装版本，dist-tags 按通道解析最新版本，bundles 解析启用状态。</summary>
    public Task<IReadOnlyList<PluginInfo>> LoadPluginInfoAsync(
        UpdateChannel channel,
        Action<string>? progress = null)
    {
        // 读取插件列表是只读操作，不占用全局写操作闸门，也不阻塞主页的 dsh 操作。
        return ReadPluginInfoInBackgroundAsync(channel, progress);
    }
    /// <summary>最近一次插件最新版本查询失败的条数（供界面判断数据是否完整）。</summary>
    public int LastPluginUpdateCheckFailures => Volatile.Read(ref _lastPluginUpdateCheckFailures);

    private async Task<IReadOnlyList<PluginInfo>> ReadPluginInfoInBackgroundAsync(
        UpdateChannel channel,
        Action<string>? progress = null)
    {
        var channelLabel = UpdateChannels.ToLabel(channel);
        try
        {
            await _readGate.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<PluginInfo>();
        }

        try
        {
            return await ReadPluginInfoAsync(channel, channelLabel, _lifetime.Token, progress);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<PluginInfo>();
        }
        catch (Exception ex)
        {
            // 失败必须让调用方看见：插件页据此判断「读取失败」并允许重试，而不是当成「0 个插件」。
            Log(LogTarget.Plugin, $"读取插件概况失败：{ex.Message}；请查看日志页获取详细信息", LogLevel.Error);
            throw;
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async Task<IReadOnlyList<PluginInfo>> ReadPluginInfoAsync(
        UpdateChannel channel,
        string channelLabel,
        CancellationToken ct,
        Action<string>? progress = null)
    {
        progress?.Invoke("正在读取插件信息…");
        Interlocked.Exchange(ref _lastPluginUpdateCheckFailures, 0);
        LogPluginCommand(PluginService.BuildListJsonCommand());
        var listResult = await _plugins.ListJsonAsync(null, null, ct);
        LogExitCode(listResult);
        if (!listResult.IsSuccess)
        {
            LogCommandFailure(listResult, "读取插件列表失败，详见 stderr");
            throw new InvalidOperationException($"读取插件列表失败（{listResult.DescribeOutcome()}）");
        }

        if (!PluginService.TryParseInstalledPlugins(listResult.StandardOutput, out var installed))
        {
            Log(LogTarget.Plugin, "无法解析 dsh plugin list --json 的输出，插件列表读取失败", LogLevel.Error);
            throw new InvalidOperationException("无法解析 dsh plugin list --json 的输出");
        }

        IReadOnlyList<string> bundles;
        try
        {
            bundles = _profileConfig.ReadBundles();
        }
        catch (Exception ex)
        {
            // 读失败不能当成「全部插件未启用」：用户会据此误改 profile。
            Log(
                LogTarget.Plugin,
                $"读取 profile 的 package.json 失败：{ex.Message}",
                LogLevel.Error);
            throw new InvalidOperationException($"读取 profile 配置失败：{ex.Message}", ex);
        }

        var latestByPackage = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (installed.Count > 0)
        {
            progress?.Invoke($"正在检查插件更新（通道 {channelLabel}）…");
            var latest = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            await Task.WhenAll(installed.Select(async item =>
            {
                // 包名来自 CLI 输出，必须先校验再拼进命令行（防命令注入）。
                if (!PluginService.IsValidPackageName(item.Name))
                {
                    Log(LogTarget.Plugin, $"已跳过包名不合法的插件条目：「{item.Name}」", LogLevel.Error);
                    return;
                }

                await _packageQueryGate.WaitAsync(ct);
                try
                {
                    var view = await _npm.ViewDistTagsAsync(item.Name, null, null, ct);
                    if (!view.IsSuccess)
                    {
                        Interlocked.Increment(ref _lastPluginUpdateCheckFailures);
                        Log(
                            LogTarget.Plugin,
                            $"查询插件 {item.Name} 的最新版本失败（{view.DescribeOutcome()}），本次按「未知」处理",
                            LogLevel.Error);
                        latest[item.Name] = null;
                        return;
                    }

                    var distTags = NpmService.ParseDistTags(view.StandardOutput);
                    latest[item.Name] =
                        UpdateChannels.TryResolve(distTags, channel, out var target) && target is not null
                            ? target.Version
                            : null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _lastPluginUpdateCheckFailures);
                    Log(LogTarget.Plugin, $"查询插件 {item.Name} 的最新版本失败：{ex.Message}", LogLevel.Error);
                    latest[item.Name] = null;
                }
                finally
                {
                    _packageQueryGate.Release();
                }
            }));
            foreach (var pair in latest)
            {
                latestByPackage[pair.Key] = pair.Value;
            }

            var failedChecks = Volatile.Read(ref _lastPluginUpdateCheckFailures);
            Log(
                LogTarget.Plugin,
                failedChecks > 0
                    ? $"已按通道 {channelLabel} 解析 {installed.Count} 个插件的最新版本，"
                      + $"其中 {failedChecks} 个查询失败（详见日志），可更新数量可能不准确"
                    : $"已按通道 {channelLabel} 解析 {installed.Count} 个插件的最新版本",
                failedChecks > 0 ? LogLevel.Error : LogLevel.System);
        }

        return (IReadOnlyList<PluginInfo>)installed
            .Select(item => new PluginInfo(
                item.Name,
                item.Version,
                latestByPackage.TryGetValue(item.Name, out var latestVersion) ? latestVersion : null,
                bundles.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>一键更新：逐个更新当前通道下所有可更新插件（按 semver 判定，绝不会降级）。</summary>
    public async Task<PluginBulkUpdateResult> UpdateAllPluginsAsync()
    {
        var plugins = await LoadPluginInfoAsync(PluginChannel);
        var targets = plugins.Where(plugin => plugin.HasUpdate).ToList();
        var updated = 0;
        var failed = 0;
        foreach (var plugin in targets)
        {
            if (await UpdatePluginAsync(plugin.Name, plugin.LatestVersion))
            {
                updated++;
            }
            else
            {
                failed++;
            }
        }

        return new PluginBulkUpdateResult(updated, failed);
    }

    /// <summary>解析指定包在当前通道下的目标版本（安装新插件时使用）。</summary>
    public async Task<ChannelTarget?> ResolveChannelTargetAsync(string package, UpdateChannel channel)
    {
        if (!PluginService.IsValidPackageName(package))
        {
            Log(LogTarget.Plugin, $"插件包名无效：「{package}」", LogLevel.Error);
            return null;
        }

        try
        {
            var result = await _npm.ViewDistTagsAsync(package, null, null, _lifetime.Token);
            if (!result.IsSuccess)
            {
                return null;
            }

            var distTags = NpmService.ParseDistTags(result.StandardOutput);
            return UpdateChannels.TryResolve(distTags, channel, out var target) ? target : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log(LogTarget.Plugin, $"解析插件 {package} 的通道版本失败：{ex.Message}", LogLevel.Error);
            return null;
        }
    }

    /// <summary>启用 / 停用插件：维护 profile package.json 的 dsh.profile.bundles。</summary>
    public async Task<bool> SetPluginEnabledAsync(string package, bool enabled)
    {
        if (!PluginService.IsValidPackageName(package))
        {
            Log(LogTarget.Plugin, $"插件包名无效：「{package}」", LogLevel.Error);
            return false;
        }

        var normalized = package.Trim();
        return await RunExclusiveResultAsync(
            enabled ? $"正在启用插件 {normalized}…" : $"正在停用插件 {normalized}…",
            BusyKind.PluginCommand,
            _ =>
            {
                var changed = _profileConfig.SetBundleEnabled(normalized, enabled);
                if (changed)
                {
                    Log(
                        LogTarget.Plugin,
                        $"已{(enabled ? "启用" : "停用")}插件 {normalized}："
                        + $"{(enabled ? "已加入" : "已移出")} profile 的 dsh.profile.bundles，重新启动 dsh web 后生效",
                        LogLevel.System);
                }
                else
                {
                    Log(
                        LogTarget.Plugin,
                        $"插件 {normalized} 已经是{(enabled ? "启用" : "停用")}状态，无需修改",
                        LogLevel.System);
                }

                return Task.FromResult(true);
            },
            false);
    }

    public async Task<bool> InstallPluginAsync(string package, string? version = null)
    {
        if (!PluginService.IsValidPackageName(package))
        {
            Log(LogTarget.Plugin, $"插件包名无效：「{package}」，仅允许 npm 包名形态（版本请单独提供）", LogLevel.Error);
            return false;
        }

        var normalized = package.Trim();
        var targetVersion = PluginService.IsValidVersion(version) ? version!.Trim() : null;
        return await RunExclusiveResultAsync($"正在安装插件 {normalized}…", BusyKind.PluginCommand, async ct =>
        {
            LogPluginCommand(PluginService.BuildAddCommand(normalized, targetVersion));
            var result = await _plugins.AddAsync(
                normalized,
                targetVersion,
                line => Log(LogTarget.Plugin, line, LogLevel.Output),
                line => Log(LogTarget.Plugin, line, LogLevel.Error),
                ct);
            LogExitCode(result);
            if (!result.IsSuccess)
            {
                Log(
                    LogTarget.Plugin,
                    $"插件 {normalized} 安装失败（{result.DescribeOutcome()}），详见日志",
                    LogLevel.Error);
                return false;
            }

            var added = _profileConfig.SetBundleEnabled(normalized, true);
            Log(
                LogTarget.Plugin,
                added
                    ? $"插件 {normalized} 安装完成，已加入 dsh.profile.bundles（重新启动 dsh web 后生效）"
                    : $"插件 {normalized} 安装完成（已在 dsh.profile.bundles 中）",
                LogLevel.System);
            return true;
        }, false);
    }

    public async Task<bool> UpdatePluginAsync(string package, string? latestVersion = null)
    {
        if (!PluginService.IsValidPackageName(package))
        {
            Log(LogTarget.Plugin, $"插件包名无效：「{package}」", LogLevel.Error);
            return false;
        }

        var normalized = package.Trim();
        var targetVersion = PluginService.IsValidVersion(latestVersion) ? latestVersion!.Trim() : null;
        if (latestVersion is not null && targetVersion is null)
        {
            Log(LogTarget.Plugin, $"最新版本号无效：「{latestVersion}」，已取消更新", LogLevel.Error);
            return false;
        }

        return await RunExclusiveResultAsync($"正在更新插件 {normalized}…", BusyKind.PluginCommand, async ct =>
        {
            LogPluginCommand(PluginService.BuildUpdateCommand(normalized, targetVersion));
            var result = await _plugins.UpdateAsync(
                normalized,
                targetVersion,
                line => Log(LogTarget.Plugin, line, LogLevel.Output),
                line => Log(LogTarget.Plugin, line, LogLevel.Error),
                ct);
            LogExitCode(result);
            if (!result.IsSuccess)
            {
                Log(
                    LogTarget.Plugin,
                    $"插件 {normalized} 更新失败（{result.DescribeOutcome()}），详见日志",
                    LogLevel.Error);
                return false;
            }

            Log(LogTarget.Plugin, $"插件 {normalized} 更新完成，重新启动 dsh web 后生效", LogLevel.System);
            return true;
        }, false);
    }

    public async Task<bool> RemovePluginAsync(string package)
    {
        if (!PluginService.IsValidPackageName(package))
        {
            Log(LogTarget.Plugin, $"插件包名无效：「{package}」", LogLevel.Error);
            return false;
        }

        var normalized = package.Trim();
        return await RunExclusiveResultAsync($"正在删除插件 {normalized}…", BusyKind.PluginCommand, async ct =>
        {
            LogPluginCommand(PluginService.BuildRemoveCommand(normalized));
            var result = await _plugins.RemoveAsync(
                normalized,
                line => Log(LogTarget.Plugin, line, LogLevel.Output),
                line => Log(LogTarget.Plugin, line, LogLevel.Error),
                ct);
            LogExitCode(result);
            if (!result.IsSuccess)
            {
                Log(
                    LogTarget.Plugin,
                    $"插件 {normalized} 删除失败（{result.DescribeOutcome()}），详见日志",
                    LogLevel.Error);
                return false;
            }

            _profileConfig.SetBundleEnabled(normalized, false);
            Log(LogTarget.Plugin, $"插件 {normalized} 已删除，重新启动 dsh web 后生效", LogLevel.System);
            return true;
        }, false);
    }

    // ---------------------------------------------------------------- 后台轮询与工具方法

    private async Task WatchLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var state = State;
                if (state.IsBusy)
                {
                    continue;
                }

                await RefreshServerStateAsync();
                _refreshTick++;
                if (_refreshTick % 20 == 0 && !state.IsBusy)
                {
                    // 只读刷新用读闸门：不占用写操作闸门，也不把界面标记为忙碌，
                    // 否则用户点击会被静默丢弃（R-10）。
                    if (!await _readGate.WaitAsync(0))
                    {
                        continue;
                    }

                    try
                    {
                        await RefreshInstallStateAsync();
                    }
                    finally
                    {
                        _readGate.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log(LogTarget.Server, $"后台刷新异常：{ex.Message}", LogLevel.Error);
            }
        }
    }

    private async Task<ProcessIdentity?> IdentifyCachedAsync(int pid, CancellationToken cancellationToken)
    {
        var cached = _cachedIdentity;
        if (cached is not null
            && cached.Pid == pid
            && DateTime.UtcNow - _cachedIdentityAt < TimeSpan.FromSeconds(60)
            && StartTimeMatches(cached))
        {
            return cached;
        }

        try
        {
            var identity = await PortProbe.IdentifyAsync(pid, cancellationToken);
            RememberIdentity(identity);
            return identity;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 缓存命中前必须确认当前 PID 仍是缓存里的进程：只比 PID 会在进程退出、PID 被复用后沿用陈旧身份。
    /// 任一侧启动时间读不到时按「不匹配」处理（重新识别），避免把不可信的缓存当真。
    /// </summary>
    private static bool StartTimeMatches(ProcessIdentity cached)
    {
        if (cached.StartTimeUtc is not { } expected)
        {
            return false;
        }

        var actual = PortProbe.TryGetStartTimeUtc(cached.Pid);
        return actual is { } now && Math.Abs((now - expected).TotalSeconds) <= 2;
    }

    private void RememberIdentity(ProcessIdentity? identity)
    {
        _cachedIdentity = identity;
        _cachedIdentityAt = DateTime.UtcNow;
    }

    private static async Task<bool> WaitForPortReleasedAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var probe = await PortProbe.FindListeningPidsAsync(port, cancellationToken);
            if (probe.Succeeded && probe.Pids.Count == 0)
            {
                return true;
            }

            await Task.Delay(300, cancellationToken);
        }

        return false;
    }

    /// <summary>进入写闸门；抢不到时明确告诉用户「上一个操作尚未结束」，而不是静默丢弃。</summary>
    private async Task<bool> TryEnterOperationGateAsync(string action)
    {
        if (await _operationGate.WaitAsync(0))
        {
            return true;
        }

        Log(LogTarget.Plugin, $"上一个操作尚未结束，已忽略本次{action}请求", LogLevel.System);
        return false;
    }

    private async Task RunExclusiveAsync(
        string operationLabel,
        BusyKind kind,
        Func<CancellationToken, Task> operation)
    {
        if (!await TryEnterOperationGateAsync(operationLabel))
        {
            return;
        }

        try
        {
            SetBusy(operationLabel, kind);
            await operation(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log(LogTarget.Plugin, $"{operationLabel.TrimEnd('…')}失败：{ex.Message}；请查看日志页获取详细信息", LogLevel.Error);
        }
        finally
        {
            ClearBusy();
            _operationGate.Release();
        }
    }

    private void LogPluginCommand(string commandLine) => Log(LogTarget.Plugin, "$ " + commandLine, LogLevel.Command);

    private async Task<TResult> RunExclusiveResultAsync<TResult>(
        string operationLabel,
        BusyKind kind,
        Func<CancellationToken, Task<TResult>> operation,
        TResult fallback)
    {
        if (!await TryEnterOperationGateAsync(operationLabel))
        {
            return fallback;
        }

        try
        {
            SetBusy(operationLabel, kind);
            return await operation(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            Log(LogTarget.Plugin, $"{operationLabel.TrimEnd('…')}失败：{ex.Message}；请查看日志页获取详细信息", LogLevel.Error);
            return fallback;
        }
        finally
        {
            ClearBusy();
            _operationGate.Release();
        }
    }

    private void LogCommandFailure(CommandResult result, string message)
    {
        Log(LogTarget.Plugin, message, LogLevel.Error);
        foreach (var line in result.StandardError.TakeLast(AppConstants.MaxStderrTailLines))
        {
            Log(LogTarget.Plugin, line, LogLevel.Error);
        }
    }

    private void LogExitCode(CommandResult result)
    {
        Log(
            LogTarget.Plugin,
            $"命令{result.DescribeOutcome()}",
            result.IsSuccess ? LogLevel.System : LogLevel.Error);
    }

    private void ReportStderrTail()
    {
        string[] tail;
        lock (_stderrTail)
        {
            tail = _stderrTail.ToArray();
        }

        if (tail.Length == 0)
        {
            Log(LogTarget.Server, "（启动失败，未捕获到 dsh stderr 输出）", LogLevel.Error);
            return;
        }

        Log(LogTarget.Server, "—— dsh stderr 尾部 ——", LogLevel.Error);
        foreach (var line in tail)
        {
            Log(LogTarget.Server, line, LogLevel.Error);
        }

        Log(LogTarget.Server, "—— stderr 尾部结束 ——", LogLevel.Error);
    }

    private void SetBusy(string operationLabel, BusyKind kind) =>
        UpdateState(s => s with { BusyOperation = operationLabel, Busy = kind });

    private void ClearBusy() =>
        UpdateState(s => s with { BusyOperation = null, Busy = BusyKind.None });

    private void Log(LogTarget target, string text, LogLevel level)
    {
        if (_disposed)
        {
            return;
        }

        var entry = new LogEntry(text, level);
        if (target == LogTarget.Server)
        {
            ServerLog?.Invoke(entry);
        }
        else
        {
            PluginLog?.Invoke(entry);
        }
    }

    private void UpdateState(Func<AppState, AppState> mutate)
    {
        if (_disposed)
        {
            return;
        }

        AppState next;
        lock (_stateLock)
        {
            next = mutate(_state);
            _state = next;

            // 在锁内发布事件：保证两个线程的更新不会乱序到达界面。
            try
            {
                StateChanged?.Invoke(next);
            }
            catch (Exception ex)
            {
                ServerLog?.Invoke(new LogEntry($"状态通知处理失败：{ex.Message}", LogLevel.Error));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // 退出即结束由本程序启动的 dsh web：它的 stdout/stderr 是本进程的管道，
        // 留在后台只会在下次写日志时撞 EPIPE 崩溃，用户也无法归因。
        try
        {
            var state = State;
            // 必须以「监听 PID 在本程序启动的进程树内」为准。IsServerOwnedByApp 只反映包装进程
            // 是否存活，与被结束的 pid 无关；外部 dsh web 占用端口时用它判断会误杀外部进程。
            if (state.ListenerPid is { } pid && _server.IsDescendantOfStartedProcess(pid))
            {
                // 结束前必须拿到启动时间做 PID 复用二次绑定：缓存可能已过期或没有启动时间，缺就现读。
                var cached = _cachedIdentity;
                var startTimeUtc = cached is { } identity && identity.Pid == pid && identity.StartTimeUtc is { } cachedStart
                    ? cachedStart
                    : PortProbe.TryGetStartTimeUtc(pid);
                ServerService.KillProcessTree(pid, startTimeUtc);
            }
        }
        catch (Exception)
        {
            // 退出路径尽力而为，不阻塞窗口关闭。
        }

        _server.Dispose();
        _lifetime.Dispose();
    }
}
