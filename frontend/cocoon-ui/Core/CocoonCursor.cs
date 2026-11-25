using System.Numerics;
using CocoonUI.Tokens;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace CocoonUI.Core;

/// <summary>Concentric 4-corner bracket focus cursor with blurred glow, 40% white rim, and 1.8 s perimeter flow.</summary>
public sealed class CocoonCursor : IDisposable
{
    private const float CurPad = 44f;
    private const float CurGlowBlurDp = 6.5f;

    private readonly CocoonContext _ctx;
    private readonly SpriteVisual _glow, _line, _line2, _rim;
    private readonly Visual[] _all;
    private readonly Dictionary<string, CachedCursor> _cache = new();

    private float _w, _h, _r;
    private bool _disposed;

    public bool Shown { get; private set; }
    public IReadOnlyList<Visual> Visuals => _all;

    private readonly record struct CachedCursor(
        CompositionDrawingSurface Glow,
        CompositionDrawingSurface Line,
        CompositionDrawingSurface Rim,
        float Raster,
        CompositionMaskBrush L1,
        CompositionMaskBrush L2,
        CompositionLinearGradientBrush G1,
        CompositionLinearGradientBrush G2);

    private CocoonCursor(CocoonContext ctx, SpriteVisual glow, SpriteVisual line, SpriteVisual line2, SpriteVisual rim)
    {
        _ctx = ctx;
        _glow = glow;
        _line = line;
        _line2 = line2;
        _rim = rim;
        _all = [glow, line, line2, rim];
        _ctx.DeviceReplaced += ClearCache;
    }

    public static CocoonCursor Create(CocoonContext ctx, ContainerVisual parent)
    {
        SpriteVisual Sp() => ctx.Sprite(ctx.C.CreateSurfaceBrush(), relative: false);
        var cur = new CocoonCursor(ctx, Sp(), Sp(), Sp(), Sp());
        foreach (var v in cur._all)
        {
            v.Opacity = 0f;
            parent.Children.InsertAtTop(v);
        }
        return cur;
    }

    public void Show(bool on, double ms = 140, double delayMs = 0)
    {
        Shown = on;
        double dur = _ctx.ReducedMotion ? 1 : Math.Max(1, ms);
        foreach (var v in _all)
        {
            v.StopAnimation("Opacity");
            v.StartAnimation("Opacity", _ctx.Scalar(on ? 1f : 0f, dur, CocoonBezier.EaseOut, _ctx.ReducedMotion ? 0 : delayMs));
        }
    }

    public void ResetHidden()
    {
        Shown = false;
        foreach (var v in _all)
        {
            v.StopAnimation("Opacity");
            v.Opacity = 0f;
        }
    }

    public void MoveToConcentric(float x, float y, float w, float h, float targetRadius, bool animate = true)
    {
        var (outset, radius) = CocoonBracketMath.ConcentricCompact(w, h, targetRadius);
        MoveTo(x, y, w, h, outset, radius, animate);
    }

    public void MoveTo(float x, float y, float w, float h, float outset, float radius, bool animate = true)
    {
        float W = w + outset * 2f;
        float H = h + outset * 2f;
        float sw = W + CurPad * 2f;
        float sh = H + CurPad * 2f;
        var to = new Vector3(x + w / 2f - sw / 2f, y + h / 2f - sh / 2f, 0f);

        bool resize = MathF.Abs(_w - W) > 0.5f || MathF.Abs(_h - H) > 0.5f || MathF.Abs(_r - radius) > 0.5f;
        if (resize)
        {
            float ow = _w, oh = _h;
            var oldCenter = new Vector3(_glow.Offset.X + (ow + CurPad * 2f) / 2f, _glow.Offset.Y + (oh + CurPad * 2f) / 2f, 0f);
            Paint(W, H, radius);
            foreach (var v in _all)
            {
                v.StopAnimation("Offset");
                v.Offset = ow > 0 ? oldCenter - new Vector3(sw / 2f, sh / 2f, 0f) : to;
                if (animate && !_ctx.ReducedMotion && ow > 0)
                {
                    v.Scale = new Vector3((ow + CurPad * 2f) / sw, (oh + CurPad * 2f) / sh, 1f);
                    v.StartAnimation("Scale", CocoonSpring.CreateVector3(_ctx, Vector3.One));
                }
                else
                {
                    v.StopAnimation("Scale");
                    v.Scale = Vector3.One;
                }
            }
        }

        float dx = to.X - _glow.Offset.X;
        float dy = to.Y - _glow.Offset.Y;
        foreach (var v in _all)
        {
            if (animate && !_ctx.ReducedMotion) v.StartAnimation("Offset", CocoonSpring.CreateVector3(_ctx, to));
            else CocoonSpring.Jump(_ctx, v, to);

            if (animate && !_ctx.ReducedMotion && !resize) CocoonSpring.Stretch(_ctx, v, dx, dy, W, H);
        }
    }

    private void Paint(float W, float H, float r)
    {
        _w = W;
        _h = H;
        _r = r;
        float R = _ctx.RasterScale;
        float sw = W + CurPad * 2f;
        float sh = H + CurPad * 2f;
        string key = $"{W:0.#}x{H:0.#}r{r:0.#}";

        if (!_cache.TryGetValue(key, out var c) || MathF.Abs(c.Raster - R) > 0.001f)
        {
            if (_cache.Remove(key, out var stale)) DisposeEntry(stale);

            int pw = Math.Max(1, (int)Math.Ceiling(sw * R));
            int ph = Math.Max(1, (int)Math.Ceiling(sh * R));
            var dev = _ctx.Device;
            var metrics = CocoonBracketMath.ComputeMetrics(W, H, r);
            using var path = BuildGeometry(dev, metrics, CurPad, CurPad);

            var glow = _ctx.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(glow))
            {
                ds.Clear(Colors.Transparent);
                using var cl = new CanvasCommandList(ds);
                using (var s = cl.CreateDrawingSession())
                {
                    s.Transform = Matrix3x2.CreateScale(R);
                    s.DrawGeometry(path, CocoonTokens.Colors.BrandAccentStart, metrics.StrokeWidth * 2f, CocoonContext.RoundStroke);
                }
                using var blur = new GaussianBlurEffect
                {
                    Source = cl,
                    BlurAmount = CurGlowBlurDp * CocoonTokens.Dp * R / 2f,
                    BorderMode = EffectBorderMode.Soft,
                };
                ds.DrawImage(blur);
            }

            var rim = _ctx.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(rim))
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateScale(R);
                float expand = CocoonTokens.Focus.RimStrokeExpandDp * CocoonTokens.Dp;
                using var outer = path.Stroke(metrics.StrokeWidth + expand, CocoonContext.RoundStroke);
                using var inner = path.Stroke(MathF.Max(0.1f, metrics.StrokeWidth - expand), CocoonContext.RoundStroke);
                using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                ds.FillGeometry(ring, CocoonTokens.Colors.ScrimFocusRim);
            }

            var line = _ctx.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(line))
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateScale(R);
                ds.DrawGeometry(path, Colors.White, metrics.StrokeWidth, CocoonContext.RoundStroke);
            }

            var (l1, g1) = CreatePerimeterFlow(line, W, H, r, right: false);
            var (l2, g2) = CreatePerimeterFlow(line, W, H, r, right: true);
            c = new CachedCursor(glow, line, rim, R, l1, l2, g1, g2);
            _cache[key] = c;
        }

        ((CompositionSurfaceBrush)_glow.Brush).Surface = c.Glow;
        ((CompositionSurfaceBrush)_rim.Brush).Surface = c.Rim;
        _line.Brush = c.L1;
        _line2.Brush = c.L2;
        _line.Clip = _ctx.C.CreateInsetClip(0f, 0f, sw / 2f, 0f);
        _line2.Clip = _ctx.C.CreateInsetClip(sw / 2f, 0f, 0f, 0f);
        foreach (var v in _all)
        {
            v.Size = new Vector2(sw, sh);
            v.CenterPoint = new Vector3(sw / 2f, sh / 2f, 0f);
        }
    }

    private static CanvasGeometry BuildGeometry(ICanvasResourceCreator dev, BracketMetrics m, float padX, float padY)
    {
        using var pb = new CanvasPathBuilder(dev);
        switch (m.Kind)
        {
            case BracketShapeKind.Circle:
            {
                float rc = m.Radius;
                float cx = padX + m.BoxW / 2f;
                float cy = padY + m.BoxH / 2f;
                float half = (MathF.PI / 2f - m.Gap) / 2f;
                for (int k = 0; k < 4; k++)
                {
                    float mid = MathF.PI / 4f + k * MathF.PI / 2f;
                    float a0 = mid - half;
                    float a1 = mid + half;
                    pb.BeginFigure(cx + rc * MathF.Cos(a0), cy + rc * MathF.Sin(a0));
                    pb.AddArc(new Vector2(cx + rc * MathF.Cos(a1), cy + rc * MathF.Sin(a1)), rc, rc, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                    pb.EndFigure(CanvasFigureLoop.Open);
                }
                break;
            }
            case BracketShapeKind.Capsule:
            {
                float rp = m.Radius;
                float cy = padY + rp;
                float lx = padX + rp;
                float rx = padX + m.BoxW - rp;
                float halfGap = MathF.Asin(Math.Clamp((m.Gap / 2f) / rp, 0.08f, 0.45f));
                float legH = m.LegX;

                pb.BeginFigure(lx + rp * MathF.Cos(MathF.PI + halfGap), cy + rp * MathF.Sin(MathF.PI + halfGap));
                pb.AddArc(new Vector2(lx, padY), rp, rp, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                if (legH > 0.5f) pb.AddLine(lx + legH, padY);
                pb.EndFigure(CanvasFigureLoop.Open);

                if (legH > 0.5f) { pb.BeginFigure(rx - legH, padY); pb.AddLine(rx, padY); }
                else pb.BeginFigure(rx, padY);
                pb.AddArc(new Vector2(rx + rp * MathF.Cos(-halfGap), cy + rp * MathF.Sin(-halfGap)), rp, rp, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                pb.EndFigure(CanvasFigureLoop.Open);

                pb.BeginFigure(rx + rp * MathF.Cos(halfGap), cy + rp * MathF.Sin(halfGap));
                pb.AddArc(new Vector2(rx, padY + m.BoxH), rp, rp, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                if (legH > 0.5f) pb.AddLine(rx - legH, padY + m.BoxH);
                pb.EndFigure(CanvasFigureLoop.Open);

                if (legH > 0.5f) { pb.BeginFigure(lx + legH, padY + m.BoxH); pb.AddLine(lx, padY + m.BoxH); }
                else pb.BeginFigure(lx, padY + m.BoxH);
                pb.AddArc(new Vector2(lx + rp * MathF.Cos(MathF.PI - halfGap), cy + rp * MathF.Sin(MathF.PI - halfGap)), rp, rp, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                pb.EndFigure(CanvasFigureLoop.Open);
                break;
            }
            default:
            {
                float cr = m.Radius;
                float x0 = padX, y0 = padY, x1 = padX + m.BoxW, y1 = padY + m.BoxH;
                float legX = m.LegX, legY = m.LegY;

                pb.BeginFigure(x1 - cr - legX, y0);
                if (legX > 0.5f) pb.AddLine(x1 - cr, y0);
                if (cr > 0.5f) pb.AddArc(new Vector2(x1, y0 + cr), cr, cr, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                if (legY > 0.5f) pb.AddLine(x1, y0 + cr + legY);
                pb.EndFigure(CanvasFigureLoop.Open);

                pb.BeginFigure(x1, y1 - cr - legY);
                if (legY > 0.5f) pb.AddLine(x1, y1 - cr);
                if (cr > 0.5f) pb.AddArc(new Vector2(x1 - cr, y1), cr, cr, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                if (legX > 0.5f) pb.AddLine(x1 - cr - legX, y1);
                pb.EndFigure(CanvasFigureLoop.Open);

                pb.BeginFigure(x0 + cr + legX, y1);
                if (legX > 0.5f) pb.AddLine(x0 + cr, y1);
                if (cr > 0.5f) pb.AddArc(new Vector2(x0, y1 - cr), cr, cr, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                if (legY > 0.5f) pb.AddLine(x0, y1 - cr - legY);
                pb.EndFigure(CanvasFigureLoop.Open);

                pb.BeginFigure(x0, y0 + cr + legY);
                if (legY > 0.5f) pb.AddLine(x0, y0 + cr);
                if (cr > 0.5f) pb.AddArc(new Vector2(x0 + cr, y0), cr, cr, 0f, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                if (legX > 0.5f) pb.AddLine(x0 + cr + legX, y0);
                pb.EndFigure(CanvasFigureLoop.Open);
                break;
            }
        }
        return CanvasGeometry.CreatePath(pb);
    }

    private (CompositionMaskBrush Mask, CompositionLinearGradientBrush Grad) CreatePerimeterFlow(
        CompositionDrawingSurface mask,
        float W,
        float H,
        float r,
        bool right)
    {
        float P = 2f * (W + H);
        float[] s = right
            ? [(W - r) / P, (W + H / 2f) / P, (W + H + r) / P]
            : [(2f * W + 2f * H + r) / P % 1f, (2f * W + 1.5f * H) / P, (2f * W + H - r) / P];

        Color At(float si, float phase)
        {
            var (cr, cg, cb) = CocoonBracketMath.SamplePerimeterColor(si, phase);
            return Color.FromArgb(255, cr, cg, cb);
        }

        var grad = _ctx.C.CreateLinearGradientBrush();
        grad.StartPoint = new Vector2(0.5f, 0f);
        grad.EndPoint = new Vector2(0.5f, 1f);
        for (int i = 0; i < 3; i++)
        {
            var stop = _ctx.C.CreateColorGradientStop(i / 2f, At(s[i], 0f));
            grad.ColorStops.Add(stop);
            if (_ctx.ReducedMotion) continue;

            var anim = _ctx.C.CreateColorKeyFrameAnimation();
            anim.InterpolationColorSpace = CompositionColorSpace.Rgb;
            const int steps = 24;
            for (int k = 0; k <= steps; k++)
                anim.InsertKeyFrame(k / (float)steps, At(s[i], k / (float)steps), _ctx.Linear);
            anim.Duration = CocoonContext.Ms(CocoonTokens.Focus.FlowPeriodMs);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;
            stop.StartAnimation("Color", anim);
        }

        var maskBrush = _ctx.C.CreateMaskBrush();
        maskBrush.Source = grad;
        maskBrush.Mask = _ctx.C.CreateSurfaceBrush(mask);
        return (maskBrush, grad);
    }

    private void ClearCache()
    {
        foreach (var entry in _cache.Values) DisposeEntry(entry);
        _cache.Clear();
    }

    private static void DisposeEntry(CachedCursor c)
    {
        c.Glow.Dispose();
        c.Line.Dispose();
        c.Rim.Dispose();
        c.L1.Dispose();
        c.L2.Dispose();
        c.G1.Dispose();
        c.G2.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx.DeviceReplaced -= ClearCache;
        ClearCache();
    }
}
