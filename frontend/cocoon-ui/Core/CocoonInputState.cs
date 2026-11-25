using CocoonUI.Tokens;

namespace CocoonUI.Core;

/// <summary>Dual-mode Gamepad ↔ Touch interaction state, overscroll rubber-banding, and inertia physics.</summary>
public sealed class CocoonInputState
{
    public const float DragThresholdPx = 18f;
    public const float DefaultEdgeBackGuardPx = 24f;

    public bool TouchMode { get; private set; }
    public bool ManualScroll { get; set; }
    public float Scroll { get; set; }
    public float MaxScroll { get; set; }
    public float EdgeBackGuardPx { get; init; } = DefaultEdgeBackGuardPx;

    public event Action<bool>? TouchModeChanged;

    public bool SetTouchMode(bool on)
    {
        if (TouchMode == on) return false;
        TouchMode = on;
        TouchModeChanged?.Invoke(on);
        return true;
    }

    public bool IsEdgeBackTouch(float xPx, bool isTouch) => isTouch && xPx <= EdgeBackGuardPx;

    public static float ApplyOverscroll(float rawScroll, float maxScroll)
    {
        float res = (float)CocoonTokens.Motion.OverscrollResistance;
        if (rawScroll < 0f) return rawScroll * res;
        if (rawScroll > maxScroll) return maxScroll + (rawScroll - maxScroll) * res;
        return rawScroll;
    }

    public static float UpdateVelocityEma(float prevVelocity, float deltaY, float deltaSec)
    {
        float dt = MathF.Max(0.001f, deltaSec);
        return prevVelocity * 0.4f + (deltaY / dt) * 0.6f;
    }

    public static (float Target, double DurationMs) ComputeInertia(float scroll, float velocity, float maxScroll)
    {
        float target = Math.Clamp(scroll - velocity * 0.32f, 0f, MathF.Max(0f, maxScroll));
        double duration = Math.Clamp(MathF.Abs(velocity) * 0.5, 220.0, 650.0);
        return (target, duration);
    }
}
