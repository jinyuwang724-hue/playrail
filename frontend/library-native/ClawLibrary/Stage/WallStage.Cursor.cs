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

    private const float CurOutset = 5.5f, CurGlowBlurDp = 6.5f;
    private static readonly Color CurGlowColor = RwC1;

    /// <summary>
    /// 照 Cocoon 的算法套同心括号：统一紧凑外扩 CurOutset，线中心圆角 = 目标圆角 + CurOutset（真正同心）。
    /// </summary>
    private void CurToCocoon(CurSet c, float x, float y, float w, float h, float targetRadius, bool move)
    {
        float tr = Math.Clamp(targetRadius, 0f, MathF.Min(w, h) / 2f);
        CurTo(c, x, y, w, h, CurOutset, tr + CurOutset, move);
    }

    private static CanvasGeometry CocoonBracketPath(ICanvasResourceCreator dev, float W, float H, float r, float pad)
        => CocoonBracketPath(dev, W, H, r, pad, pad);

    /// <summary>
    /// Cocoon 紧凑控件焦点几何：
    /// 1) 圆形按钮（|W-H|≤2 且 r≥0.45·mn）：四段对称圆弧，12/3/6/9 点留出清晰呼吸缝；
    /// 2) 胶囊/整行（W>H+2 且 r≥0.44·H 或 H-2r&lt;24）：真半圆端头 + 极短水平腿，左右中缝约 20 px；
    /// 3) 常规圆角矩形卡片/列表行：真同心圆角 + 独立水平/垂直腿长，保证两段白边之间不粘连。
    /// </summary>
    private static CanvasGeometry CocoonBracketPath(ICanvasResourceCreator dev, float W, float H, float r, float padX, float padY)
    {
        float mn = MathF.Min(W, H);
        using var pb = new CanvasPathBuilder(dev);
        if (MathF.Abs(W - H) <= 2f && r >= mn * 0.45f)
        {
            float rc = mn / 2f, cx = padX + W / 2f, cy = padY + H / 2f;
            float gapRad = Math.Clamp(26f / rc, 0.30f, 0.62f);
            float half = (MathF.PI / 2f - gapRad) / 2f;
            for (int k = 0; k < 4; k++)
            {
                float mid = MathF.PI / 4f + k * MathF.PI / 2f, a0 = mid - half, a1 = mid + half;
                pb.BeginFigure(cx + rc * MathF.Cos(a0), cy + rc * MathF.Sin(a0));
                pb.AddArc(new Vector2(cx + rc * MathF.Cos(a1), cy + rc * MathF.Sin(a1)), rc, rc, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                pb.EndFigure(CanvasFigureLoop.Open);
            }
        }
        else if (W > H + 2f && (r >= H * 0.44f || H - 2f * r < 24f))
        {
            float rp = H / 2f, cy = padY + rp, lx = padX + rp, rx = padX + W - rp;
            float gapC = Math.Clamp(20f, 18f, H * 0.30f);
            float halfGap = MathF.Asin(Math.Clamp((gapC / 2f) / rp, 0.08f, 0.45f));
            float legH = MathF.Min(Math.Clamp(0.06f * H, 4f, 10f), MathF.Max(0f, (W - H - 24f) / 2f));

            float a0 = MathF.PI + halfGap;
            pb.BeginFigure(lx + rp * MathF.Cos(a0), cy + rp * MathF.Sin(a0));
            pb.AddArc(new Vector2(lx, padY), rp, rp, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
            if (legH > 0.5f) pb.AddLine(lx + legH, padY);
            pb.EndFigure(CanvasFigureLoop.Open);

            if (legH > 0.5f) { pb.BeginFigure(rx - legH, padY); pb.AddLine(rx, padY); }
            else pb.BeginFigure(rx, padY);
            float a1 = -halfGap;
            pb.AddArc(new Vector2(rx + rp * MathF.Cos(a1), cy + rp * MathF.Sin(a1)), rp, rp, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
            pb.EndFigure(CanvasFigureLoop.Open);

            float a2 = halfGap;
            pb.BeginFigure(rx + rp * MathF.Cos(a2), cy + rp * MathF.Sin(a2));
            pb.AddArc(new Vector2(rx, padY + H), rp, rp, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
            if (legH > 0.5f) pb.AddLine(rx - legH, padY + H);
            pb.EndFigure(CanvasFigureLoop.Open);

            if (legH > 0.5f) { pb.BeginFigure(lx + legH, padY + H); pb.AddLine(lx, padY + H); }
            else pb.BeginFigure(lx, padY + H);
            float a3 = MathF.PI - halfGap;
            pb.AddArc(new Vector2(lx + rp * MathF.Cos(a3), cy + rp * MathF.Sin(a3)), rp, rp, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
            pb.EndFigure(CanvasFigureLoop.Open);
        }
        else
        {
            float innerMn = MathF.Max(1f, mn - CurOutset * 2f);
            float sf = Math.Clamp(mn / (innerMn * 2.35f), 0.35f, 1.75f);
            float gapMin = MathF.Max(26f, MathF.Min(0.28f * mn, 38f));
            float cr = MathF.Min(r, MathF.Max(0f, (mn - gapMin) / 2f));
            float legBase = 14f * RwDp * sf;
            float legX = MathF.Max(0f, MathF.Min(legBase, (W - 2f * cr - gapMin) / 2f));
            float legY = MathF.Max(0f, MathF.Min(legBase, (H - 2f * cr - gapMin) / 2f));
            float x0 = padX, y0 = padY, x1 = padX + W, y1 = padY + H;

            pb.BeginFigure(x1 - cr - legX, y0);
            if (legX > 0.5f) pb.AddLine(x1 - cr, y0);
            if (cr > 0.5f) pb.AddArc(new Vector2(x1, y0 + cr), cr, cr, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
            if (legY > 0.5f) pb.AddLine(x1, y0 + cr + legY);
            pb.EndFigure(CanvasFigureLoop.Open);

            pb.BeginFigure(x1, y1 - cr - legY);
            if (legY > 0.5f) pb.AddLine(x1, y1 - cr);
            if (cr > 0.5f) pb.AddArc(new Vector2(x1 - cr, y1), cr, cr, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
            if (legX > 0.5f) pb.AddLine(x1 - cr - legX, y1);
            pb.EndFigure(CanvasFigureLoop.Open);

            pb.BeginFigure(x0 + cr + legX, y1);
            if (legX > 0.5f) pb.AddLine(x0 + cr, y1);
            if (cr > 0.5f) pb.AddArc(new Vector2(x0, y1 - cr), cr, cr, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
            if (legY > 0.5f) pb.AddLine(x0, y1 - cr - legY);
            pb.EndFigure(CanvasFigureLoop.Open);

            pb.BeginFigure(x0, y0 + cr + legY);
            if (legY > 0.5f) pb.AddLine(x0, y0 + cr);
            if (cr > 0.5f) pb.AddArc(new Vector2(x0 + cr, y0), cr, cr, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
            if (legX > 0.5f) pb.AddLine(x0 + cr + legX, y0);
            pb.EndFigure(CanvasFigureLoop.Open);
        }
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
            var oldCenter = new Vector3(c.Glow.Offset.X + (ow + RmCurPad * 2) / 2, c.Glow.Offset.Y + (oh + RmCurPad * 2) / 2, 0);
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
        float dx = to.X - c.Glow.Offset.X, dy = to.Y - c.Glow.Offset.Y;
        foreach (var v in c.All)
        {
            if (move && !Reduced) v.StartAnimation("Offset", RwSpring(to)); else RwJump(v, to);
            if (move && !Reduced && !resize) SpStretch(v, dx, dy, W, H);
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
