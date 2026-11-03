using System.Runtime.InteropServices;
using Windows.Gaming.UI;

namespace ClawSleepAnimation;

// Game Bar uses hosted surfaces that do not appear as ordinary desktop windows.
// Its live input-redirection API decides whether Escape belongs to Game Bar.
// No protocol launch, HWND enumeration, application activation or Win+G toggle.
internal static class GameBarDismiss
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint Flags;
    }
    internal sealed record Inspection(bool CanDismiss, string Reason, bool InputRedirected, bool SystemVisible);
    internal static Inspection Inspect()
    {
        try
        {
            bool redirected = GameBar.IsInputRedirected;
            return new(redirected, redirected ? "gamebar-owns-input" : "gamebar-does-not-own-input", redirected, GameBar.Visible);
        }
        catch (Exception ex) { return new(false, ex.GetType().Name, false, false); }
    }
    private static bool OwnsInput()
    {
        try { return GameBar.IsInputRedirected; }
        catch { return false; }
    }
    public static async Task PrepareAsync(string direction)
    {
        try
        {
            Program.Log("gamebar-dismiss-inspect", new { direction, inspection = Inspect() });
            var (presses, remaining) = await GameBarDismissGate.RunAsync(OwnsInput,
                () => new[] { 0x5b, 0x5c, 0x11, 0x12, 0x1b }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0), () => {
                uint sent = SendInput(2, [new() { Type = 1, Key = 0x1b }, new() { Type = 1, Key = 0x1b, Flags = 2 }], Marshal.SizeOf<Input>());
                if (sent == 1) SendInput(1, [new() { Type = 1, Key = 0x1b, Flags = 2 }], Marshal.SizeOf<Input>());
                return sent == 2;
            }, milliseconds => Task.Delay(milliseconds));
            Program.Log("gamebar-dismiss", new { direction, presses, remaining, method = "input-owner-escape" });
            if (!remaining && presses > 0) await Task.Delay(120);
        }
        catch (Exception ex) { Program.Log("gamebar-dismiss-error", ex.Message); }
    }
}
