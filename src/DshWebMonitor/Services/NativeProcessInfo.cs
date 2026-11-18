using System.Runtime.InteropServices;
using System.Text;

namespace DshWebMonitor.Services;

/// <summary>
/// 通过 Win32 原生接口读取进程映像名、命令行与父进程。
/// 当进程处于受限令牌/低完整性等无法使用 tasklist、wmic 的场景时作为首选与兜底手段；
/// 只使用系统 DLL，不引入任何第三方组件。
/// </summary>
internal static class NativeProcessInfo
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessBasicInformationClass = 0;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    private static readonly int PointerOffset = IntPtr.Size == 8 ? 8 : 4;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr processHandle, uint flags, StringBuilder exeName, ref int size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    /// <summary>读取进程映像名（如 node.exe），失败返回 null。</summary>
    public static string? TryGetExecutableName(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                return null;
            }

            var path = buffer.ToString();
            return path.Length == 0 ? null : Path.GetFileName(path);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>读取完整命令行，失败返回 null。</summary>
    public static string? TryGetCommandLine(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var status = NtQueryInformationProcess(
                handle,
                ProcessCommandLineInformation,
                IntPtr.Zero,
                0,
                out var requiredLength);
            if (status != StatusInfoLengthMismatch || requiredLength <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                status = NtQueryInformationProcess(
                    handle,
                    ProcessCommandLineInformation,
                    buffer,
                    requiredLength,
                    out _);
                if (status != 0)
                {
                    return null;
                }

                // 返回的是 UNICODE_STRING（Length 为无符号 16 位字节数），Buffer 指向同一块缓冲区里的字符串。
                var byteLength = (ushort)Marshal.ReadInt16(buffer, 0);
                var textPointer = Marshal.ReadIntPtr(buffer, PointerOffset);
                if (textPointer == IntPtr.Zero || byteLength == 0)
                {
                    return null;
                }

                var text = Marshal.PtrToStringUni(textPointer, byteLength / 2);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>读取父进程 PID，失败返回 null。</summary>
    public static int? TryGetParentProcessId(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = Marshal.SizeOf<ProcessBasicInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessBasicInformationClass, buffer, size, out _);
                if (status != 0)
                {
                    return null;
                }

                // PROCESS_BASIC_INFORMATION 的第 6 个字段是 InheritedFromUniqueProcessId（IntPtr）。
                var parent = Marshal.ReadIntPtr(buffer, PointerOffset * 5).ToInt64();
                return parent is > 0 and <= int.MaxValue ? (int)parent : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
