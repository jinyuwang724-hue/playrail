namespace ClawLibrary.Input;

internal static class GamepadSourceChoice
{
    // A connected but neutral old XInput slot must not hide a newly created pad.
    // Keep the active source when raw/virtual copies both report the same press.
    public static int Select(int connected, int active, int previous)
    {
        active &= connected;
        if (previous >= 0 && (active & (1 << previous)) != 0) return previous;
        for (int i = 0; i < 4; i++) if ((active & (1 << i)) != 0) return i;
        if (previous >= 0 && (connected & (1 << previous)) != 0) return previous;
        for (int i = 0; i < 4; i++) if ((connected & (1 << i)) != 0) return i;
        return -1;
    }
}
