using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using DshWebMonitor;
using DshWebMonitor.Models;
using DshWebMonitor.Services;
using DshWebMonitor.Ui;

namespace DshWebMonitor.SmokeTests;

/// <summary>
/// 冒烟测试入口（纯控制台断言，不引入第三方测试框架）。
///
/// 默认运行 = 完全离线：只驱动纯函数、LogPanel（不显示窗口）、ProcessRunner（本机短命令）、
/// ProfileConfigService（临时目录）与 MainForm 的纯状态渲染逻辑；不触碰真实 3080、
/// 不执行 npm / dsh 写操作、不弹出模态框。
/// 集成用例（真实 3080 监听身份、npm 全局包、dsh plugin list、fixture 生命周期）只在 --env 下运行。
/// </summary>
internal static class Program
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan SingleCaseTimeout = TimeSpan.FromSeconds(120);

    private static readonly List<string> FailedCases = new();
    private static int _passed;
    private static int _skipped;
    private static Stopwatch _overall = Stopwatch.StartNew();
    private static bool _envMode;

    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static TimeSpan Remaining => OverallTimeout - _overall.Elapsed;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var normalized = args.Select(argument => argument.Trim().ToLowerInvariant()).ToArray();
        if (normalized.Contains("--help"))
        {
            PrintHelp();
            return 0;
        }

        if (normalized.Contains("--list"))
        {
            PrintList();
            return 0;
        }

        var unknown = args
            .Where(argument => argument.Trim().ToLowerInvariant() is not ("--env" or "--help" or "--list"))
            .ToArray();
        if (unknown.Length > 0)
        {
            Console.Error.WriteLine("未知参数：" + string.Join(" ", unknown));
            Console.Error.WriteLine("使用 --help 查看用法。");
            return 2;
        }

        _envMode = normalized.Contains("--env");

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        catch (Exception)
        {
            // 无桌面环境时也不影响纯逻辑用例。
        }

        Console.WriteLine("=== DSH Web Monitor 冒烟测试 ===");
        Console.WriteLine(
            _envMode
                ? "模式：--env 集成（真实 3080 / npm 全局包 / dsh CLI / 高位端口 fixture）"
                : "模式：默认离线（不显示窗口、不触碰真实 3080、不执行 npm/dsh 写操作）");
        Console.WriteLine();

        _overall = Stopwatch.StartNew();
        foreach (var test in BuildCases())
        {
            if (test.EnvOnly && !_envMode)
            {
                _skipped++;
                Console.WriteLine($"SKIP  {test.Name}（默认离线模式，仅在 --env 下运行）");
                continue;
            }

            if (Remaining <= TimeSpan.Zero)
            {
                FailedCases.Add(test.Name);
                Console.WriteLine($"FAIL  {test.Name}（整轮已超过 {OverallTimeout.TotalSeconds:F0} 秒，未执行）");
                continue;
            }

            await RunAsync(test);
        }

        var elapsed = _overall.Elapsed;
        Console.WriteLine();
        Console.WriteLine(
            $"结果：通过 {_passed}，失败 {FailedCases.Count}，跳过 {_skipped}，用时 {elapsed.TotalSeconds:F1} 秒");

        if (elapsed > OverallTimeout)
        {
            Console.WriteLine($"整轮超时：已运行 {elapsed.TotalSeconds:F1} 秒，超过上限 {OverallTimeout.TotalSeconds:F0} 秒");
            return 1;
        }

        if (FailedCases.Count > 0)
        {
            Console.WriteLine("失败用例：" + string.Join("、", FailedCases));
            return 1;
        }

        return 0;
    }

    // ------------------------------------------------------------------ 用例清单

    private static IReadOnlyList<TestCase> BuildCases() => new[]
    {
        new TestCase("AppState 状态派生与能力开关", false, TestAppStateAsync),
        new TestCase("PortProbe 监听候选 PID 解析与本地地址优先级", false, TestPortProbeParsingAsync),
        new TestCase("PortProbe dsh web 身份判定（R-02 回归）", false, TestDshWebIdentityAsync),
        new TestCase("npm list / dist-tags JSON 解析", false, TestNpmJsonParsingAsync),
        new TestCase("插件包名与版本校验（M-06）", false, TestPluginPackageValidationAsync),
        new TestCase("更新通道解析与 semver 比较（M-05）", false, TestChannelResolutionAsync),
        new TestCase("CommandResult 成功语义与文案（M-24/L-01）", false, TestCommandResultSemanticsAsync),
        new TestCase("ProcessRunner 正常输出与错误行判定", false, TestProcessRunnerNormalAsync),
        new TestCase("ProcessRunner 超时终止进程树", false, TestProcessRunnerTimeoutAsync),
        new TestCase("ProcessRunner 取消终止进程树", false, TestProcessRunnerCancelAsync),
        new TestCase("Kill 进程时按 PID + 启动时间二次绑定（R-06）", false, TestKillProcessBindingAsync),
        new TestCase("ProfileConfigService 原子写往返（R-07/M-24）", false, TestProfileConfigRoundTripAsync),
        new TestCase("ProfileConfigService 损坏文件不覆盖", false, TestProfileConfigCorruptAsync),
        new TestCase("LogPanel 单行批量追加与 2000 行裁剪（R-09/M-16）", false, TestLogPanelSingleLineAsync),
        new TestCase("LogPanel 多行条目/清空/销毁（R-09/R-11/M-16）", false, TestLogPanelMultiLineAsync),
        new TestCase("LogPanel 待刷新队列上限（模态阻塞）", false, TestLogPanelPendingCapAsync),
        new TestCase("UI 主窗体按钮与托盘菜单随快照同步（R-13）", false, UiBehaviorTests.TestStateDrivenControlsAsync),
        new TestCase("UI 关闭语义：托盘/无托盘/关机/任务管理器（R-13）", false, UiBehaviorTests.TestCloseSemanticsAsync),
        new TestCase("UI 不存在 TrayAvailabilityOverride 静态开关（R-13）", false, UiBehaviorTests.TestNoStaticTrayOverrideAsync),
        new TestCase("真实 3080 监听身份识别", true, TestRealDshWebIdentityAsync),
        new TestCase("npm 全局包检测", true, TestNpmGlobalProbeAsync),
        new TestCase("dsh plugin list --json 解析", true, TestDshPluginListAsync),
        new TestCase("fixture 启动→停止→重启", true, TestControllerLifecycleAsync),
        new TestCase("退出监控器时结束由本程序启动的 dsh web", true, TestDisposeStopsOwnedServerAsync),
        new TestCase("启动超时状态收敛（R-01）", true, TestStartTimeoutConvergenceAsync),
        new TestCase("进程提前退出识别", true, TestEarlyExitAsync),
    };

    // ------------------------------------------------------------------ A. 纯离线用例

    private static Task TestAppStateAsync()
    {
        var running = new AppState
        {
            Server = ServerStatus.Running,
            ListenerPid = 1234,
            Install = InstallStatus.Installed,
            InstalledVersion = "1.0.0",
        };

        Assert(running.CanStop, "运行中应允许停止");
        Assert(running.CanRestart, "运行中应允许重启");
        Assert(running.CanOpenWebUi, "运行中应允许打开 Web UI");
        Assert(!running.CanStart, "运行中不得允许启动");
        Assert(!running.IsBusy, "运行中不是忙碌状态");
        AssertEqual(BusyKind.None, running.Busy, "默认 BusyKind 应为 None");
        Assert(running.CanRunPluginCommand, "空闲时允许执行插件命令");

        var conflict = running with { Server = ServerStatus.PortConflict, ListenerPid = 4321 };
        Assert(!conflict.CanStop && !conflict.CanRestart && !conflict.CanOpenWebUi, "端口被占用时禁用停止 / 重启 / 打开");
        Assert(!conflict.CanStart, "端口被占用时不得启动");
        AssertEqual("端口 3080 被其他进程占用", conflict.ServerStatusText, "端口占用文案");

        var stopped = running with { Server = ServerStatus.Stopped, ListenerPid = null };
        Assert(stopped.CanStart, "已安装且未运行时应允许启动");
        Assert(!stopped.CanStop && !stopped.CanRestart && !stopped.CanOpenWebUi, "未运行时不允许停止 / 重启 / 打开");
        AssertEqual("未运行", stopped.ServerStatusText, "未运行文案");

        var startFailed = stopped with { Server = ServerStatus.StartFailed };
        Assert(startFailed.CanStart, "启动失败后应允许重试启动");
        AssertEqual("启动失败", startFailed.ServerStatusText, "启动失败文案");

        var probing = running with { Server = ServerStatus.Probing };
        Assert(!probing.CanStart, "探测中不得启动");

        var starting = stopped with { Server = ServerStatus.Starting };
        Assert(starting.IsBusy, "启动中应为忙碌");
        Assert(!starting.CanStart && !starting.CanStop, "启动中禁用启动 / 停止");
        Assert(!starting.CanRunPluginCommand, "启动中禁止插件命令");

        var stopping = running with { Server = ServerStatus.Stopping };
        Assert(stopping.IsBusy, "停止中应为忙碌");
        Assert(!stopping.CanStop && !stopping.CanRestart, "停止中禁用停止 / 重启");

        var busy = stopped with { BusyOperation = "正在安装插件…", Busy = BusyKind.PluginCommand };
        Assert(busy.IsBusy, "有 BusyOperation 时应为忙碌");
        AssertEqual(BusyKind.PluginCommand, busy.Busy, "BusyKind 应保留操作类型");
        Assert(!busy.CanStart && !busy.CanRunPluginCommand && !busy.CanCheckNpmUpdates, "忙碌时禁用所有写操作");

        var unknown = stopped with { Install = InstallStatus.Unknown, InstalledVersion = null };
        Assert(!unknown.CanStart, "无法检测安装状态时不得启动");
        Assert(!unknown.CanInstallPackage && !unknown.CanCheckNpmUpdates && !unknown.CanUpdatePackage, "安装状态未知时禁用 npm 操作");
        AssertEqual("无法检测", unknown.InstallText, "InstallStatus.Unknown 文案");

        var notInstalled = stopped with { Install = InstallStatus.NotInstalled, InstalledVersion = null };
        Assert(!notInstalled.CanStart, "未安装时不得启动");
        Assert(notInstalled.CanInstallPackage, "未安装时应允许安装");

        // HasUpdate 必须按 semver 比较，不能用字符串不等。
        var noUpdatePrerelease = stopped with { InstalledVersion = "1.0.0", LatestVersion = "1.0.0-rc.1" };
        Assert(!noUpdatePrerelease.HasUpdate, "latest 为同版本预发布（1.0.0-rc.1）时不应提示更新");
        var noUpdatePrefix = stopped with { InstalledVersion = "v1.2.3", LatestVersion = "1.2.3" };
        Assert(!noUpdatePrefix.HasUpdate, "v1.2.3 与 1.2.3 语义相等时不应提示更新");
        var hasUpdate = stopped with { InstalledVersion = "1.9.9", LatestVersion = "2.0.0" };
        Assert(hasUpdate.HasUpdate, "2.0.0 高于 1.9.9 时应提示更新");
        Assert(hasUpdate.CanUpdatePackage, "发现新版本时更新按钮应可用");
        var prereleaseToRelease = stopped with { InstalledVersion = "1.0.0-rc.1", LatestVersion = "1.0.0" };
        Assert(prereleaseToRelease.HasUpdate, "预发布版升级到同号正式版应提示更新");
        Assert(!stopped.HasUpdate, "未检查更新（LatestVersion 为空）时不应提示更新");
        Assert(!stopped.CanUpdatePackage, "未检查更新时更新按钮应禁用");
        AssertEqual("尚未检查更新", stopped.UpdateHintText, "未检查更新文案");

        var failedCheck = stopped with
        {
            LatestVersion = null,
            UpdateCheckError = "无法读取 npm dist-tags（退出码 1），详见日志页",
        };
        AssertEqual(
            "检查更新失败：无法读取 npm dist-tags（退出码 1），详见日志页",
            failedCheck.UpdateHintText,
            "检查失败时必须显示失败原因，而不是「尚未检查更新」");
        AssertEqual("已安装", stopped.InstallText, "已安装标签不再重复版本号");

        var newerElsewhere = stopped with
        {
            InstalledVersion = "0.2.2-rc.1",
            LatestVersion = "0.2.2-rc.1",
            NewerVersionHint = "0.2.2-rc.2（next）",
        };
        AssertEqual(
            "已是最新版本；其它通道有 v0.2.2-rc.2（next）",
            newerElsewhere.UpdateHintText,
            "当前通道无更新时应提示其它通道的更高版本");

        return Task.CompletedTask;
    }

    private static Task TestPortProbeParsingAsync()
    {
        var netstat = new[]
        {
            string.Empty,
            "活动连接",
            string.Empty,
            "  协议  本地地址          外部地址        状态           PID",
            "  TCP    0.0.0.0:3080           0.0.0.0:0              LISTENING       111",
            "  TCP    [::]:3080              [::]:0                 LISTENING       222",
            "  TCP    [::1]:3080             [::]:0                 LISTENING       444",
            "  TCP    127.0.0.1:3080         0.0.0.0:0              LISTENING       333",
            "  TCP    127.0.0.1:3080         127.0.0.1:49541        TIME_WAIT       9999",
            "  TCP    127.0.0.1:5173         0.0.0.0:0              LISTENING       5173",
        };

        var candidates = PortProbe.ParseListeningCandidatePids(netstat, 3080);
        Assert(
            candidates.SequenceEqual(new[] { 333, 111, 444, 222 }),
            "监听候选应去重并按 127.0.0.1 → 0.0.0.0 → [::1] → [::] 排序，实际：" + string.Join(",", candidates));
        AssertEqual(333, PortProbe.ParseListeningPid(netstat, 3080) ?? -1, "便捷方法应取第一个候选");
        AssertEqual(5173, PortProbe.ParseListeningPid(netstat, 5173) ?? -1, "应能解析其它端口");
        AssertEqual(0, PortProbe.ParseListeningCandidatePids(netstat, 1234).Count, "无监听端口应返回空集合");
        Assert(PortProbe.ParseListeningPid(netstat, 1234) is null, "无监听端口便捷方法应返回 null");

        var timeWaitOnly = new[]
        {
            "  TCP    127.0.0.1:3080         127.0.0.1:49541        TIME_WAIT       0",
        };
        AssertEqual(0, PortProbe.ParseListeningCandidatePids(timeWaitOnly, 3080).Count, "TIME_WAIT 行必须忽略");

        var taskList = new[] { "\"node.exe\",\"5040\",\"Console\",\"1\",\"575,024 K\"" };
        AssertEqual("node.exe", PortProbe.ParseTaskListName(taskList) ?? string.Empty, "应解析出 tasklist 映像名");

        var wmic = new[]
        {
            string.Empty,
            "CommandLine=\"C:\\nodejs\\node.exe\" C:\\npm\\@deepseek-ai\\dsh\\lib\\bin.js web",
            "Name=node.exe",
        };
        Assert(
            PortProbe.ParseWmicCommandLine(wmic)?.Contains("@deepseek-ai\\dsh", StringComparison.OrdinalIgnoreCase) == true,
            "应解析出 wmic CommandLine 字段");

        return Task.CompletedTask;
    }

    private static Task TestDshWebIdentityAsync()
    {
        const string realCommandLine =
            "\"C:\\Program Files\\nodejs\\node.exe\" "
            + "C:\\Users\\x\\AppData\\Roaming\\npm/node_modules/@deepseek-ai/dsh/lib/bin.js web --no-open";
        Assert(PortProbe.IsDshWebNode("node.exe", realCommandLine), "npm 全局安装的 dsh web 命令行应判定为 dsh web");

        Assert(
            PortProbe.IsDshWebNode("node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\npm\\@deepseek-ai\\dsh\\lib\\bin.js\" web"),
            "带引号的 bin.js + web 子命令应判定为 dsh web");
        Assert(
            PortProbe.IsDshWebNode("NODE.EXE", "C:\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js web --no-open"),
            "映像名大小写不敏感");

        // R-02 回归：命令行里只要出现 "dsh" 字样不再通过。
        var falsePositives = new[]
        {
            ("node.exe", "node D:\\dsh-projects\\my-app\\server.js"),
            ("node.exe", "node C:\\Users\\dsh\\dev\\app.js"),
            ("node.exe", "node app.js --project dsh-tools"),
            ("node.exe", "C:\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js plugin --profile web list --json"),
            ("python.exe", "python.exe \"dsh web\""),
            ("node.exe", string.Empty),
            ("node.exe", (string?)null),
        };

        foreach (var (processName, commandLine) in falsePositives)
        {
            Assert(
                !PortProbe.IsDshWebNode(processName, commandLine),
                $"不得误判为 dsh web：{processName} / {commandLine ?? "<null>"}");
        }

        Assert(
            !PortProbe.IsDshWebNode("node.exe", "C:\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js"),
            "缺少 web 子命令时不得判定为 dsh web");

        return Task.CompletedTask;
    }

    private static Task TestNpmJsonParsingAsync()
    {
        var installed = new[]
        {
            "npm WARN config global `--global` is deprecated",
            "{",
            "  \"name\": \"npm\",",
            "  \"dependencies\": {",
            "    \"@deepseek-ai/dsh\": { \"version\": \"0.2.2-rc.1\" }",
            "  }",
            "}",
        };
        Assert(NpmService.TryParseInstallState(installed, out var isInstalled, out var version), "已安装 JSON 应可解析");
        Assert(isInstalled, "应识别为已安装");
        AssertEqual("0.2.2-rc.1", version ?? string.Empty, "应解析出已安装版本");

        var notInstalled = new[]
        {
            "{",
            "  \"name\": \"npm\",",
            "  \"dependencies\": {",
            "    \"left-pad\": { \"version\": \"1.0.0\" }",
            "  }",
            "}",
        };
        Assert(NpmService.TryParseInstallState(notInstalled, out var flag, out _), "未安装 JSON 应可解析");
        Assert(!flag, "dependencies 中没有 dsh 时应识别为未安装");
        NpmService.TryParseInstallState(notInstalled, out _, out var missingVersion);
        Assert(missingVersion is null, "未安装时版本应为空");

        Assert(
            NpmService.TryParseInstallState(new[] { "{}" }, out var emptyInstalled, out _),
            "空对象仍是可解析的 JSON");
        Assert(!emptyInstalled, "空对象应视为未安装");

        Assert(
            !NpmService.TryParseInstallState(new[] { "npm ERR! code ENOENT" }, out _, out _),
            "非 JSON 输出不能被断言为未安装（返回 false）");

        var objectForm = new[]
        {
            "npm WARN config global `--global` is deprecated",
            "{\"latest\":\"0.2.2-rc.1\",\"next\":\"0.3.0-rc.2\",\"stable\":\"0.2.1\"}",
        };
        var tags = NpmService.ParseDistTags(objectForm);
        AssertEqual("0.2.2-rc.1", tags.TryGetValue("latest", out var latest) ? latest : string.Empty, "应解析对象形式 dist-tags");
        AssertEqual("0.3.0-rc.2", tags.TryGetValue("next", out var next) ? next : string.Empty, "应解析 next tag");

        var arrayForm = new[]
        {
            "npm notice",
            "[",
            "  {",
            "    \"alpha\": \"0.2.2-alpha.3\",",
            "    \"latest\": \"0.2.2-rc.1\"",
            "  }",
            "]",
        };
        var arrayTags = NpmService.ParseDistTags(arrayForm);
        AssertEqual("0.2.2-rc.1", arrayTags.TryGetValue("latest", out var arrayLatest) ? arrayLatest : string.Empty, "应解析数组形式 dist-tags");

        AssertEqual(0, NpmService.ParseDistTags(new[] { "not json" }).Count, "非 JSON 应返回空集合");
        AssertEqual(0, NpmService.ParseDistTags(Array.Empty<string>()).Count, "空输出应返回空集合");

        return Task.CompletedTask;
    }

    private static Task TestPluginPackageValidationAsync()
    {
        Assert(PluginService.IsValidPackageName("dsh-notify-me"), "普通包名应合法");
        Assert(PluginService.IsValidPackageName("@scope/pkg"), "作用域包名应合法");
        Assert(PluginService.IsValidPackageName("@deepseek-ai/dsh"), "dsh 包名应合法");
        Assert(!PluginService.IsValidPackageName("pkg@1.2.3"), "包名不得携带版本号（M-06）");
        Assert(!PluginService.IsValidPackageName("pkg@^1.0.0"), "包名不得携带 range");
        Assert(!PluginService.IsValidPackageName("-x"), "以 - 开头的包名应拒绝");
        Assert(!PluginService.IsValidPackageName("a b"), "含空白应拒绝");
        Assert(!PluginService.IsValidPackageName("a;b"), "含分号应拒绝");
        Assert(!PluginService.IsValidPackageName("a&&b"), "含 && 应拒绝");
        Assert(!PluginService.IsValidPackageName(string.Empty), "空包名应拒绝");
        Assert(!PluginService.IsValidPackageName(null), "null 包名应拒绝");

        Assert(PluginService.TryParsePackageSpec("@scope/pkg@1.2.3", out var scoped, out var scopedVersion), "作用域包名+版本应可拆分");
        AssertEqual("@scope/pkg", scoped, "作用域包名应保持不变");
        AssertEqual("1.2.3", scopedVersion ?? string.Empty, "版本应拆出");

        Assert(PluginService.TryParsePackageSpec("pkg@1.2.3", out var plain, out var plainVersion), "普通包名+版本应可拆分");
        AssertEqual("pkg", plain, "包名应去掉版本");
        AssertEqual("1.2.3", plainVersion ?? string.Empty, "版本应拆出");

        Assert(PluginService.TryParsePackageSpec("pkg", out var noVersionPackage, out var noVersion), "无版本包名应可解析");
        AssertEqual("pkg", noVersionPackage, "包名应正确");
        Assert(noVersion is null, "无版本时 version 应为 null");

        Assert(!PluginService.TryParsePackageSpec("pkg@-x", out _, out _), "非法版本应拒绝拆分");
        Assert(!PluginService.TryParsePackageSpec(string.Empty, out _, out _), "空输入应拒绝拆分");
        Assert(!PluginService.TryParsePackageSpec("@scope/pkg@", out _, out _), "空版本段应拒绝拆分");

        Assert(PluginService.IsValidVersion("1.2.3"), "1.2.3 应为合法版本");
        Assert(PluginService.IsValidVersion("0.2.2-rc.1"), "预发布版本应为合法版本");
        Assert(PluginService.IsValidVersion("v1.2.3"), "v 前缀版本应为合法版本");
        Assert(!PluginService.IsValidVersion("-x"), "-x 应被拒绝");
        Assert(PluginService.IsValidVersion("1.2.3 "), "带尾随空白的版本应被 Trim 后接受");
        Assert(!PluginService.IsValidVersion("1.2.3; rm -rf"), "含命令分隔符的版本应拒绝");
        Assert(!PluginService.IsValidVersion(string.Empty), "空版本应拒绝");
        Assert(!PluginService.IsValidVersion(null), "null 版本应拒绝");

        AssertEqual("dsh plugin --profile web list --json", PluginService.BuildListJsonCommand(), "list --json 子命令");
        AssertEqual("dsh plugin --profile web add foo", PluginService.BuildAddCommand("foo"), "add 子命令（无版本）");
        AssertEqual("dsh plugin --profile web add foo@1.2.3", PluginService.BuildAddCommand("foo", "1.2.3"), "add 子命令（指定版本）");
        AssertEqual("dsh plugin --profile web remove foo", PluginService.BuildRemoveCommand("foo"), "remove 子命令");
        AssertEqual("dsh plugin --profile web update foo", PluginService.BuildUpdateCommand("foo"), "update 子命令（无版本）");
        AssertEqual("dsh plugin --profile web update foo@1.2.3", PluginService.BuildUpdateCommand("foo", "1.2.3"), "update 子命令（指定版本）");

        var pluginList = new[]
        {
            "npm notice",
            "[",
            "  {",
            "    \"dependencies\": {",
            "      \"@scope/plugin-a\": { \"version\": \"1.0.0\" },",
            "      \"@scope/plugin-b\": { \"version\": \"2.1.0\" }",
            "    }",
            "  }",
            "]",
        };
        Assert(
            PluginService.TryParseInstalledPlugins(pluginList, out var parsedPlugins),
            "插件列表 JSON 应能解析");
        AssertEqual(2, parsedPlugins.Count, "应解析出两个插件");
        Assert(
            parsedPlugins.Any(item => item.Name == "@scope/plugin-a" && item.Version == "1.0.0"),
            "应解析插件名与版本");
        Assert(
            !PluginService.TryParseInstalledPlugins(new[] { "dsh plugin list failed" }, out _),
            "非 JSON 输出必须返回 false，不能当成「没有插件」");

        return Task.CompletedTask;
    }

    private static Task TestChannelResolutionAsync()
    {
        var onlyPrerelease = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["next"] = "2.0.0-rc.2",
            ["alpha"] = "2.0.0-alpha.1",
        };
        Assert(
            !UpdateChannels.TryResolve(onlyPrerelease, UpdateChannel.Stable, out var missingStable),
            "stable 缺失 latest 且只有预发布 tag 时必须返回 false（M-05）");
        Assert(missingStable is null, "解析失败时 target 应为 null");

        var stableFallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rc"] = "1.1.0",
            ["next"] = "1.2.0-rc.1",
        };
        Assert(UpdateChannels.TryResolve(stableFallback, UpdateChannel.Stable, out var fallback), "stable 应回退到最高正式版");
        Assert(fallback is not null, "回退时 target 不应为 null");
        AssertEqual("1.1.0", fallback!.Version, "stable 回退版本应为最高正式版");
        Assert(fallback.UsedFallback, "回退必须标记 UsedFallback");
        AssertEqual("rc", fallback.ResolvedTag, "回退来源 tag 应记录");

        // dsh 把 0.2.2-rc.2 只挂在 next tag 上：rc 通道仍必须找到它（回归用例）。
        var dshTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = "0.2.2-alpha.3",
            ["latest"] = "0.2.2-rc.1",
            ["next"] = "0.2.2-rc.2",
        };
        Assert(
            UpdateChannels.TryResolve(dshTags, UpdateChannel.Rc, out var rcFromNext),
            "rc 缺失同名 tag 时应从其它 tag 找到同一预发布线");
        Assert(rcFromNext is not null, "rc 回退 target 不应为 null");
        AssertEqual("0.2.2-rc.2", rcFromNext!.Version, "rc 通道应解析出 next 上的 0.2.2-rc.2");
        AssertEqual("next", rcFromNext.ResolvedTag, "回退来源 tag 应记录 next");
        Assert(rcFromNext.UsedFallback, "rc 回退应标记 UsedFallback");

        var rcFallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["latest"] = "1.2.0",
            ["next"] = "1.3.0-beta.1",
        };
        Assert(UpdateChannels.TryResolve(rcFallback, UpdateChannel.Rc, out var rc), "rc 缺失同名 tag 且无 rc 版本时应回退");
        Assert(rc is not null, "rc 回退 target 不应为 null");
        AssertEqual("1.2.0", rc!.Version, "无 rc 版本时应回退 latest 版本");
        AssertEqual("latest", rc.ResolvedTag, "无 rc 版本时应回退 latest");
        Assert(rc.UsedFallback, "rc 回退应标记 UsedFallback");

        var betaTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["latest"] = "1.0.0",
            ["next"] = "2.0.0-beta.3",
        };
        Assert(
            UpdateChannels.TryResolve(betaTags, UpdateChannel.Beta, out var beta),
            "beta 缺失同名 tag 时应找到其它 tag 上的 beta 版本");
        AssertEqual("2.0.0-beta.3", beta!.Version, "beta 通道应取最高 beta 版本");

        var explicitTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["latest"] = "1.2.0",
            ["next"] = "2.0.0-rc.1",
            ["alpha"] = "2.0.0-alpha.1",
        };
        Assert(UpdateChannels.TryResolve(explicitTags, UpdateChannel.Next, out var next), "next 通道应解析同名 tag");
        AssertEqual("2.0.0-rc.1", next!.Version, "next 应取 next 版本");
        Assert(!next.UsedFallback, "命中同名 tag 时不应回退");

        Assert(UpdateChannels.TryResolve(explicitTags, UpdateChannel.All, out var all), "all 通道应解析");
        AssertEqual("2.0.0-rc.1", all!.Version, "all 应取最高 semver（含预发布）");
        Assert(!all.UsedFallback, "all 不应标记回退");

        Assert(
            !UpdateChannels.TryResolve(new Dictionary<string, string>(), UpdateChannel.Stable, out _),
            "无 dist-tags 时应解析失败");

        Assert(SemverComparer.Instance.Compare("1.0.0", "1.0.0-rc.1") > 0, "正式版高于同号预发布版");
        Assert(SemverComparer.Instance.Compare("1.0.0-rc.2", "1.0.0-rc.1") > 0, "预发布序号递增");
        Assert(SemverComparer.Instance.Compare("1.0.0-rc.1", "1.0.0-beta.9") > 0, "rc 高于 beta");
        Assert(SemverComparer.Instance.Compare("2.0.0", "1.9.9") > 0, "主版本优先");
        Assert(SemverComparer.Instance.Compare("v1.2.3", "1.2.3") == 0, "v 前缀与无前缀应相等");
        Assert(SemverComparer.Instance.Compare("1.0.0+build.5", "1.0.0") == 0, "build metadata 不参与比较");

        AssertEqual("stable", UpdateChannels.ToLabel(UpdateChannel.Stable), "stable 文案");
        AssertEqual("latest", UpdateChannels.ToDistTag(UpdateChannel.Stable), "stable 对应 latest");
        Assert(UpdateChannels.IsVersion("1.2.3+build.5"), "带 build metadata 的版本应视为合法版本");
        AssertEqual("all", UpdateChannels.ToLabel(UpdateChannel.All), "all 文案");
        AssertEqual(6, UpdateChannels.Selectable.Count, "通道数量应为 6");

        // R-08 / M-24：PluginInfo.HasUpdate 必须走 semver，而不是字符串不等。
        Assert(new PluginInfo("pkg", "1.9.9", "2.0.0", true).HasUpdate, "插件 2.0.0 高于 1.9.9 时应可更新");
        Assert(!new PluginInfo("pkg", "v1.2.3", "1.2.3", true).HasUpdate, "插件 v1.2.3 与 1.2.3 语义相等时不应可更新");
        Assert(!new PluginInfo("pkg", "1.0.0", "1.0.0-rc.1", true).HasUpdate, "插件目标为同版本预发布时不应可更新");
        Assert(!new PluginInfo("pkg", "1.0.0", null, true).HasUpdate, "未查到最新版本时不应可更新");

        return Task.CompletedTask;
    }

    private static Task TestCommandResultSemanticsAsync()
    {
        var ok = new CommandResult(0, new[] { "done" }, Array.Empty<string>());
        Assert(ok.IsSuccess, "退出码 0 且无错误行应为成功");
        Assert(!ok.TimedOut, "正常结果不应标记超时");
        Assert(ok.DescribeOutcome().Contains("退出码 0", StringComparison.Ordinal), "正常结果文案应包含退出码 0");

        var exitNonZero = new CommandResult(1, Array.Empty<string>(), new[] { "boom" });
        Assert(!exitNonZero.IsSuccess, "退出码非 0 应为失败");
        Assert(exitNonZero.DescribeOutcome().Contains("退出码 1", StringComparison.Ordinal), "失败结果文案应包含退出码 1");

        var npmError = new CommandResult(0, Array.Empty<string>(), new[] { "npm ERR! code E404" });
        Assert(!npmError.IsSuccess, "stderr 出现 npm ERR! 时即使退出码 0 也应判失败");

        var npmErrorLower = new CommandResult(0, Array.Empty<string>(), new[] { "npm error code E403" });
        Assert(!npmErrorLower.IsSuccess, "stderr 出现 npm error 行时应判失败");

        var pnpmError = new CommandResult(0, Array.Empty<string>(), new[] { "pnpm error unexpected store location" });
        Assert(!pnpmError.IsSuccess, "stderr 出现 pnpm error 行时应判失败");

        var commandFailed = new CommandResult(0, Array.Empty<string>(), new[] { "Command failed with exit code 1" });
        Assert(!commandFailed.IsSuccess, "stderr 出现 Command failed with exit code 时应判失败");

        var timedOut = new CommandResult(-1, Array.Empty<string>(), new[] { "已终止整个进程树" }, TimedOut: true);
        Assert(!timedOut.IsSuccess, "超时结果必须判失败");
        Assert(timedOut.DescribeOutcome().Contains("超时", StringComparison.Ordinal), "超时文案应包含「超时」");
        Assert(timedOut.DescribeOutcome().Contains("-1", StringComparison.Ordinal), "超时文案应包含退出码 -1");

        return Task.CompletedTask;
    }

    private static async Task TestProcessRunnerNormalAsync()
    {
        var ok = await ProcessRunner.RunAsync("cmd.exe", "/d /c echo hi");
        AssertEqual(0, ok.ExitCode, "echo hi 应成功退出");
        Assert(!ok.TimedOut, "echo hi 不应超时");
        Assert(ok.IsSuccess, "echo hi 应被判定为成功");
        Assert(ok.StandardOutput.Any(line => line.Trim() == "hi"), "stdout 应包含 hi");

        // L-01：命令退出码 0，但 stdout 出现 npm ERR! 行时不得判成功。
        var npmError = await ProcessRunner.RunCommandAsync("echo npm ERR! boom & exit 0");
        AssertEqual(0, npmError.ExitCode, "组合命令应把退出码传给调用方");
        Assert(
            npmError.StandardOutput.Any(line => line.Contains("npm ERR!", StringComparison.Ordinal)),
            "stdout 应包含 npm ERR! 行");
        Assert(!npmError.IsSuccess, "stdout 出现 npm ERR! 行时 IsSuccess 必须为 false（L-01）");

        var stderrError = await ProcessRunner.RunAsync("cmd.exe", "/d /c echo Command failed with exit code 1 1>&2");
        AssertEqual(0, stderrError.ExitCode, "输出错误行的命令退出码仍为 0");
        Assert(stderrError.StandardError.Any(line => line.Contains("Command failed", StringComparison.Ordinal)), "stderr 应收到错误行");
        Assert(!stderrError.IsSuccess, "stderr 出现明确错误行时应判失败");
    }

    private static async Task TestProcessRunnerTimeoutAsync()
    {
        var before = PingProcessIds();
        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(
            "ping.exe",
            "-n 10 127.0.0.1",
            timeout: TimeSpan.FromSeconds(1));
        stopwatch.Stop();

        Assert(result.TimedOut, "超时用例应标记 TimedOut");
        AssertEqual(-1, result.ExitCode, "超时结果退出码应为 -1");
        Assert(
            result.StandardError.Any(line => line.Contains("已终止整个进程树", StringComparison.Ordinal)),
            "超时结果 stderr 应包含「已终止整个进程树」");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"超时用例应快速返回，实际 {stopwatch.Elapsed.TotalSeconds:F1} 秒");

        await WaitUntilAsync(
            () => PingProcessIds().All(before.Contains),
            TimeSpan.FromSeconds(10),
            "超时后仍残留 ping 进程（进程树未终止）");
    }

    private static async Task TestProcessRunnerCancelAsync()
    {
        var before = PingProcessIds();
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();
        var canceled = false;
        try
        {
            await ProcessRunner.RunAsync(
                "ping.exe",
                "-n 10 127.0.0.1",
                cancellationToken: source.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        stopwatch.Stop();
        Assert(canceled, "取消时应抛 OperationCanceledException");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"取消用例应快速返回，实际 {stopwatch.Elapsed.TotalSeconds:F1} 秒");

        await WaitUntilAsync(
            () => PingProcessIds().All(before.Contains),
            TimeSpan.FromSeconds(10),
            "取消后仍残留 ping 进程（进程树未终止）");
    }

    /// <summary>
    /// PID + 启动时间二次绑定：时间不一致必须拒绝结束，一致才允许结束。
    /// 用 ping 现造一个长命进程，不依赖 node / npm / dsh。
    /// </summary>
    private static Task TestKillProcessBindingAsync()
    {
        using var target = Process.Start(new ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 30 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("无法启动 ping.exe 作为被结束进程");

        var pid = target.Id;
        try
        {
            var mismatch = ServerService.KillProcessTree(pid, DateTime.UtcNow.AddMinutes(-10));
            Assert(!mismatch.Success, "启动时间不一致时必须拒绝结束进程（PID 复用保护）");
            Assert(mismatch.Error is { Length: > 0 }, "拒绝结束时应给出原因");
            Assert(ProcessExists(pid), "拒绝结束后目标进程必须仍存活");

            var startTimeUtc = PortProbe.TryGetStartTimeUtc(pid);
            Assert(startTimeUtc is not null, "应能读取被结束进程的启动时间");

            var kill = ServerService.KillProcessTree(pid, startTimeUtc);
            Assert(kill.Success, "启动时间一致时应能结束进程：" + (kill.Error ?? "无错误信息"));
            Assert(!ProcessExists(pid), "结束成功后进程应已退出");
        }
        finally
        {
            if (ProcessExists(pid))
            {
                KillProcessTree(pid);
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestProfileConfigRoundTripAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "dsh-web-monitor-tests", Guid.NewGuid().ToString("N"));
        var profileDirectory = Path.Combine(tempRoot, "profiles", "web");
        Directory.CreateDirectory(profileDirectory);
        var packageJsonPath = Path.Combine(profileDirectory, "package.json");

        var original = new JsonObject
        {
            ["name"] = "dsh-profile-web",
            ["private"] = true,
            ["dependencies"] = new JsonObject
            {
                ["@scope/plugin-a"] = "^1.0.0",
                ["@scope/plugin-b"] = "^2.0.0",
            },
            ["dsh"] = new JsonObject
            {
                ["profile"] = new JsonObject
                {
                    ["name"] = "web",
                    ["bundles"] = new JsonArray("@scope/plugin-a"),
                },
                ["other"] = "keep-me",
            },
        };
        File.WriteAllText(packageJsonPath, original.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        var previousDshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        Environment.SetEnvironmentVariable("DSH_HOME", tempRoot);
        try
        {
            var service = new ProfileConfigService("web");
            AssertEqual(packageJsonPath, service.PackageJsonPath, "package.json 路径应解析到临时 DSH_HOME");

            var initial = service.ReadBundles();
            Assert(initial.SequenceEqual(new[] { "@scope/plugin-a" }), "初始 bundles 应只包含 plugin-a");

            Assert(service.SetBundleEnabled("@scope/plugin-b", true), "启用 plugin-b 应产生变更");
            Assert(File.Exists(packageJsonPath + ".bak"), "原子写应保留 .bak 备份");
            Assert(!File.Exists(packageJsonPath + ".tmp"), "写入完成后不应残留 .tmp");

            var afterEnable = JsonNode.Parse(File.ReadAllText(packageJsonPath))!.AsObject();
            var bundlesAfterEnable = ReadStringArray(afterEnable["dsh"]?["profile"]?["bundles"]);
            Assert(bundlesAfterEnable.Contains("@scope/plugin-a") && bundlesAfterEnable.Contains("@scope/plugin-b"), "bundles 应包含两个插件");

            AssertEqual("dsh-profile-web", afterEnable["name"]?.GetValue<string>() ?? string.Empty, "其它顶层字段应保留");
            AssertEqual(true, afterEnable["private"]?.GetValue<bool>() ?? false, "布尔字段应保留");
            AssertEqual("keep-me", afterEnable["dsh"]?["other"]?.GetValue<string>() ?? string.Empty, "dsh.other 字段应保留");
            AssertEqual("web", afterEnable["dsh"]?["profile"]?["name"]?.GetValue<string>() ?? string.Empty, "dsh.profile.name 应保留");
            AssertEqual("^1.0.0", afterEnable["dependencies"]?["@scope/plugin-a"]?.GetValue<string>() ?? string.Empty, "dependencies 应保留");

            var backup = JsonNode.Parse(File.ReadAllText(packageJsonPath + ".bak"))!.AsObject();
            var backupBundles = ReadStringArray(backup["dsh"]?["profile"]?["bundles"]);
            Assert(backupBundles.SequenceEqual(new[] { "@scope/plugin-a" }), ".bak 应为替换前的旧内容");

            Assert(!service.SetBundleEnabled("@scope/plugin-b", true), "重复启用不应产生变更");

            Assert(service.SetBundleEnabled("@scope/plugin-a", false), "停用 plugin-a 应产生变更");
            Assert(!File.Exists(packageJsonPath + ".tmp"), "停用后不应残留 .tmp");
            var finalBundles = service.ReadBundles();
            Assert(finalBundles.SequenceEqual(new[] { "@scope/plugin-b" }), "停用后 bundles 应只剩 plugin-b");

            var finalJson = JsonNode.Parse(File.ReadAllText(packageJsonPath))!.AsObject();
            AssertEqual("dsh-profile-web", finalJson["name"]?.GetValue<string>() ?? string.Empty, "多次写入后其它字段仍应保留");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_HOME", previousDshHome);
            TryDeleteDirectory(tempRoot);
        }

        return Task.CompletedTask;
    }

    private static Task TestProfileConfigCorruptAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "dsh-web-monitor-tests", Guid.NewGuid().ToString("N"));
        var profileDirectory = Path.Combine(tempRoot, "profiles", "web");
        Directory.CreateDirectory(profileDirectory);
        var packageJsonPath = Path.Combine(profileDirectory, "package.json");
        const string corruptContent = "{ this is not valid json ";
        File.WriteAllText(packageJsonPath, corruptContent);

        var previousDshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        Environment.SetEnvironmentVariable("DSH_HOME", tempRoot);
        try
        {
            var service = new ProfileConfigService("web");

            var readThrew = false;
            try
            {
                service.ReadBundles();
            }
            catch (Exception)
            {
                readThrew = true;
            }

            Assert(readThrew, "损坏的 package.json 在 ReadBundles 时必须抛异常，不能静默当成空配置");

            var writeThrew = false;
            try
            {
                service.SetBundleEnabled("@scope/plugin-a", true);
            }
            catch (Exception)
            {
                writeThrew = true;
            }

            Assert(writeThrew, "损坏的 package.json 在 SetBundleEnabled 时必须抛异常");
            AssertEqual(corruptContent, File.ReadAllText(packageJsonPath), "写入失败后原文件内容必须保持不变");
            Assert(!File.Exists(packageJsonPath + ".tmp"), "写入失败后不应残留 .tmp");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_HOME", previousDshHome);
            TryDeleteDirectory(tempRoot);
        }

        return Task.CompletedTask;
    }

    private static Task TestLogPanelSingleLineAsync()
    {
        RunOnSta(() =>
        {
            using var panel = new LogPanel();
            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < 5200; index++)
            {
                panel.Append(new LogEntry($"line {index}", LogLevel.Output));
            }

            panel.FlushPending();
            stopwatch.Stop();

            AssertEqual(AppConstants.MaxLogLines, panel.BufferedLineCount, "内存缓冲应裁剪到 2000 行");
            AssertEqual(AppConstants.MaxLogLines, panel.UiLineCount, "UiLineCount 应严格等于 2000");

            var lines = ContentLines(panel);
            AssertEqual(2000, lines.Length, "控件内容行数应为 2000");
            AssertEqual("line 3200", lines[0], "最早保留行应为 line 3200");
            AssertEqual("line 5199", lines[^1], "最后一行应为 line 5199");
            Assert(!panel.Text.Contains("line 3199", StringComparison.Ordinal), "不得残留已被淘汰的 line 3199");
            Assert(stopwatch.ElapsedMilliseconds < 500, $"5200 行追加 + 裁剪应小于 500ms，实际 {stopwatch.ElapsedMilliseconds}ms");
            Console.WriteLine($"      耗时 {stopwatch.ElapsedMilliseconds} ms，缓冲 {panel.BufferedLineCount} 行，UI {panel.UiLineCount} 行");
        });

        return Task.CompletedTask;
    }

    private static Task TestLogPanelMultiLineAsync()
    {
        RunOnSta(() =>
        {
            var panel = new LogPanel();
            try
            {
                for (var index = 0; index < 1000; index++)
                {
                    panel.Append(new LogEntry(
                        $"item {index} a{Environment.NewLine}item {index} b{Environment.NewLine}item {index} c",
                        LogLevel.Output));
                }

                panel.FlushPending();

                var lines = ContentLines(panel);
                AssertEqual(panel.UiLineCount, lines.Length, "控件真实行数必须与 UiLineCount 一致（M-16）");

                // 1000 条 × 3 行 = 3000 行，超出上限 1000 行 → 按整条淘汰 334 条，剩 666 条 / 1998 行。
                AssertEqual(666, panel.BufferedLineCount, "多行条目应按真实行数淘汰到上限以内");
                AssertEqual(1998, panel.UiLineCount, "剩余 666 个条目共 1998 行");
                Assert(panel.UiLineCount <= AppConstants.MaxLogLines, "UI 行数不得超过上限");
                AssertEqual(1998, lines.Length, "控件真实行数应为 1998");

                Assert(!panel.Text.Contains("item 333 a", StringComparison.Ordinal), "已被淘汰条目的残行必须从文本中消失");
                Assert(panel.Text.Contains("item 334 a", StringComparison.Ordinal), "最早保留条目应从文本开头开始");
                AssertEqual("item 334 a", lines[0], "控件首行应为最早保留条目的第一行");
                AssertEqual("item 999 c", lines[^1], "控件末行应为最后一条目的最后一行");

                panel.Clear();
                AssertEqual(0, panel.BufferedLineCount, "Clear 后缓冲应为 0");
                AssertEqual(0, panel.UiLineCount, "Clear 后 UiLineCount 应为 0");
                AssertEqual(0, ContentLines(panel).Length, "Clear 后控件文本应为空");
            }
            finally
            {
                panel.Dispose();
            }

            // 销毁后调用不得抛异常。
            panel.Append(new LogEntry("after dispose", LogLevel.Output));
            panel.FlushPending();
            panel.Clear();
        });

        return Task.CompletedTask;
    }

    private static Task TestLogPanelPendingCapAsync()
    {
        RunOnSta(() =>
        {
            using var panel = new LogPanel();

            // 界面被模态框卡住时日志仍会持续入队：待刷新队列必须有上限，不能无限增长。
            for (var index = 0; index < 9000; index++)
            {
                panel.Append(new LogEntry($"queued {index}", LogLevel.Output));
            }

            panel.FlushPending();

            AssertEqual(AppConstants.MaxLogLines, panel.BufferedLineCount, "队列超限刷新后应只保留最近 2000 行");
            var lines = ContentLines(panel);
            AssertEqual(2000, lines.Length, "刷新后控件应只有 2000 行");
            AssertEqual("queued 7000", lines[0], "应保留最后 2000 条（首行为 queued 7000）");
            AssertEqual("queued 8999", lines[^1], "末行应为 queued 8999");
        });

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ C. --env 集成用例

    private static async Task TestRealDshWebIdentityAsync()
    {
        var probe = await PortProbe.FindListeningPidsAsync(AppConstants.Port);
        Assert(probe.Succeeded, "netstat 应能正常执行：" + (probe.Error ?? "无错误信息"));
        Assert(
            probe.Pids.Count > 0,
            $"--env 模式下 127.0.0.1:{AppConstants.Port} 应有真实 dsh web 监听（M-21：不得把无监听当作通过）");

        ProcessIdentity? dshIdentity = null;
        foreach (var pid in probe.Pids)
        {
            var identity = await PortProbe.IdentifyAsync(pid);
            Assert(identity is not null, $"应能读取监听 PID {pid} 的身份");
            Console.WriteLine(
                $"      候选 PID={pid} 映像名={identity!.ProcessName} 是dsh={identity.IsDshWebNode} 命令行={identity.CommandLine}");
            if (identity.IsDshWebNode)
            {
                dshIdentity = identity;
                break;
            }
        }

        Assert(dshIdentity is not null, "真实监听 3080 的进程必须通过 dsh web 身份校验");
        AssertEqual("node.exe", dshIdentity!.ProcessName, "dsh web 监听进程应为 node.exe");
        Assert(dshIdentity!.StartTimeUtc is not null, "应能读取监听进程启动时间（Kill 二次绑定依赖）");
    }

    private static async Task TestNpmGlobalProbeAsync()
    {
        var npm = new NpmService();
        var result = await npm.ListGlobalAsync(null, null);
        Console.WriteLine($"      npm list 退出码={result.ExitCode}，输出行数={result.StandardOutput.Count}");

        Assert(result.IsSuccess, "npm list -g 应成功：" + result.DescribeOutcome());
        Assert(NpmService.TryParseInstallState(result.StandardOutput, out var installed, out var version), "应能解析 npm list --json 输出");
        Assert(installed, "--env 模式下应检测到全局包 " + AppConstants.NpmPackage);
        Assert(!string.IsNullOrWhiteSpace(version), "应解析出已安装版本");
        Console.WriteLine($"      已安装版本={version}");
    }

    private static async Task TestDshPluginListAsync()
    {
        // 直接对真实 profile 运行 dsh plugin list 会在 profile 目录写 package.json.lock，
        // 同时也会在本机沙箱/只读环境下失败。因此把真实 profile（含 node_modules）复制到
        // 临时 DSH_HOME 后再运行真实的 dsh CLI：既验证真实 CLI 输出解析，又不写用户 profile。
        var realDshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrWhiteSpace(realDshHome))
        {
            realDshHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh");
        }

        var realProfile = Path.Combine(realDshHome, "profiles", AppConstants.PluginProfile);
        Assert(Directory.Exists(realProfile), $"--env 需要真实的 dsh profile 目录：{realProfile}");
        Assert(File.Exists(Path.Combine(realProfile, "package.json")), $"profile 缺少 package.json：{realProfile}");

        var tempHome = Path.Combine(
            Path.GetTempPath(),
            "dsh-web-monitor-env-tests",
            Guid.NewGuid().ToString("N"));
        var tempProfile = Path.Combine(tempHome, "profiles", AppConstants.PluginProfile);
        Directory.CreateDirectory(tempProfile);

        var previousDshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        try
        {
            var copy = await ProcessRunner.RunAsync(
                "robocopy.exe",
                $"\"{realProfile}\" \"{tempProfile}\" /E /XD .plugin-manager /NFL /NDL /NJH /NJS /NP",
                timeout: TimeSpan.FromSeconds(90));
            Assert(copy.ExitCode is >= 0 and < 8, $"复制 profile 失败：robocopy 退出码 {copy.ExitCode}");

            Environment.SetEnvironmentVariable("DSH_HOME", tempHome);
            var plugins = new PluginService();
            var result = await plugins.ListJsonAsync(null, null);
            Console.WriteLine($"      dsh plugin list 退出码={result.ExitCode}，输出行数={result.StandardOutput.Count}");

            Assert(
                result.IsSuccess,
                "dsh plugin --profile web list --json 应成功：" + result.DescribeOutcome() + FormatStderrTail(result));
            var installed = default(IReadOnlyList<(string Name, string Version)>);
            Assert(
                PluginService.TryParseInstalledPlugins(result.StandardOutput, out installed),
                "dsh plugin list --json 的输出应能解析" + FormatStderrTail(result));
            Assert(installed.Count > 0, "应能从 list --json 解析出至少一个插件" + FormatStderrTail(result));
            Console.WriteLine($"      解析出 {installed.Count} 个插件，首个：{installed[0].Name}@{installed[0].Version}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_HOME", previousDshHome);
            TryDeleteDirectory(tempHome);
        }
    }

    private static async Task TestControllerLifecycleAsync()
    {
        var port = await FindFreePortAsync(31810, 31830);
        var command = BuildFixtureCommand("web", port);
        var logs = new List<string>();
        MonitorController? controller = null;
        try
        {
            controller = new MonitorController(port, TimeSpan.FromSeconds(20), command);
            controller.ServerLog += entry => CaptureLog(logs, "server", entry);

            await controller.RefreshInstallStateAsync();
            await controller.RefreshServerStateAsync();
            AssertEqual(InstallStatus.Installed, controller.State.Install, "启动前应检测到 dsh 全局包");
            AssertEqual(ServerStatus.Stopped, controller.State.Server, $"端口 {port} 应空闲");

            await controller.StartServerAsync();
            AssertEqual(ServerStatus.Running, controller.State.Server, "启动后应进入运行中：" + LastLogs(logs));
            Assert(controller.State.ListenerPid is > 0, "运行态应记录监听 PID");
            Assert(controller.IsServerOwnedByApp, "由本程序启动的 dsh web 应标记为自有进程");

            await controller.StopServerAsync();
            AssertEqual(ServerStatus.Stopped, controller.State.Server, "停止后应回到未运行：" + LastLogs(logs));
            var released = await PortProbe.FindListeningPidsAsync(port);
            Assert(released.Succeeded, "停止后端口探测应成功：" + (released.Error ?? "无错误信息"));
            AssertEqual(0, released.Pids.Count, "停止后端口应释放");

            await controller.StartServerAsync();
            AssertEqual(ServerStatus.Running, controller.State.Server, "再次启动应成功");

            await controller.RestartServerAsync();
            AssertEqual(ServerStatus.Running, controller.State.Server, "重启后应处于运行中");
            Assert(controller.State.ListenerPid is > 0, "重启后应记录监听 PID");

            await controller.StopServerAsync();
            AssertEqual(ServerStatus.Stopped, controller.State.Server, "最终停止应成功");
        }
        finally
        {
            controller?.Dispose();
            await EnsurePortFreeAsync(port, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// R-04 自动化证据：退出监控器（Dispose）时必须结束由本程序启动的 dsh web，
    /// 不依赖用户先点「停止」。Dispose 是同步的（内部 KillProcessTree + ServerService.Dispose），
    /// 这里给端口释放留 10 秒宽限。
    /// </summary>
    private static async Task TestDisposeStopsOwnedServerAsync()
    {
        var port = await FindFreePortAsync(31871, 31900);
        var command = BuildFixtureCommand("web", port);
        var logs = new List<string>();
        MonitorController? controller = null;
        try
        {
            controller = new MonitorController(port, TimeSpan.FromSeconds(20), command);
            controller.ServerLog += entry => CaptureLog(logs, "server", entry);

            await controller.RefreshInstallStateAsync();
            await controller.RefreshServerStateAsync();
            AssertEqual(InstallStatus.Installed, controller.State.Install, "启动前应检测到 dsh 全局包");
            AssertEqual(ServerStatus.Stopped, controller.State.Server, $"端口 {port} 应空闲");

            await controller.StartServerAsync();
            AssertEqual(ServerStatus.Running, controller.State.Server, "启动后应进入运行中：" + LastLogs(logs));
            var listenerPid = controller.State.ListenerPid;
            Assert(listenerPid is > 0, "运行态应记录监听 PID");
            Assert(controller.IsServerOwnedByApp, "该 dsh web 应标记为由本程序启动");

            // 关键：不调用 StopServerAsync，直接 Dispose。
            controller.Dispose();
            controller = null;

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                var probe = await PortProbe.FindListeningPidsAsync(port);
                if (probe.Succeeded && probe.Pids.Count == 0)
                {
                    break;
                }

                await Task.Delay(200);
            }

            var finalProbe = await PortProbe.FindListeningPidsAsync(port);
            Assert(finalProbe.Succeeded, "Dispose 后端口探测应成功");
            AssertEqual(
                0,
                finalProbe.Pids.Count,
                $"Dispose 后端口 {port} 不应仍被监听（等待 10 秒）：" + LastLogs(logs));
            Assert(
                !ProcessExists(listenerPid!.Value),
                $"Dispose 后监听进程 PID {listenerPid} 仍存在，应由退出路径结束");
            Console.WriteLine($"      Dispose 后 {stopwatch.ElapsedMilliseconds} ms 内端口 {port} 已释放，PID {listenerPid} 已退出");
        }
        finally
        {
            controller?.Dispose();
            await EnsurePortFreeAsync(port, TimeSpan.FromSeconds(10));
        }
    }

    private static async Task TestStartTimeoutConvergenceAsync()
    {
        var port = await FindFreePortAsync(31831, 31850);
        var logs = new List<string>();
        MonitorController? controller = null;
        try
        {
            controller = new MonitorController(
                port,
                TimeSpan.FromSeconds(3),
                $"node \"{Path.Combine(Fixtures, "slow-dsh.js")}\"");
            controller.ServerLog += entry => CaptureLog(logs, "server", entry);

            await controller.RefreshInstallStateAsync();
            await controller.RefreshServerStateAsync();
            AssertEqual(InstallStatus.Installed, controller.State.Install, "fixture 用例需要 dsh 全局包已安装，否则启动会被直接拒绝");
            AssertEqual(ServerStatus.Stopped, controller.State.Server, $"端口 {port} 应空闲");

            await controller.StartServerAsync();

            AssertEqual(ServerStatus.StartFailed, controller.State.Server, "启动超时应收敛到启动失败，不能停留在正在启动：" + LastLogs(logs));
            Assert(controller.State.Server is not (ServerStatus.Starting or ServerStatus.Stopping), "失败分支不得让状态停留在过渡态");
            Assert(logs.Any(line => line.Contains("启动失败", StringComparison.Ordinal)), "日志应包含启动失败提示");
            Assert(logs.Any(line => line.Contains("simulated startup error line 1", StringComparison.Ordinal)), "应回放启动失败的 stderr 尾部");
            var residue = await PortProbe.FindListeningPidsAsync(port);
            Assert(residue.Succeeded, "启动失败后端口探测应成功：" + (residue.Error ?? "无错误信息"));
            AssertEqual(0, residue.Pids.Count, "启动失败后不应残留监听进程");
        }
        finally
        {
            controller?.Dispose();
            await EnsurePortFreeAsync(port, TimeSpan.FromSeconds(10));
        }
    }

    private static async Task TestEarlyExitAsync()
    {
        var port = await FindFreePortAsync(31851, 31870);
        var logs = new List<string>();
        MonitorController? controller = null;
        try
        {
            controller = new MonitorController(
                port,
                TimeSpan.FromSeconds(20),
                $"node \"{Path.Combine(Fixtures, "fast-exit-dsh.js")}\"");
            controller.ServerLog += entry => CaptureLog(logs, "server", entry);

            await controller.RefreshInstallStateAsync();
            await controller.RefreshServerStateAsync();
            AssertEqual(InstallStatus.Installed, controller.State.Install, "fixture 用例需要 dsh 全局包已安装，否则启动会被直接拒绝");
            await controller.StartServerAsync();

            AssertEqual(ServerStatus.StartFailed, controller.State.Server, "进程提前退出应收敛到启动失败：" + LastLogs(logs));
            Assert(
                logs.Any(line => line.Contains("退出码 3", StringComparison.Ordinal)),
                "日志应包含退出码 3：" + LastLogs(logs));
        }
        finally
        {
            controller?.Dispose();
            await EnsurePortFreeAsync(port, TimeSpan.FromSeconds(10));
        }
    }

    // ------------------------------------------------------------------ 运行基础设施

    private static async Task RunAsync(TestCase test)
    {
        var stopwatch = Stopwatch.StartNew();
        Console.Write($"RUN   {test.Name} ... ");
        try
        {
            var timeout = Remaining < SingleCaseTimeout ? Remaining : SingleCaseTimeout;
            if (timeout <= TimeSpan.Zero)
            {
                throw new TimeoutException("整轮测试预算已耗尽");
            }

            var task = test.Run();
            // 任务先完成时取消定时器，避免每个用例都留下一个跑满超时的 Task.Delay。
            using var delayCts = new CancellationTokenSource();
            var delay = Task.Delay(timeout, delayCts.Token);
            var completed = await Task.WhenAny(task, delay);
            if (!ReferenceEquals(completed, task))
            {
                // 超时任务仍在后台运行：观察其异常，避免未观察异常；其 finally 会在自身
                // ProcessRunner 超时后执行清理（用例内部命令都有独立超时）。
                _ = task.ContinueWith(
                    t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                throw new TimeoutException($"用例超时（{timeout.TotalSeconds:F0} 秒）");
            }

            delayCts.Cancel();
            await task;
            _passed++;
            Console.WriteLine($"PASS  ({stopwatch.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            FailedCases.Add(test.Name);
            Console.WriteLine($"FAIL  ({stopwatch.ElapsedMilliseconds} ms)");
            PrintException(ex);
        }
    }

    private static void PrintException(Exception exception)
    {
        var prefix = "      ";
        if (exception is TargetInvocationException { InnerException: { } inner })
        {
            exception = inner;
        }

        Console.WriteLine(prefix + exception.GetType().Name + "：" + exception.Message.Replace("\n", "\n" + prefix, StringComparison.Ordinal));
        if (exception.StackTrace is { Length: > 0 } stack)
        {
            foreach (var line in stack.Split('\n').Take(6))
            {
                Console.WriteLine(prefix + "  " + line.Trim());
            }
        }
    }

    internal static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
            Name = "SmokeTests-STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(120)))
        {
            throw new TimeoutException("STA 用例超时（120 秒）");
        }

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    private static string[] ContentLines(LogPanel panel)
    {
        var lines = panel.Lines;
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        return lines;
    }

    private static string[] ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        return array
            .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null)
            .Where(text => text is not null)
            .Select(text => text!)
            .ToArray();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CaptureLog(List<string> logs, string source, LogEntry entry)
    {
        lock (logs)
        {
            logs.Add($"[{source}] {entry.Text}");
            if (logs.Count > 200)
            {
                logs.RemoveAt(0);
            }
        }
    }

    private static string LastLogs(List<string> logs)
    {
        lock (logs)
        {
            return Environment.NewLine + "      " + string.Join(
                Environment.NewLine + "      ",
                logs.TakeLast(12));
        }
    }

    private static string FormatStderrTail(CommandResult result) =>
        result.StandardError.Count == 0
            ? string.Empty
            : Environment.NewLine + "      " + string.Join(
                Environment.NewLine + "      ",
                result.StandardError.TakeLast(10));

    // ------------------------------------------------------------------ 进程 / 端口工具

    private static string BuildFixtureCommand(string subCommand, int port)
    {
        var binJs = Path.Combine(Fixtures, "dsh", "lib", "bin.js");
        return $"node \"{binJs}\" {subCommand} --port {port}";
    }

    private static async Task<int> FindFreePortAsync(int start, int end)
    {
        for (var port = start; port <= end; port++)
        {
            var probe = await PortProbe.FindListeningPidsAsync(port);
            if (probe.Succeeded && probe.Pids.Count == 0)
            {
                return port;
            }
        }

        throw new InvalidOperationException($"在 {start}-{end} 范围内找不到空闲端口");
    }

    private static async Task EnsurePortFreeAsync(int port, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var probe = await PortProbe.FindListeningPidsAsync(port);
            if (probe.Succeeded && probe.Pids.Count == 0)
            {
                return;
            }

            foreach (var pid in probe.Pids)
            {
                KillProcessTree(pid);
            }

            await Task.Delay(200);
        }
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static HashSet<int> PingProcessIds()
    {
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("ping"))
        {
            try
            {
                result.Add(process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        if (!condition())
        {
            throw new InvalidOperationException(failureMessage);
        }
    }

    private static void KillProcessTree(int pid)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
        }
        catch (Exception)
        {
            // 清理尽力而为，不影响断言。
        }
    }

    // ------------------------------------------------------------------ 参数 / 帮助

    private static void PrintHelp()
    {
        Console.WriteLine("dsh Web Monitor 冒烟测试");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  dotnet run --project tests\\DshWebMonitor.SmokeTests              # 默认离线：纯逻辑 + UI 行为，不触碰真实 3080");
        Console.WriteLine("  dotnet run --project tests\\DshWebMonitor.SmokeTests -- --env     # 追加集成用例（真实 3080 / npm / dsh / fixture 生命周期）");
        Console.WriteLine("  dotnet run --project tests\\DshWebMonitor.SmokeTests -- --list    # 列出全部用例及模式");
        Console.WriteLine("  dotnet run --project tests\\DshWebMonitor.SmokeTests -- --help    # 显示本帮助");
        Console.WriteLine();
        Console.WriteLine("退出码：0 全部通过；1 存在失败或整轮超时；2 参数错误。");
    }

    private static void PrintList()
    {
        Console.WriteLine("用例列表：");
        foreach (var test in BuildCases())
        {
            Console.WriteLine($"  {(test.EnvOnly ? "[env]" : "[offline]")} {test.Name}");
        }
    }

    // ------------------------------------------------------------------ 断言

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
        }
    }

    private sealed record TestCase(string Name, bool EnvOnly, Func<Task> Run);
}
