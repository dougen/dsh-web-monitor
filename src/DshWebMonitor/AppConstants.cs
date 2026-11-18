namespace DshWebMonitor;

internal static class AppConstants
{
    /// <summary>dsh web 默认监听端口。</summary>
    public const int Port = 3080;

    /// <summary>npm 全局包名。</summary>
    public const string NpmPackage = "@deepseek-ai/dsh";

    /// <summary>插件操作使用的 dsh profile。</summary>
    public const string PluginProfile = "web";

    /// <summary>启动命令：直接使用全局安装的 dsh CLI，不经过 npx / npm 缓存。</summary>
    public const string StartCommand = "dsh web --no-open";

    /// <summary>启动后等待端口进入 LISTENING 的最长时间。</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>停止后等待端口释放的最长时间。</summary>
    public static readonly TimeSpan PortReleaseTimeout = TimeSpan.FromSeconds(20);

    /// <summary>写操作（npm 安装 / 更新、插件增删改）的命令超时：超时后终止整个进程树。</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>只读查询（npm view / list、dsh plugin list）的命令超时。</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    /// <summary>系统工具（netstat / tasklist / wmic / powershell）的探测超时。</summary>
    public static readonly TimeSpan SystemToolTimeout = TimeSpan.FromSeconds(10);

    /// <summary>等待管道关闭信号（子进程已退出但孙进程仍持有管道）的兜底时长。</summary>
    public static readonly TimeSpan OutputCloseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>端口探测连续失败多少次后按「未运行」收敛，避免状态永久停在检测中。</summary>
    public const int ProbeFailureThreshold = 3;

    /// <summary>插件版本查询（npm view）的最大并发数。</summary>
    public const int MaxConcurrentPackageQueries = 4;

    /// <summary>日志面板内存中保留的最大行数。</summary>
    public const int MaxLogLines = 2000;

    /// <summary>启动失败时回放的 stderr 尾部行数。</summary>
    public const int MaxStderrTailLines = 20;
}
