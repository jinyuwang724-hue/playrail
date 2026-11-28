// 「单行」首页的左侧栏 = 控制中心（11-24 jinyu 定稿，画布「控制中心 · 全新设计」第三轮）。
// 照 jinyu 录的 Cocoon 侧栏视频量的尺寸和节奏（只取数值和行为，没有用 Cocoon 的代码或素材）：
//   面板贴左、上、下三边，宽 672（屏宽 35 %），只有右边两角圆角；行数均分整个高度，行间浅分隔线。
//   行尾记号 ⊙ = 打开全屏页（11-24 jinyu：电源项和它的子列表不要）。
//   View 打开：面板从左滑入 200 ms，首页压暗 + 组件层模糊；各行从上到下依次淡入右移 14 px（间隔 55 ms），
//   全部到位后焦点框浮现（按下到落定约 0.6 s）。
//   进全屏页：侧栏滑出，游戏库淡出、页面进场；页面里按 B 退回侧栏（焦点停在刚才那一项），再按 B 回首页。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const float LsW = 672, LsRad = 30;
    private static readonly Color LsScrim = Color.FromArgb(140, 24, 24, 28);
    private static Color LsSep => Ck(Color.FromArgb(255, 0xE6, 0xE7, 0xEA));
    private static Color LsGrey => Ck(Color.FromArgb(255, 0x8E, 0x90, 0x98));

    private static readonly (string Id, string Label)[] LsMain =
    {
        ("perf", "性能"), ("pad", "手柄"), ("disp", "显示与声音"), ("sys", "系统"), ("shot", "相册"), ("set", "设置"),   // 11-27 jinyu：相册在设置前面
    };

    private sealed class LsState
    {
        public required ContainerVisual Root, Panel, List;
        public required SpriteVisual Scrim, Bg;
        public CurSet Cur = null!;
        /// <summary>焦点行底下的白色选中卡：括号套在它上面（同心），不再套一个看不见的大胶囊。</summary>
        public SpriteVisual? Card;
        public CompositionDrawingSurface? CardSurf;
        public required CsPage Page;
        public readonly List<(SpriteVisual V, CompositionDrawingSurface S)> Rows = new();
        public int Focus;
        public bool Open = true;
        public int Gen;
    }

    private LsState? _ls;
    /// <summary>从左侧栏进的全屏页（性能 / 手柄 / …）：页面退出时回左侧栏，焦点停在这一项。</summary>
    private string? _lsFrom;
    private (uint Id, string What, double X0, double Y0)? _lsTouch;

    private bool LsOpenNow => _ls is { Open: true };
    private static float LsRowH => CsH / LsMain.Length;

    // ---------------- 入口 ----------------

    /// <summary>View：单行首页开 / 关左侧栏。处理了返回 true（别的页面 View 照旧）。</summary>
    private bool LsView()
    {
        if (_ccOpen || _ob != null) return false;
        if (!CsActive || _cs?.Page is not { Style: HomeStyle.Row } p) return false;
        if (LsOpenNow) { LsClose(); return true; }
        if (_csInsert != null || _csSearchOpen || _rm != null || _rs != null || _rq != null || SpOpenNow || _spEdit != null) { Cue(Sfx.Bump); return true; }
        LsOpen(p, 0);
        return true;
    }

    /// <summary>≡：单行首页不再开控制中心（控制中心在左侧栏），改开焦点游戏的菜单；侧栏开着时 = 关掉侧栏。处理了返回 true。</summary>
    private bool LsMenuKey()
    {
        if (!CsActive || _cs?.Page is not { Style: HomeStyle.Row } p) return false;
        if (LsOpenNow) { LsClose(); return true; }
        if (SpOpenNow) { SpClose(); return true; }
        if (_csInsert != null || _csSearchOpen || _rs != null || _rq != null) { Cue(Sfx.Bump); return true; }
        if (_rm != null) { RmClose(); return true; }
        RmShow(p);
        return true;
    }

    private void LsOpen(CsPage p, int focus)
    {
        if (_cs == null || p.Rw == null) return;
        LsDispose();
        Cue(Sfx.MenuIn);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var scrim = G.Sprite(G.Solid(LsScrim), relative: false);
        scrim.Size = new Vector2(CsW, CsH);
        var panel = G.Container(relative: false);
        panel.Size = new Vector2(LsW, CsH);
        var bg = G.Sprite(SpPanelBrush(), relative: false);
        bg.Size = new Vector2(LsW, CsH);
        var geo = G.C.CreateRoundedRectangleGeometry();
        geo.Offset = new Vector2(-LsRad - 10, 0);
        geo.Size = new Vector2(LsW + LsRad + 10, CsH);
        geo.CornerRadius = new Vector2(LsRad);
        panel.Clip = G.C.CreateGeometricClip(geo);
        var list = G.Container(relative: false);
        list.Size = new Vector2(LsW, CsH);
        panel.Children.InsertAtTop(bg);
        panel.Children.InsertAtTop(list);
        var ls = new LsState { Root = root, Panel = panel, List = list, Scrim = scrim, Bg = bg, Cur = null!, Page = p, Focus = Math.Clamp(focus, 0, LsMain.Length - 1) };
        root.Children.InsertAtTop(scrim);
        root.Children.InsertAtTop(panel);
        // 焦点框不放进面板的裁剪里：括号和光晕可以压到面板右边外面（视频里就是这样）
        ls.Cur = CurNew(root);
        _cs.Root.Children.InsertAtTop(root);
        _ls = ls;
        LsBuildRows(ls);
        RwCursorShow(p, false);
        RwBackdropFx(p, true, Reduced ? 1 : 230);
        LsFocus(ls, move: false);
        if (Reduced)
        {
            CurShow(ls.Cur, true, 1);
        }
        else
        {
            scrim.Opacity = 0;
            scrim.StartAnimation("Opacity", CsS(1, 200, Bezier.EaseOut));
            panel.Offset = new Vector3(-LsW - 40, 0, 0);
            panel.StartAnimation("Offset", CsV3(Vector3.Zero, 200, new Bezier(0.2f, 0.8f, 0.2f, 1f)));
            double last = LsStagger(ls, 110);
            CurShow(ls.Cur, true, 180, last + 40);
        }
        Log.Info("left panel open");
    }

    /// <summary>逐行进场：每行淡入 + 从左 14 px 滑到位，间隔 55 ms。返回最后一行开始的时刻。</summary>
    private double LsStagger(LsState ls, double start)
    {
        double t = start;
        for (int i = 0; i < ls.Rows.Count; i++)
        {
            var v = ls.Rows[i].V;
            var to = v.Offset;
            t = start + i * 55;
            v.Opacity = 0;
            v.Offset = to - new Vector3(14, 0, 0);
            v.StartAnimation("Opacity", CsS(1, 200, Bezier.EaseOut, t));
            v.StartAnimation("Offset", CsV3(to, 200, new Bezier(0.2f, 0.8f, 0.2f, 1f), t));
        }
        return t + 200;
    }

    private void LsClose(bool instant = false)
    {
        if (_ls is not { Open: true } ls) return;
        ls.Open = false;
        int gen = ++ls.Gen;
        if (!instant) Cue(Sfx.MenuOut);
        CurShow(ls.Cur, false, 60);
        if (ls.Page.Rw is { PanelFocus: < 0, Disposed: false }) RwCursorShow(ls.Page, true);
        RwBackdropFx(ls.Page, false, instant || Reduced ? 1 : 200);
        if (instant || Reduced) { LsDispose(); return; }
        ls.Scrim.StartAnimation("Opacity", CsS(0, 200, Bezier.EaseOut));
        ls.Panel.StartAnimation("Offset", CsV3(new Vector3(-LsW - 40, 0, 0), 200, new Bezier(0.4f, 0f, 0.6f, 1f)));
        After(230, () => { if (_ls == ls && ls.Gen == gen) LsDispose(); });
        Log.Info("left panel close");
    }

    /// <summary>
    /// 进全屏页时收侧栏（11-28 jinyu：进手柄页会闪、还看到一个矩形框）：以前是侧栏滑走、首页去模糊、首页卡片的焦点框淡回来，
    /// 同时页面才开始淡入——于是先露出一下清晰的首页和卡片焦点框。现在侧栏和首页的模糊原样留着，页面直接淡入盖在上面，
    /// 等页面完全不透明了再在底下悄悄拆掉侧栏、去掉模糊（不显示首页焦点框，回来时由 LsPageClosed 重新打开侧栏）。
    /// </summary>
    private void LsCloseUnder(LsState ls)
    {
        if (!ls.Open) return;
        ls.Open = false;
        int gen = ++ls.Gen;
        CurShow(ls.Cur, false, 60);
        After(Reduced ? 1 : 520, () =>
        {
            if (_ls != ls || ls.Gen != gen) return;
            RwBackdropFx(ls.Page, false, 1);
            if (ls.Page.Rw is { PanelFocus: < 0, Disposed: false }) RwCursorShow(ls.Page, true);   // 页面已经盖住了，看不到
            LsDispose();
        });
    }

    private void LsDispose()
    {
        if (_ls is not { } ls) return;
        _ls = null;
        try
        {
            _cs?.Root.Children.Remove(ls.Root);
            foreach (var r in ls.Rows) r.S.Dispose();
            ls.CardSurf?.Dispose();
            ls.Root.Dispose();
        }
        catch (ObjectDisposedException) { }
    }

    // ---------------- 画行 ----------------

    private void LsBuildRows(LsState ls)
    {
        foreach (var r in ls.Rows) { ls.List.Children.Remove(r.V); r.S.Dispose(); r.V.Dispose(); }
        ls.Rows.Clear();
        int n = LsMain.Length;
        float h = LsRowH;
        for (int i = 0; i < n; i++)
        {
            var v = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            v.Size = new Vector2(LsW, h);
            v.Offset = new Vector3(0, i * h, 0);
            var s = G.Surface((int)Math.Ceiling(LsW * CsRaster), (int)Math.Ceiling(h * CsRaster));
            ((CompositionSurfaceBrush)v.Brush).Surface = s;
            ls.List.Children.InsertAtTop(v);
            ls.Rows.Add((v, s));
            LsPaintRow(ls, i);
        }
    }

    private void LsPaintRow(LsState ls, int i)
    {
        var (_, s) = ls.Rows[i];
        float h = LsRowH, cy = h / 2;
        var (id, label) = LsMain[i];
        using var ds = CanvasComposition.CreateDrawingSession(s);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        LsIcon(ds, id, 74, cy);
        using (var f = new CanvasTextFormat { FontFamily = RmFont(label), FontSize = 36, FontWeight = RsWt(label, 600), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(label, new Windows.Foundation.Rect(134, 0, 440, h), RmInk, f);
        {
            // ▸：打开全屏页（11-26 jinyu：侧栏右侧一律用右三角表示跳转，和页面里子页面行的 ▸ 同一个画法）
            float R = 622;
            using var pb = new CanvasPathBuilder(ds.Device);
            pb.BeginFigure(R - 10, cy - 9); pb.AddLine(R, cy); pb.AddLine(R - 10, cy + 9); pb.EndFigure(CanvasFigureLoop.Closed);
            using var tri = CanvasGeometry.CreatePath(pb);
            ds.FillGeometry(tri, RmInk);
            ds.DrawGeometry(tri, RmInk, 3, RwRoundStroke);
        }
        if (i < LsMain.Length - 1) ds.FillRectangle(0, h - 3, LsW, 3, LsSep);
    }

    /// <summary>行首的贴纸图标：主题色渐变填充 + 白描边（同游戏菜单 RmIcon 的画法）。</summary>
    private void LsIcon(CanvasDrawingSession ds, string id, float cx, float cy)
    {
        var dev = ds.Device;
        using var fill = new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(0, cy - 24), EndPoint = new Vector2(0, cy + 24) };
        using var geo = LsIconGeo(dev, id).Transform(Matrix3x2.CreateTranslation(cx, cy));
        RwSticker(ds, geo, 9);
        ds.FillGeometry(geo, fill);
    }

    private static CanvasGeometry LsUnion(CanvasGeometry a, CanvasGeometry b) { var u = a.CombineWith(b, Matrix3x2.Identity, CanvasGeometryCombine.Union); a.Dispose(); b.Dispose(); return u; }
    private static CanvasGeometry LsCut(CanvasGeometry a, CanvasGeometry b) { var u = a.CombineWith(b, Matrix3x2.Identity, CanvasGeometryCombine.Exclude); a.Dispose(); b.Dispose(); return u; }

    private static CanvasGeometry LsPoly(ICanvasResourceCreator dev, params float[] xy)
    {
        using var pb = new CanvasPathBuilder(dev);
        pb.BeginFigure(xy[0], xy[1]);
        for (int k = 2; k < xy.Length; k += 2) pb.AddLine(xy[k], xy[k + 1]);
        pb.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(pb);
    }

    /// <summary>图标形状（以 0,0 为中心、约 48 × 48）。</summary>
    private static CanvasGeometry LsIconGeo(ICanvasResourceCreator dev, string id)
    {
        switch (id)
        {
            case "perf":   // 闪电
                return LsPoly(dev, 5, -25, -16, 4, -2, 4, -6, 25, 16, -5, 2, -5);
            case "pad":   // 手柄：圆角机身挖出十字键和两个按键
            {
                var body = CanvasGeometry.CreateRoundedRectangle(dev, -25, -15, 50, 31, 15, 15);
                body = LsCut(body, CanvasGeometry.CreateRoundedRectangle(dev, -16, -2, 13, 4.5f, 2, 2));
                body = LsCut(body, CanvasGeometry.CreateRoundedRectangle(dev, -11.75f, -6.25f, 4.5f, 13, 2, 2));
                body = LsCut(body, CanvasGeometry.CreateCircle(dev, 10, -3, 3.6f));
                return LsCut(body, CanvasGeometry.CreateCircle(dev, 16, 4, 3.6f));
            }
            case "disp":   // 太阳：圆 + 八道光
            {
                var g = CanvasGeometry.CreateCircle(dev, 0, 0, 11);
                for (int k = 0; k < 8; k++)
                    g = LsUnion(g, CanvasGeometry.CreateRoundedRectangle(dev, -3, -25, 6, 9, 3, 3).Transform(Matrix3x2.CreateRotation(k * MathF.PI / 4)));
                return g;
            }
            case "sys":   // 芯片：方块 + 每边三根针脚，中间挖空
            {
                var g = CanvasGeometry.CreateRoundedRectangle(dev, -16, -16, 32, 32, 7, 7);
                for (int side = 0; side < 4; side++)
                    for (int k = -1; k <= 1; k++)
                        g = LsUnion(g, CanvasGeometry.CreateRoundedRectangle(dev, k * 10 - 2.5f, -25, 5, 10, 2.5f, 2.5f).Transform(Matrix3x2.CreateRotation(side * MathF.PI / 2)));
                return LsCut(g, CanvasGeometry.CreateRoundedRectangle(dev, -7, -7, 14, 14, 3, 3));
            }
            case "set":   // 齿轮
            {
                var g = CanvasGeometry.CreateCircle(dev, 0, 0, 17);
                for (int k = 0; k < 8; k++)
                    g = LsUnion(g, CanvasGeometry.CreateRoundedRectangle(dev, -5, -24, 10, 12, 3, 3).Transform(Matrix3x2.CreateRotation(k * MathF.PI / 4)));
                return LsCut(g, CanvasGeometry.CreateCircle(dev, 0, 0, 7));
            }
            default:   // shot：相册，相框挖出山和太阳
            {
                var g = CanvasGeometry.CreateRoundedRectangle(dev, -24, -18, 48, 36, 8, 8);
                g = LsCut(g, LsPoly(dev, -17, 12, -5, -3, 4, 8, 10, 2, 18, 12));
                return LsCut(g, CanvasGeometry.CreateCircle(dev, 9, -7, 4.5f));
            }
        }
    }

    // ---------------- 焦点 / 输入 ----------------

    // 选中卡：行内左右各让 18、上下各让 22，圆角和面板一样 30（11-26 jinyu：侧栏焦点框形状别扭——
    // 原来括号套的是一个看不见的、两头半圆的大胶囊，半径 78，和行、面板都对不上）
    private const float LsCardX = 18, LsCardInY = 22, LsCardR = LsRad, LsCardPad = 28;
    private static float LsCardW => LsW - 2 * LsCardX;
    private static float LsCardH => LsRowH - 2 * LsCardInY;

    private void LsEnsureCard(LsState ls)
    {
        if (ls.Card != null) return;
        float w = LsCardW + LsCardPad * 2, h = LsCardH + LsCardPad * 2, R = CsRaster;
        ls.CardSurf = G.Surface((int)Math.Ceiling(w * R), (int)Math.Ceiling(h * R));
        using (var ds = CanvasComposition.CreateDrawingSession(ls.CardSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(LsCardPad, LsCardPad) * Matrix3x2.CreateScale(R);
            RsSoftShadow(ds, 0, 6, LsCardW, LsCardH, LsCardR, 12, 0.12f);
            ds.FillRoundedRectangle(0, 0, LsCardW, LsCardH, LsCardR, LsCardR, Ck(Color.FromArgb(255, 0xFF, 0xFF, 0xFF)));
            ds.DrawRoundedRectangle(0.5f, 0.5f, LsCardW - 1, LsCardH - 1, LsCardR, LsCardR, Ck(Color.FromArgb(255, 0xE8, 0xE9, 0xEC)), 1);
        }
        ls.Card = G.Sprite(G.C.CreateSurfaceBrush(ls.CardSurf), relative: false);
        ls.Card.Size = new Vector2(w, h);
        ls.List.Children.InsertAtBottom(ls.Card);   // 在行的文字和图标下面
    }

    private void LsFocus(LsState ls, bool move)
    {
        // 11-26 jinyu：焦点行不要白色选中卡和投影，只留焦点框（框的位置和圆角不变）
        int i = ls.Focus;
        float x = LsCardX, y = i * LsRowH + LsCardInY;
        CurToCocoon(ls.Cur, x, y, LsCardW, LsCardH, LsCardR, move);
    }

    private void LsMove(Direction dir)
    {
        if (_ls is not { Open: true } ls) { Cue(Sfx.None); return; }
        if (dir == Direction.Right) { LsActivate(); return; }
        if (dir == Direction.Left) { Cue(Sfx.Bump, pan: 0.2f); return; }
        int d = dir == Direction.Up ? -1 : 1, n = LsMain.Length, to = ls.Focus + d;
        if (to < 0 || to >= n) { Cue(Sfx.Bump, pan: 0.5f); return; }
        CuePan(0.3f);
        ls.Focus = to;
        LsFocus(ls, move: true);
    }

    private void LsActivate()
    {
        if (_ls is not { Open: true } ls) { Cue(Sfx.None); return; }
        Cue(Sfx.ModalShow);
        LsGo(ls, LsMain[ls.Focus].Id);
    }

    private void LsBack()
    {
        if (_ls is not { Open: true }) return;
        LsClose();
    }

    // ---------------- 全屏页 ----------------

    /// <summary>进全屏页：侧栏滑出，游戏库淡出（控制中心安静打开），再进对应的页面。页面退出时回到侧栏。</summary>
    private void LsGo(LsState ls, string id)
    {
        Log.Info($"left panel → {id}");
        LsCloseUnder(ls);
        _ccLight = true;   // Cocoon 样式页面（WallStage.CocoonPages.cs）
        CpReset();
        OpenControlCenter(quiet: true);
        LtApplyBg();
        _lsFrom = id;
        switch (id)
        {
            case "perf": OpenPerf(); break;
            case "pad": OpenGamepad(); break;
            case "disp": _cpComp = "disp"; OpenDisplay(); break;
            case "sys": OpenSystem(); break;
            case "set": OpenSettingsPage(); if (!CpSettingsIds.Contains(_stCat)) StCategoryJump(0); break;
            case "shot": OpenShots(); break;
        }
    }

    private bool LsAnyPageOpen => _perfOpen || _gpOpen || _dsOpen || _syOpen || _stOpen || _shOpen || _agOpen;

    /// <summary>页面（非瞬时）关掉时调：从侧栏进来的页面退到底 = 收起控制中心、回到侧栏。</summary>
    private void LsPageClosed()
    {
        if (_lsFrom is not { } id || !_ccOpen) return;
        After(1, () =>
        {
            if (_lsFrom != id || !_ccOpen || LsAnyPageOpen) return;
            _lsFrom = null;
            // 页面退场时控制中心首页会淡回来：这里不要它
            foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); vis.Opacity = 0; }
            _ccFadeClose = true;
            CloseControlCenter();
            if (_prefs.Home == HomeStyle.Row && _cs?.Page is { Style: HomeStyle.Row } p && p.Rw is { Disposed: false })
                LsOpen(p, Math.Max(0, Array.FindIndex(LsMain, e => e.Id == id)));
            Log.Info($"left panel ← {id}");
        });
    }

    // ---------------- 触摸 ----------------

    /// <summary>侧栏开着：点行 = 选中并执行，点面板外 = 关；侧栏关着：从屏幕左缘向右划 = 打开。</summary>
    private bool LsPointerDown(PointerRoutedEventArgs e, Microsoft.UI.Input.PointerPoint pt)
    {
        if (!CsActive || _cs?.Page is not { Style: HomeStyle.Row } || _csInsert != null || _rm != null || _rs != null || _rq != null || SpOpenNow) return false;
        var (x, y) = CsLocal(pt.Position.X, pt.Position.Y);
        if (_ls is not { Open: true })
        {
            if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch || x > 28) return false;
            _lsTouch = (pt.PointerId, "edge", x, y);
        }
        else if (x > LsW) { WithCue(Sfx.None, () => LsClose()); _lsTouch = (pt.PointerId, "eat", x, y); }
        else _lsTouch = (pt.PointerId, "tap", x, y);
        _rootEl.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private bool LsPointerUp(PointerRoutedEventArgs e, bool canceled = false)
    {
        if (_lsTouch is not { } t || e.Pointer.PointerId != t.Id) return false;
        _lsTouch = null;
        _rootEl.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (canceled) return true;
        var pt = e.GetCurrentPoint(_rootEl).Position;
        var (x, y) = CsLocal(pt.X, pt.Y);
        if (t.What == "edge")
        {
            if (x - t.X0 > 80 && Math.Abs(y - t.Y0) < x - t.X0 && _cs?.Page is { Style: HomeStyle.Row } p) WithCue(Sfx.None, () => LsOpen(p, 0));
            return true;
        }
        if (t.What != "tap" || _ls is not { Open: true } ls) return true;
        if (Math.Abs(x - t.X0) + Math.Abs(y - t.Y0) > 24)
        {
            if (t.X0 - x > 80) WithCue(Sfx.None, () => LsClose());   // 向左划 = 收起
            return true;
        }
        int i = (int)Math.Floor(y / LsRowH);
        if (i < 0 || i >= LsMain.Length) return true;
        WithCue(Sfx.Nav, () =>
        {
            ls.Focus = i;
            LsFocus(ls, move: true);
            LsActivate();
        });
        return true;
    }
}
