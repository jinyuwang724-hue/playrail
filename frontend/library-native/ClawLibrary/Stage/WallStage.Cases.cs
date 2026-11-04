// 卡盒版游戏库（docs/library-cases/spec.md）：顶部标签 首页 / 全部 N / Steam N / 本地 N；
// 首页两种样式——展架（底部一排卡盒，选中的放大）和叠放（一摞卡盒）；分类页上面一排散放的卡带，下面 7 列卡盒网格。
// 坐标按 1920×1200 逻辑像素写，整层按屏幕缩放。墙（WallStage.cs）还在下面照常维护焦点、控制中心形变等，只是藏起来。
// 输入走墙的同一条链：Move / Activate / Back / SwitchTab / PadY / ClickAt / PanBy 在快捷面板、控制中心之后交给这里。
//
// 展架的思路借鉴 Selene（https://github.com/mattxslv/Selene，Playnite 全屏主题，MIT，Copyright (c) 2026 Matthew Silva）：
// 底部一排封面，选中的更大，背景是这款游戏的横幅图。只借了思路，没有用它的代码。
// 叠放的变换移植自 CollectionViewPagingLayout，许可证见 StackAt。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class CsPage
    {
        public required ContainerVisual Root, Bg, Content;
        public required int Tab;
        public HomeStyle Style;
        public required List<LibraryItem> Items;
        // 首页
        public readonly Dictionary<int, CsCase> Cases = new();
        public readonly Dictionary<int, SpriteVisual> Labels = new();
        public int Sel, Prev;
        public bool Acts;   // 焦点在信息区的按钮上
        public int Act;
        public Vector2 ActsAt;
        public SpriteVisual? InfoA, InfoB, ActsV;
        public bool InfoFront;
        public ContainerVisual? Row;
        public SpriteVisual[] BgImg = Array.Empty<SpriteVisual>();
        public LoadedImageSurface?[] BgSurf = new LoadedImageSurface?[2];
        public int BgFront;
        public string? BgPath;
        public readonly List<(float X, float Y, float W, float H, int Act)> ActRects = new();
        // 分类页
        public List<LibraryItem> Recent = new();
        public readonly Dictionary<int, CsCart> Carts = new();
        public readonly Dictionary<int, (CsCase Case, SpriteVisual Caption)> Grid = new();
        public ContainerVisual? Scroll, RecentRow, GridHost;
        public float ScrollY, RecentX;
        public bool InRecent;
        public int Ai;
        public float GridTop;
        public long Built;
        public bool BgReady;
        public long BgReadyAt;
        public readonly List<Action> OnBgReady = new();
    }

    private sealed class CsState
    {
        public required ContainerVisual Root, Pages, Header;
        public required SpriteVisual Underline, Status;
        public readonly List<(SpriteVisual V, float X, float W, int Tab)> Tabs = new();
        public int Tab;
        public CsPage? Page;
        public readonly List<CsPage> Leaving = new();
        public float K;
        public bool StylePending;
    }

    private CsState? _cs;
    private readonly HashSet<string> _csFav = new();

    private const float CsW = 1920, CsH = 1200;
    private static readonly Bezier CsQuart = new(.165f, .84f, .44f, 1f);
    private float CsK => (float)Math.Min(View.Width / CsW, View.Height / CsH);
    private float CsRaster => CsK * Chrome.Dpi;
    private bool CsActive => _cs != null && !_ccOpen;

    // 展架（规格 2）
    private const float ShW0 = 228, ShH0 = 342, ShGap = 28, ShW1 = 308, ShH1 = 462, ShBottom = CsH - 64;
    // 分类页（规格 4）
    private const int GridCols = 5;
    private const float GridGapX = 32, GridGapY = 48, CatTop = 112;
    private static float GridW => (CsW - 192 - (GridCols - 1) * GridGapX) / GridCols;
    private static float GridH => GridW * 1.5f;
    private static float GridPitch => GridH + 12 + 50 + GridGapY;

    // ---------------- 动画小工具 ----------------

    private double CsMs(double ms) => Reduced ? 1 : ms;

    private Vector3KeyFrameAnimation CsV3(Vector3 to, double ms, Bezier e, double delay = 0)
    {
        var a = G.C.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(1, to, G.Ease(e));
        a.Duration = Gfx.Ms(Math.Max(1, ms));
        if (delay > 0) { a.DelayTime = Gfx.Ms(delay); a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay; }
        return a;
    }

    private Vector2KeyFrameAnimation CsV2(Vector2 to, double ms, Bezier e, double delay = 0)
    {
        var a = G.C.CreateVector2KeyFrameAnimation();
        a.InsertKeyFrame(1, to, G.Ease(e));
        a.Duration = Gfx.Ms(Math.Max(1, ms));
        if (delay > 0) { a.DelayTime = Gfx.Ms(delay); a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay; }
        return a;
    }

    private ScalarKeyFrameAnimation CsS(float to, double ms, Bezier e, double delay = 0)
    {
        var a = G.C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1, to, G.Ease(e));
        a.Duration = Gfx.Ms(Math.Max(1, ms));
        if (delay > 0) { a.DelayTime = Gfx.Ms(delay); a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay; }
        return a;
    }

    private static void CsSet(Visual v, Vector3? offset = null, float? opacity = null)
    {
        if (offset is { } o) { v.StopAnimation("Offset"); v.Offset = o; }
        if (opacity is { } op) { v.StopAnimation("Opacity"); v.Opacity = op; }
    }

    // ---------------- 数据 ----------------

    private static readonly string[] CsTabNames = { "首页", "全部", "Steam", "本地" };

    /// <summary>tab 0 首页 / 1 全部：所有游戏按最近游玩；2 Steam；3 本地（非 Steam、非 Xbox）。</summary>
    private List<LibraryItem> CsList(int tab)
    {
        var all = _lib.Games.Where(g => g.Kind == ItemKind.Game)
            .Select((g, i) => (g, i)).OrderByDescending(x => x.g.LastPlayed ?? long.MinValue).ThenBy(x => x.i).Select(x => x.g);
        return tab switch
        {
            2 => all.Where(g => g.Platform == "steam").ToList(),
            3 => all.Where(g => g.Platform is not ("steam" or "xbox")).ToList(),
            _ => all.ToList(),
        };
    }

    private List<int> CsTabsShown() => Enumerable.Range(0, 4).Where(t => t <= 1 || CsList(t).Count > 0).ToList();

    private LibraryItem? CsSpotItem()
    {
        var p = _cs?.Page;
        if (p == null) return null;
        if (p.Tab == 0) return p.Sel < p.Items.Count ? p.Items[p.Sel] : null;
        return p.InRecent ? (p.Ai < p.Recent.Count ? p.Recent[p.Ai] : null) : (p.Ai < p.Items.Count ? p.Items[p.Ai] : null);
    }

    private string? _csSpot;

    // ---------------- 建立 ----------------

    private void CsStart()
    {
        if (Program.Args.Contains("--wall")) return;
        foreach (var v in new Visual[] { _field, _seamHost, _edgeLeft, _edgeRight, _vignette, _backdrop.Root, _wallBackgroundL, _wallBackgroundR }) v.IsVisible = false;
        var fav = _lib.Collections.FirstOrDefault(c => c.Id == "sc-favorite");
        if (fav?.Children != null) foreach (var id in fav.Children) _csFav.Add(id);
        var root = G.Container(relative: false);
        var floor = G.Sprite(G.Solid(Gfx.Rgba(0x0f, 0x0f, 0x10)), relative: false);
        floor.Size = new Vector2(CsW, CsH);
        root.Children.InsertAtTop(floor);
        var pages = G.Container(relative: false);
        pages.Size = new Vector2(CsW, CsH);
        root.Children.InsertAtTop(pages);
        var header = G.Container(relative: false);
        header.Size = new Vector2(CsW, 112);
        root.Children.InsertAtTop(header);
        var underline = G.Sprite(G.Solid(Gfx.Rgba(242, 242, 242)), relative: false);
        underline.Clip = G.C.CreateInsetClip();
        header.Children.InsertAtTop(underline);
        var status = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        header.Children.InsertAtTop(status);
        _root.Children.InsertAtTop(root);
        _cs = new CsState { Root = root, Pages = pages, Header = header, Underline = underline, Status = status };
        CsMeasure();
        CsPaintStatus();
        CsShowTab(0, 0, entry: true);
        Log.Info($"cases library: {CsList(1).Count} games, style {_prefs.Home}");
    }

    private void CsMeasure()
    {
        if (_cs == null) return;
        float k = CsK;
        _cs.K = k;
        _cs.Root.Scale = new Vector3(k, k, 1);
        _cs.Root.Offset = new Vector3((float)(View.Width - CsW * k) / 2, (float)(View.Height - CsH * k) / 2, 0);
    }

    private void CsResize()
    {
        if (_cs == null) return;
        float old = _cs.K;
        CsMeasure();
        if (Math.Abs(old - _cs.K) < 1e-4) return;
        CsRebuild();
    }

    /// <summary>游戏库换了内容（来源、筛选、Steam 云端快照）：当前页原地重建，焦点尽量留在同一款游戏上。</summary>
    private void CsRebuild()
    {
        if (_cs == null) return;
        _csAllCount = -1;
        _csFav.Clear();
        if (_lib.Collections.FirstOrDefault(c => c.Id == "sc-favorite")?.Children is { } fav) foreach (var id in fav) _csFav.Add(id);
        var tabs = CsTabsShown();
        int tab = tabs.Contains(_cs.Tab) ? _cs.Tab : 0;
        CsShowTab(tab, 0, entry: false);
    }

    /// <summary>封面套件晚到（后台生成）：用到它的卡盒、卡带换图。</summary>
    private void CsRefreshItem(string id)
    {
        if (_cs?.Page is not { } p) return;
        foreach (var c in p.Cases.Values.Concat(p.Grid.Values.Select(g => g.Case)))
            if (c.Item?.Id == id && c.Surface == null && c.Item.Art is { } k && (k.Has('p') ? k.File('p') : k.Has('v') ? k.File('v') : null) is { } path)
            {
                int px = (int)Math.Ceiling(c.Size.X * CsRaster * 1.36f);
                c.Surface = Images.AcquireReuse(path, px, px * 3 / 2);
                ((CompositionSurfaceBrush)c.Art.Brush).Surface = c.Surface;
            }
        if (CsSpotItem()?.Id == id) CsSetBackground(CsSpotItem(), force: true);
    }

    // ---------------- 顶栏 ----------------

    private void CsPaintTabs(bool animate)
    {
        if (_cs == null) return;
        foreach (var t in _cs.Tabs) { _cs.Header.Children.Remove(t.V); t.V.Dispose(); }
        _cs.Tabs.Clear();
        float x = 96;
        foreach (int tab in CsTabsShown())
        {
            string label = CsTabNames[tab], count = tab == 0 ? "" : CsList(tab).Count.ToString();
            float lw, cw = 0;
            using (var l = TextKit.Layout(G.Device, label, TextKit.Ui(500), 20, 600, CanvasHorizontalAlignment.Left)) lw = (float)l.LayoutBounds.Width;
            if (count.Length > 0) using (var c = TextKit.Layout(G.Device, count, TextKit.Num(400), 15, 200, CanvasHorizontalAlignment.Left)) cw = (float)c.LayoutBounds.Width;
            float w = MathF.Ceiling(lw + (cw > 0 ? 8 + cw : 0)) + 2;
            var v = CsSprite(w, 44, ds =>
            {
                using var l = TextKit.Layout(ds, label, TextKit.Ui(500), 20, w, CanvasHorizontalAlignment.Left);
                ds.DrawTextLayout(l, 0, 8, Gfx.Rgba(242, 242, 242));
                if (count.Length == 0) return;
                using var c = TextKit.Layout(ds, count, TextKit.Num(400), 15, w, CanvasHorizontalAlignment.Left);
                ds.DrawTextLayout(c, lw + 8, 13, Gfx.Rgba(242, 242, 242, 0.57));
            });
            v.Offset = new Vector3(x, 34, 0);
            v.Opacity = tab == _cs.Tab ? 1 : 0.6f;
            _cs.Header.Children.InsertAtTop(v);
            _cs.Tabs.Add((v, x, lw, tab));
            x += w + 30;
        }
        CsPlaceUnderline(animate);
    }

    private void CsPlaceUnderline(bool animate)
    {
        if (_cs == null) return;
        var t = _cs.Tabs.FirstOrDefault(t => t.Tab == _cs.Tab);
        if (t.V == null) return;
        var off = new Vector3(t.X, 34 + 41, 0);
        var size = new Vector2(t.W, 3);
        if (!animate || Reduced) { _cs.Underline.StopAnimation("Offset"); _cs.Underline.StopAnimation("Size"); _cs.Underline.Offset = off; _cs.Underline.Size = size; }
        else
        {
            _cs.Underline.StartAnimation("Offset", CsV3(off, 220, CsQuart));
            _cs.Underline.StartAnimation("Size", CsV2(size, 220, CsQuart));
        }
        foreach (var tab in _cs.Tabs) tab.V.StartAnimation("Opacity", CsS(tab.Tab == _cs.Tab ? 1 : 0.6f, CsMs(220), CsQuart));
    }

    /// <summary>右上角：电量和时间（竖条的计时器变了才重画）。</summary>
    private void CsPaintStatus()
    {
        if (_cs == null) return;
        var bat = _railModel.Battery;
        string clock = _railModel.Now.ToString("H:mm");
        const float w = 260, h = 44;
        CsRepaint(_cs.Status, w, h, ds =>
        {
            using var c = TextKit.Layout(ds, clock, TextKit.Num(500), 20, w, CanvasHorizontalAlignment.Right);
            float cw = (float)c.LayoutBounds.Width;
            ds.DrawTextLayout(c, 0, 9, Gfx.Rgba(242, 242, 242));
            if (!bat.HasBattery) return;
            string pct = $"{bat.Percent}%";
            using var p = TextKit.Layout(ds, pct, TextKit.Num(500), 20, w, CanvasHorizontalAlignment.Right);
            float pw = (float)p.LayoutBounds.Width;
            ds.DrawTextLayout(p, -(cw + 22), 9, Gfx.Rgba(242, 242, 242));
            float bx = w - cw - 22 - pw - 8 - 30, by = 14.5f;
            ds.DrawRoundedRectangle(bx, by, 26, 14, 4, 4, Gfx.Rgba(242, 242, 242, 0.9), 1.5f);
            ds.FillRoundedRectangle(bx + 2.5f, by + 2.5f, 21 * Math.Clamp(bat.Percent, 0, 100) / 100f, 9, 1.5f, 1.5f, bat.Charging ? Gfx.Rgba(120, 220, 140) : Gfx.Rgba(242, 242, 242));
            ds.FillRoundedRectangle(bx + 27.5f, by + 4.5f, 2, 5, 1, 1, Gfx.Rgba(242, 242, 242, 0.9));
        });
        _cs.Status.Offset = new Vector3(CsW - 96 - w, 34, 0);
    }

    // ---------------- 页面切换 ----------------

    /// <summary>
    /// 换标签：内容沿切换方向水平滑 40 px，同时淡入淡出，220 ms，下划线跟着滑过去（规格 7）。dir = 0：原地重建（无滑动）。
    /// 新页面自己的进场动画同时开始；正在离场的页面再被换掉时从此刻的位置接着走。
    /// </summary>
    private void CsShowTab(int tab, int dir, bool entry)
    {
        if (_cs == null) return;
        var old = _cs.Page;
        string? spot = CsSpotItem()?.Id ?? _csSpot;
        _cs.Tab = tab;
        CsPaintTabs(animate: old != null);
        var page = tab == 0 ? CsBuildHome(_prefs.Home, spot, entry || dir != 0) : CsBuildCat(tab, spot, entry || dir != 0);
        _cs.Page = page;
        _cs.Pages.Children.InsertAtTop(page.Root);
        double ms = CsMs(220);
        if (old != null)
        {
            // 旧页面：内容滑走、淡出；背景留在下面，等新页面的背景整层淡进来盖住再拆（不会先暗一下）
            CsLeave(old, page);
            if (dir != 0) old.Content.StartAnimation("Offset", CsV3(new Vector3(-40 * dir, 0, 0), ms, CsQuart));
            old.Content.StartAnimation("Opacity", CsS(0, dir != 0 ? ms : CsMs(160), CsQuart));
            if (dir != 0)
            {
                CsSet(page.Content, offset: new Vector3(40 * dir, 0, 0));
                page.Content.StartAnimation("Offset", CsV3(Vector3.Zero, ms, CsQuart));
                CsSet(page.Content, opacity: 0);
                page.Content.StartAnimation("Opacity", CsS(1, ms, CsQuart));
            }
        }
        CsNoteSpot();
    }

    /// <summary>页面离场：新页面的背景到位（或 1.5 s 兜底）后拆掉。</summary>
    private void CsLeave(CsPage old, CsPage next)
    {
        if (_cs == null) return;
        if (!_cs.Leaving.Contains(old)) _cs.Leaving.Add(old);
        foreach (var l in _cs.Leaving.ToList()) if (l != old) CsDropWhenCovered(l, next);
        CsDropWhenCovered(old, next);
    }

    private void CsDropWhenCovered(CsPage old, CsPage next)
    {
        next.OnBgReady.Add(() => After(40, () => CsDrop(old)));
        After(1500, () => CsDrop(old));
        // 背景已经开始淡入：等它淡完再拆（不是马上拆，否则下面露出空底）
        if (next.BgReady) { var list = next.OnBgReady.ToList(); next.OnBgReady.Clear(); After(Math.Max(1, next.BgReadyAt - NowMs), () => { foreach (var a in list) a(); }); }
    }

    private void CsDrop(CsPage page)
    {
        if (_cs == null || !_cs.Leaving.Remove(page)) return;
        _cs.Pages.Children.Remove(page.Root);
        foreach (var c in page.Cases.Values) CsDisposeCase(c);
        foreach (var (c, cap) in page.Grid.Values) { CsDisposeCase(c); cap.Dispose(); }
        foreach (var c in page.Carts.Values) CsDisposeCart(c);
        foreach (var s in page.BgSurf) Images.Release(s);
        page.Root.Dispose();
    }

    private void CsSwitchTab(int delta)
    {
        if (_cs == null || _csInsert != null) { Cue(Sfx.None); return; }
        var tabs = CsTabsShown();
        int at = Math.Max(0, tabs.IndexOf(_cs.Tab));
        int next = at + delta;
        if (next < 0 || next >= tabs.Count) { Cue(Sfx.Bump, pan: delta < 0 ? -0.6f : 0.6f); return; }
        CsShowTab(tabs[next], Math.Sign(delta), entry: true);
    }

    private void CsGoTab(int tab)
    {
        if (_cs == null || tab == _cs.Tab) return;
        var tabs = CsTabsShown();
        int dir = Math.Sign(tabs.IndexOf(tab) - tabs.IndexOf(_cs.Tab));
        Cue(Sfx.Tab);
        CsShowTab(tab, dir == 0 ? 1 : dir, entry: true);
    }

    /// <summary>设置 › 游戏库 › 首页样式：按 A 立即生效。设置页盖着游戏库时等回到游戏库再播切换动画。</summary>
    private void CsSetHomeStyle(HomeStyle style)
    {
        if (_prefs.Home == style) return;
        SaveUi(_prefs with { Home = style });
        Toast($"首页样式：{(style == HomeStyle.Shelf ? "展架" : "叠放")}");
        if (_cs == null) return;
        if (_ccOpen) { _cs.StylePending = true; return; }
        CsMorphStyle();
    }

    /// <summary>控制中心收起时：有待播的样式切换就在墙淡进来之后播。</summary>
    private void CsAfterControlCenter()
    {
        if (_cs is not { StylePending: true }) return;
        _cs.StylePending = false;
        After(CcCloseMs + 40, () => { if (CsActive) CsMorphStyle(); });
    }

    /// <summary>切换首页样式：两种布局交叉淡入淡出，当前游戏的卡盒补间到新位置（规格 7）。</summary>
    private void CsMorphStyle()
    {
        if (_cs == null) return;
        if (_cs.Tab != 0) { CsShowTab(0, -1, entry: true); return; }
        var old = _cs.Page!;
        string? spot = CsSpotItem()?.Id;
        old.Cases.TryGetValue(old.Sel, out var from);
        var page = CsBuildHome(_prefs.Home, spot, entry: false);
        _cs.Page = page;
        _cs.Pages.Children.InsertAtTop(page.Root);
        double ms = CsMs(450);
        CsLeave(old, page);
        old.Content.StartAnimation("Opacity", CsS(0, ms, CsQuart));
        CsSet(page.Content, opacity: 0);
        page.Content.StartAnimation("Opacity", CsS(1, ms, CsQuart));
        // 卡盒从旧位置补间过去：旧页面里那一盒藏起来，新页面里这一盒从它的矩形起飞，飞行期间不跟着淡入
        if (from != null && page.Cases.TryGetValue(page.Sel, out var to))
        {
            from.Root.Opacity = 0;
            var fromOff = from.Offset; var fromSize = from.Size;
            page.Row!.Children.Remove(to.Root);
            page.Root.Children.InsertAtTop(to.Root);
            to.Root.StopAnimation("Offset"); to.Root.StopAnimation("Size"); to.Root.StopAnimation("Scale"); to.Root.StopAnimation("RotationAngle");
            to.Root.Offset = fromOff; to.Root.Size = fromSize; to.Root.Scale = Vector3.One; to.Root.RotationAngle = 0;
            to.Root.CenterPoint = new Vector3(to.Size.X / 2, to.Size.Y / 2, 0);
            to.Root.StartAnimation("Offset", CsV3(to.Offset, CsMs(600), CsQuart));
            to.Root.StartAnimation("Size", CsV2(to.Size, CsMs(600), CsQuart));
            var target = to;
            After(CsMs(620), () =>
            {
                if (_cs?.Page != page || target.Root.Parent != page.Root) return;
                page.Root.Children.Remove(target.Root);
                CsInsertCaseOrdered(page, target);
                if (page.Style == HomeStyle.Stack) CsLayoutStack(page, animate: false);
            });
        }
    }

    private void CsNoteSpot()
    {
        var g = CsSpotItem();
        if (g == null) return;
        _csSpot = g.Id;
        _ccGameId = g.Id;
        _ccGameArt = g.Art is { } k && k.Has('p') ? k.File('p') : null;
    }

    // ---------------- 背景 ----------------

    private ContainerVisual CsBuildBg(CsPage page, HomeStyle style, bool cat)
    {
        var bg = G.Container(relative: false);
        bg.Size = new Vector2(CsW, CsH);
        // 整层不透明（自带底色）：第一张图解码好了整层淡进来，盖住下面离场的页面，不会透出更暗的底
        bg.Opacity = 0;
        var floor = G.Sprite(G.Solid(Gfx.Rgba(0x0f, 0x0f, 0x10)), relative: false);
        floor.Size = new Vector2(CsW, CsH);
        bg.Children.InsertAtTop(floor);
        var imgs = new SpriteVisual[2];
        for (int i = 0; i < 2; i++)
        {
            var b = G.C.CreateSurfaceBrush();
            b.Stretch = CompositionStretch.UniformToFill;
            b.VerticalAlignmentRatio = 0.3f;
            b.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
            var s = G.Sprite(b, relative: false);
            if (cat) { s.Offset = new Vector3(-60, -60, 0); s.Size = new Vector2(CsW + 120, 900); }
            else s.Size = new Vector2(CsW, CsH);
            s.CenterPoint = new Vector3(s.Size.X / 2, s.Size.Y / 2, 0);
            s.Opacity = 0;
            bg.Children.InsertAtTop(s);
            imgs[i] = s;
        }
        page.BgImg = imgs;
        Color ink = Gfx.Rgba(0x0f, 0x0f, 0x10);
        if (cat)
        {
            var fade = G.Sprite(G.LinearGradient(180, CsW + 120, 900, (0, Gfx.WithAlpha(ink, 0.15)), (0.92, ink)), relative: false);
            fade.Offset = new Vector3(-60, -60, 0); fade.Size = new Vector2(CsW + 120, 900);
            bg.Children.InsertAtTop(fade);
        }
        else if (style == HomeStyle.Shelf)
        {
            bg.Children.InsertAtTop(CsShade(90, (0, 0.86), (0.42, 0.4), (0.68, 0)));
            bg.Children.InsertAtTop(CsShade(180, (0, 0.55), (0.18, 0), (0.46, 0), (0.82, 0.92), (1, 1)));
        }
        else
        {
            bg.Children.InsertAtTop(CsShade(90, (0, 0.9), (0.38, 0.55), (0.7, 0.15), (1, 0.35)));
            bg.Children.InsertAtTop(CsShade(180, (0, 0.55), (0.18, 0), (0.6, 0), (1, 0.85)));
        }
        return bg;
    }

    private SpriteVisual CsShade(double deg, params (double At, double A)[] stops)
    {
        Color ink = Gfx.Rgba(0x0f, 0x0f, 0x10);
        var s = G.Sprite(G.LinearGradient(deg, CsW, CsH, stops.Select(x => (x.At, Gfx.WithAlpha(ink, x.A))).ToArray()), relative: false);
        s.Size = new Vector2(CsW, CsH);
        return s;
    }

    /// <summary>背景换成这款游戏的横幅图：新图解码好了再交叉淡入 400 ms（首页清晰铺满，分类页小图放大 = 模糊）。</summary>
    private void CsSetBackground(LibraryItem? g, bool force = false, bool entry = false)
    {
        if (_cs?.Page is not { } p || p.BgImg.Length < 2) return;
        if (g == null) { CsBgReady(p, entry); return; }
        bool cat = p.Tab != 0;
        var k = g.Art;
        string? path = k == null ? null : !cat && k.Has('g') ? k.File('g') : k.Has('h') ? k.File('h') : k.Has('q') ? k.File('q') : k.Has('p') ? k.File('p') : null;
        if (path == null) { CsBgReady(p, entry); return; }
        if (path == p.BgPath && !force) return;
        p.BgPath = path;
        int next = 1 - p.BgFront;
        var sprite = p.BgImg[next];
        var brush = (CompositionSurfaceBrush)sprite.Brush;
        var fx = (float)CsLookOf(g).Fx;
        void Show(ICompositionSurface surface)
        {
            if (_cs == null || (_cs.Page != p && !_cs.Leaving.Contains(p)) || p.BgPath != path) return;
            brush.Surface = surface;
            brush.HorizontalAlignmentRatio = fx;
            p.BgFront = next;
            float target = cat ? 0.4f : 1;
            var other = p.BgImg[1 - next];
            // 新图叠在旧图上面淡进来，淡完再撤旧图（中途不会透出底色变暗）
            p.Bg.Children.Remove(sprite);
            p.Bg.Children.InsertAbove(sprite, other);
            sprite.StopAnimation("Opacity");
            if (!p.BgReady)
            {
                sprite.Opacity = target;
                if (entry && !cat && !Reduced)
                {
                    sprite.Scale = new Vector3(1.04f, 1.04f, 1);
                    sprite.StartAnimation("Scale", CsV3(Vector3.One, 900, CsQuart));
                }
                CsBgReady(p, entry);
                return;
            }
            double ms = CsMs(400);
            sprite.Opacity = 0;
            sprite.StartAnimation("Opacity", CsS(target, ms, Bezier.EaseOut));
            int front = next;
            After(ms + 30, () => { if (p.BgFront == front) { other.StopAnimation("Opacity"); other.Opacity = 0; } });
        }
        if (cat) { CsBlurred(path, Show); return; }
        var surf = Images.AcquireReuse(path, (int)Math.Ceiling(CsW * CsRaster), (int)Math.Ceiling(CsH * CsRaster));
        if (surf == null) { CsBgReady(p, entry); return; }
        Images.Release(p.BgSurf[next]);
        p.BgSurf[next] = surf;
        Images.WhenLoaded(surf, () => { if (p.BgSurf[next] == surf) Show(surf); });
    }

    /// <summary>这一页的背景整层淡进来（进场 600 ms，其余 300 ms），盖住之后拆掉下面离场的页面。</summary>
    private void CsBgReady(CsPage p, bool entry)
    {
        if (p.BgReady) return;
        p.BgReady = true;
        double ms = CsMs(entry ? 600 : 300);
        p.BgReadyAt = NowMs + (long)ms;
        p.Bg.StopAnimation("Opacity");
        p.Bg.StartAnimation("Opacity", CsS(1, ms, CsQuart));
        var waiting = p.OnBgReady.ToList();
        p.OnBgReady.Clear();
        After(ms, () => { foreach (var a in waiting) a(); });
    }

    private readonly Dictionary<string, CompositionDrawingSurface> _csBlur = new();
    private readonly List<string> _csBlurOrder = new();

    /// <summary>分类页背景：横幅图缩小后高斯模糊（相当于原尺寸模糊 36 px），再拉伸铺开。按路径缓存最近 12 张。</summary>
    private void CsBlurred(string path, Action<ICompositionSurface> done)
    {
        if (_csBlur.TryGetValue(path, out var hit)) { done(hit); return; }
        var dev = G.Device;
        _ = Task.Run(async () =>
        {
            try
            {
                var bmp = await CanvasBitmap.LoadAsync(dev, path, 96);
                _dq.TryEnqueue(() =>
                {
                    try
                    {
                        const float w = 400, h = 225;
                        var surface = G.Surface((int)w, (int)h);
                        using (var ds = Microsoft.Graphics.Canvas.UI.Composition.CanvasComposition.CreateDrawingSession(surface))
                        {
                            ds.Clear(Gfx.Rgba(0x0f, 0x0f, 0x10));
                            double cover = Math.Max(w / bmp.Bounds.Width, h / bmp.Bounds.Height) * 1.12;
                            var dest = new Windows.Foundation.Rect((w - bmp.Bounds.Width * cover) / 2, (h - bmp.Bounds.Height * cover) / 2, bmp.Bounds.Width * cover, bmp.Bounds.Height * cover);
                            using var small = new CanvasCommandList(ds);
                            using (var s = small.CreateDrawingSession()) s.DrawImage(bmp, dest, bmp.Bounds);
                            using var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect { Source = small, BlurAmount = 7.5f, BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Hard };
                            ds.DrawImage(blur);
                        }
                        bmp.Dispose();
                        _csBlur[path] = surface;
                        _csBlurOrder.Add(path);
                        if (_csBlurOrder.Count > 12) { var drop = _csBlurOrder[0]; _csBlurOrder.RemoveAt(0); if (_csBlur.Remove(drop, out var old) && !CsBlurInUse(old)) old.Dispose(); }
                        done(surface);
                    }
                    catch (Exception ex) { Log.Warn($"cases blur {path}: {ex.Message}"); }
                });
            }
            catch (Exception ex) { Log.Warn($"cases blur load {path}: {ex.Message}"); }
        });
    }

    private bool CsBlurInUse(CompositionDrawingSurface s) =>
        _cs != null && _cs.Leaving.Append(_cs.Page).Any(p => p != null && p.BgImg.Any(v => (v.Brush as CompositionSurfaceBrush)?.Surface == s));

    // ---------------- 首页 ----------------

    private CsPage CsBuildHome(HomeStyle style, string? spot, bool entry)
    {
        var items = CsList(0);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var content = G.Container(relative: false);
        content.Size = new Vector2(CsW, CsH);
        var page = new CsPage { Root = root, Bg = null!, Content = content, Tab = 0, Style = style, Items = items, Built = NowMs };
        page.Bg = CsBuildBg(page, style, cat: false);
        root.Children.InsertAtTop(page.Bg);
        root.Children.InsertAtTop(content);
        int k = spot == null ? -1 : items.FindIndex(g => g.Id == spot);
        page.Sel = page.Prev = Math.Max(0, k);
        var row = G.Container(relative: false);
        row.Size = new Vector2(CsW, CsH);
        content.Children.InsertAtTop(row);
        page.Row = row;
        page.InfoA = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        page.InfoB = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        page.InfoB.Opacity = 0;
        page.ActsV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        content.Children.InsertAtTop(page.InfoA);
        content.Children.InsertAtTop(page.InfoB);
        content.Children.InsertAtTop(page.ActsV);
        var prevPage = _cs!.Page;
        _cs.Page = page;   // 背景和信息区按这一页画
        if (style == HomeStyle.Shelf) CsLayoutShelf(page, animate: false, entry: entry);
        else CsLayoutStack(page, animate: false, entry: entry);
        CsPaintInfo(page, animate: false);
        CsSetBackground(CsSpotItem(), entry: entry);
        _cs.Page = prevPage;
        if (entry && !Reduced)
        {
            foreach (var v in new Visual[] { page.InfoA, page.ActsV })
            {
                v.Opacity = 0;
                v.StartAnimation("Opacity", CsS(1, 500, CsQuart, 120));
            }
        }
        return page;
    }

    private int CsCount(CsPage p) => p.Items.Count + 1;   // 行尾的「全部 N」

    private static float ShelfRailX(int s, int n)
    {
        float total = n * (ShW0 + ShGap) - ShGap + (ShW1 - ShW0);
        float x = 96 - s * (ShW0 + ShGap);
        return Math.Max(Math.Min(96, CsW - 96 - total), x);
    }

    private static (Vector3 Off, Vector2 Size) ShelfRect(int i, int s, int n)
    {
        float x = ShelfRailX(s, n) + i * (ShW0 + ShGap) + (i > s ? ShW1 - ShW0 : 0);
        bool sel = i == s;
        float w = sel ? ShW1 : ShW0, h = sel ? ShH1 : ShH0;
        return (new Vector3(x, ShBottom - h - (sel ? 8 : 0), 0), new Vector2(w, h));
    }

    private static (int Lo, int Hi) ShelfWindow(int s, int n)
    {
        float x0 = ShelfRailX(s, n);
        int lo = (int)Math.Floor((-420 - x0) / (ShW0 + ShGap)), hi = (int)Math.Ceiling((CsW + 420 - x0) / (ShW0 + ShGap));
        return (Math.Max(0, lo), Math.Min(n - 1, hi));
    }

    /// <summary>
    /// 展架：选中的卡盒 308×462，其余 228×342，间距 28，底边距 64；整排平移让选中的左边停在 96 + 256（前面露一盒），首尾夹住。
    /// 宽高和平移 550 ms quart-out，从此刻的值接着走（可打断）。分组标签写在每组第一盒上方 36 px，选中时 44 px。
    /// </summary>
    private void CsLayoutShelf(CsPage p, bool animate, bool entry = false)
    {
        int n = CsCount(p), s = p.Sel;
        var (lo, hi) = ShelfWindow(s, n);
        var (plo, phi) = ShelfWindow(p.Prev, n);
        double ms = CsMs(550);
        int order = 0;
        for (int i = Math.Min(lo, plo); i <= Math.Max(hi, phi); i++)
        {
            bool live = i >= lo && i <= hi;
            if (!p.Cases.TryGetValue(i, out var c))
            {
                if (!live) continue;
                var (o0, z0) = ShelfRect(i, animate ? p.Prev : s, n);
                c = CsNewCase(i < p.Items.Count ? p.Items[i] : null, i, z0);
                c.Root.Offset = o0;
                c.Offset = o0;
                c.Dim.Opacity = 0.22f;
                p.Cases[i] = c;
                p.Row!.Children.InsertAtTop(c.Root);
                if (i < p.Items.Count && (i == 0 || CsBucket(p.Items[i]) != CsBucket(p.Items[i - 1])))
                {
                    string b = CsBucket(p.Items[i]);
                    var lab = CsSprite(200, 22, ds =>
                    {
                        using var l = TextKit.Layout(ds, b, TextKit.Ui(500), 15, 200, CanvasHorizontalAlignment.Left);
                        ds.DrawTextLayout(l, 0, 0, Gfx.Rgba(242, 242, 242, 0.6));
                    });
                    lab.Offset = o0 + new Vector3(0, -36, 0);
                    p.Labels[i] = lab;
                    p.Row.Children.InsertAtTop(lab);
                    c.Label = lab;
                }
            }
            var (off, size) = ShelfRect(i, s, n);
            c.Offset = off; c.Size = size;
            float labY = off.Y + (i == s ? 8 : 0) - (i == s ? 44 : 36);
            if (!animate)
            {
                c.Root.StopAnimation("Offset"); c.Root.StopAnimation("Size");
                c.Root.Size = size;
                if (entry && !Reduced && live && off.X < CsW && off.X + size.X > 0)
                {
                    c.Root.Offset = off + new Vector3(0, 40, 0);
                    c.Root.Opacity = 0;
                    double delay = 80 + order++ * 30;
                    c.Root.StartAnimation("Offset", CsV3(off, 600, CsQuart, delay));
                    c.Root.StartAnimation("Opacity", CsS(1, 420, CsQuart, delay));
                    if (c.Label is { } l0) { l0.Offset = new Vector3(off.X, labY + 40, 0); l0.Opacity = 0; l0.StartAnimation("Offset", CsV3(new Vector3(off.X, labY, 0), 600, CsQuart, delay)); l0.StartAnimation("Opacity", CsS(i == s ? 0 : 1, 420, CsQuart, delay)); }
                }
                else { c.Root.Offset = off; if (c.Label is { } l1) { l1.Offset = new Vector3(off.X, labY, 0); l1.StopAnimation("Opacity"); l1.Opacity = i == s ? 0 : 1; } }
            }
            else
            {
                c.Root.StartAnimation("Offset", CsV3(off, ms, CsQuart));
                c.Root.StartAnimation("Size", CsV2(size, ms, CsQuart));
                c.Label?.StartAnimation("Offset", CsV3(new Vector3(off.X, labY, 0), ms, CsQuart));
                // 选中那盒的分组标签藏起来（meta 里已经写了「昨天」之类）
                c.Label?.StartAnimation("Opacity", CsS(i == s ? 0 : 1, CsMs(300), CsQuart));
            }
            if (i == s) CsSelect(c, true, shine: animate && i != p.Prev);
            else if (c.Selected || !animate) { CsSelect(c, false, false); if (!animate) { c.Dim.StopAnimation("Opacity"); c.Dim.Opacity = 0.22f; } }
            if (i == s && !animate) { c.Dim.StopAnimation("Opacity"); c.Dim.Opacity = 0; }
        }
        // 窗口外的：动画走完再拆
        var gone = p.Cases.Keys.Where(i => i < lo || i > hi).ToList();
        if (gone.Count > 0)
            After(ms + 80, () =>
            {
                if (!p.Cases.ContainsKey(gone[0]) && gone.All(i => !p.Cases.ContainsKey(i))) return;
                var (lo2, hi2) = ShelfWindow(p.Sel, CsCount(p));
                foreach (var i in gone)
                    if ((i < lo2 || i > hi2) && p.Cases.Remove(i, out var c))
                    {
                        p.Row!.Children.Remove(c.Root);
                        if (c.Label != null) { p.Row.Children.Remove(c.Label); p.Labels.Remove(i); }
                        CsDisposeCase(c);
                    }
            });
    }

    // ---------------- 叠放 ----------------
    //
    // 变换移植自 CollectionViewPagingLayout（https://github.com/amirdew/CollectionViewPagingLayout）
    // Lib/Stack/StackTransformView.swift 与 "perspective" 预设，参数按规格 3 调过。
    //
    // MIT License
    //
    // Copyright (c) 2019 Amir Khorsandi
    //
    // Permission is hereby granted, free of charge, to any person obtaining a copy
    // of this software and associated documentation files (the "Software"), to deal
    // in the Software without restriction, including without limitation the rights
    // to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    // copies of the Software, and to permit persons to whom the Software is
    // furnished to do so, subject to the following conditions:
    //
    // The above copyright notice and this permission notice shall be included in all
    // copies or substantial portions of the Software.
    //
    // THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    // IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    // FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    // AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    // LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    // OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    // SOFTWARE.

    private const float StW = 420, StH = 630, StX = 920, StY = 300;
    private const int StMax = 6;

    /// <summary>p = 卡片序号 − 当前序号时的位移、缩放、旋转（弧度）、透明度和压暗。</summary>
    private static (Vector2 T, float Scale, float Rot, float Alpha, float Dim) StackAt(int i, int s)
    {
        double p = Math.Max(-1, i - s);
        double scale = Math.Max(0.2, 1 - Math.Max(0, p) * 0.08);
        double prog = Math.Clamp(p / StMax, 0, 1);
        double pp = (1 - (1 - prog) * (1 - prog)) * 0.15;
        double tx = StW * 0.2 * Math.Max(p, 0);
        double xa = -(((scale - 1) * StW) / 2 + pp * StW), ya = 0;
        if (p < 0) { xa -= StW * -1.45 * p; ya -= StH * 0.3 * p; }   // 翻过去的那张向左下甩出去
        double ang = p < 0 ? -Math.Abs(p) * Math.PI / 10 : (i % 2 == 1 ? 1 : -1) * Math.Min(p, 1) * 0.035;   // 我们加的：随手叠的一摞
        double a = 1;
        if (p >= StMax - 1) a = 1 - Math.Clamp((p - (StMax - 1)) / 10.0, 0, 1) * 10;
        if (p < 0 || i - s > StMax) a = 0;
        double dim = p > 0 ? Math.Min(0.55, p * 0.11) : 0;
        return (new Vector2((float)(tx + xa), (float)ya), (float)scale, (float)ang, (float)Math.Clamp(a, 0, 1), (float)dim);
    }

    private void CsLayoutStack(CsPage p, bool animate, bool entry = false)
    {
        int n = CsCount(p), s = p.Sel;
        int lo = Math.Max(0, Math.Min(s, p.Prev) - 2), hi = Math.Min(n - 1, Math.Max(s, p.Prev) + StMax + 1);
        double ms = CsMs(600);
        for (int i = lo; i <= hi; i++)
        {
            if (!p.Cases.TryGetValue(i, out var c))
            {
                c = CsNewCase(i < p.Items.Count ? p.Items[i] : null, i, new Vector2(StW, StH), hiRes: true);
                c.Root.CenterPoint = new Vector3(StW / 2, StH / 2, 0);
                c.Shadow.Opacity = 0.6f;
                var (t0, s0, r0, a0, d0) = StackAt(i, animate ? p.Prev : s);
                c.Root.Offset = new Vector3(StX + t0.X, StY + t0.Y, 0);
                c.Root.Scale = new Vector3(s0, s0, 1);
                c.Root.RotationAngle = r0;
                c.Root.Opacity = a0;
                c.Dim.Opacity = d0;
                p.Cases[i] = c;
                p.Row!.Children.InsertAtTop(c.Root);
            }
            var (t, sc, rot, alpha, dim) = StackAt(i, s);
            var off = new Vector3(StX + t.X, StY + t.Y, 0);
            c.Offset = off;
            c.Size = new Vector2(StW, StH);
            if (!animate)
            {
                foreach (var prop in new[] { "Offset", "Scale", "RotationAngle", "Opacity" }) c.Root.StopAnimation(prop);
                c.Dim.StopAnimation("Opacity");
                c.Dim.Opacity = dim;
                if (entry && !Reduced && alpha > 0)
                {
                    // 进场：从右侧依次落到牌堆上（最底下的先到），每张错开 40 ms
                    double delay = 80 + Math.Max(0, StMax - (i - s)) * 40;
                    c.Root.Offset = off + new Vector3(520, -90, 0);
                    c.Root.Scale = new Vector3(sc * 1.04f, sc * 1.04f, 1);
                    c.Root.RotationAngle = rot + 0.12f;
                    c.Root.Opacity = 0;
                    c.Root.StartAnimation("Offset", CsV3(off, 600, CsQuart, delay));
                    c.Root.StartAnimation("Scale", CsV3(new Vector3(sc, sc, 1), 600, CsQuart, delay));
                    c.Root.StartAnimation("RotationAngle", CsS(rot, 600, CsQuart, delay));
                    c.Root.StartAnimation("Opacity", CsS(alpha, 300, Bezier.EaseOut, delay));
                }
                else
                {
                    c.Root.Offset = off; c.Root.Scale = new Vector3(sc, sc, 1); c.Root.RotationAngle = rot; c.Root.Opacity = alpha;
                }
            }
            else
            {
                c.Root.StartAnimation("Offset", CsV3(off, ms, CsQuart));
                c.Root.StartAnimation("Scale", CsV3(new Vector3(sc, sc, 1), ms, CsQuart));
                c.Root.StartAnimation("RotationAngle", CsS(rot, ms, CsQuart));
                c.Root.StartAnimation("Opacity", CsS(alpha, ms, CsQuart));
                c.Dim.StartAnimation("Opacity", CsS(dim, ms, CsQuart));
            }
            if (i == s) { if (!c.Selected || (animate && i != p.Prev)) CsSelectStack(c, true, animate && i != p.Prev); }
            else if (c.Selected) CsSelectStack(c, false, false);
        }
        // 叠放次序：序号小的在上面
        foreach (var i in p.Cases.Keys.OrderByDescending(i => i))
        {
            var v = p.Cases[i].Root;
            if (v.Parent != p.Row) continue;
            p.Row!.Children.Remove(v);
            p.Row.Children.InsertAtTop(v);
        }
        var gone = p.Cases.Keys.Where(i => i < s - 2 || i > s + StMax + 1).ToList();
        if (gone.Count > 0)
            After(ms + 80, () =>
            {
                foreach (var i in gone)
                    if ((i < p.Sel - 2 || i > p.Sel + StMax + 1) && p.Cases.Remove(i, out var c)) { p.Row!.Children.Remove(c.Root); CsDisposeCase(c); }
            });
    }

    /// <summary>叠放：最上面那张有描边和主色光晕，压暗由层数决定。</summary>
    private void CsSelectStack(CsCase c, bool on, bool shine) => CsSelect(c, on, shine, 150, dimOthers: false);

    private void CsInsertCaseOrdered(CsPage p, CsCase c)
    {
        p.Row!.Children.InsertAtTop(c.Root);
    }

    // ---------------- 信息区 ----------------

    private static readonly string[] CsActNames = { "开始游戏", "游戏设置", "收藏" };

    /// <summary>标题（64 px / 600，展架单行省略、叠放最多两行）、meta（20 px，60% 白）和三个按钮。</summary>
    private void CsPaintInfo(CsPage p, bool animate)
    {
        if (p.InfoA == null || p.InfoB == null || p.ActsV == null) return;
        var g = p.Sel < p.Items.Count ? p.Items[p.Sel] : null;
        string title = g?.Title ?? "全部游戏";
        string meta = g != null ? CsMetaFull(g) : $"{p.Items.Count} 款";
        bool stack = p.Style == HomeStyle.Stack;
        float actsW = CsActsWidth(g);
        float w = stack ? 640 : CsW - 192;
        float gap = stack ? 12 : 10;
        var dev = G.Device;
        float titleH;
        using (var probe = TextKit.Layout(dev, title, TextKit.Ui(600), 64, w, CanvasHorizontalAlignment.Left, lineHeight: 70, maxLines: stack ? 2 : 1, balance: stack))
            titleH = Math.Max(70, (float)probe.LayoutBounds.Height);
        float h = titleH + gap + 28;
        var front = p.InfoFront ? p.InfoB : p.InfoA;
        var back = p.InfoFront ? p.InfoA : p.InfoB;
        var target = animate && !Reduced ? back : front;
        CsRepaint(target, w, h, ds =>
        {
            using var t = TextKit.Layout(ds, title, TextKit.Ui(600), 64, w, CanvasHorizontalAlignment.Left, lineHeight: 70, maxLines: stack ? 2 : 1, balance: stack);
            ds.DrawTextLayout(t, 0, 0, Gfx.Rgba(242, 242, 242));
            using var m = TextKit.Layout(ds, meta, TextKit.Num(400), 20, w, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(m, 0, titleH + gap, Gfx.Rgba(242, 242, 242, 0.6));
        });
        if (stack)
        {
            float total = h + 30 + 64;
            float top = CsH / 2 - total / 2 + 20;
            target.Offset = new Vector3(96, top, 0);
            p.ActsAt = new Vector2(96, top + h + 30);
        }
        else
        {
            // 展架（19:20 修订）：信息区左对齐叠在选中卡盒正上方，按钮底边距卡盒顶 28 px
            float actsBottom = ShBottom - ShH1 - 8 - 28;
            p.ActsAt = new Vector2(96, actsBottom - 64);
            target.Offset = new Vector3(96, actsBottom - 64 - 22 - h, 0);
        }
        if (target == back)
        {
            p.InfoFront = !p.InfoFront;
            // 旧标题先很快退掉，新标题稍后淡入：连按时不会两行字叠在一起
            back.StopAnimation("Opacity");
            back.Opacity = 0;
            back.StartAnimation("Opacity", CsS(1, 180, Bezier.EaseOut, 70));
            front.StartAnimation("Opacity", CsS(0, 70, Bezier.EaseOut));
        }
        else { front.StopAnimation("Opacity"); front.Opacity = 1; back.StopAnimation("Opacity"); back.Opacity = 0; }
        CsPaintActs(p, g, actsW);
    }

    private float CsActsWidth(LibraryItem? g)
    {
        if (g == null) return 0;
        float pill;
        using (var l = TextKit.Layout(G.Device, CsActNames[0], TextKit.Ui(600), 20, 400, CanvasHorizontalAlignment.Left)) pill = (float)l.LayoutBounds.Width + 34 * 2 + 30;
        return pill + (16 + 64) * CsActCount(g) - (16 + 64);
    }

    private int CsActCount(LibraryItem g) => CsCanFavorite(g) ? 3 : 2;

    private static bool CsCanFavorite(LibraryItem g) => g.Platform is "steam" or "local" or "other" && uint.TryParse(g.Id, out _);

    private void CsPaintActs(CsPage p, LibraryItem? g, float w)
    {
        p.ActRects.Clear();
        if (p.ActsV == null) return;
        if (g == null) { p.ActsV.IsVisible = false; return; }
        p.ActsV.IsVisible = true;
        float pill = w - (16 + 64) * (CsActCount(g) - 1);
        bool fav = _csFav.Contains(g.Id);
        int focus = p.Acts ? p.Act : -1;
        CsRepaint(p.ActsV, w + 12, 64 + 12, ds =>
        {
            float x = 6, y = 6;
            // 主按钮：白底黑字
            ds.FillRoundedRectangle(x, y, pill, 64, 32, 32, Gfx.Rgba(242, 242, 242));
            using (var play = CanvasGeometry.CreatePolygon(ds, new[] { new Vector2(x + 34, y + 22), new Vector2(x + 34, y + 42), new Vector2(x + 50, y + 32) }))
                ds.FillGeometry(play, Gfx.Rgba(17, 17, 17));
            using (var l = TextKit.Layout(ds, CsActNames[0], TextKit.Ui(600), 20, pill, CanvasHorizontalAlignment.Left))
                ds.DrawTextLayout(l, x + 34 + 28, y + 32 - (float)l.LayoutBounds.Height / 2 - (float)l.LayoutBounds.Y, Gfx.Rgba(17, 17, 17));
            if (focus == 0) ds.DrawRoundedRectangle(x - 4, y - 4, pill + 8, 72, 36, 36, Colors.White, 3);
            float bx = x + pill + 16;
            for (int a = 1; a < CsActCount(g); a++)
            {
                bool on = a == 2 && fav;
                ds.FillCircle(bx + 32, y + 32, 32, on ? Gfx.Rgba(242, 242, 242) : Gfx.Rgba(255, 255, 255, 0.14));
                var ink = on ? Gfx.Rgba(17, 17, 17) : Gfx.Rgba(242, 242, 242);
                if (a == 1) CsIconSliders(ds, bx + 20, y + 20, ink); else CsIconStar(ds, bx + 20, y + 20, ink, on);
                if (focus == a) ds.DrawCircle(bx + 32, y + 32, 36, Colors.White, 3);
                bx += 64 + 16;
            }
        });
        // 位置只由 ActsAt 决定：焦点在按钮间移动只重画描边，不挪位置
        var o = p.ActsAt;
        p.ActsV.Offset = new Vector3(o.X - 6, o.Y - 6, 0);
        p.ActRects.Add((o.X, o.Y, pill, 64, 0));
        float hx = o.X + pill + 16;
        for (int a = 1; a < CsActCount(g); a++) { p.ActRects.Add((hx, o.Y, 64, 64, a)); hx += 80; }
    }

    private static void CsIconSliders(CanvasDrawingSession ds, float x, float y, Color c)
    {
        float s = 1;
        ds.DrawLine(x + 4 * s, y + 7, x + 14, y + 7, c, 1.8f); ds.DrawLine(x + 18, y + 7, x + 20, y + 7, c, 1.8f);
        ds.DrawLine(x + 4, y + 17, x + 6, y + 17, c, 1.8f); ds.DrawLine(x + 10, y + 17, x + 20, y + 17, c, 1.8f);
        ds.DrawCircle(x + 16, y + 7, 2.2f, c, 1.8f); ds.DrawCircle(x + 8, y + 17, 2.2f, c, 1.8f);
    }

    private static void CsIconStar(CanvasDrawingSession ds, float x, float y, Color c, bool fill)
    {
        var pts = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / 5, r = i % 2 == 0 ? 9.5 : 4.2;
            pts[i] = new Vector2(x + 12 + (float)(Math.Cos(a) * r), y + 12.5f + (float)(Math.Sin(a) * r));
        }
        using var g = CanvasGeometry.CreatePolygon(ds, pts);
        if (fill) ds.FillGeometry(g, c);
        ds.DrawGeometry(g, c, 1.7f, new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round });
    }

    // ---------------- 分类页 ----------------

    private CsPage CsBuildCat(int tab, string? spot, bool entry)
    {
        var items = CsList(tab);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var content = G.Container(relative: false);
        content.Size = new Vector2(CsW, CsH);
        content.Clip = G.C.CreateInsetClip(0, CatTop, 0, 0);
        var page = new CsPage { Root = root, Bg = null!, Content = content, Tab = tab, Items = items, Built = NowMs };
        page.Bg = CsBuildBg(page, HomeStyle.Shelf, cat: true);
        root.Children.InsertAtTop(page.Bg);
        root.Children.InsertAtTop(content);
        page.Recent = items.Where(g => g.LastPlayedDays is int d && d < 30).Take(8).ToList();
        var scroll = G.Container(relative: false);
        scroll.Size = new Vector2(CsW, CsH);
        content.Children.InsertAtTop(scroll);
        page.Scroll = scroll;
        float y = CatTop + 16;
        if (page.Recent.Count > 0)
        {
            var sec = CsSection("最近", null);
            sec.Offset = new Vector3(96, y + 16, 0);
            scroll.Children.InsertAtTop(sec);
            var row = G.Container(relative: false);
            row.Offset = new Vector3(96, y + 86 + 14, 0);
            scroll.Children.InsertAtTop(row);
            page.RecentRow = row;
            for (int i = 0; i < page.Recent.Count; i++)
            {
                var cart = CsNewCart(page.Recent[i], i);
                cart.Root.Offset = new Vector3(i * (400 + 24), 0, 0);
                row.Children.InsertAtTop(cart.Root);
                page.Carts[i] = cart;
                if (entry && !Reduced)
                {
                    cart.Root.Offset = new Vector3(i * 424, 24, 0);
                    cart.Root.Opacity = 0;
                    cart.Root.StartAnimation("Offset", CsV3(new Vector3(i * 424, 0, 0), 600, CsQuart, 60 + i * 40));
                    cart.Root.StartAnimation("Opacity", CsS(1, 420, CsQuart, 60 + i * 40));
                }
            }
            y += 86 + 14 + 300 + 12 + 50 + 18 + 44;
        }
        var all = CsSection("全部", items.Count.ToString());
        all.Offset = new Vector3(96, y + 16 - (page.Recent.Count > 0 ? 44 : 0) + (page.Recent.Count > 0 ? 44 : 0), 0);
        scroll.Children.InsertAtTop(all);
        y += 16 + 32 + 22;
        var grid = G.Container(relative: false);
        grid.Offset = new Vector3(0, y + 8, 0);
        scroll.Children.InsertAtTop(grid);
        page.GridHost = grid;
        page.GridTop = y + 8;
        if (entry && !Reduced)
        {
            grid.Opacity = 0;
            grid.Offset = new Vector3(0, page.GridTop + 16, 0);
            grid.StartAnimation("Opacity", CsS(1, 500, CsQuart, 120));
            grid.StartAnimation("Offset", CsV3(new Vector3(0, page.GridTop, 0), 600, CsQuart, 120));
            foreach (var v in new Visual[] { all })
            {
                v.Opacity = 0;
                v.StartAnimation("Opacity", CsS(1, 400, CsQuart, 80));
            }
        }
        // 焦点：从首页带过来的游戏在最近里就落在那张卡带上，否则落在网格里
        int k = spot == null ? -1 : items.FindIndex(g => g.Id == spot);
        int r = spot == null ? -1 : page.Recent.FindIndex(g => g.Id == spot);
        if (page.Recent.Count > 0 && (k < 0 || r == 0)) { page.InRecent = true; page.Ai = Math.Max(0, r); }
        else { page.InRecent = false; page.Ai = Math.Max(0, k); }
        var prev = _cs!.Page;
        _cs.Page = page;
        CsCatFocus(page, instant: true);
        CsSetBackground(CsSpotItem());
        _cs.Page = prev;
        return page;
    }

    private SpriteVisual CsSection(string title, string? count)
    {
        return CsSprite(600, 34, ds =>
        {
            using var t = TextKit.Layout(ds, title, TextKit.Ui(600), 24, 600, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(t, 0, 0, Gfx.Rgba(242, 242, 242));
            if (count == null) return;
            using var c = TextKit.Layout(ds, count, TextKit.Num(400), 17, 200, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(c, (float)t.LayoutBounds.Width + 12, 7, Gfx.Rgba(242, 242, 242, 0.34));
        });
    }

    private float CsCatContentH(CsPage p) => p.GridTop + (float)Math.Ceiling(p.Items.Count / (double)GridCols) * GridPitch - GridGapY + 120;

    /// <summary>网格里第 i 格在滚动层里的位置。</summary>
    private static Vector3 GridCell(int i) => new(96 + (i % GridCols) * (GridW + GridGapX), (i / GridCols) * GridPitch, 0);

    /// <summary>可见的几行建出来（滚动目标和此刻位置都覆盖到），远离的拆掉。</summary>
    private void CsCatRealize(CsPage p, float scrollA, float scrollB)
    {
        if (p.GridHost == null) return;
        float top = Math.Min(scrollA, scrollB) - p.GridTop - 300, bottom = Math.Max(scrollA, scrollB) - p.GridTop + CsH + 300;
        int r0 = Math.Max(0, (int)Math.Floor(top / GridPitch)), r1 = (int)Math.Ceiling(bottom / GridPitch);
        int i0 = r0 * GridCols, i1 = Math.Min(p.Items.Count - 1, (r1 + 1) * GridCols - 1);
        for (int i = i0; i <= i1; i++)
        {
            if (p.Grid.ContainsKey(i)) continue;
            var c = CsNewCase(p.Items[i], i, new Vector2(GridW, GridH));
            c.Root.CenterPoint = new Vector3(GridW / 2, GridH / 2, 0);
            var cell = GridCell(i);
            c.Root.Offset = cell; c.Offset = cell;
            var cap = CsCaption(p.Items[i], GridW, false);
            cap.Offset = cell + new Vector3(0, GridH + 12, 0);
            p.GridHost.Children.InsertAtTop(c.Root);
            p.GridHost.Children.InsertAtTop(cap);
            p.Grid[i] = (c, cap);
            if (!p.InRecent && i == p.Ai) CsGridFocus(c, true, shine: false);
        }
        foreach (var i in p.Grid.Keys.Where(i => i < i0 - GridCols || i > i1 + GridCols).ToList())
            if (p.Grid.Remove(i, out var e)) { p.GridHost.Children.Remove(e.Case.Root); p.GridHost.Children.Remove(e.Caption); CsDisposeCase(e.Case); e.Caption.Dispose(); }
    }

    private void CsGridFocus(CsCase c, bool on, bool shine)
    {
        double ms = CsMs(400);
        c.Root.StartAnimation("Offset", CsV3(c.Offset + new Vector3(0, on ? -8 : 0, 0), ms, CsQuart));
        c.Glow.StartAnimation("Opacity", CsS(on ? 1 : 0, ms, CsQuart));
        c.Shadow.StartAnimation("Opacity", CsS(on ? 0.75f : 0.45f, ms, CsQuart));
        c.Ring.StartAnimation("Opacity", CsS(on ? 1 : 0, ms, CsQuart));
        if (on && shine) CsSelect(c, true, true, 100, dimOthers: false);
        else c.Selected = on;
    }

    /// <summary>分类页焦点：卡带 / 卡盒高亮，页面纵向滚到焦点在可见区中部，最近一排横向滚到焦点完整可见；背景换图。</summary>
    private void CsCatFocus(CsPage p, bool instant, int prevAi = -1, bool prevRecent = false)
    {
        if (prevAi >= 0)
        {
            if (prevRecent && p.Carts.TryGetValue(prevAi, out var pc)) CsFocusCart(pc, false);
            if (!prevRecent && p.Grid.TryGetValue(prevAi, out var pg)) CsGridFocus(pg.Case, false, false);
        }
        float view = CsH - CatTop;
        float target;
        if (p.InRecent) target = 0;
        else
        {
            float cy = p.GridTop + GridCell(p.Ai).Y + GridH / 2;
            target = Math.Clamp(cy - (CatTop + view / 2), 0, Math.Max(0, CsCatContentH(p) - CsH));
        }
        CsCatRealize(p, p.ScrollY, target);
        p.ScrollY = target;
        double ms = instant ? 1 : CsMs(450);
        if (instant) CsSet(p.Scroll!, offset: new Vector3(0, -target, 0));
        else p.Scroll!.StartAnimation("Offset", CsV3(new Vector3(0, -target, 0), ms, CsQuart));
        if (p.InRecent && p.RecentRow != null)
        {
            float l = 96 + p.Ai * 424 - p.RecentX, r = l + 400;
            float rx = p.RecentX;
            if (l < 96) rx -= 96 - l; else if (r > CsW - 96) rx += r - (CsW - 96);
            rx = Math.Max(0, rx);
            p.RecentX = rx;
            var to = new Vector3(96 - rx, p.RecentRow.Offset.Y, 0);
            if (instant) CsSet(p.RecentRow, offset: to); else p.RecentRow.StartAnimation("Offset", CsV3(to, ms, CsQuart));
            if (p.Carts.TryGetValue(p.Ai, out var cart)) CsFocusCart(cart, true);
        }
        else if (p.Grid.TryGetValue(p.Ai, out var cell)) CsGridFocus(cell.Case, true, shine: !instant);
        if (_cs?.Page == p) { CsSetBackground(CsSpotItem()); CsNoteSpot(); }
    }

    // ---------------- 输入 ----------------

    private void CsMove(Direction dir)
    {
        if (_cs?.Page is not { } p || _csInsert != null) { Cue(Sfx.None); return; }
        if (p.Tab == 0) CsHomeMove(p, dir); else CsCatMove(p, dir);
    }

    private void CsHomeMove(CsPage p, Direction dir)
    {
        if (p.Style == HomeStyle.Stack) { CsStackMove(p, dir); return; }
        if (p.Acts)
        {
            var g = p.Sel < p.Items.Count ? p.Items[p.Sel] : null;
            int n = g != null ? CsActCount(g) : 0;
            if (dir == Direction.Down) { p.Acts = false; CsPaintActs(p, g, CsActsWidth(g)); CsLayoutHome(p, animate: true); return; }
            if (dir is Direction.Left or Direction.Right)
            {
                int next = p.Act + (dir == Direction.Left ? -1 : 1);
                if (next < 0 || next >= n) { Cue(Sfx.Bump, pan: dir == Direction.Left ? -0.6f : 0.6f); return; }
                p.Act = next;
                CsPaintActs(p, g, CsActsWidth(g));
                return;
            }
            Cue(Sfx.Bump);
            return;
        }
        if (dir == Direction.Up)
        {
            if (p.Sel >= p.Items.Count) { Cue(Sfx.Bump); return; }
            p.Acts = true; p.Act = 0;
            var g = p.Items[p.Sel];
            CsPaintActs(p, g, CsActsWidth(g));
            if (p.Cases.TryGetValue(p.Sel, out var c)) c.Ring.StartAnimation("Opacity", CsS(0, CsMs(250), CsQuart));
            return;
        }
        if (dir == Direction.Down) { Cue(Sfx.Bump); return; }
        CsStep(p, dir == Direction.Left ? -1 : 1);
    }

    /// <summary>
    /// 叠放（19:20 修订）：牌堆在按钮右边。上 / 下翻牌；牌堆上按左进按钮，先落到最右边那个（收藏），再往左是设置、开始游戏；
    /// 最右边的按钮上按右、或按钮上按上 / 下，回到牌堆。
    /// </summary>
    private void CsStackMove(CsPage p, Direction dir)
    {
        var g = p.Sel < p.Items.Count ? p.Items[p.Sel] : null;
        int n = g != null ? CsActCount(g) : 0;
        if (!p.Acts)
        {
            if (dir is Direction.Up or Direction.Down) { CsStep(p, dir == Direction.Up ? -1 : 1); return; }
            if (dir == Direction.Left && n > 0)
            {
                p.Acts = true; p.Act = n - 1;
                CsPaintActs(p, g, CsActsWidth(g));
                if (p.Cases.TryGetValue(p.Sel, out var c)) c.Ring.StartAnimation("Opacity", CsS(0, CsMs(250), CsQuart));
                CuePan(-0.4f);
                return;
            }
            Cue(Sfx.Bump, pan: dir == Direction.Left ? -0.6f : 0.6f);
            return;
        }
        if (dir is Direction.Up or Direction.Down || (dir == Direction.Right && p.Act == n - 1))
        {
            p.Acts = false;
            CsPaintActs(p, g, CsActsWidth(g));
            if (p.Cases.TryGetValue(p.Sel, out var c)) c.Ring.StartAnimation("Opacity", CsS(1, CsMs(300), CsQuart));
            CuePan(0.4f);
            return;
        }
        int next = p.Act + (dir == Direction.Left ? -1 : 1);
        if (next < 0) { Cue(Sfx.Bump, pan: -0.6f); return; }
        p.Act = next;
        CsPaintActs(p, g, CsActsWidth(g));
    }

    private void CsStep(CsPage p, int d)
    {
        int n = CsCount(p);
        int j = Math.Clamp(p.Sel + d, 0, n - 1);
        if (j == p.Sel) { Cue(Sfx.Bump, pan: d < 0 ? -0.6f : 0.6f); return; }
        CuePan(Math.Clamp((j - p.Sel) * 0.3f, -0.6f, 0.6f));
        p.Prev = p.Sel;
        p.Sel = j;
        CsLayoutHome(p, animate: true);
        CsPaintInfo(p, animate: true);
        CsSetBackground(CsSpotItem());
        CsNoteSpot();
    }

    private void CsLayoutHome(CsPage p, bool animate)
    {
        if (p.Style == HomeStyle.Shelf) CsLayoutShelf(p, animate); else CsLayoutStack(p, animate);
        if (!p.Acts && p.Cases.TryGetValue(p.Sel, out var c)) c.Ring.StartAnimation("Opacity", CsS(1, CsMs(300), CsQuart));
        p.Prev = p.Sel;
    }

    private void CsCatMove(CsPage p, Direction dir)
    {
        int nR = p.Recent.Count, nG = p.Items.Count;
        int pa = p.Ai; bool pr = p.InRecent;
        float colW = GridW + GridGapX;
        if (p.InRecent)
        {
            if (dir is Direction.Left or Direction.Right)
            {
                int j = p.Ai + (dir == Direction.Left ? -1 : 1);
                if (j < 0 || j >= nR) { Cue(Sfx.Bump, pan: dir == Direction.Left ? -0.6f : 0.6f); return; }
                p.Ai = j;
            }
            else if (dir == Direction.Down && nG > 0)
            {
                // 落到网格里最近的一列
                // 卡带和格子宽度不同：按列宽换算到最近的一列
                int col = Math.Clamp((int)Math.Round(p.Ai * 424 / colW), 0, GridCols - 1);
                p.InRecent = false; p.Ai = Math.Min(nG - 1, col);
            }
            else { Cue(Sfx.Bump); return; }
        }
        else
        {
            if (dir is Direction.Left or Direction.Right)
            {
                int j = p.Ai + (dir == Direction.Left ? -1 : 1);
                if (j < 0 || j >= nG || j / GridCols != p.Ai / GridCols) { Cue(Sfx.Bump, pan: dir == Direction.Left ? -0.6f : 0.6f); return; }
                p.Ai = j;
            }
            else
            {
                int j = p.Ai + (dir == Direction.Up ? -GridCols : GridCols);
                if (j >= 0 && j < nG) p.Ai = j;
                else if (j < 0 && nR > 0)
                {
                    p.InRecent = true; p.Ai = Math.Min(nR - 1, (int)Math.Round((p.Ai % GridCols) * colW / 424));
                }
                else if (j >= nG && p.Ai / GridCols < (nG - 1) / GridCols) p.Ai = nG - 1;
                else { Cue(Sfx.Bump); return; }
            }
        }
        CsCatFocus(p, instant: false, pa, pr);
    }

    private void CsActivate()
    {
        if (_cs?.Page is not { } p) return;
        if (_csInsert != null) { Cue(Sfx.None); return; }
        if (p.Tab == 0)
        {
            if (p.Sel >= p.Items.Count) { CsGoTab(1); return; }
            var g = p.Items[p.Sel];
            if (p.Acts && p.Act == 1) { CsOpenGameSettings(g); return; }
            if (p.Acts && p.Act == 2) { CsToggleFavorite(p, g); return; }
            if (p.Cases.TryGetValue(p.Sel, out var c)) CsLaunch(g, c.Root, CsCaseRect(p, c));
            return;
        }
        var item = CsSpotItem();
        if (item == null) return;
        if (p.InRecent && p.Carts.TryGetValue(p.Ai, out var cart))
        {
            // 卡带没有盒子：卡盒从卡带的位置（按卡带高度）飞出来
            var o = p.RecentRow!.Offset + cart.Root.Offset + new Vector3(0, -p.ScrollY - 10, 0);
            CsLaunch(item, cart.Body, (o.X + 100, o.Y, 200, 300));
        }
        else if (p.Grid.TryGetValue(p.Ai, out var cell))
        {
            var o = cell.Case.Offset + new Vector3(0, -8, 0) + p.GridHost!.Offset + new Vector3(0, -p.ScrollY, 0);
            CsLaunch(item, cell.Case.Root, (o.X, o.Y, GridW, GridH));
        }
    }


    private (float X, float Y, float W, float H) CsCaseRect(CsPage p, CsCase c)
    {
        if (p.Style == HomeStyle.Stack) return (c.Offset.X, c.Offset.Y, StW, StH);
        return (c.Offset.X, c.Offset.Y, c.Size.X, c.Size.Y);
    }

    private bool CsBack()
    {
        if (_cs?.Page is not { } p) return false;
        if (_csInsert != null) { CsInsertBack(); return true; }
        if (p.Tab != 0) { Cue(Sfx.Tab); CsShowTab(0, -1, entry: true); return true; }
        if (p.Acts) { CsHomeMove(p, Direction.Down); return true; }
        return false;
    }

    /// <summary>Y：选中的游戏打开游戏设置（和墙上展开的卡按 Y 一样）。</summary>
    private void CsGameSettings()
    {
        if (_csInsert != null) return;
        var g = CsSpotItem();
        if (g == null) { Cue(Sfx.Bump); return; }
        CsOpenGameSettings(g);
    }

    private void CsOpenGameSettings(LibraryItem g)
    {
        if (g.Platform == "folder") { Toast("文件夹里的游戏先在设置里添加成本地游戏"); return; }
        if (!g.Installed) { Toast($"「{g.Title}」未安装"); return; }
        Cue(Sfx.ModalShow);
        OpenGameSettings(g);
    }

    private void CsToggleFavorite(CsPage p, LibraryItem g)
    {
        bool on = !_csFav.Contains(g.Id);
        if (on) _csFav.Add(g.Id); else _csFav.Remove(g.Id);
        Cue(on ? Sfx.ToggleOn : Sfx.ToggleOff);
        CsPaintActs(p, g, CsActsWidth(g));
        _ = Task.Run(async () =>
        {
            try
            {
                _agent ??= new AgentClient();
                var req = new JsonObject { ["id"] = g.Id, ["platform"] = g.Platform, ["title"] = g.Title, ["exe"] = "", ["magpieKey"] = "" };
                await _agent.CallLong("game_settings_set", 40, req, JsonValue.Create("favorite"), JsonValue.Create(on));
            }
            catch (Exception ex)
            {
                _dq.TryEnqueue(() =>
                {
                    if (on) _csFav.Remove(g.Id); else _csFav.Add(g.Id);
                    if (_cs?.Page == p && p.Sel < p.Items.Count && p.Items[p.Sel] == g) CsPaintActs(p, g, CsActsWidth(g));
                    Toast($"没有改成：{ex.Message}");
                });
            }
        });
    }

    // ---------------- 触摸 / 鼠标 ----------------

    private (float X, float Y) CsLocal(double sx, double sy)
    {
        var o = _cs!.Root.Offset;
        return ((float)((sx - o.X) / _cs.K), (float)((sy - o.Y) / _cs.K));
    }

    /// <summary>点一下选中，再点一下启动；点标签切页。</summary>
    private bool CsClick(double sx, double sy)
    {
        if (_cs?.Page is not { } p) return false;
        if (_csInsert != null) { if (_csInsert.Done) { } return true; }
        var (x, y) = CsLocal(sx, sy);
        if (y < CatTop)
        {
            foreach (var t in _cs.Tabs)
                if (x >= t.X - 12 && x <= t.X + t.V.Size.X + 12) { CsGoTab(t.Tab); return true; }
            return true;
        }
        if (p.Tab == 0)
        {
            foreach (var r in p.ActRects)
                if (x >= r.X && x <= r.X + r.W && y >= r.Y && y <= r.Y + r.H)
                {
                    p.Acts = true; p.Act = r.Act;
                    WithCue(Sfx.Activate, CsActivate);
                    return true;
                }
            CsCase? hit = null;
            if (p.Style == HomeStyle.Shelf)
            {
                foreach (var c in p.Cases.Values)
                    if (x >= c.Offset.X && x <= c.Offset.X + c.Size.X && y >= c.Offset.Y && y <= c.Offset.Y + c.Size.Y) hit = c;
            }
            else
            {
                // 最上面那张优先（序号小的在上）
                foreach (var c in p.Cases.Values.OrderByDescending(c => c.Index))
                {
                    var (t, sc, _, a, _) = StackAt(c.Index, p.Sel);
                    float w = StW * sc, h = StH * sc, cx = StX + t.X + StW / 2, cy = StY + t.Y + StH / 2;
                    if (a > 0.05f && x >= cx - w / 2 && x <= cx + w / 2 && y >= cy - h / 2 && y <= cy + h / 2) hit = c;
                }
            }
            if (hit == null) return true;
            if (hit.Index == p.Sel)
            {
                if (p.Acts) { p.Acts = false; CsPaintActs(p, p.Sel < p.Items.Count ? p.Items[p.Sel] : null, CsActsWidth(p.Sel < p.Items.Count ? p.Items[p.Sel] : null)); }
                WithCue(Sfx.Activate, CsActivate);
            }
            else
            {
                if (p.Acts) { p.Acts = false; }
                WithCue(Sfx.Nav, () => CsStep(p, hit.Index - p.Sel));
            }
            return true;
        }
        // 分类页
        float sy2 = y + p.ScrollY;
        if (p.RecentRow != null)
        {
            var ro = p.RecentRow.Offset;
            float ry = sy2 - ro.Y;
            if (ry >= -12 && ry <= 360)
            {
                int i = (int)Math.Floor((x - ro.X) / 424);
                if (i >= 0 && i < p.Recent.Count && (x - ro.X) - i * 424 <= 400)
                {
                    CsCatTap(p, true, i);
                    return true;
                }
            }
        }
        float gy = sy2 - p.GridTop;
        if (gy >= 0)
        {
            int row = (int)Math.Floor(gy / GridPitch), col = (int)Math.Floor((x - 96) / (GridW + GridGapX));
            float inCol = (x - 96) - col * (GridW + GridGapX);
            int i = row * GridCols + col;
            if (col >= 0 && col < GridCols && inCol <= GridW && gy - row * GridPitch <= GridH + 62 && i < p.Items.Count) CsCatTap(p, false, i);
        }
        return true;
    }

    private void CsCatTap(CsPage p, bool recent, int i)
    {
        if (p.InRecent == recent && p.Ai == i) { WithCue(Sfx.Activate, CsActivate); return; }
        int pa = p.Ai; bool pr = p.InRecent;
        p.InRecent = recent; p.Ai = i;
        WithCue(Sfx.Nav, () => CsCatFocus(p, instant: false, pa, pr));
    }

    private float _csDragAcc;
    private int _csDragAxis;   // 0 未定 / 1 横 / 2 竖

    /// <summary>拖动（屏幕 DIP）：展架横向翻页，叠放横竖都行；分类页纵向滚动，最近一排横向拖。</summary>
    private void CsDrag(double dx, double dy)
    {
        if (_cs?.Page is not { } p || _csInsert != null) return;
        float k = _cs.K;
        float lx = (float)(dx / k), ly = (float)(dy / k);
        if (_csDragAxis == 0) _csDragAxis = Math.Abs(lx) >= Math.Abs(ly) ? 1 : 2;
        if (p.Tab == 0)
        {
            float d = p.Style == HomeStyle.Shelf ? lx : (_csDragAxis == 1 ? lx : ly);
            float unit = p.Style == HomeStyle.Shelf ? 120 : 90;
            _csDragAcc += d;
            while (Math.Abs(_csDragAcc) > unit)
            {
                int step = _csDragAcc > 0 ? -1 : 1;
                _csDragAcc += step * unit;
                if (p.Acts) p.Acts = false;
                WithCue(Sfx.Nav, () => CsStep(p, step));
            }
            return;
        }
        var (_, fy) = CsLocal(_dragFrom.x, _dragFrom.y);
        bool inRecent = p.RecentRow != null && fy + p.ScrollY >= p.RecentRow.Offset.Y - 12 && fy + p.ScrollY <= p.RecentRow.Offset.Y + 360;
        if (_csDragAxis == 1 && inRecent)
        {
            float max = Math.Max(0, p.Recent.Count * 424 - 24 - (CsW - 192));
            p.RecentX = Math.Clamp(p.RecentX - lx, 0, max);
            CsSet(p.RecentRow!, offset: new Vector3(96 - p.RecentX, p.RecentRow!.Offset.Y, 0));
        }
        else if (_csDragAxis == 2)
        {
            float max = Math.Max(0, CsCatContentH(p) - CsH);
            float before = p.ScrollY;
            p.ScrollY = Math.Clamp(p.ScrollY - ly, 0, max);
            CsCatRealize(p, before, p.ScrollY);
            CsSet(p.Scroll!, offset: new Vector3(0, -p.ScrollY, 0));
        }
    }

    private void CsDragEnd() { _csDragAcc = 0; _csDragAxis = 0; }

    // ---------------- 墙那边的挂钩 ----------------

    /// <summary>墙的某些路径（启动画面、截屏）要「此刻选中的那张卡」的屏幕矩形。</summary>
    private Windows.Foundation.Rect? CsScreenRect((float X, float Y, float W, float H) r)
    {
        if (_cs == null) return null;
        var o = _cs.Root.Offset; float k = _cs.K;
        return new Windows.Foundation.Rect(o.X + r.X * k, o.Y + r.Y * k, r.W * k, r.H * k);
    }
}
