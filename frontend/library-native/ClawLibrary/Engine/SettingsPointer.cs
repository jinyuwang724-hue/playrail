namespace ClawLibrary.Engine;

internal enum SettingsPointerMode { None, Pending, Scroll, Slider }
internal enum SettingsTargetKind { Row, Choice, Decrease, Increase, Slider, Category, Item, Tab, Cell, Action, Secondary }

internal readonly record struct SettingsPointerTarget(int Row, SettingsTargetKind Kind, double X, double Y, double Width, double Height, int Choice = -1)
{
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>点按在抬起时生效；越过阈值后锁定滑动方向，取消和其他手指都不能触发点按。</summary>
internal sealed class SettingsPointerGesture
{
    private uint _id;
    private bool _slider;
    public SettingsPointerMode Mode { get; private set; }
    public bool Active => Mode != SettingsPointerMode.None;
    public double StartX { get; private set; }
    public double StartY { get; private set; }

    public bool Begin(uint id, double x, double y, bool slider)
    {
        if (Active) return false;
        _id = id; StartX = x; StartY = y; _slider = slider;
        Mode = SettingsPointerMode.Pending;
        return true;
    }

    public bool Owns(uint id) => Active && _id == id;

    public SettingsPointerMode Move(uint id, double x, double y)
    {
        if (!Owns(id)) return SettingsPointerMode.None;
        if (Mode == SettingsPointerMode.Pending && Math.Max(Math.Abs(x - StartX), Math.Abs(y - StartY)) >= 8)
            Mode = _slider && Math.Abs(x - StartX) > Math.Abs(y - StartY) ? SettingsPointerMode.Slider : SettingsPointerMode.Scroll;
        return Mode;
    }

    public SettingsPointerMode End(uint id)
    {
        if (!Owns(id)) return SettingsPointerMode.None;
        var result = Mode; Mode = SettingsPointerMode.None;
        return result;
    }

    public void Cancel(uint id) { if (Owns(id)) Mode = SettingsPointerMode.None; }
    public void Reset() => Mode = SettingsPointerMode.None;

    public static int SliderValue(double x, double left, double width, int min, int max, int step)
    {
        double value = min + Math.Clamp((x - left) / Math.Max(1, width), 0, 1) * (max - min);
        return Math.Clamp(min + (int)Math.Round((value - min) / Math.Max(1, step), MidpointRounding.AwayFromZero) * Math.Max(1, step), min, max);
    }

    public static float Scroll(double value, float height, float viewport) => (float)Math.Clamp(value, 0, Math.Max(0, height - viewport));
}
