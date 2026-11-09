using System.Runtime.InteropServices;

namespace ClawLibrary;

/// <summary>Win32 通知图标，无额外 UI 框架依赖；子类回调保留到窗口关闭，兼容 NativeAOT。</summary>
internal sealed class LibraryTray : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 81;
    private readonly nint _hwnd;
    private readonly SubclassProc _callback;
    private readonly Action _restore, _exit;
    private readonly uint _taskbarCreated;
    private nint _icon;
    private bool _added, _disposed;

    public LibraryTray(nint hwnd, Action restore, Action exit)
    {
        _hwnd = hwnd; _restore = restore; _exit = exit;
        _callback = HandleMessage;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "ClawIcon.ico"), 1, 32, 32, 0x10);
        if (_icon == 0) throw new InvalidOperationException("无法加载托盘图标");
        if (!SetWindowSubclass(hwnd, _callback, 81, 0))
        {
            DestroyIcon(_icon); _icon = 0;
            throw new InvalidOperationException("无法注册托盘窗口");
        }
    }

    public bool EnsureIcon()
    {
        if (_disposed) return false;
        var data = IconData();
        if (_added && Shell_NotifyIcon(1, ref data)) return true;
        _added = Shell_NotifyIcon(0, ref data);
        return _added;
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _hwnd, Id = 1,
        Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = _icon,
        Tip = "Claw 游戏库 · 双击打开，右键退出", Info = "", InfoTitle = "",
    };

    private nint HandleMessage(nint hwnd, uint message, nuint wp, nint lp, nuint id, nuint data)
    {
        try
        {
            if (message == _taskbarCreated) { _added = false; EnsureIcon(); }
            if (message == CallbackMessage)
            {
                if ((uint)lp is 0x203 or 0x405) _restore(); // WM_LBUTTONDBLCLK / NIN_KEYSELECT
                else if ((uint)lp is 0x205 or 0x7B) ShowMenu();
                return 0;
            }
        }
        catch (Exception ex) { Log.Error("tray", ex); }
        return DefSubclassProc(hwnd, message, wp, lp);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 0, 1, "打开游戏库");
            AppendMenu(menu, 0, 2, "退出游戏库");
            GetCursorPos(out var p);
            SetForegroundWindow(_hwnd);
            uint command = TrackPopupMenu(menu, 0x100 | 2, p.X, p.Y, 0, _hwnd, 0);
            PostMessage(_hwnd, 0, 0, 0);
            if (command == 1) _restore();
            else if (command == 2) _exit();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = IconData();
        Shell_NotifyIcon(2, ref data);
        RemoveWindowSubclass(_hwnd, _callback, 81);
        if (_icon != 0) { DestroyIcon(_icon); _icon = 0; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint action, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string path, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nuint wp, nint lp);
}
