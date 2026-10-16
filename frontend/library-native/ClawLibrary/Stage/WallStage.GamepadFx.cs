// 控制中心 → 手柄：线稿上的动效，全用 Composition 动画在合成线程上跑（满帧，不靠整页重画）。
// 震动：线稿层带衰减地抖一下（幅度跟强度走），对应握把亮一下，再有一圈握把形状的描边往外扩散淡出（左右各按自己的强度）。
// 灯光：两个摇杆外圈各一个光环，按当前效果动 —— 常亮、呼吸（明暗循环）、幻彩（色相循环）、水波（光环往外扩）、模式指示。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private ContainerVisual _gpArtLayer = null!;
    private readonly Dictionary<string, SpriteVisual> _gpGlow = new(), _gpEcho = new();
    private readonly Dictionary<string, (ShapeVisual Visual, CompositionColorBrush Brush, CompositionEllipseGeometry Ring)> _gpRings = new();
    private string _gpLightShown = "";

    /// <summary>握把发光 / 描边扩散的两层，和两个摇杆灯环；线稿和这些都在 _gpArtLayer 里，抖的时候一起抖。</summary>
    private void EnsureGpFx()
    {
        foreach (var side in new[] { "GripL", "GripR" })
        {
            foreach (var dict in new[] { _gpGlow, _gpEcho })
            {
                var s = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
                s.Opacity = 0;
                dict[side] = s;
                _gpArtLayer.Children.InsertAtTop(s);
            }
        }
        foreach (var stick in new[] { "LS", "RS" })
        {
            var sv = G.C.CreateShapeVisual();
            var ring = G.C.CreateEllipseGeometry();
            var brush = G.C.CreateColorBrush(Gfx.Rgba(0xff, 0xff, 0xff));
            var shape = G.C.CreateSpriteShape(ring);
            shape.StrokeBrush = brush;
            shape.StrokeThickness = 3;
            sv.Shapes.Add(shape);
            sv.Opacity = 0;
            _gpRings[stick] = (sv, brush, ring);
            _gpArtLayer.Children.InsertAtTop(sv);
        }
    }

    /// <summary>按当前缩放把握把发光 / 描边画进各自的面，摆好位置；灯环摆到摇杆上。线稿和区域都载入后调。</summary>
    private void PaintGpFx(float k, float raster)
    {
        var accent = PerfAccent;
        foreach (var side in new[] { "GripL", "GripR" })
        {
            if (!_gpRegions.TryGetValue(side, out var region)) continue;
            var b = region.ComputeBounds();
            float pad = 6, w = (float)b.Width + pad * 2, h = (float)b.Height + pad * 2;
            var shift = Matrix3x2.CreateTranslation(-(float)b.X + pad, -(float)b.Y + pad);
            foreach (var (dict, fill) in new[] { (_gpGlow, true), (_gpEcho, false) })
            {
                var sprite = dict[side];
                CcSet(sprite, CcDraw(w, h, raster, ds =>
                {
                    using var g = region.Transform(shift);
                    if (fill) ds.FillGeometry(g, Gfx.WithAlpha(accent, 0.75f));
                    else ds.DrawGeometry(g, accent, 2.2f);
                }));
                sprite.Size = new Vector2(w * k, h * k);
                sprite.Offset = new Vector3(((float)b.X - pad) * k, ((float)b.Y - pad) * k, 0);
                sprite.CenterPoint = new Vector3(w * k / 2, h * k / 2, 0);
            }
        }
        foreach (var (stick, (visual, _, ring)) in _gpRings)
        {
            var key = GpKeys[stick];
            float r = key.W / 2 * GpS + 5, size = r * 2 + 16;
            var c = GpMap("front", key.X, key.Y);
            visual.Size = new Vector2(size * k, size * k);
            visual.Offset = new Vector3((c.X - size / 2) * k, (c.Y - size / 2) * k, 0);
            visual.CenterPoint = new Vector3(size * k / 2, size * k / 2, 0);
            ring.Center = new Vector2(size * k / 2, size * k / 2);
            ring.Radius = new Vector2(r * k, r * k);
            ((CompositionSpriteShape)visual.Shapes[0]).StrokeThickness = 3 * k;
        }
        _gpLightShown = "";
        GpUpdateLightPreview();
    }

    /// <summary>震一下的画面：左右强度 0–1。抖动 420 ms 衰减到 0；握把亮起再暗下；握把形状的描边往外扩 1.0→1.3 淡出。</summary>
    private void GpPlayRumble(float left, float right)
    {
        if (_gp == null || Reduced) return;
        float amp = 1.2f + 2.8f * Math.Max(left, right);
        var shake = G.C.CreateVector3KeyFrameAnimation();
        var linear = G.C.CreateLinearEasingFunction();
        const int steps = 14;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps, decay = (1 - t) * (1 - t);
            float x = i == steps ? 0 : (i % 2 == 0 ? 1 : -1) * amp * decay * _ccK;
            float y = i == steps ? 0 : (i % 4 < 2 ? 0.35f : -0.35f) * amp * decay * _ccK;
            shake.InsertKeyFrame(t, new Vector3(x, y, 0), linear);
        }
        shake.Duration = TimeSpan.FromMilliseconds(420);
        _gpArtLayer.StartAnimation("Offset", shake);
        foreach (var (side, s) in new[] { ("GripL", left), ("GripR", right) })
        {
            if (s <= 0 || !_gpGlow.ContainsKey(side)) continue;
            var glow = G.C.CreateScalarKeyFrameAnimation();
            glow.InsertKeyFrame(0, 0);
            glow.InsertKeyFrame(0.18f, 0.25f + 0.6f * s);
            glow.InsertKeyFrame(1, 0);
            glow.Duration = TimeSpan.FromMilliseconds(650);
            _gpGlow[side].StartAnimation("Opacity", glow);
            var fade = G.C.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0.9f * s + 0.1f);
            fade.InsertKeyFrame(1, 0);
            fade.Duration = TimeSpan.FromMilliseconds(720);
            var grow = G.C.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0, Vector3.One);
            grow.InsertKeyFrame(1, new Vector3(1.18f + 0.14f * s, 1.18f + 0.14f * s, 1), G.C.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1), new Vector2(0.36f, 1)));
            grow.Duration = TimeSpan.FromMilliseconds(720);
            _gpEcho[side].StartAnimation("Opacity", fade);
            _gpEcho[side].StartAnimation("Scale", grow);
        }
    }

    /// <summary>灯光页的灯环预览：只在灯光页、固件读得到时显示，按效果设动画（同一效果不重复启动，免得动画跳）。</summary>
    private void GpUpdateLightPreview()
    {
        if (_gpRings.Count == 0) return;
        var l = _gpFw?["light"];
        string effect = GpCatId == "light" && _gpPicker == null && l != null ? Str(l["effect"]) ?? "off" : "off";
        var color = GpLightColors[GpLightColorIndex(l)];
        float bright = Math.Clamp(Int(l?["brightness"], 100), 0, 100) / 100f;
        int speed = Math.Clamp(Int(l?["speedLevel"], 1), 0, 2);
        if (effect == "modeinfo") color = Str(_gpFw?["mode"]) == "desktop" ? GpLightColors[4] : GpLightColors[6];
        string key = $"{effect}|{color}|{bright}|{speed}";
        if (key == _gpLightShown) return;
        _gpLightShown = key;
        double period = speed switch { 0 => 3.2, 2 => 1.1, _ => 2 };
        foreach (var (_, (visual, brush, _)) in _gpRings)
        {
            visual.StopAnimation("Opacity"); visual.StopAnimation("Scale"); brush.StopAnimation("Color");
            visual.Scale = Vector3.One;
            brush.Color = color;
            if (effect == "off") { visual.Opacity = 0; continue; }
            float top = 0.25f + 0.75f * bright;
            visual.Opacity = top;
            switch (effect)
            {
                case "breathe":
                {
                    var a = G.C.CreateScalarKeyFrameAnimation();
                    a.InsertKeyFrame(0, top * 0.12f); a.InsertKeyFrame(0.5f, top); a.InsertKeyFrame(1, top * 0.12f);
                    a.Duration = TimeSpan.FromSeconds(period); a.IterationBehavior = AnimationIterationBehavior.Forever;
                    visual.StartAnimation("Opacity", a);
                    break;
                }
                case "rainbow":
                {
                    var c = G.C.CreateColorKeyFrameAnimation();
                    c.InterpolationColorSpace = CompositionColorSpace.Hsl;
                    var linear = G.C.CreateLinearEasingFunction();
                    for (int i = 0; i <= 6; i++) c.InsertKeyFrame(i / 6f, GpHue(i / 6f), linear);
                    c.Duration = TimeSpan.FromSeconds(period * 2); c.IterationBehavior = AnimationIterationBehavior.Forever;
                    brush.StartAnimation("Color", c);
                    break;
                }
                case "ripple":
                {
                    var s = G.C.CreateVector3KeyFrameAnimation();
                    s.InsertKeyFrame(0, new Vector3(0.85f, 0.85f, 1)); s.InsertKeyFrame(1, new Vector3(1.35f, 1.35f, 1));
                    s.Duration = TimeSpan.FromSeconds(period); s.IterationBehavior = AnimationIterationBehavior.Forever;
                    var o = G.C.CreateScalarKeyFrameAnimation();
                    o.InsertKeyFrame(0, top); o.InsertKeyFrame(1, 0);
                    o.Duration = TimeSpan.FromSeconds(period); o.IterationBehavior = AnimationIterationBehavior.Forever;
                    visual.StartAnimation("Scale", s);
                    visual.StartAnimation("Opacity", o);
                    break;
                }
            }
        }
    }

    private static Color GpHue(float h)
    {
        float r = Math.Clamp(Math.Abs(h * 6 - 3) - 1, 0, 1), g = Math.Clamp(2 - Math.Abs(h * 6 - 2), 0, 1), b = Math.Clamp(2 - Math.Abs(h * 6 - 4), 0, 1);
        return Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
