using System.Reflection;
using System.Runtime.ExceptionServices;
using DshWebMonitor;

namespace DshWebMonitor.SmokeTests;

/// <summary>
/// 界面层行为测试（R-13）：只驱动 <see cref="MainForm"/> 的纯逻辑入口——
/// 反射调用 ApplyState 断言按钮 / 托盘菜单与同一份 AppState 快照一致，
/// 反射调用 OnFormClosing 断言关闭语义；不 Show 窗口、不触发真实 npm / dsh / 网络、
/// 不弹模态框（MainForm 测试模式 autoStart:false）。
/// 全部用例在独立 STA 线程执行，finally 中解绑并 Dispose 窗体与 controller。
/// </summary>
internal static class UiBehaviorTests
{
    private const int TestPort = 31080;
    private static readonly TimeSpan TestStartTimeout = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------------ 用例

    public static Task TestStateDrivenControlsAsync()
    {
        Program.RunOnSta(() =>
        {
            var controller = NewController();
            MainForm? form = null;
            try
            {
                form = new MainForm(controller, autoStart: false, trayAvailableOverride: true);
                var applyState = GetMethod(form, "ApplyState");

                var btnStart = GetField<AntdUI.Button>(form, "_btnStart");
                var btnStop = GetField<AntdUI.Button>(form, "_btnStop");
                var btnRestart = GetField<AntdUI.Button>(form, "_btnRestart");
                var btnOpenWebUi = GetField<AntdUI.Button>(form, "_btnOpenWebUi");
                var trayStart = GetField<ToolStripMenuItem>(form, "_trayStart");
                var trayStop = GetField<ToolStripMenuItem>(form, "_trayStop");
                var trayRestart = GetField<ToolStripMenuItem>(form, "_trayRestart");
                var trayOpenWebUi = GetField<ToolStripMenuItem>(form, "_trayOpenWebUi");
                var trayExit = GetField<ToolStripMenuItem>(form, "_trayExit");
                var trayMenu = GetField<ContextMenuStrip>(form, "_trayMenu");

                AssertEqual(5, trayMenu.Items.OfType<ToolStripMenuItem>().Count(), "托盘菜单应包含 启动/停止/重新启动/打开 Web UI/退出 五项");
                AssertEqual(1, trayMenu.Items.OfType<ToolStripSeparator>().Count(), "托盘菜单应只有一个分隔条");
                AssertEqual("退出", trayExit.Text, "托盘菜单应包含退出项");
                Assert(trayExit.Enabled, "退出项始终可用");

                var running = new AppState
                {
                    Server = ServerStatus.Running,
                    ListenerPid = 1234,
                    Install = InstallStatus.Installed,
                    InstalledVersion = "1.0.0",
                    LatestVersion = "1.0.0",
                };

                ApplyState(form, applyState, running);
                Assert(!btnStart.Enabled, "运行中主窗体应禁用启动");
                Assert(btnStop.Enabled && btnRestart.Enabled && btnOpenWebUi.Enabled, "运行中主窗体应启用停止 / 重启 / 打开 Web UI");
                Assert(!trayStart.Enabled, "运行中托盘应禁用启动");
                Assert(trayStop.Enabled && trayRestart.Enabled && trayOpenWebUi.Enabled, "运行中托盘应启用停止 / 重启 / 打开 Web UI");

                var conflict = running with { Server = ServerStatus.PortConflict, ListenerPid = 4321 };
                ApplyState(form, applyState, conflict);
                Assert(!btnStart.Enabled && !btnStop.Enabled && !btnRestart.Enabled && !btnOpenWebUi.Enabled, "端口被占用时主窗体应禁用全部服务操作");
                Assert(!trayStart.Enabled && !trayStop.Enabled && !trayRestart.Enabled && !trayOpenWebUi.Enabled, "端口被占用时托盘菜单应同步禁用");

                var stopped = running with { Server = ServerStatus.Stopped, ListenerPid = null };
                ApplyState(form, applyState, stopped);
                Assert(btnStart.Enabled, "未运行时允许启动");
                Assert(!btnStop.Enabled && !btnRestart.Enabled && !btnOpenWebUi.Enabled, "未运行时禁用停止 / 重启 / 打开");
                Assert(trayStart.Enabled && !trayStop.Enabled && !trayRestart.Enabled && !trayOpenWebUi.Enabled, "未运行时托盘菜单同步");

                var busy = stopped with { BusyOperation = "正在安装插件…", Busy = BusyKind.PluginCommand };
                ApplyState(form, applyState, busy);
                Assert(!btnStart.Enabled, "忙碌时禁用启动");
                Assert(!trayStart.Enabled, "忙碌时托盘禁用启动");

                var starting = stopped with { Server = ServerStatus.Starting };
                ApplyState(form, applyState, starting);
                Assert(!btnStart.Enabled && !btnStop.Enabled, "启动过程中禁用启动 / 停止");
                Assert(!trayStart.Enabled && !trayStop.Enabled, "启动过程中托盘同步禁用");

                // 结构回归：三页 Tab、更新通道、单一日志面板、窗口基线。
                var tabs = GetField<AntdUI.Tabs>(form, "_tabs");
                AssertEqual(3, tabs.Pages.Count, "应为主页 / 插件管理 / 日志三个 Tab");
                AssertEqual("主页", tabs.Pages[0].Text, "第一个 Tab 应为主页");
                AssertEqual("插件管理", tabs.Pages[1].Text, "第二个 Tab 应为插件管理");
                AssertEqual("日志", tabs.Pages[2].Text, "第三个 Tab 应为日志");

                var channel = GetField<AntdUI.Select>(form, "_cmbNpmChannel");
                var expectedChannels = new[] { "stable", "rc", "beta", "alpha", "next", "all" };
                AssertEqual(expectedChannels.Length, channel.Items.Count, "主页更新通道应为 6 个");
                for (var index = 0; index < expectedChannels.Length; index++)
                {
                    AssertEqual(expectedChannels[index], channel.Items[index] as string, $"第 {index + 1} 个通道文案应为 {expectedChannels[index]}");
                }

                AssertEqual(0, channel.SelectedIndex, "默认更新通道应为 stable");

                var logView = GetField<LogView>(form, "_logView");
                AssertEqual(1, CountControls<DshWebMonitor.Ui.LogPanel>(logView), "日志页应只有一个混合输出面板");
                Assert(GetField<object>(form, "_pluginView") is not null, "插件管理页应存在");

                AssertEqual(AutoScaleMode.Dpi, form.AutoScaleMode, "主窗口应使用 DPI 自动缩放");
                AssertEqual(new SizeF(96F, 96F), form.AutoScaleDimensions, "应显式声明 96 DPI 设计基线（R-12）");
                AssertEqual(FormBorderStyle.FixedSingle, form.FormBorderStyle, "主窗口应为固定边框");
                Assert(!form.MaximizeBox, "主窗口不应允许最大化");
                var scale = form.DeviceDpi / 96f;
                AssertEqual((int)Math.Round(820 * scale), form.ClientSize.Width, "客户区宽度应与 820x540 基线按 DPI 成比例");
                AssertEqual((int)Math.Round(540 * scale), form.ClientSize.Height, "客户区高度应与 820x540 基线按 DPI 成比例");
            }
            finally
            {
                DisposeForm(form);
                controller.Dispose();
            }
        });

        return Task.CompletedTask;
    }

    public static Task TestCloseSemanticsAsync()
    {
        Program.RunOnSta(() =>
        {
            // 1) 托盘明确不可用 + 服务未运行：用户关闭应放行（关闭即退出），且不弹模态框。
            var controller = NewController();
            MainForm? form = null;
            try
            {
                form = new MainForm(controller, autoStart: false, trayAvailableOverride: false);
                Assert(!form.TrayAvailable, "trayAvailableOverride:false 时 TrayAvailable 应为 false");
                Assert(!form.TrayRegistered, "trayAvailableOverride:false 时不应注册托盘图标");

                var args = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);
                InvokeClose(form, args);
                Assert(!args.Cancel, "托盘不可用且服务未运行时应放行关闭");
                Assert(!form.IsDisposed, "OnFormClosing 本身不应销毁窗体");
            }
            finally
            {
                DisposeForm(form);
                controller.Dispose();
            }

            // 2) WindowsShutDown / TaskManagerClosing：任何托盘状态下都必须放行。
            foreach (var trayOverride in new bool?[] { true, false })
            {
                controller = NewController();
                form = null;
                try
                {
                    form = new MainForm(controller, autoStart: false, trayAvailableOverride: trayOverride);
                    AssertClosingAllowed(form, CloseReason.WindowsShutDown);
                    AssertClosingAllowed(form, CloseReason.TaskManagerClosing);
                }
                finally
                {
                    DisposeForm(form);
                    controller.Dispose();
                }
            }

            // 3) 托盘预判可用：以真实注册结果为准断言一致性，避免环境相关假失败。
            controller = NewController();
            form = null;
            try
            {
                form = new MainForm(controller, autoStart: false, trayAvailableOverride: true);
                var reason = $"TrayAvailable={form.TrayAvailable}，TrayRegistered={form.TrayRegistered}";
                var args = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);

                if (form.TrayRegistered)
                {
                    InvokeClose(form, args);
                    Assert(args.Cancel, "托盘图标真实存在时，用户关闭应取消关闭并收起到托盘（" + reason + "）");
                    Assert(!form.IsDisposed, "收起后窗体不得销毁（" + reason + "）");
                }
                else
                {
                    // 环境不允许注册托盘（无通知区域 / 受限会话）：降级为「关闭即退出」，
                    // 测试模式 autoStart:false 不弹确认框，因此关闭必须放行。
                    Assert(!form.TrayAvailable, "托盘注册失败时 TrayAvailable 必须降级为 false（" + reason + "）");
                    InvokeClose(form, args);
                    Assert(!args.Cancel, "托盘不可用时用户关闭应放行（关闭即退出），不得隐藏到不可见状态（" + reason + "）");
                }
            }
            finally
            {
                DisposeForm(form);
                controller.Dispose();
            }
        });

        return Task.CompletedTask;
    }

    public static Task TestNoStaticTrayOverrideAsync()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance
                                   | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        Assert(
            typeof(MainForm).GetProperty("TrayAvailabilityOverride", flags) is null,
            "MainForm 不得再暴露 TrayAvailabilityOverride 静态开关（R-13）");
        Assert(
            typeof(MainForm).GetField("TrayAvailabilityOverride", flags) is null,
            "MainForm 不得再暴露 TrayAvailabilityOverride 静态字段（R-13）");

        var injection = typeof(MainForm).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(MonitorController), typeof(bool), typeof(bool?) },
            modifiers: null);
        Assert(injection is not null, "MainForm 应保留可注入 controller / autoStart / trayAvailableOverride 的 internal 构造函数");

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ 工具

    private static MonitorController NewController() =>
        new(TestPort, TestStartTimeout, "node -v");

    private static void ApplyState(MainForm form, MethodInfo applyState, AppState state)
    {
        try
        {
            applyState.Invoke(form, new object[] { state });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static void AssertClosingAllowed(MainForm form, CloseReason reason)
    {
        var args = new FormClosingEventArgs(reason, cancel: false);
        InvokeClose(form, args);
        if (args.Cancel)
        {
            throw new InvalidOperationException($"{reason} 时必须放行关闭");
        }
    }

    private static void InvokeClose(MainForm form, FormClosingEventArgs args)
    {
        var method = GetMethod(form, "OnFormClosing");
        try
        {
            method.Invoke(form, new object[] { args });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static void DisposeForm(MainForm? form)
    {
        if (form is null)
        {
            return;
        }

        try
        {
            form.Dispose();
        }
        catch (Exception)
        {
            // 已销毁 / 资源已释放：不影响断言。
        }
    }

    private static T GetField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("找不到字段：" + name);
        var value = field.GetValue(instance)
                    ?? throw new InvalidOperationException("字段为 null：" + name);
        return (T)value;
    }

    private static MethodInfo GetMethod(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到方法：" + name);

    private static int CountControls<T>(Control root)
        where T : Control
    {
        var count = root.Controls.OfType<T>().Count();
        foreach (Control child in root.Controls)
        {
            count += CountControls<T>(child);
        }

        return count;
    }

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
}
