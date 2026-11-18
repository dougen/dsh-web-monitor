using DshWebMonitor.Services;

namespace DshWebMonitor;

internal enum ServerStatus
{
    /// <summary>正在检测。</summary>
    Probing,

    /// <summary>端口空闲，dsh web 未运行。</summary>
    Stopped,

    /// <summary>正在启动，等待端口进入 LISTENING。</summary>
    Starting,

    /// <summary>已通过身份校验，dsh web 运行中。</summary>
    Running,

    /// <summary>正在停止。</summary>
    Stopping,

    /// <summary>端口被非 dsh 进程占用，不提供停止/重启。</summary>
    PortConflict,

    /// <summary>启动失败（超时或进程提前退出）。</summary>
    StartFailed,
}

internal enum InstallStatus
{
    Probing,
    Unknown,
    NotInstalled,
    Installed,
}

/// <summary>当前占用写闸门的操作类型：界面据此显示加载状态，不再解析中文文案。</summary>
internal enum BusyKind
{
    None,
    CheckNpmUpdates,
    InstallNpmPackage,
    UpdateNpmPackage,
    PluginCommand,
}

/// <summary>
/// 唯一的界面状态快照：主窗体控件、托盘菜单、状态文本全部由它派生，
/// 不在任何一方维护独立的启用/禁用判断。
/// </summary>
internal sealed record AppState
{
    /// <summary>被监控的端口；界面文案统一从快照读取，不在控件里硬编码。</summary>
    public int Port { get; init; } = AppConstants.Port;

    public ServerStatus Server { get; init; } = ServerStatus.Probing;

    public InstallStatus Install { get; init; } = InstallStatus.Probing;

    /// <summary>监听端口的进程 PID（运行中或端口被占用时有效）。</summary>
    public int? ListenerPid { get; init; }

    public string? InstalledVersion { get; init; }

    public string? LatestVersion { get; init; }

    /// <summary>
    /// 当前通道没有更高版本、但其它通道存在更高版本时的提示（含来源 tag），
    /// 例如 "0.2.2-rc.2（next）"，用于补充「已是最新版本」的说明。
    /// </summary>
    public string? NewerVersionHint { get; init; }

    /// <summary>最近一次检查更新的失败原因；非空时界面必须展示失败，而不是停在「尚未检查更新」。</summary>
    public string? UpdateCheckError { get; init; }

    /// <summary>非空表示正在执行 npm / 插件命令，此时禁止一切写操作。</summary>
    public string? BusyOperation { get; init; }

    /// <summary>忙碌操作的类型，供界面显示加载状态。</summary>
    public BusyKind Busy { get; init; }

    public bool IsBusy =>
        BusyOperation is not null || Server is ServerStatus.Starting or ServerStatus.Stopping;

    public bool HasUpdate =>
        InstalledVersion is not null
        && LatestVersion is not null
        && SemverComparer.Instance.Compare(LatestVersion, InstalledVersion) > 0;

    public bool CanStart =>
        !IsBusy
        && Install == InstallStatus.Installed
        && Server is ServerStatus.Stopped or ServerStatus.StartFailed;

    public bool CanStop => !IsBusy && Server == ServerStatus.Running;

    public bool CanRestart => !IsBusy && Server == ServerStatus.Running;

    public bool CanOpenWebUi => Server == ServerStatus.Running;

    public bool CanInstallPackage => !IsBusy && Install == InstallStatus.NotInstalled;

    public bool CanCheckNpmUpdates => !IsBusy && Install == InstallStatus.Installed;

    public bool CanUpdatePackage => !IsBusy && Install == InstallStatus.Installed && HasUpdate;

    public bool CanRunPluginCommand => !IsBusy;

    public string ServerStatusText => Server switch
    {
        ServerStatus.Probing => "正在检测…",
        ServerStatus.Stopped => "未运行",
        ServerStatus.Starting => "正在启动…",
        ServerStatus.Running => "运行中",
        ServerStatus.Stopping => "正在停止…",
        ServerStatus.PortConflict => $"端口 {Port} 被其他进程占用",
        ServerStatus.StartFailed => "启动失败",
        _ => "未知",
    };

    public string InstallText => Install switch
    {
        InstallStatus.Probing => "检测中…",
        InstallStatus.Unknown => "无法检测",
        InstallStatus.NotInstalled => "未安装",
        InstallStatus.Installed => "已安装",
        _ => string.Empty,
    };

    public string UpdateHintText => Install switch
    {
        InstallStatus.Probing => "正在读取 npm 全局包信息",
        InstallStatus.Unknown => "无法读取 npm 全局包状态，请确认 npm 可用后重试",
        InstallStatus.NotInstalled => "尚未安装，点击「下载安装」从 npm 获取",
        InstallStatus.Installed when UpdateCheckError is { Length: > 0 } error => $"检查更新失败：{error}",
        InstallStatus.Installed when HasUpdate => $"发现新版本 v{LatestVersion}",
        InstallStatus.Installed when LatestVersion is not null && NewerVersionHint is { Length: > 0 } hint =>
            $"已是最新版本；其它通道有 v{hint}",
        InstallStatus.Installed when LatestVersion is not null => "已是最新版本",
        InstallStatus.Installed => "尚未检查更新",
        _ => string.Empty,
    };
}
