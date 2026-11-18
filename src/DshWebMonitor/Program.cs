using System.Runtime.InteropServices;

namespace DshWebMonitor;

internal static class Program
{
    private const string GlobalMutexName = @"Global\DshWebMonitor.SingleInstance";
    private const string LocalMutexName = "DshWebMonitor.SingleInstance";
    private const string WindowTitle = "dsh Web Monitor";
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const int MaxStackChars = 2000;

    /// <summary>异常弹窗节流窗口：窗口内的重复异常只弹一次，但每次都记录到崩溃日志。</summary>
    private static readonly TimeSpan ExceptionDialogThrottle = TimeSpan.FromSeconds(10);
    private static long _lastExceptionDialogTicks;

    [STAThread]
    private static void Main()
    {
        using var mutex = CreateSingleInstanceMutex(out var createdNew);
        if (!createdNew)
        {
            ActivateExistingWindow();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // 主窗体构造或消息循环本身抛出异常时，ThreadException 兜不住，这里给出最后一道现场记录。
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            RecordUnhandledException(ex);
            MessageBox.Show(Describe(ex), "程序异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        GC.KeepAlive(mutex);
    }

    /// <summary>
    /// 优先使用跨会话的 Global 命名互斥体；受限会话没有 SeCreateGlobalPrivilege 时
    /// 创建可能抛 <see cref="UnauthorizedAccessException"/>，此时退回会话内名称，行为一致。
    /// </summary>
    private static Mutex CreateSingleInstanceMutex(out bool createdNew)
    {
        try
        {
            return new Mutex(true, GlobalMutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // 无法创建 Global 对象时先确认它是否已存在：存在说明已有实例持有，应按「非新实例」
            // 处理并激活已有窗口；只有确实不存在才退回会话内名称，避免两个名称互不排斥。
            try
            {
                var existing = Mutex.OpenExisting(GlobalMutexName);
                createdNew = false;
                return existing;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new Mutex(true, LocalMutexName, out createdNew);
        }
    }

    /// <summary>UI 线程未处理异常：记录现场并弹窗，随后继续运行（运行时按 CatchException 模式接管）。</summary>
    private static void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        // 每次异常都落盘，避免带病运行时诊断现场永久缺失。
        RecordUnhandledException(e.Exception);

        // 仅在时间窗口内节流弹窗，避免对话框叠成死循环；窗口过后仍会提示，不会永久静默。
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastExceptionDialogTicks);
        if (last != 0 && now - last < ExceptionDialogThrottle.TotalMilliseconds)
        {
            return;
        }

        Interlocked.Exchange(ref _lastExceptionDialogTicks, now);
        MessageBox.Show(
            Describe(e.Exception),
            "程序异常",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    /// <summary>其它线程未处理异常：记录现场，处理函数返回后由运行时终止进程（不吞异常）。</summary>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        RecordUnhandledException(e.ExceptionObject as Exception);
    }

    private static string Describe(Exception exception)
    {
        var stack = exception.StackTrace ?? "（无堆栈信息）";
        if (stack.Length > MaxStackChars)
        {
            stack = string.Concat(stack.AsSpan(0, MaxStackChars), "…（已截断）");
        }

        return $"异常类型：{exception.GetType().FullName}{Environment.NewLine}" +
               $"异常消息：{exception.Message}{Environment.NewLine}{Environment.NewLine}" +
               $"堆栈：{Environment.NewLine}{stack}";
    }

    private static void RecordUnhandledException(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshWebMonitor");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}]{Environment.NewLine}" +
                $"{Describe(exception)}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 记录失败不影响运行时继续终止进程。
        }
    }

    /// <summary>已有实例时尽量把它的窗口带到前台；找不到窗口则提示。</summary>
    private static void ActivateExistingWindow()
    {
        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            var handle = FindWindow(null, WindowTitle);
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SwRestore);
                ShowWindow(handle, SwShow);
                SetForegroundWindow(handle);
                return;
            }

            Thread.Sleep(150);
        }

        MessageBox.Show(
            "dsh Web Monitor 已在运行。",
            WindowTitle,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
