using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DshWebMonitor.Services;

internal sealed record CommandResult(
    int ExitCode,
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError,
    bool TimedOut = false)
{
    /// <summary>
    /// 退出码为 0 时仍可能打印错误（npm / pnpm 有时把错误写到 stdout），
    /// 这类结果按失败处理。stdout 与 stderr 都要检查。
    /// </summary>
    public bool HasErrorOutput => HasErrorMarker(StandardOutput) || HasErrorMarker(StandardError);

    private static bool HasErrorMarker(IReadOnlyList<string> lines) =>
        lines.Any(line =>
            line.Contains("ERR_", StringComparison.OrdinalIgnoreCase)
            || line.Contains("npm ERR!", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("npm error", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("pnpm error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Command failed with exit code", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 成功语义的唯一来源：退出码为 0、未超时、且没有明确的错误输出。
    /// 所有调用点都必须用它判断成败，不得单独比较 ExitCode。
    /// </summary>
    public bool IsSuccess => ExitCode == 0 && !TimedOut && !HasErrorOutput;

    /// <summary>给日志用的简短结论，统一描述「退出码 + 是否超时」。</summary>
    public string DescribeOutcome() => TimedOut
        ? $"超时（退出码 {ExitCode}）"
        : $"退出码 {ExitCode}";
}

/// <summary>
/// 统一的外部命令执行器：不占用 UI 线程、隐藏控制台窗口、实时回调输出。
/// 每条命令都有超时；超时或取消时会结束整个进程树，不会留下孤儿进程，
/// 也不会因为孙进程继承了管道而永久挂起。
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// 系统工具（netstat / tasklist / wmic / powershell）使用控制台 OEM 代码页解码，
    /// 否则中文 Windows 下中文路径与进程名会变成乱码。
    /// </summary>
    public static Encoding SystemToolEncoding { get; } = CreateSystemToolEncoding();

    /// <summary>通过 cmd.exe 执行一条命令并等待结束（npm / dsh 均为 .cmd 形式的命令行工具）。</summary>
    public static Task<CommandResult> RunCommandAsync(
        string commandLine,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        // /d 关闭 AutoRun 命令，避免注册表里的 AutoRun 影响输出。
        return RunAsync(
            "cmd.exe",
            "/d /c " + commandLine,
            onStandardOutput,
            onStandardError,
            cancellationToken,
            timeout: timeout ?? AppConstants.CommandTimeout);
    }

    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        Encoding? outputEncoding = null)
    {
        var startInfo = BuildStartInfo(fileName, arguments, outputEncoding);
        var standardOutput = new List<string>();
        var standardError = new List<string>();
        var timedOut = false;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                outputClosed.TrySetResult();
                return;
            }

            lock (standardOutput)
            {
                standardOutput.Add(e.Data);
            }

            onStandardOutput?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errorClosed.TrySetResult();
                return;
            }

            lock (standardError)
            {
                standardError.Add(e.Data);
            }

            onStandardError?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var deadline = timeout ?? AppConstants.QueryTimeout;
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedSource.CancelAfter(deadline);

        var treeKilled = true;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消（例如程序退出）：必须结束整个进程树，不能留下孤儿子进程。
            treeKilled = KillTree(process);
            await WaitForExitQuietlyAsync(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            // 命令级超时。
            timedOut = true;
            treeKilled = KillTree(process);
            await WaitForExitQuietlyAsync(process);
        }

        // 孙进程可能继承了 stdout/stderr 管道句柄并活过父进程，此时 EOF 永远不来：
        // 只等有限时间，不把「管道关闭」当成命令结束的必要条件。
        await Task.WhenAny(
            Task.WhenAll(outputClosed.Task, errorClosed.Task),
            Task.Delay(AppConstants.OutputCloseTimeout, CancellationToken.None));

        if (timedOut)
        {
            lock (standardError)
            {
                standardError.Add(
                    treeKilled
                        ? $"命令执行超过 {deadline.TotalSeconds:F0} 秒，已终止整个进程树"
                        : $"命令执行超过 {deadline.TotalSeconds:F0} 秒，已请求结束进程树，部分子进程可能仍在运行");
            }
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        string[] output;
        string[] error;
        lock (standardOutput)
        {
            output = standardOutput.ToArray();
        }

        lock (standardError)
        {
            error = standardError.ToArray();
        }

        return new CommandResult(timedOut ? -1 : exitCode, output, error, timedOut);
    }

    /// <summary>
    /// 启动一个长期运行的子进程（dsh web），输出通过回调实时投递，不做同步等待。
    /// </summary>
    public static Process StartLongRunning(
        string fileName,
        string arguments,
        Action<string> onStandardOutput,
        Action<string> onStandardError)
    {
        // dsh web 与 npm 命令共用同一套子进程环境（日志编码、缓存、临时目录）。
        var startInfo = BuildStartInfo(fileName, arguments, outputEncoding: null);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onStandardOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onStandardError(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(source.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>结束进程树；返回是否确认整棵树都已结束（失败时调用方只报告「已请求结束」）。</summary>
    private static bool KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出。
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 无权限或进程已消失；调用方会看到超时/取消结果。
            return false;
        }
        catch (AggregateException)
        {
            // 树中有无法结束的后代（提权 / 受保护）：主进程已收到结束请求，仍按超时/取消返回，
            // 不把异常抛给调用方、丢失超时语义。
            return false;
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        string fileName,
        string arguments,
        Encoding? outputEncoding)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = outputEncoding ?? Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        // node 系工具向管道输出 UTF-8；同时关闭交互式确认与彩色转义，保证日志面板可读。
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["FORCE_COLOR"] = "0";
        startInfo.Environment["npm_config_yes"] = "true";
        startInfo.Environment["CI"] = "1";

        // 低完整性 / 受限会话里 npm / npx 写不了默认缓存，改用应用内可写缓存；
        // 检查更新、安装 / 更新 dsh 等 npm 子命令都会继承这个环境变量。
        NpmCache.ApplyTo(startInfo);

        // 受限会话里子进程也无法在 %TEMP% 下创建目录（dsh web 的 spill-local 插件因此启动失败），
        // 换成应用内可写临时目录。
        TempDirectory.ApplyTo(startInfo);
        return startInfo;
    }

    private static Encoding CreateSystemToolEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }
}
