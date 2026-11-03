namespace ClawSleepAnimation;

// Only lives for a short power animation. Consuming the modifier together with
// its chord prevents a suppressed Win+G from opening Start on modifier release.
internal sealed class TransitionShortcutState
{
    private int windows, inherited, swallowedWindows;
    private readonly HashSet<uint> swallowedKeys = new();
    private bool ended;
    public int BlockedWindowsPresses { get; private set; }
    public TransitionShortcutState(int initiallyDown = 0) { windows = inherited = initiallyDown & 3; }
    public bool Key(uint key, bool up)
    {
        if (ended) return false;
        if (key is 0x5b or 0x5c)
        {
            int bit = key == 0x5b ? 1 : 2;
            if (up)
            {
                windows &= ~bit; inherited &= ~bit;
                bool swallow = (swallowedWindows & bit) != 0;
                swallowedWindows &= ~bit; return swallow;
            }
            bool fresh = (windows & bit) == 0;
            windows |= bit;
            // A modifier delivered before the animation must still be released
            // normally; never leave another application's Win state held down.
            if ((inherited & bit) != 0) return false;
            swallowedWindows |= bit;
            if (fresh) BlockedWindowsPresses++;
            return true;
        }
        if (up) return swallowedKeys.Remove(key);
        if (windows == 0) return false;
        swallowedKeys.Add(key); return true;
    }
    public void End() { ended = true; windows = inherited = swallowedWindows = 0; swallowedKeys.Clear(); }
}
