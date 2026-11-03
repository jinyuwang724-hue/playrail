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
    private uint _returnPid;
    private static readonly string[] Routes = ["library", "control-center", "restore-library", "restore-control-center", "minimize-library", "minimize-control-center"];

    public LibraryWindow(nint window, Action<string, nint> activate)
    {
        _window = window; _activate = activate; _callback = HandleMessage;
        _message = RegisterWindowMessage("ClawToolbox.Library.Activation.v1");
        if (_message == 0 || !SetWindowSubclass(window, _callback, 82, 0))
            throw new InvalidOperationException("无法注册游戏库唤出消息");
        SetProp(window, "ClawToolbox.Library.Activation.v1", 1);
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wp, nint lp, nuint id, nuint data)
    {
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

    public void SetTopmost(bool on)
    {
        bool current = (GetWindowLongPtr(_window, -20).ToInt64() & 0x8) != 0;
        if (current != on) SetWindowPos(_window, on ? -1 : -2, 0, 0, 0, 0, 0x13);
    }

    public static bool BringForward(nint window)
    {
        if (window == 0 || !IsWindow(window)) return false;
        if (ToolboxWindow.IsIconic(window)) ShowWindow(window, 9);
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

    public void ReturnToPreviousWindow(bool returnFocus)
    {
        GetWindowThreadProcessId(_returnWindow, out uint pid);
        if (returnFocus && pid == _returnPid && IsWindow(_returnWindow) && IsWindowVisible(_returnWindow))
            Log.Info($"shortcut returned hwnd={_returnWindow} foreground={BringForward(_returnWindow)}");
    }

    public void Dispose() { SetTopmost(false); RemoveProp(_window, "ClawToolbox.Library.Activation.v1"); RemoveWindowSubclass(_window, _callback, 82); }

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
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
}
