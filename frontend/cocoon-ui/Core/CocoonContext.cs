using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Windows.UI;

namespace CocoonUI.Core;

/// <summary>Shared WinUI Composition + Win2D rendering context injected into CocoonUI components.</summary>
public sealed class CocoonContext : IDisposable
{
    public Compositor C { get; }
    public CanvasDevice Device { get; private set; }
    public CompositionGraphicsDevice GraphicsDevice { get; }
    public CocoonTypography Typography { get; }
    public float RasterScale { get; set; } = 1f;
    public bool ReducedMotion { get; set; }
    public Action<CocoonCue, float, float>? OnCue { get; set; }

    public event Action? DeviceReplaced;

    public static readonly CanvasStrokeStyle RoundStroke = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    private readonly Dictionary<CocoonBezier, CompositionEasingFunction> _easings = new();
    private CompositionEasingFunction? _linear;
    private bool _disposed;

    public CocoonContext(
        Compositor compositor,
        CocoonFontConfig? fontConfig = null,
        float rasterScale = 1f,
        bool reducedMotion = false,
        Action<CocoonCue, float, float>? onCue = null)
    {
        C = compositor;
        Device = CanvasDevice.GetSharedDevice();
        GraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(C, Device);
        Device.DeviceLost += OnDeviceLost;
        Typography = new CocoonTypography(fontConfig);
        RasterScale = rasterScale;
        ReducedMotion = reducedMotion;
        OnCue = onCue;
    }

    private void OnDeviceLost(CanvasDevice sender, object args)
    {
        if (_disposed) return;
        try
        {
            Device.DeviceLost -= OnDeviceLost;
            Device = CanvasDevice.GetSharedDevice();
            Device.DeviceLost += OnDeviceLost;
            CanvasComposition.SetCanvasDevice(GraphicsDevice, Device);
            DeviceReplaced?.Invoke();
        }
        catch
        {
            // Host may recover on next frame if device creation temporarily fails.
        }
    }

    public static TimeSpan Ms(double ms) =>
        TimeSpan.FromTicks((long)Math.Round(Math.Max(0, ms) * TimeSpan.TicksPerMillisecond));

    public CompositionEasingFunction Linear => _linear ??= C.CreateLinearEasingFunction();

    public CompositionEasingFunction Ease(CocoonBezier b)
    {
        if (!_easings.TryGetValue(b, out var e))
            _easings[b] = e = C.CreateCubicBezierEasingFunction(new Vector2(b.X1, b.Y1), new Vector2(b.X2, b.Y2));
        return e;
    }

    public CompositionDrawingSurface Surface(int pxW, int pxH) =>
        GraphicsDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(Math.Max(1, pxW), Math.Max(1, pxH)),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            DirectXAlphaMode.Premultiplied);

    public CompositionDrawingSurface EnsureSurface(
        ref CompositionDrawingSurface? surface,
        SpriteVisual visual,
        float x,
        float y,
        float w,
        float h)
    {
        int pw = Math.Max(1, (int)Math.Ceiling(w * RasterScale));
        int ph = Math.Max(1, (int)Math.Ceiling(h * RasterScale));
        if (surface == null || (int)surface.Size.Width != pw || (int)surface.Size.Height != ph)
        {
            surface?.Dispose();
            surface = Surface(pw, ph);
            if (visual.Brush is CompositionSurfaceBrush sb) sb.Surface = surface;
            else visual.Brush = C.CreateSurfaceBrush(surface);
        }
        visual.Size = new Vector2(w, h);
        visual.Offset = new Vector3(x, y, 0);
        return surface;
    }

    public SpriteVisual Sprite(CompositionBrush? brush = null, bool relative = false)
    {
        var s = C.CreateSpriteVisual();
        s.Brush = brush;
        if (relative) s.RelativeSizeAdjustment = Vector2.One;
        return s;
    }

    public ContainerVisual Container(bool relative = false)
    {
        var v = C.CreateContainerVisual();
        if (relative) v.RelativeSizeAdjustment = Vector2.One;
        return v;
    }

    public CompositionColorBrush Solid(Color c) => C.CreateColorBrush(c);

    public ScalarKeyFrameAnimation Scalar(float to, double ms, CocoonBezier ease, double delay = 0)
    {
        var a = C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, to, Ease(ease));
        a.Duration = Ms(Math.Max(1, ms));
        if (delay > 0) a.DelayTime = Ms(delay);
        return a;
    }

    public Vector3KeyFrameAnimation Vector3Anim(Vector3 to, double ms, CocoonBezier ease, double delay = 0)
    {
        var a = C.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(1f, to, Ease(ease));
        a.Duration = Ms(Math.Max(1, ms));
        if (delay > 0) a.DelayTime = Ms(delay);
        return a;
    }

    public void Cue(CocoonCue cue, float pan = 0f, float pitch = 1f) => OnCue?.Invoke(cue, pan, pitch);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Device.DeviceLost -= OnDeviceLost;
        GraphicsDevice.Dispose();
    }
}
