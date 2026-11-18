// PS5 式焦点（11-19 jinyu，第二版）：
// - 焦点框只有一圈：2 DIP 的白线，约一半透明；线上有一小团很淡的蓝→粉颜色沿着框慢慢走（7 秒一圈），不细看看不出来；
// - 按目标的真实位置和大小用弹簧移过去，落下时先大一圈再收紧（套圈）；
// - 只有带图标的组件才做选中填充（Reveal）：白底从移来那一侧边缘的中点长成一个圆盖满，底板先缩一点再弹回，
//   黑图标同时从移动方向滑进来、稍放大后回落；上一个的白色缩成圆收进移动方向那一侧。
// - 没有图标的（设置行、分类、页签）不填白，只换粗体 + 焦点框；图片（封面、大卡）只套框。
// 坐标：To 收设计坐标 × scale = 宿主像素；宿主本身缩放时用 SetUnit 让间距和线宽仍按 DIP。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed class FocusFx
{
    public const float Gap = 5, Line = 2;
    private static readonly Bezier Ease = new(.22f, .8f, .24f, 1f);
    private static readonly Spring Move = Spring.Visual(0.30, 0.12);

    private readonly Gfx G;
    public readonly ContainerVisual Root;
    private readonly ContainerVisual _ring, _lines;
    private readonly CompositionRoundedRectangleGeometry _ringGeo;
    private readonly CompositionRadialGradientBrush _tint;
    private readonly SpriteVisual _spot;
    private readonly ContainerVisual _band;
    private readonly CompositionPathGeometry _bandGeo;
    private readonly Slot[] _slots = new Slot[2];
    private int _slot;
    private (float X, float Y, float W, float H)? _last, _from;
    private bool _lastFill;
    private float _unit = 1;
    private readonly List<(CompositionSpriteShape Shape, float Width)> _strokes = new();
    private readonly ExpressionAnimation _sizeExpr;
    private const double LapSeconds = 5.5;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private Vector2 _walkSize;

    private sealed class Slot
    {
        public ContainerVisual Box = null!;
        public CompositionRoundedRectangleGeometry PlateGeo = null!;
        public ShapeVisual Plate = null!;
        public SpriteVisual Ink = null!;
        public Reveal Fill = null!;
    }

    public FocusFx(Gfx g)
    {
        G = g;
        Root = g.C.CreateContainerVisual();
        for (int i = 0; i < 2; i++)
        {
            var s = new Slot { Box = g.C.CreateContainerVisual() };
            s.Box.Opacity = 0;
            s.PlateGeo = g.C.CreateRoundedRectangleGeometry();
            var fill = g.C.CreateSpriteShape(s.PlateGeo);
            fill.FillBrush = g.C.CreateColorBrush(Colors.White);
            s.Plate = g.C.CreateShapeVisual();
            s.Plate.RelativeSizeAdjustment = Vector2.One;
            s.Plate.Shapes.Add(fill);
            s.Ink = g.C.CreateSpriteVisual();
            var layer = g.C.CreateContainerVisual();
            layer.RelativeSizeAdjustment = Vector2.One;
            layer.Children.InsertAtTop(s.Plate);
            layer.Children.InsertAtTop(s.Ink);
            s.Box.Children.InsertAtTop(layer);
            s.Fill = new Reveal(g, layer) { Plate = s.Plate, Icon = s.Ink };
            Root.Children.InsertAtTop(s.Box);
            _slots[i] = s;
        }

        // 焦点框：外层容器按弹簧移动、改尺寸；里面一圈半透明白线 + 同一圈上一团沿线走的蓝粉色
        _ring = g.C.CreateContainerVisual();
        _ring.Opacity = 0;
        _lines = g.C.CreateContainerVisual();
        _lines.RelativeSizeAdjustment = Vector2.One;
        _ring.Children.InsertAtTop(_lines);
        _ringGeo = g.C.CreateRoundedRectangleGeometry();
        _sizeExpr = g.C.CreateExpressionAnimation("Vector2(Max(1, r.Size.X - l), Max(1, r.Size.Y - l))");
        _sizeExpr.SetReferenceParameter("r", _ring);
        _sizeExpr.SetScalarParameter("l", Line);
        _ringGeo.StartAnimation("Size", _sizeExpr);
        _ringGeo.Offset = new Vector2(Line / 2, Line / 2);

        ShapeVisual Stroke(CompositionBrush brush, float opacity)
        {
            var sh = g.C.CreateSpriteShape(_ringGeo);
            sh.StrokeBrush = brush;
            sh.StrokeThickness = Line;
            var v = g.C.CreateShapeVisual();
            v.RelativeSizeAdjustment = Vector2.One;
            v.Shapes.Add(sh);
            v.Opacity = opacity;
            _strokes.Add((sh, Line));
            return v;
        }
        _lines.Children.InsertAtTop(Stroke(g.C.CreateColorBrush(Colors.White), 0.52f));

        // 沿线走的颜色：一个径向渐变（中心浅蓝、往外浅粉、再往外透明），中心沿框的周长匀速走一圈
        _tint = g.C.CreateRadialGradientBrush();
        _tint.ColorStops.Add(g.C.CreateColorGradientStop(0, Gfx.Rgba(0xc6, 0xdc, 0xff, 0.95)));
        _tint.ColorStops.Add(g.C.CreateColorGradientStop(0.45f, Gfx.Rgba(0xe6, 0xd6, 0xff, 0.8)));
        _tint.ColorStops.Add(g.C.CreateColorGradientStop(0.75f, Gfx.Rgba(0xff, 0xcf, 0xe8, 0.55)));
        _tint.ColorStops.Add(g.C.CreateColorGradientStop(1, Gfx.Rgba(0xff, 0xcf, 0xe8, 0)));
        // 中心沿框的周长匀速走（Walk：按框的目标尺寸重排关键帧，从当前走到的位置接着走，不回起点）
        // 11-19：渐变笔描在形状上、动画它的中心不生效（实机看是一条不动的白线）。改成一团真正的光点（精灵）沿周长走，
        // 用和线一样宽的环形裁切框住，只在线上露出来。
        _spot = g.Sprite(_tint, relative: false);
        var band = _band = g.C.CreateContainerVisual();
        band.RelativeSizeAdjustment = Vector2.One;
        _bandGeo = g.C.CreatePathGeometry();
        band.Clip = g.C.CreateGeometricClip(_bandGeo);
        band.Children.InsertAtTop(_spot);
        band.Opacity = 0.85f;
        _lines.Children.InsertAtTop(band);

        var mid = g.C.CreateExpressionAnimation("Vector3(r.Size.X / 2, r.Size.Y / 2, 0)");
        mid.SetReferenceParameter("r", _ring);
        _ring.StartAnimation("CenterPoint", mid);
        Root.Children.InsertAtTop(_ring);
    }

    public bool Visible => _last != null;

    /// <summary>宿主坐标里一个 DIP 有多长（宿主本身缩放了 k 倍时 = 1 / k）：焦点框的间距、线宽、流光大小按 DIP 定。</summary>
    public void SetUnit(float unit)
    {
        if (MathF.Abs(unit - _unit) < 0.0001f) return;
        _unit = unit;
        foreach (var (sh, w) in _strokes) sh.StrokeThickness = w * unit;
        _ringGeo.Offset = new Vector2(Line / 2 * unit, Line / 2 * unit);
        _sizeExpr.SetScalarParameter("l", Line * unit);
        _ringGeo.StartAnimation("Size", _sizeExpr);
        _walkSize = Vector2.Zero;
    }

    /// <summary>
    /// 移到新目标（设计坐标 x/y/w/h，圆角 r）。fill = 带图标的组件：白底 + ink（黑图标层，画在 inkRect、和目标同一坐标系），按方向长出来；
    /// 同一个位置再调用只换 ink，不重放动画（读数刷新时走这里）。
    /// </summary>
    public void To(float x, float y, float w, float h, float r, float scale, bool fill, CompositionBrush? ink = null,
        (float X, float Y, float W, float H) inkRect = default, bool instant = false)
    {
        // 以下一律用宿主像素：不同比例的目标之间（拼盘卡 × _ccK、功能坞 DIP）方向和位置也能比
        float k = scale;
        x *= k; y *= k; w *= k; h *= k; r *= k;
        inkRect = (inkRect.X * k, inkRect.Y * k, inkRect.W * k, inkRect.H * k);
        var target = (x, y, w, h);
        bool same = _last is { } l && MathF.Abs(l.X - x) < 0.5f && MathF.Abs(l.Y - y) < 0.5f && MathF.Abs(l.W - w) < 0.5f && MathF.Abs(l.H - h) < 0.5f
            && fill == _lastFill;
        if (same && !instant)
        {
            if (fill) SetInk(_slots[_slot], ink, inkRect, x, y);
            return;
        }

        int dir = DirTo(x, y, w, h);
        bool first = (_last ?? _from) == null || instant;
        var old = _slots[_slot];
        if (_last != null && _lastFill && !instant) old.Fill.Exit(dir, old.Box.Size.X, old.Box.Size.Y);
        else { old.Box.StopAnimation("Opacity"); old.Box.Opacity = 0; }

        _slot ^= 1;
        var s = _slots[_slot];
        s.Box.StopAnimation("Opacity");
        if (fill)
        {
            s.Box.Offset = new Vector3(x, y, 0);
            s.Box.Size = new Vector2(w, h);
            s.PlateGeo.Size = s.Box.Size;
            s.PlateGeo.CornerRadius = new Vector2(r);
            SetInk(s, ink, inkRect, x, y);
            s.Box.Opacity = 1;
            if (first) s.Fill.Set(true, w, h);
            else s.Fill.Enter(dir, w, h);
        }
        else s.Box.Opacity = 0;
        _last = target;
        _lastFill = fill;

        // 焦点框：离目标 Gap，按弹簧移过去；第一次出现直接放到位
        float gap = Gap * _unit;
        var ro = new Vector3(x - gap, y - gap, 0);
        var rs = new Vector2(w + 2 * gap, h + 2 * gap);
        _ringGeo.CornerRadius = new Vector2(r + gap);
        Walk(rs);
        if (first)
        {
            _ring.StopAnimation("Offset"); _ring.StopAnimation("Size");
            _ring.Offset = ro; _ring.Size = rs;
        }
        else
        {
            _ring.StartAnimation("Offset", G.SpringV3(ro, Move));
            // 移动途中光点的环形裁切还是新尺寸，先藏起来，落定后再亮
            var bo = G.C.CreateScalarKeyFrameAnimation();
            bo.InsertKeyFrame(0, 0, G.Linear); bo.InsertKeyFrame(0.55f, 0, G.Linear); bo.InsertKeyFrame(1, 0.85f, G.Linear);
            bo.Duration = Gfx.Ms(600);
            _band.StartAnimation("Opacity", bo);
            var sz = G.C.CreateSpringVector2Animation();
            sz.FinalValue = rs; sz.DampingRatio = Move.Zeta; sz.Period = Move.Period;
            _ring.StartAnimation("Size", sz);
        }
        _ring.StopAnimation("Opacity");
        _ring.Opacity = 1;
        Hoop();
    }

    /// <summary>
    /// 流光：一小团蓝粉色沿框的周长走一圈 LapSeconds 秒。大小按周长定（小按钮上是一小段，大卡上也只占一截），
    /// 关键帧按框的尺寸在周长上均匀取点，起点是按时钟算出来的当前位置，所以换目标时颜色接着往前走，不跳回起点。
    /// </summary>
    private void Walk(Vector2 size)
    {
        if (Vector2.Distance(size, _walkSize) < 1) return;
        _walkSize = size;
        float w = size.X, h = size.Y, per = 2 * (w + h);
        float rad = Math.Clamp(per * 0.085f, 26 * _unit, 110 * _unit);
        _spot.Size = new Vector2(rad * 2, rad * 2);
        // 环形裁切：外圈 = 线的外沿，内圈 = 线的内沿
        float lw = Line * _unit, cr = _ringGeo.CornerRadius.X;
        using (var outer = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateRoundedRectangle(G.Device, 0, 0, w, h, cr + lw / 2, cr + lw / 2))
        using (var inner = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateRoundedRectangle(G.Device, lw, lw, Math.Max(1, w - 2 * lw), Math.Max(1, h - 2 * lw), Math.Max(0, cr - lw / 2), Math.Max(0, cr - lw / 2)))
        using (var ring = outer.CombineWith(inner, Matrix3x2.Identity, Microsoft.Graphics.Canvas.Geometry.CanvasGeometryCombine.Exclude))
            _bandGeo.Path = new CompositionPath(ring);
        Vector2 At(float t)
        {
            float d = ((t % 1) + 1) % 1 * per;
            if (d < w) return new(d, 0);
            d -= w; if (d < h) return new(w, d);
            d -= h; if (d < w) return new(w - d, h);
            d -= w; return new(0, h - d);
        }
        float phase = (float)(Clock.Elapsed.TotalSeconds / LapSeconds % 1);
        const int n = 64;
        var half = new Vector2(rad, rad);
        var a3 = G.C.CreateVector3KeyFrameAnimation();
        for (int i = 0; i <= n; i++) { var v = At(phase + i / (float)n) - half; a3.InsertKeyFrame(i / (float)n, new Vector3(v, 0), G.Linear); }
        a3.Duration = TimeSpan.FromSeconds(LapSeconds);
        a3.IterationBehavior = AnimationIterationBehavior.Forever;
        _spot.StartAnimation("Offset", a3);
    }

    /// <summary>套圈：先大一圈、淡一些，再收紧到位（稍微收过一点再回来）。</summary>
    public void Hoop()
    {
        var sc = G.C.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0, new Vector3(1.06f, 1.06f, 1));
        sc.InsertKeyFrame(0.7f, new Vector3(0.99f, 0.99f, 1), G.Ease(Ease));
        sc.InsertKeyFrame(1, Vector3.One, G.Ease(Ease));
        sc.Duration = Gfx.Ms(420);
        _ring.StartAnimation("Scale", sc);
        var op = G.C.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0, 0.3f);
        op.InsertKeyFrame(0.6f, 1, G.Ease(Ease));
        op.InsertKeyFrame(1, 1);
        op.Duration = Gfx.Ms(420);
        _lines.StartAnimation("Opacity", op);
    }

    public void Hide(bool animate = true)
    {
        if (_last == null) return;
        _from = _last;
        _last = null;
        if (animate)
        {
            _ring.StartAnimation("Opacity", G.Scalar(1, 0, 160, Ease));
            foreach (var s in _slots) if (s.Box.Opacity > 0) s.Box.StartAnimation("Opacity", G.Scalar(1, 0, 160, Ease));
        }
        else
        {
            _ring.StopAnimation("Opacity"); _ring.Opacity = 0;
            foreach (var s in _slots) { s.Box.StopAnimation("Opacity"); s.Box.Opacity = 0; }
        }
    }

    private static void SetInk(Slot s, CompositionBrush? ink, (float X, float Y, float W, float H) r, float x, float y)
    {
        if (s.Ink.Brush != ink && s.Ink.Brush is CompositionSurfaceBrush ob && ob != ink) { (ob.Surface as IDisposable)?.Dispose(); ob.Dispose(); }
        s.Ink.Brush = ink;
        s.Ink.Offset = new Vector3(r.X - x, r.Y - y, 0);
        s.Ink.Size = new Vector2(r.W, r.H);
    }

    /// <summary>从上一个目标移到这个目标的方向（0 左 1 右 2 上 3 下），按两个中心的实际位移取主轴；没有上一个算往右。</summary>
    public int DirTo(float x, float y, float w, float h)
    {
        if ((_last ?? _from) is not { } a) return 1;
        float dx = (x + w / 2) - (a.X + a.W / 2), dy = (y + h / 2) - (a.Y + a.H / 2);
        return MathF.Abs(dx) >= MathF.Abs(dy) ? (dx < 0 ? 0 : 1) : (dy < 0 ? 2 : 3);
    }
}

/// <summary>
/// 图标组件的选中层（白底 + 黑图标）：用一个圆形裁切露出来。
/// 进来：圆心在移来那一侧边缘的中点，半径 0 → 盖满；底板先缩一点再弹回；图标从移动方向滑进来、放大一点再回落。
/// 退出：圆心在移动方向那一侧边缘的中点，半径缩回 0（白色跟着焦点收走）。
/// </summary>
public sealed class Reveal
{
    private static readonly Bezier Grow = new(.3f, .7f, .2f, 1f), Shrink = new(.5f, 0f, .75f, 0f);
    private readonly Gfx G;
    private readonly CompositionEllipseGeometry _geo;
    /// <summary>进来时缩一下再弹回的那层（白底）；图标层（滑进来、放大回落）。可以为空。</summary>
    public Visual? Plate, Icon;

    public Reveal(Gfx g, Visual layer)
    {
        G = g;
        _geo = g.C.CreateEllipseGeometry();
        _geo.Radius = Vector2.Zero;
        layer.Clip = g.C.CreateGeometricClip(_geo);
    }

    private static Vector2 EntrySide(int dir, float w, float h) => dir switch { 0 => new(w, h / 2), 1 => new(0, h / 2), 2 => new(w / 2, h), _ => new(w / 2, 0) };
    private static Vector2 ExitSide(int dir, float w, float h) => dir switch { 0 => new(0, h / 2), 1 => new(w, h / 2), 2 => new(w / 2, 0), _ => new(w / 2, h) };
    private static float Cover(float w, float h) => MathF.Sqrt(w * w + h * h) * 1.05f;

    public void Set(bool on, float w, float h)
    {
        _geo.StopAnimation("Radius");
        _geo.Center = new Vector2(w / 2, h / 2);
        float r = on ? Cover(w, h) : 0;
        _geo.Radius = new Vector2(r, r);
    }

    public void Enter(int dir, float w, float h)
    {
        _geo.StopAnimation("Radius");
        _geo.Center = EntrySide(dir, w, h);
        float r = Cover(w, h);
        var a = G.C.CreateVector2KeyFrameAnimation();
        a.InsertKeyFrame(0, Vector2.Zero, G.Linear);
        a.InsertKeyFrame(1, new Vector2(r, r), G.Ease(Grow));
        a.Duration = Gfx.Ms(420);
        a.DelayTime = Gfx.Ms(50);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        _geo.StartAnimation("Radius", a);

        float m = MathF.Max(w, h);
        if (Plate != null)
        {
            float s0 = 1 - Math.Clamp(12 / m, 0.015f, 0.06f);
            Pop(Plate, w, h, s0, 1 + (1 - s0) * 0.25f, Vector3.Zero, 460);
        }
        if (Icon != null)
        {
            float s0 = 1 - Math.Clamp(16 / m, 0.03f, 0.18f);
            float slide = MathF.Min(10, m * 0.08f);
            var from = dir switch { 0 => new Vector3(slide, 0, 0), 1 => new Vector3(-slide, 0, 0), 2 => new Vector3(0, slide, 0), _ => new Vector3(0, -slide, 0) };
            Pop(Icon, w, h, s0, 1 + (1 - s0) * 0.45f, from, 480);
        }
    }

    public void Exit(int dir, float w, float h)
    {
        _geo.StopAnimation("Radius");
        var cur = Cover(w, h);
        _geo.Center = ExitSide(dir, w, h);
        var a = G.C.CreateVector2KeyFrameAnimation();
        a.InsertKeyFrame(0, new Vector2(cur, cur), G.Linear);
        a.InsertKeyFrame(1, Vector2.Zero, G.Ease(Shrink));
        a.Duration = Gfx.Ms(260);
        _geo.StartAnimation("Radius", a);
    }

    private void Pop(Visual v, float w, float h, float s0, float over, Vector3 from, double ms)
    {
        var size = v is SpriteVisual sv && sv.Size != Vector2.Zero ? sv.Size : new Vector2(w, h);
        var o = v.Offset;
        v.CenterPoint = new Vector3(size.X / 2, size.Y / 2, 0);
        var sc = G.C.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0, new Vector3(s0, s0, 1), G.Linear);
        sc.InsertKeyFrame(0.62f, new Vector3(over, over, 1), G.Ease(Grow));
        sc.InsertKeyFrame(1, Vector3.One, G.Ease(new Bezier(.4f, 0f, .2f, 1f)));
        sc.Duration = Gfx.Ms(ms);
        sc.DelayTime = Gfx.Ms(50);
        sc.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Scale", sc);
        if (from != Vector3.Zero)
        {
            var of = G.C.CreateVector3KeyFrameAnimation();
            of.InsertKeyFrame(0, o + from, G.Linear);
            of.InsertKeyFrame(1, o, G.Ease(Grow));
            of.Duration = Gfx.Ms(ms * 0.8);
            of.DelayTime = Gfx.Ms(50);
            of.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            v.StartAnimation("Offset", of);
        }
    }
}
