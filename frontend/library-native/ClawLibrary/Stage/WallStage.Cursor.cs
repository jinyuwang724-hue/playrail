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

    /// <summary>
    /// 照 Cocoon 的算法套括号：外扩 = clamp(0.12 × 短边, 5 dp, 24 dp) × 粗细系数 0.6；括号圆角 = 目标自己的圆角 + 外扩 / 2（和目标同心）。
    /// </summary>
    private void CurToCocoon(CurSet c, float x, float y, float w, float h, float targetRadius, bool move)
    {
        float o = Math.Clamp(0.12f * MathF.Min(w, h), 5 * RwDp, 24 * RwDp) * RwThick;
        CurTo(c, x, y, w, h, o, targetRadius + o / 2, move);
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
            using var path = SpBracketPath(dev, W, H, r, RmCurPad);
            var glow = G.Surface(pw, ph);
            using (var ds = CanvasComposition.CreateDrawingSession(glow))
            {
                ds.Clear(Colors.Transparent);
                using var cl = new CanvasCommandList(ds);
                using (var s = cl.CreateDrawingSession()) { s.Transform = Matrix3x2.CreateScale(R); s.DrawGeometry(path, RwC2, RwSw * 2, RwRoundStroke); }
                using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 7 * RwDp * R / 2, BorderMode = EffectBorderMode.Soft };
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
