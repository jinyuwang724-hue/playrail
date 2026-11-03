namespace ClawLibrary.Engine;

internal enum ShortcutEntrance { None, KeepCurrent, Reveal, Reverse }
internal enum ShortcutMotionDirection { None, Enter, Leave }

// Animation ownership is established before Show/Activate can dispatch old callbacks.
// Covered windows only change foreground; hidden/minimized windows retain their reveal.
internal sealed class ShortcutMotionState
{
    public int Version { get; private set; }
    public ShortcutMotionDirection Direction { get; private set; }

    public ShortcutEntrance Show(bool initial, bool windowVisible, bool foreground)
    {
        if (Direction == ShortcutMotionDirection.Enter && windowVisible && foreground) return ShortcutEntrance.KeepCurrent;
        bool reverse = Direction == ShortcutMotionDirection.Leave;
        ++Version;
        var mode = reverse ? ShortcutEntrance.Reverse : initial || !windowVisible ? ShortcutEntrance.Reveal : ShortcutEntrance.None;
        Direction = mode == ShortcutEntrance.None ? ShortcutMotionDirection.None : ShortcutMotionDirection.Enter;
        return mode;
    }

    public int Hide() { ++Version; Direction = ShortcutMotionDirection.Leave; return Version; }
    public void Cancel() { ++Version; Direction = ShortcutMotionDirection.None; }
    public bool IsCurrent(int version) => version == Version && Direction != ShortcutMotionDirection.None;
    public bool Complete(int version)
    {
        if (!IsCurrent(version)) return false;
        Direction = ShortcutMotionDirection.None;
        return true;
    }
}
