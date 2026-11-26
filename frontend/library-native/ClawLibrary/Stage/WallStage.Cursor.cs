// 单行首页一族的焦点框（四角括号：光晕 + 沿周长流光的主线 + 白边光），右侧栏和左侧栏共用这一份。
// 移动：尺寸不变时位置弹簧 + 沿移动方向的橡皮筋；尺寸变了按新尺寸重画，从旧大小弹过去。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class CurSet
    {
        public required SpriteVisual Glow, Line, Line2, Rim;
        public float W, H, R;
        /// <summary>上一次给的目标位置（精灵的 Offset）。动画跑着时读 Visual.Offset 拿到的是旧的静态值，不能拿它算距离。</summary>
        public Vector3 To;
        public bool HasTo;
        public bool Shown;
        public Visual[] All => new Visual[] { Glow, Line, Line2, Rim };
    }

    /// <summary>新建一套焦点框精灵（放在 parent 最上面），一开始全透明。</summary>
    private CurSet CurNew(ContainerVisual parent)
    {
        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var c = new CurSet { Glow = Sp(), Line = Sp(), Line2 = Sp(), Rim = Sp() };
        foreach (var v in c.All) { v.Opacity = 0; parent.Children.InsertAtTop(v); }
        return c;
    }

    private readonly Dictionary<string, (CompositionDrawingSurface Glow, CompositionDrawingSurface Line, CompositionDrawingSurface Rim, float Raster, CompositionBrush L1, CompositionBrush L2)> _spCurCache = new();

    private void CurShow(CurSet c, bool on, double ms, double delay = 0)
    {
        c.Shown = on;
        foreach (var v in c.All) { v.StopAnimation("Opacity"); v.StartAnimation("Opacity", CsS(on ? 1 : 0, Math.Max(1, ms), Bezier.EaseOut, delay)); }
    }

    private const float CurGlowBlurDp = 6.5f;
    private static readonly Windows.UI.Color CurGlowColor = RwC1;

    /// <summary>括号离容器边的距离：按容器短边取，小按钮 6、大卡片最多 14（11-26 统一：之前侧栏 / 控制中心固定 5.5，括号贴着容器）。</summary>
    private static float CurOutsetFor(float w, float h) => Math.Clamp(0.045f * MathF.Min(w, h), 6f, 14f);

    /// <summary>
    /// 套到容器 (x, y, w, h) 上：外扩按容器大小统一取，括号圆角 = 容器圆角 + 外扩（同心，线和容器的间距一圈都一样）。
    /// </summary>
    private void CurToCocoon(CurSet c, float x, float y, float w, float h, float targetRadius, bool move)
    {
        float tr = Math.Clamp(targetRadius, 0f, MathF.Min(w, h) / 2f), o = CurOutsetFor(w, h);
        CurTo(c, x, y, w, h, o, tr + o, move);
    }

    private static CanvasGeometry CocoonBracketPath(ICanvasResourceCreator dev, float W, float H, float r, float pad)
        => CocoonBracketPath(dev, W, H, r, pad, pad);

    /// <summary>
    /// 全局唯一的括号几何（11-26 jinyu：照 Cocoon，括号几乎只盖住容器的圆角，不再伸出直边）。
    /// W×H 是线的中心框，r 是框的圆角（= 容器圆角 + 外扩，已同心）。每个角：
    ///   一段真圆弧（不是折线拼的），两头各接一小截直线，直线 0.28·r（实测 Cocoon 卡片上括号每边只盖住约 27%）；
    ///   同一条边上两个括号之间至少留 gap 的缺口；边太短（胶囊、圆）放不下缺口时，从弧的两头往回剪，
    ///   所以胶囊两端是带缺口的半圆，正圆是四段对称的弧——都是同一套算法，不再分三种写法。
    /// r 太小的方角容器（左侧栏的行）至少给 0.12·短边 的直线，括号不会缩成一个点。
    /// </summary>
    private static CanvasGeometry CocoonBracketPath(ICanvasResourceCreator dev, float W, float H, float r, float padX, float padY)
    {
        float mn = MathF.Min(W, H);
        r = Math.Clamp(r, 0f, mn / 2f);
        float gap = Math.Clamp(0.16f * mn, 14f, 34f);
        float legBase = r >= 0.1f * mn ? 0.28f * r : 0.12f * mn;   // 有圆角：腿只有圆角的 0.28（实测 Cocoon 卡片上每边只盖住约 27%）；方角才给最短 0.12·短边
        // 某条边上：两个括号之间的直线有多长可用；够留缺口就给腿，不够就剪弧
        (float Leg, float Trim) Side(float len)
        {
            float free = len - 2 * r;
            if (free >= gap) return (MathF.Min(legBase, (free - gap) / 2f), 0f);
            float t = (gap - MathF.Max(0f, free)) / 2f;   // 弧在这条边方向上要让出的距离
            return (0f, r > 0.5f ? MathF.Asin(Math.Clamp(t / r, 0f, 1f)) : 0f);
        }
        var (legX, trimX) = Side(W);   // 上下边：腿沿 x，剪的是弧靠上 / 下边那头
        var (legY, trimY) = Side(H);   // 左右边
        // 四段弧都超过 90° 被剪光（极端小框）就退回一个整圈，避免画空
        if (trimX + trimY >= MathF.PI / 2 - 0.05f) { trimX = trimY = MathF.PI / 4 - 0.35f; }
        float x0 = padX, y0 = padY, x1 = padX + W, y1 = padY + H;
        using var pb = new CanvasPathBuilder(dev);
        // 角：圆心 (cx, cy)，弧从 a0 顺时针扫到 a1（弧度，y 向下）；aEdge 那头接 legA，bEdge 那头接 legB
        void Corner(float cx, float cy, float a0, float a1, Vector2 legStartDir, float legStart, Vector2 legEndDir, float legEnd)
        {
            var p0 = new Vector2(cx + r * MathF.Cos(a0), cy + r * MathF.Sin(a0));
            var p1 = new Vector2(cx + r * MathF.Cos(a1), cy + r * MathF.Sin(a1));
            pb.BeginFigure(p0 + legStartDir * legStart);
            if (legStart > 0.5f) pb.AddLine(p0);
            if (r > 0.5f && a1 - a0 > 0.01f) pb.AddArc(p1, r, r, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
            if (legEnd > 0.5f) pb.AddLine(p1 + legEndDir * legEnd);
            pb.EndFigure(CanvasFigureLoop.Open);
        }
        float P = MathF.PI;
        // 左上：从左边（180°）到上边（270°）；左边那头剪 trimY、腿往下；上边那头剪 trimX、腿往右
        Corner(x0 + r, y0 + r, P + trimY, 1.5f * P - trimX, new Vector2(0, 1), legY, new Vector2(1, 0), legX);
        // 右上：上边（270°）→ 右边（360°）
        Corner(x1 - r, y0 + r, 1.5f * P + trimX, 2 * P - trimY, new Vector2(-1, 0), legX, new Vector2(0, 1), legY);
        // 右下：右边（0°）→ 下边（90°）
        Corner(x1 - r, y1 - r, trimY, 0.5f * P - trimX, new Vector2(0, -1), legY, new Vector2(-1, 0), legX);
        // 左下：下边（90°）→ 左边（180°）
        Corner(x0 + r, y1 - r, 0.5f * P + trimX, P - trimY, new Vector2(1, 0), legX, new Vector2(0, -1), legY);
        return CanvasGeometry.CreatePath(pb);
    }

    /// <summary>括号套到方框 (x, y, w, h) 上：外扩 pad、括号圆角 r。</summary>
    private void CurTo(CurSet c, float x, float y, float w, float h, float pad, float r, bool move)
    {
        float W = w + pad * 2, H = h + pad * 2;
        float sw = W + RmCurPad * 2, sh = H + RmCurPad * 2;
        var to = new Vector3(x + w / 2 - sw / 2, y + h / 2 - sh / 2, 0);
        bool resize = MathF.Abs(c.W - W) > 0.5f || MathF.Abs(c.H - H) > 0.5f || MathF.Abs(c.R - r) > 0.5f;
        if (resize)
        {
            float ow = c.W, oh = c.H;
            var from = c.HasTo ? c.To : c.Glow.Offset;
            var oldCenter = new Vector3(from.X + (ow + RmCurPad * 2) / 2, from.Y + (oh + RmCurPad * 2) / 2, 0);
            CurPaint(c, W, H, r);
            foreach (var v in c.All)
            {
                v.StopAnimation("Offset");
                v.Offset = ow > 0 ? oldCenter - new Vector3(sw / 2, sh / 2, 0) : to;
                if (move && !Reduced && ow > 0)
                {
                    v.Scale = new Vector3((ow + RmCurPad * 2) / sw, (oh + RmCurPad * 2) / sh, 1);
                    v.StartAnimation("Scale", RwSpring(Vector3.One));
                }
                else { v.StopAnimation("Scale"); v.Scale = Vector3.One; }
            }
        }
        // 11-26 jinyu：上下移动 / 连按时焦点框偶尔「往外一扩」像抽搐 —— 原因是这里的橡皮筋拉伸：
        // 距离拿 Visual.Offset 算，动画跑着时那是很久以前的静态值，原地重画（读数刷新、内容刷新）也会算出一大段距离，
        // 框就突然拉长 45 %；连按时每次又从 1 重新开始拉。现在不拉伸，只用位置弹簧；目标没变就不重启动画。
        bool same = c.HasTo && Vector3.Distance(c.To, to) < 0.25f && !resize;
        c.To = to; c.HasTo = true;
        if (same) return;
        foreach (var v in c.All)
        {
            if (move && !Reduced) v.StartAnimation("Offset", RwSpring(to)); else RwJump(v, to);
        }
    }

    private void CurPaint(CurSet cs, float W, float H, float r)
    {
        cs.W = W; cs.H = H; cs.R = r;
        float R = CsRaster, sw = W + RmCurPad * 2, sh = H + RmCurPad * 2;
        string key = $"{W:0.#}x{H:0.#}r{r:0.#}";
        if (!_spCurCache.TryGetValue(key, out var c) || c.Raster != R)
        {
            int pw = (int)Math.Ceiling(sw * R), ph = (int)Math.Ceiling(sh * R);
            var dev = G.Device;
            using var path = CocoonBracketPath(dev, W, H, r, RmCurPad);
            var glow = G.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(glow))
            {
                ds.Clear(Colors.Transparent);
                using var cl = new CanvasCommandList(ds);
                using (var s = cl.CreateDrawingSession()) { s.Transform = Matrix3x2.CreateScale(R); s.DrawGeometry(path, CurGlowColor, RwSw * 2, RwRoundStroke); }
                using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = CurGlowBlurDp * RwDp * R / 2, BorderMode = EffectBorderMode.Soft };
                ds.DrawImage(blur);
            }
            var rim = G.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(rim))
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateScale(R);
                using var outer = path.Stroke(RwSw + 2 * RwDp, RwRoundStroke);
                using var inner = path.Stroke(MathF.Max(0.1f, RwSw - 2 * RwDp), RwRoundStroke);
                using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                ds.FillGeometry(ring, Windows.UI.Color.FromArgb(102, 0xfa, 0xfa, 0xfa));
            }
            var line = G.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(line))
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateScale(R);
                ds.DrawGeometry(path, Colors.White, RwSw, RwRoundStroke);
            }
            c = (glow, line, rim, R, RsPerimFlow(line, W, H, r, right: false), RsPerimFlow(line, W, H, r, right: true));
            _spCurCache[key] = c;
        }
        ((CompositionSurfaceBrush)cs.Glow.Brush).Surface = c.Glow;
        ((CompositionSurfaceBrush)cs.Rim.Brush).Surface = c.Rim;
        cs.Line.Brush = c.L1;
        cs.Line2.Brush = c.L2;
        cs.Line.Clip = G.C.CreateInsetClip(0, 0, sw / 2, 0);
        cs.Line2.Clip = G.C.CreateInsetClip(sw / 2, 0, 0, 0);
        foreach (var v in cs.All) { v.Size = new Vector2(sw, sh); v.CenterPoint = new Vector3(sw / 2, sh / 2, 0); }
    }
}
