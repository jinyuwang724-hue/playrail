using System.Runtime.InteropServices;

namespace ClawLibrary.Data;

/// <summary>Explicit shortcut activation only; no global foreground-policy changes.</summary>
internal sealed class LibraryWindow : IDisposable
{
    private readonly nint _window;
    private readonly SubclassProc _callback;
    private readonly Action<string, nint> _activate;
    private readonly uint _message;
    private nint _returnWindow;
    private nint _overlayTarget;
    private uint _overlayPid;
    private Windows.Graphics.RectInt32 _overlayBounds;
    private uint _returnPid;
    private static readonly string[] Routes = ["library", "control-center", "restore-library", "restore-control-center", "minimize-library", "minimize-control-center", "window-switcher", "game-shell", "keep-game-shell"];

    public LibraryWindow(nint window, Action<string, nint> activate)
    {
        _window = window; _activate = activate; _callback = HandleMessage;
        _message = RegisterWindowMessage("ClawToolbox.Library.Activation.v1");
        if (_message == 0 || !SetWindowSubclass(window, _callback, 82, 0))
            throw new InvalidOperationException("无法注册游戏库唤出消息");
        SetProp(window, "ClawToolbox.Library.Activation.v1", 1);
        // 关掉 DWM 的显示 / 隐藏淡入淡出：窗口从托盘出来时系统先淡入一张还没画好的黑底，录屏里就是一段黑闪。进出场动画由我们自己做
        int off = 1;
        DwmSetWindowAttribute(window, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref off, 4);
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wp, nint lp, nuint id, nuint data)
    {
        if (message == 0x0083 && !Program.Args.Contains("--windowed")) return 0; // WM_NCCALCSIZE: the entire monitor is client area.
        if (message == 0x007e || message == 0x02e0) Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(EnsureFullscreenBounds);
        if (_overlayTarget != 0 && message == 0x21) return 3; // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (_overlayTarget != 0 && message == 0x0245) return 3; // WM_POINTERACTIVATE / PA_NOACTIVATE
        if (message == 0x112 && (wp & 0xfff0) == 0xf020 && IsWindowVisible(hwnd) && !ToolboxWindow.IsIconic(hwnd)) // WM_SYSCOMMAND / SC_MINIMIZE
        {
            try { _activate("minimize-library", 0); }
            catch (Exception ex) { Log.Error("window minimize", ex); return DefSubclassProc(hwnd, message, wp, lp); }
            return 0;
        }
        if (message == _message)
        {
            if (wp >= 1 && wp <= (nuint)Routes.Length)
                try { _activate(Routes[(int)wp - 1], lp); }
                catch (Exception ex) { Log.Error("window shortcut", ex); }
            return 0;
        }
        return DefSubclassProc(hwnd, message, wp, lp);
    }

    public static bool CanOverlay(nint window)
    {
        if (window == 0 || !IsWindowVisible(window) || ToolboxWindow.IsIconic(window)) return false;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        return pid != 0;
    }

    public bool OverlayTargetIsForeground => _overlayTarget != 0 && Foreground == _overlayTarget
        && GetWindowThreadProcessId(_overlayTarget, out uint pid) != 0 && pid == _overlayPid;

    public void SetOverlayTarget(nint target)
    {
        _overlayTarget = target;
        GetWindowThreadProcessId(target, out _overlayPid);
        long style = GetWindowLongPtr(_window, -20).ToInt64();
        SetWindowLongPtr(_window, -20, (nint)(target != 0 ? style | 0x08000000L : style & ~0x08000000L));
    }

    private bool _sizing;
    public void EnsureFullscreenBounds()
    {
        if (_sizing || Program.Args.Contains("--windowed")) return;
        _sizing = true;
        try
        {
            nint monitor = MonitorFromWindow(_overlayTarget != 0 ? _overlayTarget : _window, 2);
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return;
            var r = info.Monitor;
            long style = GetWindowLongPtr(_window, -16).ToInt64();
            long desired = (style & ~0x00CF0000L) | 0x80000000L;
            long extended = GetWindowLongPtr(_window, -20).ToInt64();
            bool changed = style != desired || (extended & 0x300) != 0;
            if (style != desired) SetWindowLongPtr(_window, -16, (nint)desired);
            if ((extended & 0x300) != 0) SetWindowLongPtr(_window, -20, (nint)(extended & ~0x300L));
            int square = 1, noBorder = unchecked((int)0xfffffffe);
            DwmSetWindowAttribute(_window, 33, ref square, 4);
            DwmSetWindowAttribute(_window, 34, ref noBorder, 4);
            if (changed || !GetWindowRect(_window, out var current) || !current.Equals(r))
                SetWindowPos(_window, 0, r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top, 0x234);
        }
        finally { _sizing = false; }
    }

    public void ShowGameShell(bool activate)
    {
        SetTopmost(false);
        ShowWindow(_window, 4);
        EnsureFullscreenBounds();
        SetWindowPos(_window, -2, 0, 0, 0, 0, 0x253);
        if (activate) BringForward(_window);
        else StayBehindForeground();
    }

    public static bool HasForegroundApp(nint self)
    {
        nint fg = Foreground;
        if (fg == self) return true;
        if (fg == 0 || !IsWindow(fg) || !IsWindowVisible(fg) || ToolboxWindow.IsIconic(fg)) return false;
        var cls = new System.Text.StringBuilder(128);
        GetClassName(fg, cls, cls.Capacity);
        return cls.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd");
    }

    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder text, int capacity);

    public void ShowOverlay()
    {
        ShowWindow(_window, 4); // SW_SHOWNOACTIVATE (also restores from minimized)
        EnsureFullscreenBounds();
        bool shown = SetWindowPos(_window, -1, 0, 0, 0, 0, 0x253); // NOOWNERZORDER | NOACTIVATE | SHOWWINDOW | NOMOVE | NOSIZE
        Log.Info($"overlay show topmost={shown} style={GetWindowLongPtr(_window, -20).ToInt64():X}");
    }

    public static nint Foreground => GetForegroundWindow();
    public bool IsForeground => Foreground == _window;
    public void RememberReturnWindow(nint previous)
    {
        if (previous == 0) previous = Foreground;
        if (previous == _window || previous == 0 || !IsWindow(previous) || !IsWindowVisible(previous)) return;
        GetWindowThreadProcessId(previous, out uint pid);
        if (pid == (uint)Environment.ProcessId) return;
        _returnWindow = previous; _returnPid = pid;
        Log.Info($"shortcut return target hwnd={previous} pid={pid}");
    }

    public bool SetTopmost(bool on)
    {
        bool current = (GetWindowLongPtr(_window, -20).ToInt64() & 0x8) != 0;
        if (current == on) return false;
        SetWindowPos(_window, on ? -1 : -2, 0, 0, 0, 0, 0x213);
        if (!on) StayBehindForeground();
        return true;
    }

    /// <summary>
    /// HWND_NOTOPMOST lifts the window above every normal window, including the one that just took
    /// foreground (a game Steam started): it then has focus but sits behind the fullscreen library.
    /// </summary>
    public void StayBehindForeground()
    {
        nint fg = Foreground;
        if (fg == 0 || fg == _window || !IsWindowVisible(fg) || (GetWindowLongPtr(fg, -20).ToInt64() & 0x8) != 0) return;
        if ((GetWindowLongPtr(_window, -20).ToInt64() & 0x8) != 0) return;
        SetWindowPos(_window, fg, 0, 0, 0, 0, 0x213);
    }

    /// <summary>Before handing a launch to Steam / a game: no topmost, and whoever opens the game window may take foreground.</summary>
    public void HandOffForeground()
    {
        SetTopmost(false);
        AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
    }

    public static bool BringForward(nint window)
    {
        if (window == 0 || !IsWindow(window)) return false;
        if (ToolboxWindow.IsIconic(window)) ShowWindow(window, 9);
        nint popup = GetLastActivePopup(window);
        if (popup != 0 && IsWindowVisible(popup)) window = popup;
        SetForegroundWindow(window);
        if (Foreground == window) return true;
        uint current = GetCurrentThreadId();
        uint foreground = GetWindowThreadProcessId(Foreground, out _);
        // A bounded input-queue handoff, immediately detached, also supports an
        // already running single instance that did not receive the shell launch.
        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
            SetFocus(window);
        }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
        return Foreground == window;
    }

    public nint ReturnWindow => _returnWindow;

    public void ReturnToPreviousWindow(bool returnFocus)
    {
        GetWindowThreadProcessId(_returnWindow, out uint pid);
        if (returnFocus && pid == _returnPid && IsWindow(_returnWindow) && IsWindowVisible(_returnWindow))
            Log.Info($"shortcut returned hwnd={_returnWindow} foreground={BringForward(_returnWindow)}");
    }

    public void Dispose() { SetTopmost(false); RemoveProp(_window, "ClawToolbox.Library.Activation.v1"); RemoveWindowSubclass(_window, _callback, 82); }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint hwnd);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint hwnd, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint hwnd, string name);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
}
