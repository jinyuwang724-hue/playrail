using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClawSleepAnimation;

// Brief, same-desktop keyboard hook. There is no hook while idle or during the
// login loading loop, and no ordinary typing is recorded or logged.
internal sealed class TransitionShortcutFilter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Stroke { public uint Key, Scan, Flags, Time; public nuint Extra; }
    private delegate nint Hook(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private readonly TransitionShortcutState state;
    private readonly Hook callback;
    private nint hook;
    private TransitionShortcutFilter()
    {
        int down = ((GetAsyncKeyState(0x5b) & 0x8000) != 0 ? 1 : 0) | ((GetAsyncKeyState(0x5c) & 0x8000) != 0 ? 2 : 0);
        state = new TransitionShortcutState(down);
        callback = OnKey;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Program.Log("transition-shortcut-filter-start");
    }
    public static TransitionShortcutFilter? TryCreate()
    {
        try { return new TransitionShortcutFilter(); }
        catch (Win32Exception ex) { Program.Log("transition-shortcut-filter-unavailable", ex.NativeErrorCode); return null; }
    }
    private nint OnKey(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<Stroke>(data);
            if (state.Key(key.Key, (key.Flags & 0x80) != 0)) return 1;
        }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose()
    {
        if (hook == 0) return;
        UnhookWindowsHookEx(hook); hook = 0; state.End();
        Program.Log("transition-shortcut-filter-end", new { blockedWindowsPresses = state.BlockedWindowsPresses });
    }
}
