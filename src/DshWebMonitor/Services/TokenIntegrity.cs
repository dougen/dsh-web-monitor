using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DshWebMonitor.Services;

/// <summary>
/// 读取当前进程的令牌完整性级别。
/// 低完整性进程受 UIPI 限制，Shell_NotifyIcon 会被 Windows 拒绝，
/// 界面据此退化为“最小化到任务栏”，避免窗口隐藏后无法找回。
/// </summary>
internal static class TokenIntegrity
{
    private const int TokenIntegrityLevel = 25;
    private const uint TokenQueryAccess = 0x0008;
    private const int MediumIntegrityRid = 0x2000;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "GetTokenInformation")]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static int? GetCurrentIntegrityRid()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQueryAccess, out var token))
        {
            return null;
        }

        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var requiredLength);
            if (requiredLength <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, requiredLength, out _))
                {
                    return null;
                }

                var sidPointer = Marshal.ReadIntPtr(buffer);
                if (sidPointer == IntPtr.Zero)
                {
                    return null;
                }

                var sid = new SecurityIdentifier(sidPointer).Value;
                var lastSegment = sid[(sid.LastIndexOf('-') + 1)..];
                return int.TryParse(lastSegment, out var rid) ? rid : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// 当前进程是否具备注册系统托盘图标的完整性级别。
    /// 查询失败时按「不可用」处理（fail-closed）：宁可退化为最小化到任务栏，
    /// 也不要出现「窗口已隐藏、托盘却没有图标」的不可恢复状态。
    /// </summary>
    public static bool IsAtLeastMedium
    {
        get
        {
            var rid = GetCurrentIntegrityRid();
            return rid is { } value && value >= MediumIntegrityRid;
        }
    }
}
