using System.Runtime.InteropServices;

namespace DshWebMonitor.Ui;

/// <summary>
/// 托盘图标的直接实现（Shell_NotifyIcon）：
/// 注册结果可以校验、可以查询图标是否真的存在，并在 Explorer 重启后重新登记，
/// 避免「窗口已隐藏但托盘没有图标」的不可恢复状态。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;

    private const int WmUser = 0x0400;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly TrayWindow _window;
    private readonly uint _taskbarCreatedMessage;
    private string _tooltip;
    private bool _disposed;

    public TrayIconHost(Icon icon, string tooltip, ContextMenuStrip menu)
    {
        _icon = icon;
        _tooltip = tooltip;
        _menu = menu;
        _window = new TrayWindow(this);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>图标当前是否真的存在于通知区域。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>图标注册状态变化（Explorer 重启后重新登记失败时为 false）。</summary>
    public event Action<bool>? RegistrationChanged;

    public event Action? Activated;

    public bool TryRegister()
    {
        if (_disposed)
        {
            return false;
        }

        IsRegistered = AddAndVerify();
        RegistrationChanged?.Invoke(IsRegistered);
        return IsRegistered;
    }

    /// <summary>
    /// 登记图标并确认它真的出现在通知区域。Explorer 重启后 shell 可能尚未就绪，
    /// 一次探测失败就降级会让托盘在本次会话内永久失效，因此短暂重试。
    /// </summary>
    private bool AddAndVerify()
    {
        if (!Shell_NotifyIcon(NimAdd, BuildData()))
        {
            return false;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (ExistsInNotificationArea())
            {
                return true;
            }

            if (attempt < 2)
            {
                Thread.Sleep(100);
            }
        }

        return false;
    }

    public void UpdateTooltip(string text)
    {
        if (_disposed || text == _tooltip)
        {
            return;
        }

        _tooltip = text;
        if (IsRegistered)
        {
            Shell_NotifyIcon(NimModify, BuildData());
        }
    }

    /// <summary>重新查询图标是否仍然存在（例如 Explorer 崩溃后）。</summary>
    public bool Recheck()
    {
        if (_disposed || !IsRegistered)
        {
            return false;
        }

        if (ExistsInNotificationArea())
        {
            return true;
        }

        IsRegistered = false;
        RegistrationChanged?.Invoke(false);
        return false;
    }

    internal void OnTrayMessage(int message)
    {
        switch (message)
        {
            case WmLButtonDblClk:
                Activated?.Invoke();
                break;
            case WmRButtonUp:
            case WmContextMenu:
                ShowMenu();
                break;
        }
    }

    internal void OnTaskbarCreated()
    {
        if (_disposed)
        {
            return;
        }

        // Explorer 重启后通知区域被清空，必须重新登记，否则图标永久消失。
        IsRegistered = AddAndVerify();
        RegistrationChanged?.Invoke(IsRegistered);
    }

    private void ShowMenu()
    {
        SetForegroundWindow(_window.Handle);
        _menu.Show(Cursor.Position);
    }

    private bool ExistsInNotificationArea()
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = Marshal.SizeOf<NotifyIconIdentifier>(),
            WindowHandle = _window.Handle,
            IconId = _iconId,
            Guid = Guid.Empty,
        };

        return Shell_NotifyIconGetRect(ref identifier, out _) == 0;
    }

    private readonly uint _iconId = 1;

    private NotifyIconData BuildData() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _window.Handle,
        IconId = _iconId,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = WmUser + 1,
        IconHandle = _icon.Handle,
        Tip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // 无条件删除：登记状态可能因一次探测失败而失真，漏发 NIM_DELETE 会在通知区域
            // 留下一个点不动的旧图标（图标句柄随后被释放）。
            Shell_NotifyIcon(NimDelete, BuildData());
        }
        catch (Exception)
        {
            // 通知区域已随会话结束，忽略。
        }

        IsRegistered = false;
        _window.DestroyHandle();
        _menu.Dispose();
        _icon.Dispose();
    }

    /// <summary>
    /// NOTIFYICONDATAW 的完整布局。cbSize 必须等于 shell 认识的结构体大小，
    /// 只声明到 szTip 会让 Marshal.SizeOf 小于任何一个已知版本，NIM_ADD 会被直接拒绝。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr WindowHandle;
        public uint IconId;
        public int Flags;
        public int CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public int State;
        public int StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public int TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public int InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int Size;
        public IntPtr WindowHandle;
        public uint IconId;
        public Guid Guid;
    }

    /// <summary>托盘回调用的隐藏顶层窗口（必须是顶层窗口，才能收到 Explorer 的 TaskbarCreated 广播）。</summary>
    private sealed class TrayWindow : NativeWindow
    {
        private readonly TrayIconHost _owner;

        public TrayWindow(TrayIconHost owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "DshWebMonitorTray",
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Style = unchecked((int)0x80000000), // WS_POPUP：顶层但不显示
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmUser + 1)
            {
                _owner.OnTrayMessage((int)(m.LParam.ToInt64() & 0xFFFF));
                return;
            }

            if (_owner._taskbarCreatedMessage != 0
                && m.Msg == (int)_owner._taskbarCreatedMessage)
            {
                _owner.OnTaskbarCreated();
                return;
            }

            base.WndProc(ref m);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, NotifyIconData data);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out RECT iconLocation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
