namespace ClawLibrary.Engine;

internal readonly record struct TouchSurface(int Page, int Detail = 0, int Version = 0, string? Item = null);
internal enum TouchResult { None, Tap, Horizontal, Vertical, Canceled }

/// <summary>A release belongs to one pointer and one unchanged page. Movement intent locks once.</summary>
internal sealed class TouchPointerGesture
{
    private uint? _owner;
    private TouchSurface _surface;
    private TouchResult _intent;
    public bool Active => _owner != null;
    public double StartX { get; private set; }
    public double StartY { get; private set; }
    public bool Owns(uint id) => _owner == id;
    public bool Matches(TouchSurface surface) => Active && _surface == surface;

    public bool Begin(uint id, double x, double y, TouchSurface surface)
    {
        if (Active) return false;
        _owner = id; _surface = surface; StartX = x; StartY = y; _intent = TouchResult.Tap;
        return true;
    }

    public TouchResult Move(uint id, double x, double y, TouchSurface surface)
    {
        if (!Owns(id)) return TouchResult.None;
        if (_surface != surface) _intent = TouchResult.Canceled;
        if (_intent == TouchResult.Tap && Math.Max(Math.Abs(x - StartX), Math.Abs(y - StartY)) >= 8)
            _intent = Math.Abs(x - StartX) > Math.Abs(y - StartY) ? TouchResult.Horizontal : TouchResult.Vertical;
        return _intent;
    }

    public TouchResult End(uint id, double x, double y, TouchSurface surface)
    {
        if (!Owns(id)) return TouchResult.None;
        var result = Move(id, x, y, surface);
        _owner = null;
        return result;
    }

    public void Cancel(uint id) { if (Owns(id)) Reset(); }
    public void Reset() { _owner = null; _intent = TouchResult.None; }
}
