// 二级页的局部动效（11-21 jinyu 发的 Cocoon「Button animation」视频逐帧）：
//   开关：圆钮 ~120 ms 滑到另一头，底色同时渐变；开关管着的子项（资料类型、资料来源……）跟着展开 / 收起——
//         展开时下面的行和分组卡片往下滑开、卡片从旧高度长到新高度，新出来的行淡入；收起时下面的行滑上来；
//   来源排序：焦点落到来源行上，右边的「↓ ↑」胶囊往左展开成两个按钮，各带一个十字键图标（←/→ 就是挪位置）；
//         换位置时两行互相滑过去，焦点框跟着这一行走；
//   焦点框流光：沿整圈周长走（宽框两端各是一段渐变，慢慢流），不是整块左右换色。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static readonly Bezier RsSlideEase = new(0.22f, 1f, 0.36f, 1f);
    private const double RsSlideMs = 170;

    /// <summary>重画后：按上一次排版的位置，把挪了的行 / 分组卡片从旧位置滑到新位置，新出来的淡入，长高的卡片从旧高度展开。</summary>
    private void RsAnimateRelayout(RsState rs, RsPage page, Dictionary<string, float> oldRows, Dictionary<string, (float Y0, float Y1)> oldGroups)
    {
        if (Reduced) return;
        const float pad = 12;
        for (int k = 0; k < rs.Layout.Count && k < rs.RowVis.Count; k++)
        {
            var (i, y) = rs.Layout[k];
            var v = rs.RowVis[k];
            var to = new Vector3(RsListX - pad, y - pad, 0);
            string key = RsKey(page.Rows[i]);
            if (oldRows.TryGetValue(key, out float oy))
            {
                if (MathF.Abs(oy - y) < 0.5f) continue;
                v.Offset = new Vector3(RsListX - pad, oy - pad, 0);
                v.StartAnimation("Offset", CsV3(to, RsSlideMs, RsSlideEase));
            }
            else
            {
                // 新出来的子项：从上面一点淡入（跟着展开的卡片一起出来）
                float target = v.Opacity;
                v.Opacity = 0;
                v.Offset = to - new Vector3(0, 22, 0);
                v.StartAnimation("Offset", CsV3(to, RsSlideMs + 40, RsSlideEase, 30));
                v.StartAnimation("Opacity", CsS(target, 140, Bezier.EaseOut, 50));
            }
        }
        const float gpad = 24;
        int g = 0;
        foreach (var (key, (y0, y1)) in rs.GroupRects)
        {
            if (g >= rs.GroupVis.Count) break;
            var v = rs.GroupVis[g++];
            var to = new Vector3(RsListX - gpad, y0 - gpad, 0);
            if (oldGroups.TryGetValue(key, out var old))
            {
                if (MathF.Abs(old.Y0 - y0) > 0.5f)
                {
                    v.Offset = new Vector3(RsListX - gpad, old.Y0 - gpad, 0);
                    v.StartAnimation("Offset", CsV3(to, RsSlideMs, RsSlideEase));
                }
                float grow = (y1 - y0) - (old.Y1 - old.Y0);
                if (grow > 0.5f)
                {
                    // 卡片长高：先裁成旧高度，再展开
                    var clip = G.C.CreateInsetClip(0, 0, 0, grow);
                    v.Clip = clip;
                    clip.StartAnimation("BottomInset", CsS(0, RsSlideMs, RsSlideEase));
                }
            }
            else
            {
                v.Opacity = 0;
                v.StartAnimation("Opacity", CsS(1, 140, Bezier.EaseOut));
            }
        }
    }

    // ---------------- 焦点框流光：沿周长走 ----------------

    /// <summary>
    /// 宽焦点框（一整行宽）的流光。卡片上的流光是渐变方向绕中心转，套到 1700×156 的框上就成了左右两块整体换色。
    /// 照 Cocoon 改成沿周长：周长上 s 处的颜色 = 混色(0.5 + 0.5·cos 2π(s − φ))，φ 随时间走；
    /// 转速全局统一（11-21 jinyu）：和首页游戏卡片同一个绝对周期 RwPeriodMs（1.8 s 一圈），不按周长算——小框不会转得更快。
    /// 每一半括号用一个竖向渐变，上 / 中 / 下三个色标各按自己在周长上的位置做颜色关键帧动画。
    /// </summary>
    private CompositionMaskBrush RsPerimFlow(CompositionDrawingSurface mask, float W, float H, float r, bool right)
    {
        float P = 2 * (W + H);
        // 顺时针：上边左→右，右边上→下，下边右→左，左边下→上（起点在左上角）
        float[] s = right
            ? new[] { (W - r) / P, (W + H / 2) / P, (W + H + r) / P }
            : new[] { (2 * W + 2 * H + r) / P % 1f, (2 * W + 1.5f * H) / P, (2 * W + H - r) / P };   // 上、中、下
        Color Mix(float t) => CocoonPalette.MixFlow(t);
        Color At(float si, float phase) => Mix(0.5f + 0.5f * MathF.Cos(2 * MathF.PI * (si - phase)));
        var grad = G.C.CreateLinearGradientBrush();
        grad.StartPoint = new Vector2(0.5f, 0);
        grad.EndPoint = new Vector2(0.5f, 1);
        double ms = RwPeriodMs;
        for (int i = 0; i < 3; i++)
        {
            var stop = CkStop(i / 2f, At(s[i], 0));
            grad.ColorStops.Add(stop);
            if (Reduced) continue;
            var a = G.C.CreateColorKeyFrameAnimation();
            a.InterpolationColorSpace = CompositionColorSpace.Rgb;
            const int n = 24;
            for (int k = 0; k <= n; k++) a.InsertKeyFrame(k / (float)n, At(s[i], k / (float)n), G.Linear);
            a.Duration = TimeSpan.FromMilliseconds(ms);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            stop.StartAnimation("Color", a);
        }
        var maskBrush = G.C.CreateMaskBrush();
        maskBrush.Source = grad;
        maskBrush.Mask = G.C.CreateSurfaceBrush(mask);
        return maskBrush;
    }

    // ---------------- 来源行：↓↑ 胶囊展开成两个十字键按钮 ----------------

    private const float RsPillX = 1374, RsPillW = 258, RsPillH = 84;

    /// <summary>焦点在来源（排序）行上：右边画一层展开的按钮，从小胶囊的宽度往左展开（锚在右边），淡入；离开时淡出。</summary>
    private void RsUpdatePill(RsState rs, RsPage page, bool animate)
    {
        int sel = page.Open < 0 && page.Paint == null && page.Sel < page.Rows.Count && page.Rows[page.Sel].Kind == RsKind.Order ? page.Sel : -1;
        if (sel < 0)
        {
            if (rs.Pill != null && rs.PillRow >= 0) rs.Pill.StartAnimation("Opacity", CsS(0, animate && !Reduced ? 90 : 1, Bezier.EaseOut));
            rs.PillRow = -1;
            return;
        }
        var row = page.Rows[sel];
        float R = CsRaster, pad = 10, w = RsPillW + pad * 2, h = RsPillH + pad * 2;
        if (rs.Pill == null)
        {
            rs.Pill = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            rs.Pill.Size = new Vector2(w, h);
            rs.Pill.CenterPoint = new Vector3(pad + RsPillW, h / 2, 0);
            rs.PillSurf = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
            ((CompositionSurfaceBrush)rs.Pill.Brush).Surface = rs.PillSurf;
            rs.Rows.Children.InsertAtTop(rs.Pill);
        }
        using (var ds = CanvasComposition.CreateDrawingSession(rs.PillSurf!))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(R);
            RsPaintPillExpanded(ds, row.CanDown, row.CanUp);
        }
        float y = RsRowY(rs, sel), cy = y + RsRowH / 2;
        rs.Pill.Offset = new Vector3(RsPillX - pad, cy - RsPillH / 2 - pad, 0);
        // 行精灵是后加的会盖住它：挪回最上面（马上要重新起动画，挪动不碍事）
        rs.Rows.Children.Remove(rs.Pill);
        rs.Rows.Children.InsertAtTop(rs.Pill);
        rs.Pill.StopAnimation("Opacity");
        rs.Pill.StopAnimation("Scale");
        bool fresh = rs.PillRow != sel;
        rs.PillRow = sel;
        if (animate && fresh && !Reduced)
        {
            rs.Pill.Opacity = 0;
            rs.Pill.Scale = new Vector3(164f / RsPillW, 1, 1);
            rs.Pill.StartAnimation("Opacity", CsS(1, 80, Bezier.EaseOut));
            rs.Pill.StartAnimation("Scale", CsV3(Vector3.One, 150, RsSlideEase));
        }
        else { rs.Pill.Opacity = 1; rs.Pill.Scale = Vector3.One; }
    }

    /// <summary>展开的排序按钮：灰底大胶囊里两个深一点的小胶囊，左「✚ ↓」右「✚ ↑」，挪不动的那边变淡。</summary>
    private static void RsPaintPillExpanded(CanvasDrawingSession ds, bool canDown, bool canUp)
    {
        var dev = ds.Device;
        if (CocoonPalette.Dark) DkWell(ds, 0, 0, RsPillW, RsPillH, RsPillH / 2); else ds.FillRoundedRectangle(0, 0, RsPillW, RsPillH, RsPillH / 2, RsPillH / 2, Color.FromArgb(255, 0xE4, 0xE5, 0xE9));
        float iw = (RsPillW - 18) / 2, ih = RsPillH - 14;
        for (int s = 0; s < 2; s++)
        {
            bool on = s == 0 ? canDown : canUp;
            float x = 6 + s * (iw + 6), y = 7;
            ds.FillRoundedRectangle(x, y, iw, ih, ih / 2, ih / 2, Ck(Color.FromArgb(255, 0xCC, 0xCD, 0xD2)));
            var ink = on ? Ck(Color.FromArgb(255, 0x1E, 0x1E, 0x24)) : Ck(Color.FromArgb(255, 0xA8, 0xAA, 0xB0));
            // 十字键：整个十字淡灰，要按的那一臂（↓ 按钮是左、↑ 按钮是右）深色并带小三角，提示按十字键的左 / 右
            float dx = x + 34, dy = y + ih / 2;
            var faint = Ck(Color.FromArgb(255, 0xA8, 0xAA, 0xB0));
            ds.FillRoundedRectangle(dx - 16, dy - 6, 32, 12, 3, 3, faint);
            ds.FillRoundedRectangle(dx - 6, dy - 16, 12, 32, 3, 3, faint);
            float side = s == 0 ? -1 : 1;
            ds.FillRoundedRectangle(side < 0 ? dx - 16 : dx + 4, dy - 6, 12, 12, 3, 3, ink);
            using (var tp = new CanvasPathBuilder(dev))
            {
                float tx = dx + side * 10;
                tp.BeginFigure(tx + side * 3, dy); tp.AddLine(tx - side * 2, dy - 4); tp.AddLine(tx - side * 2, dy + 4);
                tp.EndFigure(CanvasFigureLoop.Closed);
                using var tri = CanvasGeometry.CreatePath(tp);
                ds.FillGeometry(tri, Color.FromArgb(255, 0xF2, 0xF2, 0xF4));
            }
            // 箭头
            float ax = x + iw - 34, d = s == 0 ? 1 : -1;
            using var pb = new CanvasPathBuilder(dev);
            pb.BeginFigure(ax - 13, dy - 6 * d); pb.AddLine(ax, dy + 7 * d); pb.AddLine(ax + 13, dy - 6 * d);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(g, ink, 7, RwRoundStroke);
        }
    }

    // ---------------- 开关：圆钮滑动 + 底色渐变 ----------------

    /// <summary>
    /// 开关切换的过渡：在开关位置盖三层（关的底、开的底、圆钮），开的底淡入 / 淡出、圆钮滑到另一头，
    /// 结束后拿掉（下面那行已经按新状态画好了）。
    /// </summary>
    private void RsToggleFx(RsState rs, float rowY, bool nowOn)
    {
        if (Reduced) return;
        float R = CsRaster, x = 1656, w = 120, h = 76, cy = rowY + RsRowH / 2, ty = cy - h / 2, pad = 8;
        SpriteVisual Layer(Action<CanvasDrawingSession> paint, float lw, float lh)
        {
            var surf = G.Surface((int)Math.Ceiling((lw + pad * 2) * R), (int)Math.Ceiling((lh + pad * 2) * R));
            using (var ds = CanvasComposition.CreateDrawingSession(surf))
            {
                ds.Clear(Colors.Transparent);
                ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(R);
                paint(ds);
            }
            var v = G.Sprite(G.C.CreateSurfaceBrush(surf), relative: false);
            v.Size = new Vector2(lw + pad * 2, lh + pad * 2);
            v.Comment = "fx";
            rs.Rows.Children.InsertAtTop(v);
            After(200, () => { try { rs.Rows.Children.Remove(v); v.Dispose(); surf.Dispose(); } catch (ObjectDisposedException) { } });
            return v;
        }
        var off = Layer(ds =>
        {
            CpToggleOffTrack(ds, 0, 0, w, h, 4);
        }, w, h);
        var on = Layer(ds =>
        {
            using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(w, h) };
            ds.FillRoundedRectangle(0, 0, w, h, h / 2, h / 2, br);
        }, w, h);
        var knob = Layer(ds =>
        {
            RsSoftShadow(ds, 0, 3, 54, 54, 27, 4, 0.22f);
            ds.FillCircle(27, 27, 27, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
        }, 54, 54);
        foreach (var v in new[] { off, on }) v.Offset = new Vector3(x - pad, ty - pad, 0);
        float k0 = nowOn ? x + 38 - 27 : x + 82 - 27, k1 = nowOn ? x + 82 - 27 : x + 38 - 27;
        knob.Offset = new Vector3(k0 - pad, cy - 27 - pad, 0);
        knob.StartAnimation("Offset", CsV3(new Vector3(k1 - pad, cy - 27 - pad, 0), 150, RsSlideEase));
        on.Opacity = nowOn ? 0 : 1;
        on.StartAnimation("Opacity", CsS(nowOn ? 1 : 0, 150, Bezier.EaseOut));
    }
}
