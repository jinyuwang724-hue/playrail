namespace ClawSleepAnimation;

// Game Bar must still own input immediately before each bounded Escape pair.
internal static class GameBarDismissGate
{
    internal static async Task<(int Presses, bool Remaining)> RunAsync(Func<bool> visible, Func<bool> modifiersHeld,
        Func<bool> escape, Func<int, Task> delay)
    {
        int presses = 0;
        await delay(40);
        for (int attempt = 0; attempt < 3 && visible(); attempt++)
        {
            if (modifiersHeld()) { await delay(60); continue; }
            if (!visible()) break;
            if (!escape()) break;
            presses++;
            for (int wait = 0; wait < 8 && visible(); wait++) await delay(25);
        }
        return (presses, visible());
    }
}
