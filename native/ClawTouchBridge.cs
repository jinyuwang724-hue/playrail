// Touch bridge for Magpie scaling. Magpie's output window is click-through, so a
// tap lands on whatever really sits under the finger (Steam, the desktop) instead
// of the small source window, which steals focus and stops scaling. Upstream
// Magpie fixes this with a UIAccess TouchHelper that needs a root certificate;
// this bridge needs no system change: a no-activate, almost transparent window
// above Magpie's output takes touch/pen contacts only and replays them as mouse
// input at the mapped point inside the game window. Real mouse input is forwarded
// as-is: Magpie (patched to see through this window) keeps the real cursor inside
// the source window, so its position is already in game coordinates.
// Lifetime: exits when stdin closes (the plugin backend owns it). For a game that
// runs elevated (Steam started as administrator), the backend starts this bridge
// elevated too, since Windows drops input posted from a lower integrity level; it
// then gets "--parent <pid>" (the Magpie launcher) and exits when that process ends.
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class ClawTouchBridge
{
    const string ScalingClass = "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";
    const string BridgeClass = "ClawTouchBridge_7A1C";
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_LAYERED = 0x80000, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80;
    const uint WM_CLOSE = 0x10, WM_DESTROY = 0x2, WM_TIMER = 0x113, WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21;
    const uint WM_POINTERUPDATE = 0x245, WM_POINTERDOWN = 0x246, WM_POINTERUP = 0x247, WM_POINTERACTIVATE = 0x24B;
    const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202;
    const int HTCLIENT = 1, HTTRANSPARENT = -1, MA_NOACTIVATE = 3, PA_NOACTIVATE = 3;
    const uint PT_TOUCH = 2, PT_PEN = 3;
    const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOSIZE = 1, SWP_NOMOVE = 2;
    const int MK_LBUTTON = 1;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT_MESSAGE_SOURCE { public int deviceType, originId; }
    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int c);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr h, string name);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool GetPointerType(uint id, out uint type);
    [DllImport("user32.dll")] static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE s);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetMessageExtraInfo();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
    static IntPtr bridge, source;
    static int sl, st, sr, sb, dl, dt, dr, db;
    static RECT cover;  // Magpie's whole output window, including any letterbox bars
    static bool shown, contact, focused;
    static IntPtr magpieWindow;
    static uint contactId;
    static readonly WndProc proc = Handle;  // kept alive for the native callback

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr OpenEvent(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool all, uint ms);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);

    // Repaint pump for an elevated game while a Steam side menu is open (see
    // plugin/claw_overlay_frames.py). Windows ignores InvalidateRect from the lower
    // integrity plugin on such a window, so the plugin starts this mode elevated. It
    // marks the window dirty every 8 ms while it is in front, and exits when the
    // plugin signals the named event, the plugin process ends, or the window closes.
    static int Pump(IntPtr hwnd, string eventName, int parentId)
    {
        const uint SYNCHRONIZE = 0x100000, WAIT_TIMEOUT = 0x102;
        IntPtr stop = OpenEvent(SYNCHRONIZE, false, eventName);
        IntPtr parent = OpenProcess(SYNCHRONIZE, false, parentId);
        if (stop == IntPtr.Zero || parent == IntPtr.Zero) return 4;
        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x1F0003);  // high resolution
        var handles = new[] { stop, parent };
        bool here = false;
        for (int tick = 0; WaitForMultipleObjects(2, handles, false, 0) == WAIT_TIMEOUT; tick++)
        {
            if (tick % 8 == 0)
            {
                if (!IsWindow(hwnd)) break;
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && !IsWindowVisible(fg)) fg = GetAncestor(fg, 3);  // hidden owned focus window
                here = fg == hwnd;
            }
            if (here) InvalidateRect(hwnd, IntPtr.Zero, false);
            long due = -80000;  // 8 ms
            if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(timer, 1000);
            else Thread.Sleep(8);
        }
        return 0;
    }

    [STAThread]
    static int Main(string[] args)
    {
        long pumpWindow; int pumpParent;
        if (args.Length == 4 && args[0] == "--pump" && long.TryParse(args[1], out pumpWindow) && int.TryParse(args[3], out pumpParent))
            return Pump(new IntPtr(pumpWindow), args[2], pumpParent);
        System.Diagnostics.Process parent = null;
        int parentId;
        if (args.Length == 2 && args[0] == "--parent" && int.TryParse(args[1], out parentId))
        {
            try { parent = System.Diagnostics.Process.GetProcessById(parentId); } catch { return 4; }
        }
        else if (args.Length != 0) return 1;
        SetProcessDpiAwarenessContext(new IntPtr(-4));  // physical pixels, like Magpie's props
        var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)), lpfnWndProc = proc,
            hInstance = GetModuleHandle(null), lpszClassName = BridgeClass, hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)) };
        if (RegisterClassEx(ref wc) == 0) return 2;
        bridge = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, BridgeClass, "",
            WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (bridge == IntPtr.Zero) return 3;
        // Alpha 1 keeps the window hit-testable while showing nothing visible.
        SetLayeredWindowAttributes(bridge, 0, 1, 2);
        SetTimer(bridge, new IntPtr(1), 100, IntPtr.Zero);
        var owner = new Thread(() => {
            try { if (parent != null) parent.WaitForExit(); else while (Console.In.Read() >= 0) { } } catch { }
            PostMessage(bridge, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        });
        owner.IsBackground = true; owner.Start();
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        return 0;
    }

    static int Prop(IntPtr h, string name) { return (int)GetProp(h, "Magpie." + name).ToInt64(); }

    static void Track()
    {
        IntPtr magpie = FindWindow(ScalingClass, null);
        bool active = magpie != IntPtr.Zero && IsWindowVisible(magpie);
        if (active)
        {
            source = GetProp(magpie, "Magpie.SrcHWND");
            sl = Prop(magpie, "SrcLeft"); st = Prop(magpie, "SrcTop"); sr = Prop(magpie, "SrcRight"); sb = Prop(magpie, "SrcBottom");
            dl = Prop(magpie, "DestLeft"); dt = Prop(magpie, "DestTop"); dr = Prop(magpie, "DestRight"); db = Prop(magpie, "DestBottom");
            active = IsWindow(source) && sr > sl && sb > st && dr > dl && db > dt && GetWindowRect(magpie, out cover);
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && !IsWindowVisible(fg)) fg = GetAncestor(fg, 3);  // hidden owned focus window
            focused = fg == source;
            magpieWindow = magpie;
        }
        if (active && focused)
        {
            // Re-assert topmost order above Magpie's own topmost output each tick.
            SetWindowPos(bridge, new IntPtr(-1), cover.L, cover.T, cover.R - cover.L, cover.B - cover.T, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            shown = true;
        }
        else if (active)
        {
            // The player is elsewhere (task bar, another window). Magpie keeps scaling
            // below it, so sit just above Magpie, not above the other window: taps on
            // that window reach it, and a tap on the game picture brings the game back.
            SetWindowPos(bridge, new IntPtr(-2), 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
            IntPtr above = GetWindow(magpieWindow, 3);  // GW_HWNDPREV
            SetWindowPos(bridge, above == IntPtr.Zero || above == bridge ? IntPtr.Zero : above, cover.L, cover.T,
                cover.R - cover.L, cover.B - cover.T, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            shown = true;
        }
        else if (shown)
        {
            SetWindowPos(bridge, IntPtr.Zero, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE);
            shown = false; contact = false;
        }
    }

    static bool FromMouseMessage()
    {
        // Mouse messages promoted from touch/pen carry the MI_WP_SIGNATURE extra info.
        return (GetMessageExtraInfo().ToInt64() & 0xFFFFFF00) != 0xFF515700;
    }

    static void Replay(IntPtr lParam, uint message)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        // Map the finger from Magpie's output rectangle back into the captured source.
        int mx = sl + (int)Math.Round((x - dl) * (double)(sr - sl) / (dr - dl));
        int my = st + (int)Math.Round((y - dt) * (double)(sb - st) / (db - dt));
        mx = Math.Max(sl, Math.Min(sr - 1, mx)); my = Math.Max(st, Math.Min(sb - 1, my));
        SetCursorPos(mx, my);
        var client = new POINT { X = mx, Y = my };
        ScreenToClient(source, ref client);
        var coords = new IntPtr((client.Y << 16) | (client.X & 0xFFFF));
        IntPtr keys = new IntPtr(message == WM_LBUTTONUP ? 0 : MK_LBUTTON);
        if (message != WM_MOUSEMOVE) PostMessage(source, WM_MOUSEMOVE, keys, coords);
        PostMessage(source, message, keys, coords);
    }

    static IntPtr Handle(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case WM_TIMER: Track(); return IntPtr.Zero;
            // Cross-process HTTRANSPARENT is ignored by Windows, so take mouse input too
            // and pass it on to the game window below.
            case WM_NCHITTEST: return new IntPtr(HTCLIENT);
            case 0x200: case 0x201: case 0x202: case 0x203: case 0x204: case 0x205: case 0x206:
            case 0x207: case 0x208: case 0x209: case 0x20B: case 0x20C: case 0x20D:
            {
                // WM_MOUSEMOVE .. WM_XBUTTONDBLCLK from a real mouse (touch is handled as pointer input).
                if (!FromMouseMessage() || !shown || !IsWindow(source)) break;
                if (!focused && (m == 0x201 || m == 0x204 || m == 0x207)) { SetForegroundWindow(source); focused = true; Track(); return IntPtr.Zero; }
                var pt = new POINT { X = (short)(l.ToInt64() & 0xFFFF), Y = (short)((l.ToInt64() >> 16) & 0xFFFF) };
                ClientToScreen(h, ref pt); ScreenToClient(source, ref pt);
                PostMessage(source, m, w, new IntPtr((pt.Y << 16) | (pt.X & 0xFFFF)));
                return IntPtr.Zero;
            }
            case 0x20A: case 0x20E:  // wheel messages carry screen coordinates already
                if (!FromMouseMessage() || !shown || !IsWindow(source)) break;
                PostMessage(source, m, w, l); return IntPtr.Zero;
            case WM_MOUSEACTIVATE: return new IntPtr(MA_NOACTIVATE);
            case WM_POINTERACTIVATE: return new IntPtr(PA_NOACTIVATE);
            case WM_POINTERDOWN: case WM_POINTERUPDATE: case WM_POINTERUP:
            {
                uint id = (uint)(w.ToInt64() & 0xFFFF), type;
                bool primary = (w.ToInt64() & 0x20000000) != 0;  // POINTER_MESSAGE_FLAG_PRIMARY
                if (!GetPointerType(id, out type) || (type != PT_TOUCH && type != PT_PEN)) break;
                if (!shown || !IsWindow(source)) return IntPtr.Zero;
                if (m == WM_POINTERDOWN && primary && !contact && !focused)
                {
                    // First tap after the player left only returns to the game; it does not click.
                    SetForegroundWindow(source); focused = true; Track(); return IntPtr.Zero;
                }
                if (m == WM_POINTERDOWN && primary && !contact) { contact = true; contactId = id; Replay(l, WM_LBUTTONDOWN); }
                else if (contact && id == contactId && m == WM_POINTERUPDATE) Replay(l, WM_MOUSEMOVE);
                else if (contact && id == contactId && m == WM_POINTERUP) { contact = false; Replay(l, WM_LBUTTONUP); }
                return IntPtr.Zero;  // handled: no mouse promotion, no activation
            }
            case WM_CLOSE: DestroyWindow(h); return IntPtr.Zero;
            case WM_DESTROY: PostQuitMessage(0); return IntPtr.Zero;
        }
        return DefWindowProc(h, m, w, l);
    }
}
