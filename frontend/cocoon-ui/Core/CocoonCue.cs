namespace CocoonUI.Core;

/// <summary>Abstract UI sound feedback cues reported to host.</summary>
public enum CocoonCue
{
    None,
    Nav,
    Positive,
    MenuIn,
    MenuOut,
    Back,
    SliderUp,
    SliderDown,
    Bump,
}

/// <summary>Cardinal focus navigation directions.</summary>
public enum CocoonDirection
{
    Up,
    Down,
    Left,
    Right,
}

/// <summary>Cubic bezier easing curve parameters matching Cocoon Design System tokens.</summary>
public readonly record struct CocoonBezier(float X1, float Y1, float X2, float Y2)
{
    public static readonly CocoonBezier EaseOut = new(0f, 0f, 0.2f, 1f);
    public static readonly CocoonBezier SlideOut = new(0.22f, 1f, 0.36f, 1f);
    public static readonly CocoonBezier DrawerIn = new(0.2f, 0.8f, 0.2f, 1f);
    public static readonly CocoonBezier DrawerOut = new(0.4f, 0f, 0.6f, 1f);
    public static readonly CocoonBezier StretchPeak = new(0.3f, 0f, 0.4f, 1f);
    public static readonly CocoonBezier InertiaDecel = new(0.15f, 0.7f, 0.25f, 1f);
}
