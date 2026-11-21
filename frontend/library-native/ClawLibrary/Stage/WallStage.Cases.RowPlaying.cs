// 「单行」样式的正在游玩（11-21 jinyu：照 Cocoon「game quit」视频，替换原来的「正在游戏」整屏面板）：
//   回到游戏库：不弹整屏面板，首页照常显示，正在跑的那张卡盖一层淡紫、右上角一个红色 ✕ 角标，焦点落到它上面；
//     A 回到游戏，X（或点 ✕ 角标）弹「退出游戏」小窗；
//   退出游戏小窗：标题栏「B ←  退出游戏」、游戏图标、「未保存的进度会丢失」、一整条绿色「Ⓐ 强制退出」；
//     按下后按钮变「正在关闭游戏…」，关掉后小窗直接消失，右上角弹一张会话卡「游戏名 / 已保存 · 本次玩了 N 分钟」；
//   另一款在跑时启动新游戏：「切换游戏」小窗——两个图标中间一个紫色箭头、一段说明、
//     白色「Ⓨ 直接启动」（两个都开着）和绿色「Ⓐ 退出并启动」。
// 小窗背景和游戏菜单同一套：首页组件层模糊 + 深灰遮罩；出现约 80 ms（0.94 → 1 放大 + 淡入），关掉几乎是一帧。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class RqState
    {
        public required ContainerVisual Root;
        public required SpriteVisual Scrim, Card, Icon;
        public SpriteVisual? Icon2;
        public required CsPage Page;
        public required GameSession Running;
        public LibraryItem? Next;
        public CompositionDrawingSurface? CardSurf;
        public LoadedImageSurface? IconSurf, Icon2Surf;
        public bool Busy, Failed, Closing;
        /// <summary>按内容量出来的卡片高度、说明文字底边（切换游戏的说明可能一行也可能三行）。</summary>
        public float H, TextBottom;
    }

    private RqState? _rq;
    /// <summary>「直接启动」：这一次启动不拦（两个游戏同时开着）。</summary>
    private bool _rqBypass;

    // 竖版封面 200×300（游戏库自己的封面，不裁成方形）；卡片宽 920，高按内容量
    private const float RqW = 920, RqCoverW = 200, RqCoverH = 300, RqCoverY = 148, RqPad = 40, RqTextW = 800, RqBtnH = 112;

    /// <summary>
    /// 排版：标题栏 37–111；封面 148–448；切换游戏时说明文字从 492 起（36 号，行距 1.3，最宽 800），
    /// 下面 28 的空再一行灰色提示，再空 36 是按钮，按钮下边距 40。退出游戏没有说明，提示直接在封面下面。
    /// </summary>
    private (float H, float TextBottom) RqMeasure(RqState rq)
    {
        float y = RqCoverY + RqCoverH + 44;
        if (rq.Next != null)
        {
            using var lay = RqTextLayout(G.Device, rq);
            y += (float)lay.LayoutBounds.Height;
            y += 28;
        }
        float textBottom = y;
        float warnBottom = y + 44;
        return (warnBottom + 36 + RqBtnH + 40, textBottom);
    }

    private static string RqSwitchText(RqState rq) => $"「{rq.Running.Item.Title}」还在运行。先退出它再启动「{rq.Next!.Title}」，或者直接启动，两个游戏同时开着。";

    private static CanvasTextLayout RqTextLayout(ICanvasResourceCreator dev, RqState rq)
    {
        string a = rq.Running.Item.Title, b = rq.Next!.Title, text = RqSwitchText(rq);
        using var tf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 36, FontWeight = new Windows.UI.Text.FontWeight(400), HorizontalAlignment = CanvasHorizontalAlignment.Center, WordWrapping = CanvasWordWrapping.Wrap };
        var lay = new CanvasTextLayout(dev, text, tf, RqTextW, 400);
        lay.SetFontWeight(1, a.Length, new Windows.UI.Text.FontWeight(600));
        int bi = text.IndexOf(b, a.Length + 2, StringComparison.Ordinal);
        if (bi >= 0) lay.SetFontWeight(bi, b.Length, new Windows.UI.Text.FontWeight(600));
        lay.LineSpacingMode = CanvasLineSpacingMode.Proportional;
        lay.LineSpacing = 1.3f;
        return lay;
    }
    private static readonly Color RqGreenA = Color.FromArgb(255, 0x72, 0xE0, 0x96), RqGreenB = Color.FromArgb(255, 0x0E, 0xCB, 0xAA);
    private static readonly Color RqBadge = Color.FromArgb(255, 0xF2, 0x56, 0x6E);

    private static string? RqArt(LibraryItem g) => g.Art is { } k ? (k.Has('q') ? k.File('q') : k.Has('p') ? k.File('p') : null) : null;

    // ---------------- 正在跑的卡：淡紫 + 红色 ✕ 角标 ----------------

    /// <summary>给这一页的卡标上「正在玩」（Label 当淡紫罩层、Caption 当角标；单行样式不用这两个）。</summary>
    private void RwMarkRunning(CsPage p)
    {
        if (p.Style != HomeStyle.Row) return;
        foreach (var c in p.Cases.Values)
        {
            bool run = c.Item != null && RunningGame(c.Item.Id) != null;
            if (!run)
            {
                if (c.Label != null) { c.Label.Parent?.Children.Remove(c.Label); c.Label.Dispose(); c.Label = null; }
                if (c.Caption != null) { c.Root.Children.Remove(c.Caption); c.Caption.Dispose(); c.Caption = null; }
                continue;
            }
            float w = c.Size.X, h = c.Size.Y;
            if (c.Label == null)
            {
                var tint = G.Sprite(G.Solid(Color.FromArgb(150, 0xA5, 0x96, 0xFF)), relative: false);
                tint.Size = new Vector2(w, h);
                var rr = G.C.CreateRoundedRectangleGeometry();
                rr.Size = new Vector2(w, h);
                rr.CornerRadius = new Vector2(w * RwRound);
                tint.Clip = G.C.CreateGeometricClip(rr);
                (c.Art.Parent ?? c.Root).Children.InsertAbove(tint, c.Art);   // 封面可能包在一层裁切容器里
                c.Label = tint;
                if (!Reduced) { tint.Opacity = 0; tint.StartAnimation("Opacity", CsS(1, 200, Bezier.EaseOut)); }
            }
            if (c.Caption == null)
            {
                float d = 64, R = CsRaster, pad = 8;
                var surf = CsDraw(d + pad * 2, d + pad * 2, ds =>
                {
                    float cx = pad + d / 2, cy = pad + d / 2, r = d / 2 - 4;
                    RsSoftShadow(ds, cx - r, cy - r + 3, 2 * r, 2 * r, r, 4, 0.25f);
                    ds.FillCircle(cx, cy, r, RqBadge);
                    ds.DrawCircle(cx, cy, r - 1, Color.FromArgb(90, 0xFF, 0xFF, 0xFF), 2);
                    ds.DrawLine(cx - 9, cy - 9, cx + 9, cy + 9, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 5, RwRoundStroke);
                    ds.DrawLine(cx + 9, cy - 9, cx - 9, cy + 9, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 5, RwRoundStroke);
                });
                var badge = G.Sprite(G.C.CreateSurfaceBrush(surf), relative: false);
                badge.Size = new Vector2(d + pad * 2, d + pad * 2);
                // 角标中心在卡的右上角往外一点（Cocoon：右 +8、上 −7）
                badge.Offset = new Vector3(w + 8 - d / 2 - pad, -7 - d / 2 - pad, 0);
                badge.CenterPoint = new Vector3(pad + d / 2, pad + d / 2, 0);
                c.Root.Children.InsertAtTop(badge);
                c.Caption = badge;
                if (!Reduced)
                {
                    badge.Scale = new Vector3(0.4f, 0.4f, 1);
                    badge.StartAnimation("Scale", CsV3(Vector3.One, 220, new Bezier(0.3f, 1.4f, 0.5f, 1f)));
                }
            }
        }
    }

    /// <summary>游戏库回到前台、有游戏在跑（单行样式）：不弹整屏面板，焦点落到正在跑的那张卡上。</summary>
    private bool RwFocusRunning(GameSession g)
    {
        if (_cs?.Page is not { Style: HomeStyle.Row } p) return false;
        RwMarkRunning(p);
        if (_rm != null || _rs != null || _rq != null) return true;
        int k = p.Items.FindIndex(x => x.Id == g.Item.Id);
        if (k < 0 || k == p.Sel || p.Rw is { PanelFocus: >= 0 }) return true;
        p.Prev = p.Sel;
        p.Sel = k;
        if (p.Tab == 0) CsLayoutRow(p, animate: true); else RwLayoutGrid(p, animate: true);
        RwMarkRunning(p);
        return true;
    }

    /// <summary>X：焦点卡正在跑就弹「退出游戏」。</summary>
    private bool RwQuitFocused()
    {
        if (_cs?.Page is not { Style: HomeStyle.Row } p || _rm != null || _rs != null || _rq != null) return false;
        if (p.Rw is { PanelFocus: >= 0 } || p.Sel >= p.Items.Count || RunningGame(p.Items[p.Sel].Id) is not { } g) return false;
        RqShow(p, g, null);
        return true;
    }

    // ---------------- 小窗 ----------------

    private void RqShow(CsPage p, GameSession running, LibraryItem? next)
    {
        if (_cs == null || _rq != null) return;
        Cue(Sfx.ModalShow);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var scrim = G.Sprite(G.Solid(RmScrim), relative: false);
        scrim.Size = new Vector2(CsW, CsH);
        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var rq = new RqState { Root = root, Scrim = scrim, Card = Sp(), Icon = Sp(), Page = p, Running = running, Next = next };
        if (next != null) rq.Icon2 = Sp();
        _rq = rq;
        root.Children.InsertAtTop(scrim);
        root.Children.InsertAtTop(rq.Card);
        root.Children.InsertAtTop(rq.Icon);
        if (rq.Icon2 != null) root.Children.InsertAtTop(rq.Icon2);
        _cs.Root.Children.InsertAtTop(root);
        RwBackdropFx(p, true, Reduced ? 1 : 230);
        (rq.H, rq.TextBottom) = RqMeasure(rq);
        float h = rq.H, x0 = (CsW - RqW) / 2, y0 = (CsH - h) / 2;
        // 退出：封面居中；切换：两张封面各在中线左右 200，中间是箭头
        RqPlaceIcon(rq.Icon, ref rq.IconSurf, running.Item, next == null ? x0 + RqW / 2 - RqCoverW / 2 : x0 + RqW / 2 - 200 - RqCoverW / 2, y0 + RqCoverY);
        if (rq.Icon2 != null) RqPlaceIcon(rq.Icon2, ref rq.Icon2Surf, next!, x0 + RqW / 2 + 200 - RqCoverW / 2, y0 + RqCoverY);
        RqPaint(rq);
        if (Reduced) return;
        scrim.Opacity = 0;
        scrim.StartAnimation("Opacity", CsS(1, 230, Bezier.EaseOut));
        rq.Card.Opacity = 0;
        rq.Card.Scale = new Vector3(0.94f, 0.94f, 1);
        rq.Card.StartAnimation("Opacity", CsS(1, 80, Bezier.EaseOut));
        rq.Card.StartAnimation("Scale", CsV3(Vector3.One, 160, new Bezier(0.22f, 1f, 0.36f, 1f)));
        foreach (var ic in new[] { rq.Icon, rq.Icon2 })
        {
            if (ic == null) continue;
            ic.Opacity = 0;
            ic.StartAnimation("Opacity", CsS(1, 150, Bezier.EaseOut, 50));
        }
    }

    private void RqPlaceIcon(SpriteVisual v, ref LoadedImageSurface? surf, LibraryItem g, float x, float y)
    {
        float R = CsRaster;
        if (RqArt(g) is { } art)
        {
            surf = Images.AcquireReuse(art, (int)Math.Ceiling(RqCoverW * R), (int)Math.Ceiling(RqCoverH * R));
            var b = G.C.CreateSurfaceBrush(surf);
            b.Stretch = CompositionStretch.UniformToFill;
            v.Brush = b;
        }
        else v.Brush = G.Solid(Color.FromArgb(255, 0xD8, 0xD9, 0xE0));
        v.Size = new Vector2(RqCoverW, RqCoverH);
        v.Offset = new Vector3(x, y, 0);
        var rr = G.C.CreateRoundedRectangleGeometry();
        rr.Size = new Vector2(RqCoverW, RqCoverH);
        rr.CornerRadius = new Vector2(RqCoverW * RwRound);
        v.Clip = G.C.CreateGeometricClip(rr);
    }

    private void RqPaint(RqState rq)
    {
        bool sw = rq.Next != null;
        float h = rq.H, x0 = (CsW - RqW) / 2, y0 = (CsH - h) / 2;
        float R = CsRaster, w = RqW + RqPad * 2, hh = h + RqPad * 2;
        rq.CardSurf ??= G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(hh * R));
        using (var ds = CanvasComposition.CreateDrawingSession(rq.CardSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(RqPad, RqPad) * Matrix3x2.CreateScale(R);
            RsSoftShadow(ds, 0, 12, RqW, h, 60, 22, 0.35f);
            ds.FillRoundedRectangle(0, 0, RqW, h, 60, 60, RsBg);
            RqHeaderPill(ds, 37, 37);
            string title = sw ? "切换游戏" : "退出游戏";
            using (var tf = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 48, FontWeight = RsWt(title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(title, new Windows.Foundation.Rect(192, 37, 600, 74), RmInk, tf);
            // 封面下的投影（封面本身是单独的精灵）
            foreach (float cx in sw ? new[] { RqW / 2 - 200, RqW / 2 + 200 } : new[] { RqW / 2 })
                RsSoftShadow(ds, cx - RqCoverW / 2, RqCoverY + 8, RqCoverW, RqCoverH, RqCoverW * RwRound, 12, 0.28f);
            if (sw)
            {
                RqArrow(ds, RqW / 2, RqCoverY + RqCoverH / 2);
                using var lay = RqTextLayout(ds.Device, rq);
                ds.DrawTextLayout(lay, (RqW - RqTextW) / 2, RqCoverY + RqCoverH + 44, RmInk);
            }
            string warn = rq.Failed ? "没能结束游戏，可以再试一次" : "未保存的进度会丢失";
            using (var wf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 32, FontWeight = new Windows.UI.Text.FontWeight(500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
                ds.DrawText(warn, new Windows.Foundation.Rect(0, rq.TextBottom, RqW, 44), rq.Failed ? RmRed : RsSub, wf);
            float by = h - 40 - RqBtnH;
            if (sw)
            {
                RqButton(ds, 37, by, 404, "Y", "直接启动", green: false, busy: false);
                RqButton(ds, 479, by, 404, "A", rq.Busy ? "正在关闭游戏…" : "退出并启动", green: true, busy: rq.Busy);
            }
            else RqButton(ds, 37, by, RqW - 74, "A", rq.Busy ? "正在关闭游戏…" : "强制退出", green: true, busy: rq.Busy);
        }
        ((CompositionSurfaceBrush)rq.Card.Brush).Surface = rq.CardSurf;
        rq.Card.Size = new Vector2(w, hh);
        rq.Card.Offset = new Vector3(x0 - RqPad, y0 - RqPad, 0);
        rq.Card.CenterPoint = new Vector3(w / 2, hh / 2, 0);
    }

    private static void RqHeaderPill(CanvasDrawingSession ds, float x, float y)
    {
        ds.FillRoundedRectangle(x, y, 134, 74, 37, 37, Color.FromArgb(255, 0xE2, 0xE3, 0xE7));
        ds.FillCircle(x + 36, y + 37, 21, Color.FromArgb(255, 0x9A, 0x9C, 0xA3));
        using (var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center })
            ds.DrawText("B", new Windows.Foundation.Rect(x + 15, y + 16, 42, 42), Color.FromArgb(255, 0xF2, 0xF2, 0xF4), f);
        using var pb = new CanvasPathBuilder(ds.Device);
        pb.BeginFigure(x + 114, y + 37); pb.AddLine(x + 78, y + 37); pb.EndFigure(CanvasFigureLoop.Open);
        pb.BeginFigure(x + 94, y + 21); pb.AddLine(x + 78, y + 37); pb.AddLine(x + 94, y + 53); pb.EndFigure(CanvasFigureLoop.Open);
        using var arrow = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(arrow, RmInk, 7, RwRoundStroke);
    }

    /// <summary>两个图标中间的紫色箭头（渐变、白描边、淡投影）。</summary>
    private void RqArrow(CanvasDrawingSession ds, float cx, float cy)
    {
        using var pb = new CanvasPathBuilder(ds.Device);
        pb.BeginFigure(cx - 34, cy); pb.AddLine(cx + 30, cy); pb.EndFigure(CanvasFigureLoop.Open);
        pb.BeginFigure(cx + 6, cy - 24); pb.AddLine(cx + 30, cy); pb.AddLine(cx + 6, cy + 24); pb.EndFigure(CanvasFigureLoop.Open);
        using var g = CanvasGeometry.CreatePath(pb);
        using (var cl = new CanvasCommandList(ds.Device))
        {
            using (var s = cl.CreateDrawingSession()) { s.Transform = ds.Transform; s.DrawGeometry(g, Colors.Black, 24, RwRoundStroke); }
            using var bl = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect { Source = cl, BlurAmount = 6 * CsRaster };
            using var op = new Microsoft.Graphics.Canvas.Effects.OpacityEffect { Source = bl, Opacity = 0.2f };
            var t = ds.Transform; ds.Transform = Matrix3x2.Identity; ds.DrawImage(op); ds.Transform = t;
        }
        ds.DrawGeometry(g, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 20, RwRoundStroke);
        using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(cx - 34, cy), EndPoint = new Vector2(cx + 30, cy) };
        ds.DrawGeometry(g, br, 12, RwRoundStroke);
    }

    /// <summary>按钮：绿色（渐变、白字、淡绿圆章）或白色（淡投影、灰圆章、黑字），左边圆章写手柄键；忙的时候圆章换成转圈。</summary>
    private void RqButton(CanvasDrawingSession ds, float x, float y, float w, string key, string label, bool green, bool busy)
    {
        float h = 112, r = 56;
        if (green)
        {
            RsSoftShadow(ds, x, y + 6, w, h, r, 10, 0.18f);
            using var br = new CanvasLinearGradientBrush(ds.Device, RqGreenA, RqGreenB) { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x, y + h) };
            ds.FillRoundedRectangle(x, y, w, h, r, r, br);
        }
        else
        {
            RsSoftShadow(ds, x, y + 5, w, h, r, 9, 0.16f);
            ds.FillRoundedRectangle(x, y, w, h, r, r, Color.FromArgb(255, 0xFC, 0xFC, 0xFD));
            ds.DrawRoundedRectangle(x + 0.5f, y + 0.5f, w - 1, h - 1, r, r, Color.FromArgb(255, 0xEA, 0xEB, 0xEE), 1);
        }
        var ink = green ? Color.FromArgb(255, 0xFF, 0xFF, 0xFF) : RmInk;
        using var tf = new CanvasTextFormat { FontFamily = RmFont(label), FontSize = 42, FontWeight = RsWt(label), WordWrapping = CanvasWordWrapping.NoWrap };
        using var lay = new CanvasTextLayout(ds.Device, label, tf, 2000, 100);
        float tw = (float)lay.LayoutBounds.Width, total = 44 + 18 + tw;
        float bx = x + (w - total) / 2, cy = y + h / 2;
        if (busy)
        {
            ds.DrawCircle(bx + 22, cy, 15, Color.FromArgb(110, 0xFF, 0xFF, 0xFF), 5);
            using var pb = new CanvasPathBuilder(ds.Device);
            pb.BeginFigure(bx + 22, cy - 15);
            pb.AddArc(new Vector2(bx + 22, cy), 15, 15, -MathF.PI / 2, MathF.PI * 0.9f);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var arc = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(arc, Color.FromArgb(255, 0xFF, 0xFF, 0xFF), 5, RwRoundStroke);
        }
        else
        {
            ds.FillCircle(bx + 22, cy, 22, green ? Color.FromArgb(90, 0xFF, 0xFF, 0xFF) : Color.FromArgb(255, 0x9A, 0x9C, 0xA3));
            using var kf = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
            ds.DrawText(key, new Windows.Foundation.Rect(bx, cy - 22, 44, 44), green ? RqGreenB : Color.FromArgb(255, 0xF2, 0xF2, 0xF4), kf);
        }
        ds.DrawTextLayout(lay, bx + 44 + 18, cy - (float)lay.LayoutBounds.Height / 2 - (float)lay.LayoutBounds.Y, ink);
    }

    private void RqClose(bool instant = false)
    {
        if (_rq is not { } rq || rq.Closing) return;
        rq.Closing = true;
        _rq = null;
        RwBackdropFx(rq.Page, false, instant || Reduced ? 1 : 150);
        void Drop()
        {
            _cs?.Root.Children.Remove(rq.Root);
            rq.CardSurf?.Dispose();
            Images.Release(rq.IconSurf);
            Images.Release(rq.Icon2Surf);
            rq.Root.Dispose();
        }
        if (instant || Reduced) { Drop(); return; }
        rq.Root.StartAnimation("Opacity", CsS(0, 90, Bezier.EaseOut));
        After(110, Drop);
    }

    // ---------------- 输入 ----------------

    private void RqActivate()
    {
        if (_rq is not { } rq || rq.Busy) return;
        _ = RqQuit(rq, rq.Next);
    }

    private void RqY()
    {
        if (_rq is not { Next: { } next } rq || rq.Busy) { Cue(Sfx.Bump); return; }
        // 直接启动：两个都开着（这一次不拦）
        Cue(Sfx.Activate);
        RqClose(instant: true);
        _rqBypass = true;
        try { LaunchOrInstall(next); }
        finally { _rqBypass = false; }
    }

    private bool RqBack()
    {
        if (_rq is not { } rq) return false;
        if (rq.Busy) { Cue(Sfx.Bump); return true; }
        Cue(Sfx.Back);
        RqClose();
        return true;
    }

    /// <summary>强制退出（「退出并启动」退完再启动下一款）：按钮变「正在关闭游戏…」，退掉后小窗直接消失。</summary>
    private async Task RqQuit(RqState rq, LibraryItem? next)
    {
        Cue(Sfx.Activate);
        rq.Busy = true; rq.Failed = false;
        RqPaint(rq);
        var g = rq.Running;
        await ForceQuitAsync(g);
        if (_rq != rq) return;
        if (_game == g)
        {
            rq.Busy = false; rq.Failed = true;
            RqPaint(rq);
            return;
        }
        RqClose(instant: true);
        if (next != null) After(250, () => LaunchOrInstall(next));
    }

    // ---------------- 会话卡（退出后右上角） ----------------

    /// <summary>右上角弹一张会话卡：图标、游戏名、「已保存 · 本次玩了 N 分钟」，停 4 秒淡出。</summary>
    private void RwSessionToast(LibraryItem item, TimeSpan played)
    {
        if (_cs == null) return;
        float w = 616, h = 130, x = 1882 - w, y = 152, pad = 30, R = CsRaster;
        string sub = played.TotalMinutes < 1 ? "已保存 · 刚刚开始" : $"已保存 · 本次玩了 {(played.TotalHours >= 1 ? $"{(int)played.TotalHours} 小时 " : "")}{played.Minutes} 分钟";
        var root = G.Container(relative: false);
        root.Size = new Vector2(w + pad * 2, h + pad * 2);
        root.Offset = new Vector3(x - pad, y - pad, 0);
        var card = CsSprite(w + pad * 2, h + pad * 2, ds =>
        {
            ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * ds.Transform;
            RsSoftShadow(ds, 0, 8, w, h, 36, 16, 0.25f);
            ds.FillRoundedRectangle(0, 0, w, h, 36, 36, Color.FromArgb(240, 0xFA, 0xFA, 0xFA));
            ds.DrawRoundedRectangle(0.5f, 0.5f, w - 1, h - 1, 36, 36, Color.FromArgb(255, 0xE6, 0xE7, 0xEA), 1);
            using var tf = new CanvasTextFormat { FontFamily = RmFont(item.Title), FontSize = 36, FontWeight = RsWt(item.Title), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(item.Title, new Windows.Foundation.Rect(110, 18, w - 130, 50), RmInk, tf);
            using var sf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 32, FontWeight = new Windows.UI.Text.FontWeight(500), WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(sub, new Windows.Foundation.Rect(110, 68, w - 130, 46), RsSub, sf);
        });
        root.Children.InsertAtTop(card);
        LoadedImageSurface? iconSurf = null;
        var icon = G.Sprite(G.Solid(Color.FromArgb(255, 0xD8, 0xD9, 0xE0)), relative: false);
        if (RqArt(item) is { } art)
        {
            iconSurf = Images.AcquireReuse(art, (int)Math.Ceiling(64 * R), (int)Math.Ceiling(96 * R));
            var b = G.C.CreateSurfaceBrush(iconSurf);
            b.Stretch = CompositionStretch.UniformToFill;
            icon.Brush = b;
        }
        icon.Size = new Vector2(64, 64);
        icon.Offset = new Vector3(pad + 28, pad + (h - 64) / 2, 0);
        var rr = G.C.CreateRoundedRectangleGeometry();
        rr.Size = new Vector2(64, 64);
        rr.CornerRadius = new Vector2(64 * RwRound);
        icon.Clip = G.C.CreateGeometricClip(rr);
        root.Children.InsertAtTop(icon);
        _cs.Root.Children.InsertAtTop(root);
        if (!Reduced)
        {
            root.Opacity = 0;
            root.Offset -= new Vector3(0, 24, 0);
            root.StartAnimation("Opacity", CsS(1, 180, Bezier.EaseOut));
            root.StartAnimation("Offset", CsV3(new Vector3(x - pad, y - pad, 0), 260, new Bezier(0.22f, 1f, 0.36f, 1f)));
            After(4000, () => { try { root.StartAnimation("Opacity", CsS(0, 300, Bezier.EaseOut)); } catch (ObjectDisposedException) { } });
        }
        After(Reduced ? 4000 : 4350, () =>
        {
            try { _cs?.Root.Children.Remove(root); root.Dispose(); } catch (ObjectDisposedException) { }
            Images.Release(iconSurf);
        });
    }
}
