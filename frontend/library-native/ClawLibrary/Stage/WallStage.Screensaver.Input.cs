using Windows.System;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // 唤醒之后要等手柄回到中立，避免同一帧的方向 + A、按住的摇杆继续触发动作。
    public bool GamepadInputBlocked { get; private set; }
    private VirtualKey? _screensaverWakeKey;
    private readonly HashSet<uint> _screensaverWakePointers = new();
    private (double x, double y)? _lastPointerPosition;

    public void GamepadActivity(bool active)
    {
        if (!active) { GamepadInputBlocked = false; return; }
        if (ConsumeScreensaverWake()) GamepadInputBlocked = true;
        MarkInput();
    }
}
