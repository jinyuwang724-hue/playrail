// 动态背景（参照 folia 首页墙后面透出来的那层 visualizer「common」模式的机制，自己实现）：
// - 底色 + 15 个缓慢漂移、自转的几何形（圆 / 方 / 三角 / 叉，40–140 px，不透明度 11–19 %，七成描边三成实心，30–60 s 一圈），
//   20 个往上飘、淡入淡出的小光点，最外一层暗角；
// - 颜色取自当前焦点游戏的主色（封面套件里的 c），换焦点时 600 ms 渐变过去；
// - 全部是合成线程上的无限循环关键帧动画，UI 线程不逐帧干活；只在缝透出来的那一条里画（裁剪到缝的开口），
//   缝合上、窗口不在前台、关掉动态背景或降低动态效果时暂停。
using System.Numerics;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed class Backdrop
{
    private readonly Gfx _g;
    public readonly ContainerVisual Root;
    private readonly SpriteVisual _base;
    private readonly ContainerVisual _shapes;
    private readonly SpriteVisual _vignette;
    private readonly CompositionColorBrush _baseBrush, _shapeBrush, _particleBrush;
    private readonly List<Visual> _animated = new();
    private bool _running;
    private bool _started;
    private Color _key = Theme.Ink;

    public Backdrop(Gfx g, float w, float h)
    {
        _g = g;
        Root = g.Container();
        _baseBrush = g.Solid(Theme.Bg);
        _shapeBrush = g.Solid(Gfx.Rgba(160, 160, 170));
        _particleBrush = g.Solid(Gfx.Rgba(220, 220, 230));
        _base = g.Sprite(_baseBrush);
        Root.Children.InsertAtTop(_base);
        _shapes = g.Container();
        Root.Children.InsertAtTop(_shapes);
        _vignette = g.Sprite(null);
        Root.Children.InsertAtTop(_vignette);
        _ = (w, h);   // 构图由 LayoutChrome 调 Build 建（知道视口大小之后）
    }

    /// <summary>按视口重建（随机数固定种子：每次启动同一幅构图）。</summary>
    public void Build(float w, float h)
    {
        _shapes.Children.RemoveAll();
        _animated.Clear();
        _started = false;
        var rnd = new Random(20261014);
        var c = _g.C;
        for (int i = 0; i < 15; i++)
        {
            int type = rnd.Next(4);                       // 0 圆 1 方 2 三角 3 叉
            float size = 40 + (float)rnd.NextDouble() * 100;
            float x = (float)rnd.NextDouble() * w, y = (float)rnd.NextDouble() * h;
            double duration = 30 + rnd.NextDouble() * 30;
            double delay = rnd.NextDouble() * 5;
            float opacity = 0.11f + (float)rnd.NextDouble() * 0.08f;
            bool reverse = rnd.NextDouble() > 0.5;
            bool filled = rnd.NextDouble() < 0.3 || type >= 2;
            float rot = (float)rnd.NextDouble() * 360;

            var sv = c.CreateShapeVisual();
            sv.Size = new Vector2(size, size);
            sv.CenterPoint = new Vector3(size / 2, size / 2, 0);
            CompositionGeometry geo = type switch
            {
                0 => Ellipse(size),
                1 => Rect(size),
                2 => Path(size, new[] { (0.5f, 0f), (0f, 1f), (1f, 1f) }),
                _ => Path(size, new[] { (0.2f, 0f), (0f, 0.2f), (0.3f, 0.5f), (0f, 0.8f), (0.2f, 1f), (0.5f, 0.7f), (0.8f, 1f), (1f, 0.8f), (0.7f, 0.5f), (1f, 0.2f), (0.8f, 0f), (0.5f, 0.3f) }),
            };
            var shape = c.CreateSpriteShape(geo);
            if (filled) shape.FillBrush = _shapeBrush;
            else { shape.StrokeBrush = _shapeBrush; shape.StrokeThickness = 1; }
            sv.Shapes.Add(shape);
            sv.Opacity = opacity;
            sv.Offset = new Vector3(x, y, 0);
            sv.RotationAngleInDegrees = rot;
            sv.Comment = $"{duration:F3}|{delay:F3}|{(reverse ? 1 : 0)}|{rot:F2}";
            _shapes.Children.InsertAtTop(sv);
            _animated.Add(sv);
        }
        for (int i = 0; i < 20; i++)
        {
            float size = (float)rnd.NextDouble() * 4 + 1;
            var dot = c.CreateShapeVisual();
            dot.Size = new Vector2(size, size);
            var e = c.CreateSpriteShape(Ellipse(size));
            e.FillBrush = _particleBrush;
            dot.Shapes.Add(e);
            dot.Offset = new Vector3((float)rnd.NextDouble() * w, (float)rnd.NextDouble() * h, 0);
            float op = (float)rnd.NextDouble() * 0.3f;
            double duration = 15 + rnd.NextDouble() * 20, delay = rnd.NextDouble() * 10;
            dot.Opacity = 0;
            dot.Comment = $"p|{duration:F3}|{delay:F3}|{op:F3}";
            _shapes.Children.InsertAtTop(dot);
            _animated.Add(dot);
        }
        // radial-gradient(circle, transparent 40%, rgba(0,0,0,.6) 100%)：圆，半径到最远角
        var v = c.CreateRadialGradientBrush();
        v.MappingMode = CompositionMappingMode.Absolute;
        float r = MathF.Sqrt(w * w + h * h) / 2;
        v.EllipseCenter = new Vector2(w / 2, h / 2);
        v.EllipseRadius = new Vector2(r, r);
        v.ColorStops.Add(c.CreateColorGradientStop(0.4f, Gfx.Rgba(0, 0, 0, 0)));
        v.ColorStops.Add(c.CreateColorGradientStop(1f, Gfx.Rgba(0, 0, 0, 0.6)));
        _vignette.Brush = v;
        if (_running) { _running = false; Run(true); }
    }

    private CompositionGeometry Ellipse(float size)
    {
        var e = _g.C.CreateEllipseGeometry();
        e.Center = new Vector2(size / 2, size / 2);
        e.Radius = new Vector2(size / 2 - 0.5f, size / 2 - 0.5f);
        return e;
    }

    private CompositionGeometry Rect(float size)
    {
        var r = _g.C.CreateRectangleGeometry();
        r.Offset = new Vector2(0.5f, 0.5f);
        r.Size = new Vector2(size - 1, size - 1);
        return r;
    }

    private CompositionGeometry Path(float size, (float x, float y)[] pts)
    {
        using var builder = new CanvasPathBuilder(_g.Device);
        builder.BeginFigure(pts[0].x * size, pts[0].y * size);
        for (int i = 1; i < pts.Length; i++) builder.AddLine(pts[i].x * size, pts[i].y * size);
        builder.EndFigure(CanvasFigureLoop.Closed);
        var geo = CanvasGeometry.CreatePath(builder);
        return _g.C.CreatePathGeometry(new CompositionPath(geo));
    }

    /// <summary>开 / 停循环动画（停的时候画面定在原处，不占合成帧）。</summary>
    public void Run(bool on)
    {
        if (on == _running) return;
        _running = on;
        if (_started)
        {
            // 停：原地冻住（动画控制器暂停），不跳回起点；动：从冻住的地方接着走
            foreach (var v in _animated)
                foreach (var prop in new[] { "Offset", "RotationAngleInDegrees", "Opacity" })
                {
                    var ctl = v.TryGetAnimationController(prop);
                    if (ctl == null) continue;
                    if (on) ctl.Resume(); else ctl.Pause();
                }
            return;
        }
        if (!on) return;
        _started = true;
        var lin = _g.Linear;
        foreach (var v in _animated)
        {
            var parts = v.Comment.Split('|');
            var o = v.Offset;
            if (parts[0] == "p")
            {
                double dur = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), delay = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                float op = float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                var up = _g.C.CreateVector3KeyFrameAnimation();
                up.InsertKeyFrame(0, o, lin);
                up.InsertKeyFrame(1, o + new Vector3(0, -100, 0), lin);
                var fade = _g.C.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0, 0, lin);
                fade.InsertKeyFrame(0.5f, op, lin);
                fade.InsertKeyFrame(1, 0, lin);
                foreach (var a in new KeyFrameAnimation[] { up, fade }) { a.Duration = Gfx.Ms(dur * 1000); a.DelayTime = Gfx.Ms(delay * 1000); a.IterationBehavior = AnimationIterationBehavior.Forever; }
                v.StartAnimation("Offset", up);
                v.StartAnimation("Opacity", fade);
            }
            else
            {
                double dur = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), delay = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                bool reverse = parts[2] == "1";
                float rot = float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                float sy = reverse ? -30 : 30, sx = reverse ? 15 : -15;
                var drift = _g.C.CreateVector3KeyFrameAnimation();
                drift.InsertKeyFrame(0, o + new Vector3(sx, sy, 0), lin);
                drift.InsertKeyFrame(0.5f, o + new Vector3(-sx, -sy, 0), lin);
                drift.InsertKeyFrame(1, o + new Vector3(sx, sy, 0), lin);
                var spin = _g.C.CreateScalarKeyFrameAnimation();
                spin.InsertKeyFrame(0, rot, lin);
                spin.InsertKeyFrame(1, rot + 360, lin);
                foreach (var a in new KeyFrameAnimation[] { drift, spin }) { a.Duration = Gfx.Ms(dur * 1000); a.DelayTime = Gfx.Ms(delay * 1000); a.IterationBehavior = AnimationIterationBehavior.Forever; }
                v.StartAnimation("Offset", drift);
                v.StartAnimation("RotationAngleInDegrees", spin);
            }
        }
    }

    /// <summary>主题色跟着焦点游戏走：底色微染、几何形用提亮的主色、光点更亮。</summary>
    public void SetKey(Color key, bool animate)
    {
        if (key == _key) return;
        _key = key;
        var white = Gfx.Rgba(255, 255, 255);
        To(_baseBrush, Gfx.Mix(key, 0.14, Theme.Bg), animate);
        // 几何形和光点的颜色直接换（动画会让每个矢量形状每帧重新栅格化，展开 / 走位时掉帧）；只有底色渐变
        To(_shapeBrush, Gfx.Mix(key, 0.65, white), false);
        To(_particleBrush, Gfx.Mix(key, 0.35, white), false);
    }

    private void To(CompositionColorBrush b, Color to, bool animate)
    {
        if (!animate) { b.StopAnimation("Color"); b.Color = to; return; }
        var a = _g.C.CreateColorKeyFrameAnimation();
        a.InsertKeyFrame(0, b.Color, _g.Linear);
        a.InsertKeyFrame(1, to, _g.Ease(Engine.Bezier.EaseInOut));
        a.Duration = Gfx.Ms(600);
        b.StartAnimation("Color", a);
        b.Color = to;
    }
}
