// 游戏模式窗口管理（试验，设置 › 通用里的开关，默认关）：系统一有窗口出现、最小化、关掉就收到通知（SetWinEventHook），
// 不再等 0.5 秒的轮询 ——
//   新出现的应用窗口：能调大小的直接最大化铺满（游戏本来就全屏的不动；弹窗、对话框不管）；
//   应用窗口最小化 / 关掉、前台没有应用了：游戏库马上回到前台（游戏模式没有任务栏，最小化的窗口只能从切换器找回）。
// 窗口还是各自的进程窗口，不嵌进游戏库；管理员身份运行的窗口我们摆不动，跳过。
using System.Runtime.InteropServices;
using ClawLibrary.Data;

namespace ClawLibrary;

public sealed partial class MainWindow
{
    private delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);
    private WinEventProc? _wmProc;
    private readonly List<nint> _wmHooks = new();
    private readonly HashSet<nint> _wmPlaced = new();

    private void InitializeWindowManager()
    {
        _wmProc = OnWinEvent;
        // OUTOFCONTEXT | SKIPOWNPROCESS：回调在本线程的消息循环里，自己的窗口不报
        foreach (var (from, to) in new (uint, uint)[] { (0x0016, 0x0016), (0x8001, 0x8002) })
        {
            nint h = SetWinEventHook(from, to, 0, _wmProc, 0, 0, 0x0000 | 0x0002);
            if (h != 0) _wmHooks.Add(h);
        }
        Log.Info($"window manager hooks {_wmHooks.Count}");
        Closed += (_, _) => { foreach (var h in _wmHooks) UnhookWinEvent(h); _wmHooks.Clear(); };
    }

    private bool WmActive => _wasGameMode && _stage?.WindowManagerOn == true && !_desktopHandoff && !_shortcutClosed;

    private void OnWinEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == 0 || !WmActive) return;   // 只管窗口本身（OBJID_WINDOW）
        try
        {
            switch (ev)
            {
                case 0x8002:   // EVENT_OBJECT_SHOW
                    if (_wmPlaced.Contains(hwnd) || OpenWindows.AppWindow(hwnd) is not { } w) return;
                    _wmPlaced.Add(hwnd);
                    // 窗口刚出现时常常还在自己摆位置：等一下再铺
                    Later(150, () => WmPlace(w));
                    break;
                case 0x0016:   // EVENT_SYSTEM_MINIMIZESTART
                    if (OpenWindows.AppWindow(hwnd) is not { } m) return;
                    Log.Info($"wm: {m.Process} minimized; library to front");
                    Later(60, () => { if (WmActive) RestoreGameShell(true); });
                    break;
                case 0x8001:   // EVENT_OBJECT_DESTROY
                    if (!_wmPlaced.Remove(hwnd)) return;
                    Later(120, () => { if (WmActive && !LibraryWindow.HasForegroundApp(WinRT.Interop.WindowNative.GetWindowHandle(this))) RestoreGameShell(true); });
                    break;
            }
        }
        catch (Exception ex) { Log.Warn($"wm event 0x{ev:x}: {ex.Message}"); }
    }

    private void WmPlace(OpenWindow w)
    {
        if (!WmActive || !OpenWindows.Matches(w) || IsZoomed(w.Hwnd) || ToolboxWindow.IsIconic(w.Hwnd)) return;
        long style = GetWindowLongPtr(w.Hwnd, -16).ToInt64();
        if ((style & 0x00010000) == 0 || (style & 0x00040000) == 0) return;   // 没有最大化按钮 / 不能调大小：照它自己的尺寸
        if (CoversMonitor(w.Hwnd)) return;   // 已经全屏（游戏）
        bool ok = PlaceFull(w.Hwnd);
        EdgeGestures.Set(w.Hwnd, true);   // 铺满的应用窗口上也不让底边上滑弹开始菜单
        Log.Info($"wm: {w.Process} filled ok={ok}");
    }

    /// <summary>
    /// 工作区就是整屏（真游戏模式，没有任务栏）：直接最大化。
    /// 保留 explorer 的游戏模式里任务栏虽然藏着，explorer 还给它留着工作区那一条，最大化的窗口底下会空一截露出游戏库：
    /// 这时不最大化，按窗口的可见边框把它摆满整块屏幕（工作区本身不改 —— 改了 explorer 会改回去，来回闪）。
    /// </summary>
    private static bool PlaceFull(nint hwnd)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return ShowWindow(hwnd, 3);
        var m = info.Monitor; var wk = info.Work;
        if (wk.Left == m.Left && wk.Top == m.Top && wk.Right == m.Right && wk.Bottom == m.Bottom) return ShowWindow(hwnd, 3);   // SW_MAXIMIZE
        if (!GetWindowRect(hwnd, out var r)) return false;
        var f = r;
        DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out f, Marshal.SizeOf<WmRect>());
        int l = f.Left - r.Left, t = f.Top - r.Top, rr = r.Right - f.Right, b = r.Bottom - f.Bottom;
        return SetWindowPos(hwnd, 0, m.Left - l, m.Top - t, (m.Right - m.Left) + l + rr, (m.Bottom - m.Top) + t + b, 0x0014 /* NOZORDER | NOACTIVATE */);
    }

    private static bool CoversMonitor(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return false;
        return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }

    private void Later(int ms, Action action)
    {
        var t = DispatcherQueue.CreateTimer();
        t.IsRepeating = false; t.Interval = TimeSpan.FromMilliseconds(ms);
        t.Tick += (_, _) => { t.Stop(); try { action(); } catch (Exception ex) { Log.Warn($"wm: {ex.Message}"); } };
        t.Start();
    }

    [StructLayout(LayoutKind.Sequential)] private struct WmRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public WmRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out WmRect value, int size);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out WmRect r);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
}
