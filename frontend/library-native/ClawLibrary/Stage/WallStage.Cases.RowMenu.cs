// 「单行」样式的游戏菜单（11-20 jinyu：照 Cocoon 的游戏菜单——卡片上按 Y 弹出）。
// 整屏压暗，左边是这款游戏放大的封面和标题，右边一张白色圆角卡，一行一个操作：
// 启动 / 编辑 / 游戏配置 / 打开本地文件 / 移除（红色）；焦点是同一套四角括号（流光、光晕、边光），上下移动时弹簧滑过去。
// 编辑、游戏配置的二级页还在做：现在先进原来的游戏设置页（编辑 → 封面分类，游戏配置 → 画面分类）。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class RmState
    {
        public required ContainerVisual Root;
        public required SpriteVisual Scrim, Card, Cover, Title, Glow, Line, Rim;
        public required LibraryItem Item;
        public required CsPage Page;
        public CompositionDrawingSurface? CardSurf, TitleSurf, GlowSurf, LineSurf, RimSurf;
        public LoadedImageSurface? CoverSurf;
        public int Sel;
        public bool Armed;   // 移除：按第一次变成「再按一次确认」
        public bool Closing;
        /// <summary>焦点框拆成左右两半：开菜单时先在「启动」行左边冒出一对小括号，右半再滑到行尾（Cocoon 的展开方式）。</summary>
        public ContainerVisual Cur = null!, CurL = null!, CurR = null!;
        /// <summary>行里那张卡（开菜单时藏起来，封面从它的位置飞出去；关的时候飞回来再显示）。</summary>
        public Visual? From;
        public Vector3 FromCenter;
        public float FromScale = 1;
    }

    private RmState? _rm;

    // 卡片几何（设计单位，1920×1200）：右边白卡 x 1260–1882，行高 117，行间 3 px 分隔线；左边封面 300×450
    private const float RmCardX = 1260, RmCardW = 622, RmRowH = 117, RmSep = 3, RmCardR = 40;
    private const float RmCoverW = 300, RmCoverH = 450, RmCoverCx = 640;
    private static readonly string[] RmLabels = { "启动", "编辑", "游戏配置", "打开本地文件", "移除" };
    private static float RmCardH => RmLabels.Length * RmRowH + (RmLabels.Length - 1) * RmSep;
    private static float RmCardY => (CsH - RmCardH) / 2;
    private static Color RmInk => Ck(Color.FromArgb(255, 0x1E, 0x1E, 0x24));
    private static Color RmRed => Color.FromArgb(255, 0xF2, 0x4B, 0x5A);

    private bool RmOpen => _rm is { Closing: false };

    // 按 Y 后的背景（逐像素比对 jinyu 的 Cocoon 视频，11-20）：
    //   压暗不是乘暗也不去饱和，而是一层深灰盖上去——壁纸每个像素都满足 out ≈ 0.24·in + 27（三个通道斜率一样），
    //   即 rgb(35,35,37) 不透明度 0.76 的遮罩：暗部被抬起、整体反差压扁。壁纸本身不模糊。
    //   首页组件（卡片、面板、焦点框）在遮罩下面整层轻度模糊：卡片边缘 10–90 % 过渡约 16–24 px，σ ≈ 8 px。
    //   菜单层（封面、标题、白卡）在遮罩上面，清晰不变。
    private static readonly Color RmScrim = Color.FromArgb(194, 35, 35, 37);
    private const float RmFxBlur = 8;

    private CompositionEffectBrush RwBlurBrush()
    {
        var blur = new GaussianBlurEffect { Name = "blur", BlurAmount = 0, BorderMode = EffectBorderMode.Soft, Optimization = EffectOptimization.Balanced, Source = new CompositionEffectSourceParameter("src") };
        return G.C.CreateEffectFactory(blur, new[] { "blur.BlurAmount" }).CreateBrush();
    }

    /// <summary>开 / 关菜单时组件层（LayerVisual：内容层 + 背景层里的面板层）整层模糊；壁纸不糊。压暗由菜单的遮罩负责。</summary>
    private void RwBackdropFx(CsPage p, bool on, double ms)
    {
        if (p.Rw is not { Disposed: false } rw) return;
        try
        {
            var content = p.Content as LayerVisual;
            if (on)
            {
                rw.UiFx ??= RwBlurBrush();
                rw.Ui.Effect = rw.UiFx;
                if (content != null) { rw.ContentFx ??= RwBlurBrush(); content.Effect = rw.ContentFx; }
            }
            var e = on ? Bezier.EaseOut : new Bezier(0.3f, 0f, 0.2f, 1f);
            foreach (var b in new[] { rw.UiFx, rw.ContentFx })
                b?.StartAnimation("blur.BlurAmount", CsS(on ? RmFxBlur : 0, ms, e));
            if (!on)
                After(ms + 30, () =>
                {
                    if (_rm != null || _rq != null || rw.Disposed) return;   // 又开了菜单 / 退出小窗
                    try
                    {
                        rw.Ui.Effect = null;
                        if (content != null) content.Effect = null;
                    }
                    catch (ObjectDisposedException) { }
                });
        }
        catch (Exception ex) { Log.Warn($"menu backdrop fx: {ex.Message}"); }
    }

    /// <summary>Y：焦点游戏的菜单（单行样式的首页和网格页）。</summary>
    private void RmShow(CsPage p)
    {
        if (_cs == null || _rm != null || p.Rw is not { PanelFocus: < 0 }) { Cue(Sfx.Bump); return; }
        var g = p.Sel < p.Items.Count ? p.Items[p.Sel] : null;
        if (g == null) { Cue(Sfx.Bump); return; }
        Cue(Sfx.MenuIn);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        // 背景：组件层整层轻度模糊（RwBackdropFx），再盖一层深灰遮罩（壁纸和组件一起压暗、压反差），菜单在遮罩上面。
        var scrim = G.Sprite(G.Solid(RmScrim), relative: false);
        scrim.Size = new Vector2(CsW, CsH);
        RwBackdropFx(p, true, Reduced ? 1 : 230);
        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var rm = new RmState { Root = root, Scrim = scrim, Card = Sp(), Cover = Sp(), Title = Sp(), Glow = Sp(), Line = Sp(), Rim = Sp(), Item = g, Page = p };
        _rm = rm;
        root.Children.InsertAtTop(scrim);
        foreach (var v in new Visual[] { rm.Title, rm.Card }) root.Children.InsertAtTop(v);
        rm.Cur = G.Container(relative: false);
        rm.CurL = G.Container(relative: false);
        rm.CurR = G.Container(relative: false);
        rm.Cur.Children.InsertAtTop(rm.CurL);
        rm.Cur.Children.InsertAtTop(rm.CurR);
        foreach (var v in new Visual[] { rm.Glow, rm.Line, rm.Rim }) rm.CurL.Children.InsertAtTop(v);
        root.Children.InsertAtTop(rm.Cur);
        root.Children.InsertAtTop(rm.Cover);   // 封面在最上面：飞行途中会掠过白卡
        _cs.Root.Children.InsertAtTop(root);

        // 封面：竖封面，圆角同卡片（宽 × 0.174），左半屏居中，标题在下面
        string? art = g.Art is { } k ? (k.Has('q') ? k.File('q') : k.Has('p') ? k.File('p') : null) : null;
        float R = CsRaster;
        if (art != null)
        {
            rm.CoverSurf = Images.AcquireReuse(art, (int)Math.Ceiling(RmCoverW * R), (int)Math.Ceiling(RmCoverH * R));
            var b = G.C.CreateSurfaceBrush(rm.CoverSurf);
            b.Stretch = CompositionStretch.UniformToFill;
            rm.Cover.Brush = b;
        }
        rm.Cover.Size = new Vector2(RmCoverW, RmCoverH);
        float coverY = (CsH - RmCoverH) / 2 - 40;
        rm.Cover.Offset = new Vector3(RmCoverCx - RmCoverW / 2, coverY, 0);
        var rr = G.C.CreateRoundedRectangleGeometry();
        rr.CornerRadius = new Vector2(RmCoverW * RwRound);
        rr.Size = new Vector2(RmCoverW, RmCoverH);
        rm.Cover.Clip = G.C.CreateGeometricClip(rr);
        RmPaintTitle(rm, coverY + RmCoverH + 34);
        RmPaintCard(rm);
        RmPaintCursor(rm);
        RmCursorTo(rm, move: false);

        // 封面从行里那张卡的位置起飞（那张先藏起来）
        if (p.Cases.TryGetValue(p.Sel, out var srcCase) && p.Row != null)
        {
            var (fx, fy, fw, fh) = RwFocusRect(srcCase);
            var off = p.Content.Offset + p.Row.Offset;
            rm.From = srcCase.Root;
            rm.FromCenter = new Vector3(fx + fw / 2 + off.X, fy + fh / 2 + off.Y, 0);
            rm.FromScale = fw / RmCoverW;
        }
        RwCursorShow(p, false);   // 行上的焦点框先收起来（封面飞走后那一格是空的）
        if (!Reduced) RmIntro(rm);
    }

    /// <summary>
    /// 开菜单（逐帧拆 jinyu 录的 Cocoon 视频，11-20）：
    ///   0–230 ms 背景模糊压暗；
    ///   0–270 ms 封面从卡片原位飞到左侧：先往上冲（越过目标高度约 1/4 行程）、放大到约 1.35 倍、斜 7°，
    ///            再横着飞过去、缩回、反摆 4°、落定——起点在右边的先上后左，在左边的往右上走、斜向相反；
    ///   100–200 ms 标题淡入；约 260 ms「启动」行左边冒出一对小括号；约 300 ms 白卡从 0.85 倍（左上角为锚）弹出；
    ///   300–460 ms 右括号滑到行尾。
    /// </summary>
    private void RmIntro(RmState rm)
    {
        rm.Scrim.Opacity = 0;
        rm.Scrim.StartAnimation("Opacity", CsS(1, 230, Bezier.EaseOut));
        var to = rm.Cover.Offset;
        var tc = to + new Vector3(RmCoverW / 2, RmCoverH / 2, 0);
        var sc = rm.From != null ? rm.FromCenter : tc + new Vector3(0, 120, 0);
        float s0 = rm.From != null ? rm.FromScale : 0.9f;
        float dx = tc.X - sc.X, dy = tc.Y - sc.Y;
        float lean = -MathF.Sign(dx) * MathF.Min(1, MathF.Abs(dx) / 300);
        Vector3 At(float px, float py) => new(sc.X + dx * px - RmCoverW / 2, sc.Y + dy * py - RmCoverH / 2, 0);
        var ease = G.Ease(new Bezier(0.33f, 0f, 0.25f, 1f));
        var pos = G.C.CreateVector3KeyFrameAnimation();
        pos.InsertKeyFrame(0, At(0, 0), G.Linear);
        pos.InsertKeyFrame(0.37f, At(0.05f, 1.26f), ease);
        pos.InsertKeyFrame(0.62f, At(0.6f, 1.08f), ease);
        pos.InsertKeyFrame(0.85f, At(0.95f, 1.0f), ease);
        pos.InsertKeyFrame(1, to, ease);
        pos.Duration = Gfx.Ms(270);
        var scale = G.C.CreateVector3KeyFrameAnimation();
        Vector3 S(float k) => new(k, k, 1);
        scale.InsertKeyFrame(0, S(s0), G.Linear);
        scale.InsertKeyFrame(0.37f, S(1.35f), ease);
        scale.InsertKeyFrame(0.62f, S(1.12f), ease);
        scale.InsertKeyFrame(0.85f, S(1.02f), ease);
        scale.InsertKeyFrame(1, S(1), ease);
        scale.Duration = Gfx.Ms(270);
        var rot = G.C.CreateScalarKeyFrameAnimation();
        rot.InsertKeyFrame(0, 0, G.Linear);
        rot.InsertKeyFrame(0.37f, 7 * lean, ease);
        rot.InsertKeyFrame(0.62f, -4 * lean, ease);
        rot.InsertKeyFrame(0.85f, -1 * lean, ease);
        rot.InsertKeyFrame(1, 0, ease);
        rot.Duration = Gfx.Ms(270);
        rm.Cover.CenterPoint = new Vector3(RmCoverW / 2, RmCoverH / 2, 0);
        rm.Cover.StartAnimation("Offset", pos);
        rm.Cover.StartAnimation("Scale", scale);
        rm.Cover.StartAnimation("RotationAngleInDegrees", rot);
        if (rm.From != null) rm.From.Opacity = 0;
        rm.Title.Opacity = 0;
        rm.Title.StartAnimation("Opacity", CsS(1, 100, Bezier.EaseOut, 100));
        rm.Card.CenterPoint = new Vector3(40, 40, 0);
        rm.Card.Opacity = 0;
        rm.Card.Scale = new Vector3(0.85f, 0.85f, 1);
        rm.Card.StartAnimation("Opacity", CsS(1, 60, Bezier.EaseOut, 300));
        rm.Card.StartAnimation("Scale", CsV3(Vector3.One, 120, new Bezier(0.2f, 0.9f, 0.3f, 1.1f), 300));
        var (W, H, _, _) = RmBracketGeom();
        rm.Cur.CenterPoint = new Vector3(RmCurPad + 30, RmCurPad + H / 2, 0);
        rm.Cur.Opacity = 0;
        rm.Cur.Scale = new Vector3(0.35f, 0.35f, 1);
        rm.Cur.StartAnimation("Opacity", CsS(1, 40, Bezier.EaseOut, 260));
        rm.Cur.StartAnimation("Scale", CsV3(Vector3.One, 90, Bezier.EaseOut, 260));
        rm.CurR.Offset = new Vector3(-(W - 70), 0, 0);
        rm.CurR.StartAnimation("Offset", CsV3(Vector3.Zero, 160, new Bezier(0.25f, 0.8f, 0.25f, 1f), 300));
    }

    /// <summary>
    /// 菜单开着时游戏库刷新（比如存了元数据、换了封面），首页原地重建：新页面没有模糊、焦点框和那张卡又露出来了。
    /// 把菜单挂到新页面上：组件层直接糊上，行焦点框收起，对应的卡藏起来（关菜单时封面飞回它那里）。
    /// </summary>
    private void RmAdoptPage(CsPage p)
    {
        if (_rm is not { } rm || rm.Closing || rm.Page == p) return;
        rm.Page = p;
        RwBackdropFx(p, true, 1);
        RwCursorShow(p, false);
        int k = p.Items.FindIndex(x => x.Id == rm.Item.Id);
        if (k >= 0 && p.Cases.TryGetValue(k, out var c) && p.Row != null)
        {
            var (fx, fy, fw, fh) = RwFocusRect(c);
            var off = p.Content.Offset + p.Row.Offset;
            rm.From = c.Root;
            rm.FromCenter = new Vector3(fx + fw / 2 + off.X, fy + fh / 2 + off.Y, 0);
            rm.FromScale = fw / RmCoverW;
            c.Root.Opacity = 0;
        }
        else rm.From = null;
    }

    private static Vector3 RmCoverHome => new(RmCoverCx - RmCoverW / 2, (CsH - RmCoverH) / 2 - 40, 0);

    /// <summary>进二级页：白卡压扁消失、焦点框收掉，封面往左飞出屏幕、标题淡出；背景（模糊 + 遮罩）留着给二级页当底。</summary>
    private void RmHideForSheet()
    {
        if (_rm is not { } rm) return;
        if (Reduced) { foreach (var v in new Visual[] { rm.Card, rm.Cur, rm.Cover, rm.Title }) v.Opacity = 0; return; }
        rm.Cur.StopAnimation("Opacity");
        rm.Cur.Opacity = 0;
        rm.Card.CenterPoint = new Vector3(RmCardW / 2 + 40, RmCardH / 2 + 40, 0);
        rm.Card.StartAnimation("Scale", CsV3(new Vector3(1, 0.55f, 1), 60, Bezier.EaseOut));
        rm.Card.StartAnimation("Opacity", CsS(0, 60, Bezier.EaseOut));
        var easeIn = new Bezier(0.5f, 0f, 0.9f, 0.6f);
        rm.Cover.StopAnimation("RotationAngleInDegrees");
        rm.Cover.RotationAngleInDegrees = 0;
        rm.Cover.StartAnimation("Offset", CsV3(new Vector3(-RmCoverW - 60, RmCoverHome.Y, 0), 110, easeIn, 30));
        rm.Cover.StartAnimation("Opacity", CsS(0, 40, Bezier.EaseOut, 100));
        rm.Title.StartAnimation("Opacity", CsS(0, 70, Bezier.EaseOut));
    }

    /// <summary>从二级页退回：白卡和焦点框回来，封面从左边飞回原位（170 ms）、标题淡入。</summary>
    private void RmBackFromSheet(bool instant)
    {
        if (_rm is not { } rm) return;
        foreach (var v in new Visual[] { rm.Card, rm.Cover, rm.Title, rm.Cur }) { v.StopAnimation("Opacity"); v.StopAnimation("Scale"); }
        rm.Card.Scale = Vector3.One;
        rm.Cover.Scale = Vector3.One;
        if (instant || Reduced)
        {
            foreach (var v in new Visual[] { rm.Card, rm.Cover, rm.Title, rm.Cur }) v.Opacity = 1;
            RwJump(rm.Cover, RmCoverHome);
            return;
        }
        rm.Card.Opacity = 0;
        rm.Card.Scale = new Vector3(0.94f, 0.94f, 1);
        rm.Card.StartAnimation("Opacity", CsS(1, 60, Bezier.EaseOut));
        rm.Card.StartAnimation("Scale", CsV3(Vector3.One, 140, new Bezier(0.22f, 1f, 0.36f, 1f)));
        rm.Cur.Opacity = 1;
        rm.Cover.Opacity = 1;
        rm.Cover.Offset = new Vector3(-RmCoverW - 60, RmCoverHome.Y, 0);
        rm.Cover.StartAnimation("Offset", CsV3(RmCoverHome, 170, new Bezier(0.2f, 0.8f, 0.3f, 1f)));
        rm.Title.Opacity = 0;
        rm.Title.StartAnimation("Opacity", CsS(1, 100, Bezier.EaseOut, 80));
    }

    private void RmClose(bool instant = false)
    {
        if (_rm is not { } rm || rm.Closing) return;
        rm.Closing = true;
        void Drop()
        {
            if (rm.Root.Comment == "dropped") return;
            rm.Root.Comment = "dropped";
            if (_cs != null) _cs.Root.Children.Remove(rm.Root);
            Images.Release(rm.CoverSurf);
            foreach (var s in new[] { rm.CardSurf, rm.TitleSurf, rm.GlowSurf, rm.LineSurf, rm.RimSurf }) s?.Dispose();
            rm.Root.Dispose();
            if (_rm == rm) _rm = null;
        }
        void Restore()
        {
            try { if (rm.From != null) rm.From.Opacity = 1; } catch (ObjectDisposedException) { }
            if (rm.Page.Rw is { PanelFocus: < 0, Disposed: false }) RwCursorShow(rm.Page, true);
        }
        RwBackdropFx(rm.Page, false, instant || Reduced ? 1 : 220);
        if (instant || Reduced) { Restore(); Drop(); return; }
        // 关：白卡、焦点框、标题先淡掉，封面原路飞回卡片位置，背景同时退回清晰
        foreach (var v in new Visual[] { rm.Card, rm.Cur, rm.Title }) v.StartAnimation("Opacity", CsS(0, 100, Bezier.EaseOut));
        rm.Scrim.StartAnimation("Opacity", CsS(0, 220, Bezier.EaseOut));
        if (rm.From != null)
        {
            var back = rm.FromCenter - new Vector3(RmCoverW / 2, RmCoverH / 2, 0);
            rm.Cover.StartAnimation("Offset", CsV3(back, 220, new Bezier(0.3f, 0f, 0.2f, 1f)));
            rm.Cover.StartAnimation("Scale", CsV3(new Vector3(rm.FromScale, rm.FromScale, 1), 220, new Bezier(0.3f, 0f, 0.2f, 1f)));
            rm.Cover.StartAnimation("RotationAngleInDegrees", CsS(0, 120, Bezier.EaseOut));
            After(225, () => { Restore(); Drop(); });
            return;
        }
        rm.Root.StartAnimation("Opacity", CsS(0, 180, CsQuart));
        After(200, () => { Restore(); Drop(); });
    }

    private void RmPaintTitle(RmState rm, float y)
    {
        var dev = G.Device;
        float R = CsRaster, w = 760, h = 60;
        rm.TitleSurf = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        using (var ds = CanvasComposition.CreateDrawingSession(rm.TitleSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            using var f = new CanvasTextFormat { FontFamily = RmFont(rm.Item.Title), FontSize = 38, FontWeight = new Windows.UI.Text.FontWeight(500), HorizontalAlignment = CanvasHorizontalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };
            ds.DrawText(rm.Item.Title, new Windows.Foundation.Rect(0, 0, w, h), Color.FromArgb(255, 0xFA, 0xFA, 0xFA), f);
        }
        ((CompositionSurfaceBrush)rm.Title.Brush).Surface = rm.TitleSurf;
        rm.Title.Size = new Vector2(w, h);
        rm.Title.Offset = new Vector3(RmCoverCx - w / 2, y, 0);
    }

    /// <summary>中文用苹方，纯西文用 Poppins（和 Cocoon 一样的字形）。</summary>
    private static string RmFont(string text) => text.Any(ch => ch >= 0x2E80) ? RwCjk : RwPoppins;

    /// <summary>白卡：每行左边彩色贴纸图标、粗体字；移除是红色。编辑行尾有 Y 提示。</summary>
    private void RmPaintCard(RmState rm)
    {
        var dev = G.Device;
        float R = CsRaster, pad = 40, w = RmCardW + pad * 2, h = RmCardH + pad * 2;
        rm.CardSurf ??= G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        using (var ds = CanvasComposition.CreateDrawingSession(rm.CardSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            using (var shadow = new CanvasCommandList(dev))
            {
                using (var s = shadow.CreateDrawingSession()) s.FillRoundedRectangle(pad, pad + 10, RmCardW, RmCardH, RmCardR, RmCardR, Colors.Black);
                using var blur = new GaussianBlurEffect { Source = shadow, BlurAmount = 18 * R, BorderMode = EffectBorderMode.Soft };
                using var op = new OpacityEffect { Source = blur, Opacity = 0.35f };
                ds.Transform = Matrix3x2.Identity;
                ds.DrawImage(op);
                ds.Transform = Matrix3x2.CreateScale(R);
            }
            ds.FillRoundedRectangle(pad, pad, RmCardW, RmCardH, RmCardR, RmCardR, Ck(Color.FromArgb(255, 0xF7, 0xF8, 0xFA)));
            for (int i = 0; i < RmLabels.Length; i++)
            {
                float y = pad + i * (RmRowH + RmSep);
                if (i > 0) ds.FillRectangle(pad, y - RmSep, RmCardW, RmSep, Ck(Color.FromArgb(255, 0xE6, 0xE8, 0xEC)));
                bool danger = i == RmLabels.Length - 1;
                string label = danger && rm.Armed ? "再按一次确认移除" : RmLabels[i];
                RmIcon(ds, i, pad + 62, y + RmRowH / 2);
                using var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 38, VerticalAlignment = CanvasVerticalAlignment.Center };
                ds.DrawText(label, new Windows.Foundation.Rect(pad + 112, y, RmCardW - 200, RmRowH), danger ? RmRed : RmInk, f);
                if (i == 1) RmBadge(ds, pad + RmCardW - 58, y + RmRowH / 2, "Y");
            }
        }
        ((CompositionSurfaceBrush)rm.Card.Brush).Surface = rm.CardSurf;
        rm.Card.Size = new Vector2(w, h);
        rm.Card.Offset = new Vector3(RmCardX - pad, RmCardY - pad, 0);
    }

    /// <summary>行尾的手柄键提示：灰色圆底白字（Cocoon 的 Y / R1 小圆章）。</summary>
    private static void RmBadge(CanvasDrawingSession ds, float cx, float cy, string text)
    {
        ds.FillCircle(cx, cy, 21, Ck(Color.FromArgb(255, 0x8E, 0x90, 0x98)));
        using var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        ds.DrawText(text, new Windows.Foundation.Rect(cx - 21, cy - 21, 42, 42), Color.FromArgb(255, 0xF2, 0xF2, 0xF4), f);
    }

    /// <summary>每行的贴纸图标（主题色渐变 + 白描边；移除是红色）。</summary>
    private void RmIcon(CanvasDrawingSession ds, int i, float cx, float cy)
    {
        var dev = ds.Device;
        bool danger = i == RmLabels.Length - 1;
        using var fill = danger ? new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xFF, 0x7A, 0x84), RmRed) { StartPoint = new Vector2(0, cy - 22), EndPoint = new Vector2(0, cy + 22) }
                                : new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(0, cy - 22), EndPoint = new Vector2(0, cy + 22) };
        CanvasGeometry geo;
        switch (i)
        {
            case 0:
            {
                using var pb = new CanvasPathBuilder(dev);
                pb.BeginFigure(cx - 13, cy - 19); pb.AddLine(cx + 19, cy); pb.AddLine(cx - 13, cy + 19); pb.EndFigure(CanvasFigureLoop.Closed);
                using var tri = CanvasGeometry.CreatePath(pb);
                geo = tri.Stroke(6, RwRoundStroke).CombineWith(tri, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                break;
            }
            case 1:
            {
                // 铅笔：斜着的圆角长条 + 笔尖
                using var body = CanvasGeometry.CreateRoundedRectangle(dev, -6, -24, 12, 38, 4, 4);
                using var pb = new CanvasPathBuilder(dev);
                pb.BeginFigure(-6, 16); pb.AddLine(6, 16); pb.AddLine(0, 26); pb.EndFigure(CanvasFigureLoop.Closed);
                using var tip = CanvasGeometry.CreatePath(pb);
                using var pen = body.CombineWith(tip, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                geo = pen.Transform(Matrix3x2.CreateRotation(MathF.PI / 4) * Matrix3x2.CreateTranslation(cx, cy));
                break;
            }
            case 2:
            {
                // 游戏配置：三条滑杆
                CanvasGeometry? acc = null;
                for (int k = 0; k < 3; k++)
                {
                    float yy = cy - 14 + k * 14, kx = cx + (k == 1 ? 8 : -8);
                    using var bar = CanvasGeometry.CreateRoundedRectangle(dev, cx - 20, yy - 2.5f, 40, 5, 2.5f, 2.5f);
                    using var knob = CanvasGeometry.CreateCircle(dev, kx, yy, 6.5f);
                    var one = bar.CombineWith(knob, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                    if (acc == null) acc = one; else { var u = acc.CombineWith(one, Matrix3x2.Identity, CanvasGeometryCombine.Union); acc.Dispose(); one.Dispose(); acc = u; }
                }
                geo = acc!;
                break;
            }
            case 3:
            {
                using var body = CanvasGeometry.CreateRoundedRectangle(dev, cx - 21, cy - 10, 42, 28, 6, 6);
                using var tab = CanvasGeometry.CreateRoundedRectangle(dev, cx - 21, cy - 17, 19, 12, 4, 4);
                geo = body.CombineWith(tab, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                break;
            }
            default:
            {
                // 垃圾桶：桶身 + 盖子 + 提手
                using var can = CanvasGeometry.CreateRoundedRectangle(dev, cx - 14, cy - 9, 28, 29, 5, 5);
                using var lid = CanvasGeometry.CreateRoundedRectangle(dev, cx - 19, cy - 16, 38, 6, 3, 3);
                using var handle = CanvasGeometry.CreateRoundedRectangle(dev, cx - 6, cy - 21, 12, 7, 3, 3);
                using var a = can.CombineWith(lid, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                geo = a.CombineWith(handle, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                break;
            }
        }
        using (geo)
        {
            RwSticker(ds, geo, 9);
            ds.FillGeometry(geo, fill);
            if (i == 4)
                for (int k = -1; k <= 1; k++) ds.FillRoundedRectangle(cx + k * 7 - 1.5f, cy - 3, 3, 17, 1.5f, 1.5f, Color.FromArgb(160, 255, 255, 255));
        }
    }

    // ---------------- 焦点 ----------------

    private const float RmCurPad = 44;

    /// <summary>
    /// 行的四角括号：外扩按行的大小统一取，圆角 = 白卡圆角 + 外扩（和卡片同心），几何走全局 CocoonBracketPath。
    /// </summary>
    private static (float W, float H, float R, float Leg) RmBracketGeom()
    {
        float o = CurOutsetFor(RmCardW, RmRowH);
        float W = RmCardW + o * 2f, H = RmRowH + o * 2f, r = RmCardR + o;
        return (W, H, r, 0f);
    }

    private void RmPaintCursor(RmState rm)
    {
        var dev = G.Device;
        float R = CsRaster;
        var (W, H, r, _) = RmBracketGeom();
        float sw = W + RmCurPad * 2, sh = H + RmCurPad * 2;
        int pw = (int)Math.Ceiling(sw * R), ph = (int)Math.Ceiling(sh * R);
        using var path = CocoonBracketPath(dev, W, H, r, RmCurPad);
        rm.GlowSurf = G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rm.GlowSurf))
        {
            ds.Clear(Colors.Transparent);
            using var cl = new CanvasCommandList(ds);
            using (var s = cl.CreateDrawingSession()) { s.Transform = Matrix3x2.CreateScale(R); s.DrawGeometry(path, CurGlowColor, RwSw * 2, RwRoundStroke); }
            using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = CurGlowBlurDp * RwDp * R / 2, BorderMode = EffectBorderMode.Soft };
            ds.DrawImage(blur);
        }
        rm.RimSurf = G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rm.RimSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            using var outer = path.Stroke(RwSw + 2 * RwDp, RwRoundStroke);
            using var inner = path.Stroke(MathF.Max(0.1f, RwSw - 2 * RwDp), RwRoundStroke);
            using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            ds.FillGeometry(ring, Color.FromArgb(102, 0xfa, 0xfa, 0xfa));
        }
        rm.LineSurf = G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rm.LineSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            ds.DrawGeometry(path, Colors.White, RwSw, RwRoundStroke);
        }
        ((CompositionSurfaceBrush)rm.Glow.Brush).Surface = rm.GlowSurf;
        ((CompositionSurfaceBrush)rm.Rim.Brush).Surface = rm.RimSurf;
        rm.Line.Brush = RsPerimFlow(rm.LineSurf!, W, H, r, right: false);   // 沿周长流（和二级页一样）
        foreach (var v in new Visual[] { rm.Glow, rm.Line, rm.Rim }) v.Size = new Vector2(sw, sh);
        // 右半：同样三层（共用画刷），裁成右半；左半裁掉右半
        rm.CurR.Children.RemoveAll();
        foreach (var v in new[] { rm.Glow, rm.Line, rm.Rim })
        {
            var copy = G.Sprite(v == rm.Line ? RsPerimFlow(rm.LineSurf!, W, H, r, right: true) : v.Brush, relative: false);
            copy.Size = v.Size;
            rm.CurR.Children.InsertAtTop(copy);
        }
        rm.CurL.Clip = G.C.CreateInsetClip(0, 0, sw / 2, 0);
        rm.CurR.Clip = G.C.CreateInsetClip(sw / 2, 0, 0, 0);
        rm.CurL.Size = rm.CurR.Size = rm.Cur.Size = new Vector2(sw, sh);
    }

    private void RmCursorTo(RmState rm, bool move)
    {
        var (W, H, _, _) = RmBracketGeom();
        float rowY = RmCardY + rm.Sel * (RmRowH + RmSep);
        var to = new Vector3(RmCardX + RmCardW / 2 - W / 2 - RmCurPad, rowY + RmRowH / 2 - H / 2 - RmCurPad, 0);
        float dy = to.Y - rm.Cur.Offset.Y;
        if (move && !Reduced)
        {
            rm.Cur.StartAnimation("Offset", RwSpring(to));
            rm.Cur.CenterPoint = new Vector3(rm.Cur.Size.X / 2, rm.Cur.Size.Y / 2, 0);
            RsStretch(rm.Cur, dy, H);   // 行间移动先拉长再缩回
        }
        else RwJump(rm.Cur, to);
    }

    // ---------------- 输入 ----------------

    private void RmMove(Direction dir)
    {
        if (_rm is not { } rm || rm.Closing) return;
        if (dir is Direction.Left or Direction.Right) { Cue(Sfx.Bump); return; }
        int j = rm.Sel + (dir == Direction.Down ? 1 : -1);
        if (j < 0 || j >= RmLabels.Length) { Cue(Sfx.Bump); return; }
        CuePan(0);
        rm.Sel = j;
        if (rm.Armed) { rm.Armed = false; RmPaintCard(rm); }
        RmCursorTo(rm, move: true);
    }

    private void RmActivate()
    {
        if (_rm is not { } rm || rm.Closing) return;
        var g = rm.Item;
        var p = rm.Page;
        switch (rm.Sel)
        {
            case 0:
                RmClose(instant: true);
                RwActivate(p);
                return;
            case 1:
                Cue(Sfx.ModalShow);
                RsOpen(g, p, RsEditPage(g, p));   // 菜单留在下面：编辑页退到底回到菜单
                return;
            case 2:
                Cue(Sfx.ModalShow);
                RsOpen(g, p, RsConfigPage(g));
                return;
            case 3:
                Cue(Sfx.Activate);
                _ = RmOpenFolder(g);
                return;
            default:
                if (!rm.Armed) { rm.Armed = true; Cue(Sfx.Error); RmPaintCard(rm); return; }
                RmClose();
                _ = RmRemove(g);
                return;
        }
    }

    /// <summary>菜单开着时按 Y：直接进「编辑」（Cocoon 的编辑行尾就是 Y）。</summary>
    private void RmActivateY()
    {
        if (_rm is not { } rm || rm.Closing) return;
        rm.Sel = 1;
        RmActivate();
    }

    private bool RmBack()
    {
        if (_rm is not { } rm || rm.Closing) return false;
        Cue(Sfx.MenuOut);
        RmClose();
        return true;
    }

    /// <summary>打开本地文件：问后台这款游戏的安装目录 / 程序位置，用资源管理器打开。</summary>
    private async Task RmOpenFolder(LibraryItem g)
    {
        try
        {
            _agent ??= new AgentClient();
            var req = new JsonObject { ["id"] = g.Id, ["platform"] = g.Platform, ["title"] = g.Title };
            if (await _agent.CallLong("game_settings_get", 30, req) is JsonObject s)
            {
                string? folder = Str(s["folder"]), exe = Str(s["exe"]);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) { SystemInfo.Open("explorer.exe", $"\"{folder}\""); return; }
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) { SystemInfo.Open("explorer.exe", $"/select,\"{exe}\""); return; }
            }
            Toast("找不到这款游戏的文件");
        }
        catch (Exception ex) { Log.Warn($"open folder {g.Id}: {ex.Message}"); Toast("没读到游戏位置"); }
    }

    /// <summary>移除：本地游戏从游戏库删掉记录（不删文件）；Steam 游戏在库中隐藏（和 Steam 里「隐藏此游戏」一样）。</summary>
    private async Task RmRemove(LibraryItem g)
    {
        try
        {
            _agent ??= new AgentClient();
            if (g.Platform == "steam")
            {
                var req = new JsonObject { ["id"] = g.Id, ["platform"] = g.Platform, ["title"] = g.Title };
                await _agent.CallLong("game_settings_set", 40, req, JsonValue.Create("hidden"), JsonValue.Create(true));
                Toast($"已在游戏库中隐藏「{g.Title}」");
            }
            else
            {
                await _agent.Call("local_games_remove", JsonValue.Create(g.Id));
                Toast($"已移除「{g.Title}」");
            }
            await LgRefresh();
        }
        catch (Exception ex) { Toast(ex.Message.Contains("不支持的后台调用") ? "工具箱后台版本太旧" : $"没有移除：{ex.Message}"); }
    }
}
