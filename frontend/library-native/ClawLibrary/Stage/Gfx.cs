// 合成层的公用件：Compositor、Win2D 设备、颜色、缓动、关键帧动画的小工具。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.Graphics.DirectX;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed class Gfx
{
    public readonly Compositor C;
    public CanvasDevice Device { get; private set; }
    public CompositionGraphicsDevice GraphicsDevice { get; private set; }
    /// <summary>世界 px → 物理像素（相机缩放 × 系统缩放），文字与图片按它栅格化。</summary>
    public float RasterScale = 1;
    public event Action? DeviceReplaced;

    private readonly Dictionary<Bezier, CompositionEasingFunction> _easings = new();
    private CompositionEasingFunction? _linear;

    public Gfx(Compositor compositor)
    {
        C = compositor;
        Device = CanvasDevice.GetSharedDevice();
        GraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(C, Device);
        Device.DeviceLost += OnDeviceLost;
    }

    private void OnDeviceLost(CanvasDevice sender, object args)
    {
        Log.Warn("canvas device lost; recreating");
        try
        {
            Device.DeviceLost -= OnDeviceLost;
            Device = CanvasDevice.GetSharedDevice();
            Device.DeviceLost += OnDeviceLost;
            CanvasComposition.SetCanvasDevice(GraphicsDevice, Device);
            DeviceReplaced?.Invoke();
        }
        catch (Exception ex) { Log.Error("device recreate", ex); }
    }

    public CompositionDrawingSurface Surface(int pxW, int pxH) =>
        GraphicsDevice.CreateDrawingSurface(new Windows.Foundation.Size(Math.Max(1, pxW), Math.Max(1, pxH)),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);

    public CompositionEasingFunction Ease(Bezier b)
    {
        if (!_easings.TryGetValue(b, out var e))
            _easings[b] = e = C.CreateCubicBezierEasingFunction(new Vector2(b.X1, b.Y1), new Vector2(b.X2, b.Y2));
        return e;
    }

    public CompositionEasingFunction Linear => _linear ??= C.CreateLinearEasingFunction();

    public static TimeSpan Ms(double ms) => TimeSpan.FromTicks((long)Math.Round(Math.Max(0, ms) * TimeSpan.TicksPerMillisecond));

    // ---------- 颜色 ----------

    public static Color Rgba(int r, int g, int b, double a = 1) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), (byte)r, (byte)g, (byte)b);

    public static Color Hex(string? hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length != 7) return fallback;
        try { return Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)); }
        catch { return fallback; }
    }

    /// <summary>color-mix(in srgb, a p, b)：按 p 混合（不透明色）。</summary>
    public static Color Mix(Color a, double p, Color b) => Color.FromArgb(
        (byte)Math.Round(a.A * p + b.A * (1 - p)),
        (byte)Math.Round(a.R * p + b.R * (1 - p)),
        (byte)Math.Round(a.G * p + b.G * (1 - p)),
        (byte)Math.Round(a.B * p + b.B * (1 - p)));

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    public static Color Hsl(double h, double s, double l, double a = 1)
    {
        h = ((h % 360) + 360) % 360 / 360; s /= 100; l /= 100;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Hue(double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return Rgba((int)Math.Round(Hue(h + 1.0 / 3) * 255), (int)Math.Round(Hue(h) * 255), (int)Math.Round(Hue(h - 1.0 / 3) * 255), a);
    }

    // ---------- 画刷 ----------

    public CompositionColorBrush Solid(Color c) => C.CreateColorBrush(c);

    /// <summary>CSS linear-gradient(角度, stops)：按盒子宽高比算起止点（相对坐标）。</summary>
    public CompositionLinearGradientBrush LinearGradient(double cssAngleDeg, double boxW, double boxH, params (double offset, Color color)[] stops)
    {
        var b = C.CreateLinearGradientBrush();
        SetLinear(b, cssAngleDeg, boxW, boxH);
        foreach (var (o, col) in stops) b.ColorStops.Add(C.CreateColorGradientStop((float)o, col));
        return b;
    }

    public static void SetLinear(CompositionLinearGradientBrush b, double cssAngleDeg, double boxW, double boxH)
    {
        double a = cssAngleDeg * Math.PI / 180;
        double dx = Math.Sin(a), dy = -Math.Cos(a);
        double len = Math.Abs(boxW * dx) + Math.Abs(boxH * dy);
        double hx = dx * len / 2 / Math.Max(1, boxW), hy = dy * len / 2 / Math.Max(1, boxH);
        b.MappingMode = CompositionMappingMode.Relative;
        b.StartPoint = new Vector2((float)(0.5 - hx), (float)(0.5 - hy));
        b.EndPoint = new Vector2((float)(0.5 + hx), (float)(0.5 + hy));
    }

    // ---------- 视觉 ----------

    public SpriteVisual Sprite(CompositionBrush? brush = null, bool relative = true)
    {
        var s = C.CreateSpriteVisual();
        s.Brush = brush;
        if (relative) s.RelativeSizeAdjustment = Vector2.One;
        return s;
    }

    public ContainerVisual Container(bool relative = true)
    {
        var v = C.CreateContainerVisual();
        if (relative) v.RelativeSizeAdjustment = Vector2.One;
        return v;
    }

    // ---------- 动画 ----------

    public ScalarKeyFrameAnimation Scalar(double from, double to, double ms, Bezier ease, double delay = 0, bool holdBeforeDelay = true)
    {
        var a = C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, (float)from, Linear);
        a.InsertKeyFrame(1f, (float)to, Ease(ease));
        a.Duration = Ms(Math.Max(1, ms));
        if (delay > 0) a.DelayTime = Ms(delay);
        a.DelayBehavior = holdBeforeDelay ? AnimationDelayBehavior.SetInitialValueBeforeDelay : AnimationDelayBehavior.SetInitialValueAfterDelay;
        return a;
    }

    /// <summary>把 0..1 的曲线采样成关键帧（让位弹簧用；每段线性）。</summary>
    public Vector2KeyFrameAnimation SampledV2(Vector2 from, Vector2 to, double ms, Func<double, double> curve, int frames = 48)
    {
        var a = C.CreateVector2KeyFrameAnimation();
        for (int i = 0; i <= frames; i++)
        {
            float t = i / (float)frames;
            a.InsertKeyFrame(t, Vector2.Lerp(from, to, (float)curve(t)), Linear);
        }
        a.Duration = Ms(ms);
        return a;
    }

    public Vector3KeyFrameAnimation SampledV3(Vector3 from, Vector3 to, double ms, Func<double, double> curve, int frames = 48)
    {
        var a = C.CreateVector3KeyFrameAnimation();
        for (int i = 0; i <= frames; i++)
        {
            float t = i / (float)frames;
            a.InsertKeyFrame(t, Vector3.Lerp(from, to, (float)curve(t)), Linear);
        }
        a.Duration = Ms(ms);
        return a;
    }
}
