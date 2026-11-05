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
        // 展台
        public CsCase? StandCase, StandLeaving;
        public float RailX;
        public Action? StandFinish;
        public long Built;
        public bool BgReady;
        public long BgReadyAt;
        public readonly List<Action> OnBgReady = new();
    }

    private sealed class CsState
    {
        public required ContainerVisual Root, Pages, Header;
        public required SpriteVisual Underline, Status, Floor;
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
    /// <summary>页边距：和控制中心、设置、性能、系统、显示页一样（它们按 1280 宽设计、左右各 40，换到 1920 宽是 60）。</summary>
    private const float CsEdge = 60;
    private static readonly Bezier CsQuart = new(.165f, .84f, .44f, 1f);
    private float CsK => (float)Math.Min(View.Width / CsW, View.Height / CsH);
    private float CsRaster => CsK * Chrome.Dpi;
    /// <summary>卡盒版接管输入（首页是海报墙时，首页的输入还给墙）。</summary>
    private bool CsActive => _cs != null && !_ccOpen && !CsWallHome;
    /// <summary>首页样式是海报墙、正停在首页：墙照原样工作，顶栏标签叠在上面。</summary>
    private bool CsWallHome => _cs?.Page is { Tab: 0, Style: HomeStyle.Wall };
    /// <summary>墙藏起来了（卡盒版的页面整层盖住它）。离开海报墙时等新页面的背景盖满了才藏。</summary>
    private bool CsHidesWall => _cs != null && !_csWallShown;
    private bool _csWallShown, _csRailTab;
    private SystemInfo.NetState _csNetPainted;

    // 展架（规格 2）
    // 展架 A「等高」（第三轮原型）：每盒一样大，选中的上抬 16、其余压暗到 .74；信息区在卡盒排上方一条固定的线上，
    // 什么都不会从它下面滑过去（以前放大的卡盒在切换途中会穿过信息区）
    private const float ShW0 = 260, ShH0 = 390, ShGap = 32, ShW1 = 260, ShH1 = 390, ShLift = 16, ShBottom = CsH - 64, ShDim = 0.26f;
    // 分类页（规格 4）
    private const int GridCols = 5;
    private const float GridGapX = 32, GridGapY = 48, CatTop = 140;
    private static float GridW => (CsW - 2 * CsEdge - (GridCols - 1) * GridGapX) / GridCols;
    private static float GridH => GridW * 1.5f;
    private static float GridPitch => GridH + 12 + CapH + GridGapY;
    private const float CapH = 70;

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
        if (p.Style == HomeStyle.Wall && p.Tab == 0) return _ccGameId != null ? Lookup(_ccGameId) : null;
        if (p.Tab == 0) return p.Sel < p.Items.Count ? p.Items[p.Sel] : null;
        return p.InRecent ? (p.Ai < p.Recent.Count ? p.Recent[p.Ai] : null) : (p.Ai < p.Items.Count ? p.Items[p.Ai] : null);
    }

    private string? _csSpot, _csSpotForce;

    // ---------------- 建立 ----------------

    private void CsStart()
    {
        if (Program.Args.Contains("--wall")) return;
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
        header.Size = new Vector2(CsW, CatTop);
        root.Children.InsertAtTop(header);
        var underline = G.Sprite(G.Solid(Gfx.Rgba(242, 242, 242)), relative: false);
        underline.Clip = G.C.CreateInsetClip();
        header.Children.InsertAtTop(underline);
        var status = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        header.Children.InsertAtTop(status);
        _root.Children.InsertAtTop(root);
        _cs = new CsState { Root = root, Pages = pages, Header = header, Underline = underline, Status = status, Floor = floor };
        _csWallShown = _prefs.Home == HomeStyle.Wall;
        CsWallVisuals();
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

    // 顶栏和控制中心同一套尺寸：控制中心按 1280×800 设计、放大 1.5 倍到 1920×1200（CcBoost），这里直接用 1920 坐标，
    // 所以数值是控制中心的 1.5 倍——标签 = 标题字号 24 → 36，状态栏同字号常规字重、图标 22 → 33，上沿 32 → 48。
    // 和控制中心一样只随屏幕缩放、不随系统缩放（150% / 175% 下物理大小相同）。
    private const float HdTop = 48, HdFont = 36, HdCount = 24, HdIcon = 33, HdGap = 48, HdLine = 54, HdBaseline = 40, HdMid = 27;

    private void CsPaintTabs(bool animate)
    {
        if (_cs == null) return;
        foreach (var t in _cs.Tabs) { _cs.Header.Children.Remove(t.V); t.V.Dispose(); }
        _cs.Tabs.Clear();
        float x = CsEdge;
        foreach (int tab in CsTabsShown())
        {
            string label = CsTabNames[tab], count = tab == 0 ? "" : CsList(tab).Count.ToString();
            float lw, cw = 0;
            using (var l = TextKit.Layout(G.Device, label, TextKit.Ui(600), HdFont, 600, CanvasHorizontalAlignment.Left)) lw = (float)l.LayoutBounds.Width;
            if (count.Length > 0) using (var c = TextKit.Layout(G.Device, count, TextKit.Num(400), HdCount, 200, CanvasHorizontalAlignment.Left)) cw = (float)c.LayoutBounds.Width;
            float w = MathF.Ceiling(lw + (cw > 0 ? 10 + cw : 0)) + 2;
            var v = CsSprite(w, HdLine, ds =>
            {
                // 「Steam」是 SF Pro、「首页」是苹方：两套字体的行框和基线都不一样，中文字形还比拉丁基线低一截。
                // 标签按实际字形的垂直中心对齐到同一条中线（看起来一样高）；数量的数字底边对齐到拉丁大写字母底边那条线。
                using var l = TextKit.Layout(ds, label, TextKit.Ui(600), HdFont, w, CanvasHorizontalAlignment.Left);
                var ink = l.DrawBounds;
                float cap = TextInk.Metrics(ds, TextKit.Ui(600), HdFont, "H").Cap;
                ds.DrawTextLayout(l, 0, HdMid - (float)(ink.Y + ink.Height / 2), Gfx.Rgba(242, 242, 242));
                if (count.Length == 0) return;
                using var c = TextKit.Layout(ds, count, TextKit.Num(400), HdCount, w, CanvasHorizontalAlignment.Left);
                ds.DrawTextLayout(c, lw + 10, HdMid + cap / 2 - TextInk.Metrics(ds, TextKit.Num(400), HdCount, count, "0").Baseline, Gfx.Rgba(242, 242, 242, 0.57));
            });
            v.Offset = new Vector3(x, HdTop, 0);
            v.Opacity = tab == _cs.Tab ? 1 : 0.6f;
            _cs.Header.Children.InsertAtTop(v);
            _cs.Tabs.Add((v, x, lw, tab));
            x += w + HdGap;
        }
        CsPlaceUnderline(animate);
    }

    private void CsPlaceUnderline(bool animate)
    {
        if (_cs == null) return;
        var t = _cs.Tabs.FirstOrDefault(t => t.Tab == _cs.Tab);
        if (t.V == null) return;
        var off = new Vector3(t.X, HdTop + HdLine + 4, 0);
        var size = new Vector2(t.W, 4);
        if (!animate || Reduced) { _cs.Underline.StopAnimation("Offset"); _cs.Underline.StopAnimation("Size"); _cs.Underline.Offset = off; _cs.Underline.Size = size; }
        else
        {
            _cs.Underline.StartAnimation("Offset", CsV3(off, 220, CsQuart));
            _cs.Underline.StartAnimation("Size", CsV2(size, 220, CsQuart));
        }
        foreach (var tab in _cs.Tabs) tab.V.StartAnimation("Opacity", CsS(tab.Tab == _cs.Tab ? 1 : 0.6f, CsMs(220), CsQuart));
    }

    private bool _csNetBusy;
    private long _csNetRead = -1_000_000;   // 不能用 long.MinValue：NowMs - MinValue 会溢出成负数，永远不读

    /// <summary>网络状态自己在后台读（不等控制中心的蓝牙查询），10 s 一次，变了才重画状态栏。</summary>
    private void CsReadNet()
    {
        if (_csNetBusy || NowMs - _csNetRead < 10_000) return;
        _csNetBusy = true;
        _csNetRead = NowMs;
        _ = Task.Run(() =>
        {
            SystemInfo.NetState net = default;
            try { net = SystemInfo.Network(); } catch (Exception ex) { Log.Warn($"cases net: {ex.Message}"); }
            _dq.TryEnqueue(() =>
            {
                _csNetBusy = false;
                _ccNet = net;
                if (_cs != null && !net.Equals(_csNetPainted)) CsPaintStatus();
            });
        });
    }

    /// <summary>右上角：和控制中心的状态栏同一套（网络、电池 + 电量、时间），同字号、同基线、常规字重。</summary>
    private void CsPaintStatus()
    {
        if (_cs == null) return;
        CsReadNet();
        _csNetPainted = _ccNet;
        var bat = _railModel.Battery;
        var texts = new List<string>();
        if (bat.HasBattery) texts.Add($"{bat.Percent}%");
        texts.Add(_railModel.Now.ToString("HH:mm"));
        var layouts = texts.Select(t => TextKit.Layout(G.Device, t, TextKit.Ui(400), HdFont, 1000, CanvasHorizontalAlignment.Left)).ToList();
        try
        {
            // 数字的实际高度和基线：电池外框和数字一样高、垂直居中（照 Windows 任务栏），网络图标也对齐数字中线
            var (baseL, cap) = TextInk.Metrics(G.Device, TextKit.Ui(400), HdFont, "0", "0");
            float battW = bat.HasBattery ? MathF.Round(cap * 1.9f) + MathF.Max(1.2f, cap * 0.09f) * 0.5f + MathF.Max(1.5f, cap * 0.11f) : 0;
            const float gap = 39, pctGap = 10;
            float w = HdIcon + (bat.HasBattery ? gap + battW : 0);
            for (int i = 0; i < layouts.Count; i++) w += (i == 0 && bat.HasBattery ? pctGap : gap) + (float)layouts[i].LayoutBounds.Width;
            w = MathF.Ceiling(w) + 2;
            CsRepaint(_cs.Status, w, HdLine, ds =>
            {
                var ink = Gfx.WithAlpha(CcText, 0.9);
                float mid = HdBaseline - cap / 2, x = 0;
                var t0 = ds.Transform;
                ds.Transform = Matrix3x2.CreateScale(HdIcon / CcIcon) * Matrix3x2.CreateTranslation(x, mid - HdIcon / 2 - HdIcon * 0.08f) * t0;
                CcIconNet(ds, ink);
                ds.Transform = t0;
                x += HdIcon;
                if (bat.HasBattery) { x += gap; x += BatteryGlyph.Draw(ds, x, HdBaseline - cap, cap, ink, bat, Theme.ChargingGreen, Gfx.Rgba(0xef, 0x44, 0x44)); }
                for (int i = 0; i < layouts.Count; i++)
                {
                    x += i == 0 && bat.HasBattery ? pctGap : gap;
                    var b = layouts[i].LayoutBounds;
                    ds.DrawTextLayout(layouts[i], x - (float)b.X, HdBaseline - baseL, ink);
                    x += (float)b.Width;
                }
            });
            _cs.Status.Offset = new Vector3(CsW - CsEdge - w, HdTop, 0);
        }
        finally { foreach (var l in layouts) l.Dispose(); }
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
        string? spot = _csSpotForce ?? CsSpotItem()?.Id ?? _csSpot;
        _csSpotForce = null;
        _cs.Tab = tab;
        CsPaintTabs(animate: old != null);
        var page = tab == 0 && _prefs.Home == HomeStyle.Wall ? CsBuildWallPage()
            : tab == 0 ? CsBuildHome(_prefs.Home, spot, entry || dir != 0) : CsBuildCat(tab, spot, entry || dir != 0);
        _cs.Page = page;
        _cs.Pages.Children.InsertAtTop(page.Root);
        double ms = CsMs(220);
        if (page.Style == HomeStyle.Wall && page.Tab == 0)
        {
            // 回到海报墙：墙马上在下面亮出来，上面的页面整层淡掉。墙有自己的竖条（页签、时间、电量），
            // 顶栏叠上去会和竖条打架、页签也重复，所以顶栏跟着淡出；LB / RB 照样切到全部 / Steam / 本地，那边顶栏再回来
            _cs.Header.StartAnimation("Opacity", CsS(0, CsMs(220), CsQuart));
            _csWallShown = true;
            CsWallVisuals();
            if (old != null)
            {
                if (!_cs.Leaving.Contains(old)) _cs.Leaving.Add(old);
                if (dir != 0) old.Content.StartAnimation("Offset", CsV3(new Vector3(-40 * dir, 0, 0), ms, CsQuart));
                old.Root.StartAnimation("Opacity", CsS(0, CsMs(320), CsQuart));
                After(CsMs(340), () => CsDrop(old));
            }
            if (dir != 0 && !Reduced)
            {
                // 墙自己从切换方向滑进来一点（和标签切换同一套 40 px / 220 ms）
                var w = _field;
                w.StopAnimation("Offset");
                var a = G.C.CreateVector3KeyFrameAnimation();
                a.InsertKeyFrame(0, new Vector3(40 * dir * _cs.K, 0, 0), G.Linear);
                a.InsertKeyFrame(1, Vector3.Zero, G.Ease(CsQuart));
                a.Duration = Gfx.Ms(220);
                w.StartAnimation("Offset", a);
            }
            CsNoteSpot();
            return;
        }
        if (_csInsert == null) _cs.Header.StartAnimation("Opacity", CsS(1, CsMs(220), CsQuart));
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

    /// <summary>海报墙作为首页：卡盒版这一页是空的，看到的全是下面的墙（顶栏也藏起来，见 CsShowTab）。</summary>
    private CsPage CsBuildWallPage()
    {
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var bg = G.Container(relative: false);
        var content = G.Container(relative: false);
        root.Children.InsertAtTop(bg);
        root.Children.InsertAtTop(content);
        var page = new CsPage { Root = root, Bg = bg, Content = content, Tab = 0, Style = HomeStyle.Wall, Items = CsList(0), Built = NowMs, BgReady = true };
        return page;
    }

    /// <summary>墙的几层跟着 CsHidesWall 显示 / 隐藏；卡盒版的底色只在墙藏起来时铺。</summary>
    private void CsWallVisuals()
    {
        bool show = !CsHidesWall;
        foreach (var v in new Visual[] { _seamHost, _edgeLeft, _edgeRight }) v.IsVisible = show;
        if (_cs != null) _cs.Floor.IsVisible = !show;
        UpdateBackdropRunning();
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
        CsStandDispose(page);
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

    internal static readonly HomeStyle[] CsStyleOrder = { HomeStyle.Wall, HomeStyle.Shelf, HomeStyle.Stand, HomeStyle.Stack };
    internal static string CsStyleName(HomeStyle s) => s switch { HomeStyle.Wall => "海报墙", HomeStyle.Stack => "叠放", HomeStyle.Stand => "展台", _ => "展架" };

    /// <summary>设置 › 游戏库 › 首页样式：按 A 立即生效。设置页盖着游戏库时等回到游戏库再播切换动画。</summary>
    private void CsSetHomeStyle(HomeStyle style)
    {
        if (_prefs.Home == style) return;
        SaveUi(_prefs with { Home = style });
        Toast($"首页样式：{CsStyleName(style)}");
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
        // 和海报墙之间：墙和卡盒页整层交叉淡入淡出（卡盒页自带不透明背景，淡进来盖住墙；回墙时卡盒页淡掉）
        if (old.Style is HomeStyle.Wall or HomeStyle.Stand || _prefs.Home is HomeStyle.Wall or HomeStyle.Stand) { CsShowTab(0, 0, entry: true); return; }
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
        else if (style is HomeStyle.Shelf or HomeStyle.Stand)
        {
            // 比原型深一些：背景在动，标题和 meta 要始终清楚
            bg.Children.InsertAtTop(CsShade(90, (0, 0.9), (0.45, 0.55), (0.75, 0.18), (1, 0.12)));
            bg.Children.InsertAtTop(CsShade(180, (0, 0.6), (0.18, 0.12), (0.4, 0.2), (0.8, 0.94), (1, 1)));
        }
        else
        {
            bg.Children.InsertAtTop(CsShade(90, (0, 0.92), (0.38, 0.62), (0.7, 0.25), (1, 0.42)));
            bg.Children.InsertAtTop(CsShade(180, (0, 0.6), (0.18, 0.1), (0.6, 0.12), (1, 0.88)));
        }
        return bg;
    }

    private int _csDriftDir;

    private void CsDrift(SpriteVisual sprite, int dir)
    {
        if (Reduced) return;
        float w = sprite.Size.X;
        var ease = G.Ease(Bezier.EaseInOut);
        var sc = G.C.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0, new Vector3(1.04f, 1.04f, 1), G.Linear);
        sc.InsertKeyFrame(1, new Vector3(1.10f, 1.10f, 1), ease);
        var off = G.C.CreateVector3KeyFrameAnimation();
        off.InsertKeyFrame(0, new Vector3(dir * w * 0.012f, 0, 0), G.Linear);
        off.InsertKeyFrame(1, new Vector3(-dir * w * 0.012f, -CsH * 0.01f, 0), ease);
        foreach (var a in new Vector3KeyFrameAnimation[] { sc, off })
        {
            a.Duration = Gfx.Ms(24_000);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            a.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
        }
        sprite.StartAnimation("Scale", sc);
        sprite.StartAnimation("Offset", off);
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
            // 首页背景：和屏保同一种慢速推近平移（scale 1.04 → 1.10、横向 ±1.2 %、纵向 −1 %，24 s 来回），每次换图换方向
            if (!cat) CsDrift(sprite, ++_csDriftDir % 2 == 0 ? 1 : -1);
            if (!p.BgReady)
            {
                sprite.Opacity = target;
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
        After(ms, () =>
        {
            foreach (var a in waiting) a();
            // 从海报墙过来：新页面的背景盖满了，墙可以藏起来（停掉它的动态背景）
            if (_cs?.Page == p && _csWallShown && !(p.Tab == 0 && p.Style == HomeStyle.Wall)) { _csWallShown = false; CsWallVisuals(); }
        });
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
        else if (style == HomeStyle.Stand) CsLayoutStand(page, animate: false, entry: entry);
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
        // 选中的停在最左（页边距）；到尾巴时整排夹住，不留右边的空
        float total = n * (ShW0 + ShGap) - ShGap + (ShW1 - ShW0);
        return Math.Max(Math.Min(CsEdge, CsW - CsEdge - total), CsEdge - s * (ShW0 + ShGap));
    }

    private static (Vector3 Off, Vector2 Size) ShelfRect(int i, int s, int n)
    {
        float x = ShelfRailX(s, n) + i * (ShW0 + ShGap) + (i > s ? ShW1 - ShW0 : 0);
        bool sel = i == s;
        float w = sel ? ShW1 : ShW0, h = sel ? ShH1 : ShH0;
        return (new Vector3(x, ShBottom - h - (sel ? ShLift : 0), 0), new Vector2(w, h));
    }

    private static (int Lo, int Hi) ShelfWindow(int s, int n)
    {
        float x0 = ShelfRailX(s, n);
        int lo = (int)Math.Floor((-420 - x0) / (ShW0 + ShGap)), hi = (int)Math.Ceiling((CsW + 420 - x0) / (ShW0 + ShGap));
        return (Math.Max(0, lo), Math.Min(n - 1, hi));
    }

    /// <summary>
    /// 展架 A：卡盒 260×390，间距 32，底边距 64；选中的上抬 16，停在最左（页边距）。
    /// 宽高和平移 550 ms quart-out，从此刻的值接着走（可打断）。分组标签在每组第一盒下方（08:30 修订），选中的也显示。
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
                c.Dim.Opacity = ShDim;
                p.Cases[i] = c;
                p.Row!.Children.InsertAtTop(c.Root);
                if (i < p.Items.Count && (i == 0 || CsBucket(p.Items[i]) != CsBucket(p.Items[i - 1])))
                {
                    string b = CsBucket(p.Items[i]);
                    var lab = CsSprite(260, 32, ds =>
                    {
                        using var l = TextKit.Layout(ds, b, TextKit.Ui(500), 22, 260, CanvasHorizontalAlignment.Left);
                        ds.DrawTextLayout(l, 0, 0, Gfx.Rgba(242, 242, 242, 0.6));
                    });
                    lab.Offset = new Vector3(o0.X, ShBottom + 16, 0);
                    p.Labels[i] = lab;
                    p.Row.Children.InsertAtTop(lab);
                    c.Label = lab;
                }
            }
            var (off, size) = ShelfRect(i, s, n);
            c.Offset = off; c.Size = size;
            // 卡盒底边下方，所有标签同一条线（普通卡盒底在 ShBottom）
            float labY = ShBottom + 16;
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
                    if (c.Label is { } l0) { l0.Offset = new Vector3(off.X, labY + 40, 0); l0.Opacity = 0; l0.StartAnimation("Offset", CsV3(new Vector3(off.X, labY, 0), 600, CsQuart, delay)); l0.StartAnimation("Opacity", CsS(1, 420, CsQuart, delay)); }
                }
                else { c.Root.Offset = off; if (c.Label is { } l1) { l1.Offset = new Vector3(off.X, labY, 0); l1.StopAnimation("Opacity"); l1.Opacity = 1; } }
            }
            else
            {
                c.Root.StartAnimation("Offset", CsV3(off, ms, CsQuart));
                c.Root.StartAnimation("Size", CsV2(size, ms, CsQuart));
                c.Label?.StartAnimation("Offset", CsV3(new Vector3(off.X, labY, 0), ms, CsQuart));

            }
            if (i == s) CsSelect(c, true, shine: animate && i != p.Prev);
            else if (c.Selected || !animate) { CsSelect(c, false, false); if (!animate) { c.Dim.StopAnimation("Opacity"); c.Dim.Opacity = ShDim; } }
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
    // 按钮和控制中心底坞一样高（48 → 72），字号跟设置页正文（17 → 26）；meta 同字号。触控目标都 ≥ 72（= 48 epx）
    private const float ActH = 72, ActGap = 16, ActFont = 26, MetaFont = 26, TitleFont = 64, TitleLh = 76, InfoRowGap = 28;
    // 展架（08:30 修订）：信息区在放大卡盒右侧——左边 = 选中卡盒右边 + 40，底边 = 普通卡盒顶 + 30（屏幕底上方 430）
    // 展架 A：信息区左右贴页边距，按钮底边在 y = 1200 − 526（离上抬的选中卡盒顶 56）
    private const float ShInfoX = CsEdge, ShInfoBottom = CsH - 526;

    /// <summary>
    /// 展架：第一行标题（单行省略），第二行 [开始游戏][设置][收藏] 后面接 meta；叠放：标题（最多两行）、meta、按钮各占一行。
    /// </summary>
    private void CsPaintInfo(CsPage p, bool animate)
    {
        if (p.InfoA == null || p.InfoB == null || p.ActsV == null) return;
        var g = p.Sel < p.Items.Count ? p.Items[p.Sel] : null;
        string title = g?.Title ?? "全部游戏";
        string meta = g != null ? CsMetaFull(g) : $"{p.Items.Count} 款";
        bool stack = p.Style == HomeStyle.Stack;
        float actsW = CsActsWidth(g);
        bool stand = p.Style == HomeStyle.Stand;
        float w = stack ? 640 : stand ? CsW - CsEdge - StdInfoX : CsW - CsEdge - ShInfoX;
        bool rows = true;   // 标题、meta、按钮各占一行（展架 A 和叠放都是）
        var dev = G.Device;
        float titleH;
        using (var probe = TextKit.Layout(dev, title, TextKit.Ui(600), TitleFont, w, CanvasHorizontalAlignment.Left, lineHeight: TitleLh, maxLines: stack ? 2 : 1, balance: stack))
            titleH = Math.Max(TitleLh, (float)probe.LayoutBounds.Height);
        float vgap = stack ? 12 : 30;   // 展架 A / 展台：标题、meta、按钮三者间距 30
        float h = rows ? titleH + vgap + 36 : titleH + InfoRowGap + ActH;
        float metaX = rows || g == null ? 0 : actsW + InfoRowGap, metaY = rows ? titleH + vgap : titleH + InfoRowGap + (ActH - 34) / 2;
        var front = p.InfoFront ? p.InfoB : p.InfoA;
        var back = p.InfoFront ? p.InfoA : p.InfoB;
        var target = animate && !Reduced ? back : front;
        CsRepaint(target, w, h, ds =>
        {
            using var t = TextKit.Layout(ds, title, TextKit.Ui(600), TitleFont, w, CanvasHorizontalAlignment.Left, lineHeight: TitleLh, maxLines: stack ? 2 : 1, balance: stack);
            ds.DrawTextLayout(t, 0, 0, Gfx.Rgba(242, 242, 242));
            using var m = TextKit.Layout(ds, meta, TextKit.Num(400), MetaFont, Math.Max(1, w - metaX), CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(m, metaX, metaY, Gfx.Rgba(242, 242, 242, 0.6));
        });
        if (stack)
        {
            // 叠放：左边只留标题和 meta（垂直居中）；按钮行挪到最上面那张牌正下方，左边和牌对齐，间距 28
            target.Offset = new Vector3(CsEdge, CsH / 2 - h / 2, 0);
            p.ActsAt = new Vector2(StX, StY + StH + 28);
        }
        else if (stand)
        {
            // 展台：信息区在大卡盒右边（大卡盒右边 + 56）、顶 536，往下依次是标题、meta、按钮
            target.Offset = new Vector3(StdInfoX, StdInfoTop, 0);
            p.ActsAt = new Vector2(StdInfoX, StdInfoTop + h + 30);
        }
        else
        {
            p.ActsAt = new Vector2(ShInfoX, ShInfoBottom - ActH);
            target.Offset = new Vector3(ShInfoX, ShInfoBottom - ActH - 30 - h, 0);
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
        return CsPillWidth() + (ActGap + ActH) * (CsActCount(g) - 1);
    }

    private float CsPillWidth()
    {
        using var l = TextKit.Layout(G.Device, CsActNames[0], TextKit.Ui(600), ActFont, 400, CanvasHorizontalAlignment.Left);
        return MathF.Ceiling((float)l.LayoutBounds.Width) + 40 + 26 + 14 + 40;
    }

    private int CsActCount(LibraryItem g) => CsCanFavorite(g) ? 3 : 2;

    private static bool CsCanFavorite(LibraryItem g) => g.Platform is "steam" or "local" or "other" && uint.TryParse(g.Id, out _);

    private void CsPaintActs(CsPage p, LibraryItem? g, float w)
    {
        p.ActRects.Clear();
        if (p.ActsV == null) return;
        if (g == null) { p.ActsV.IsVisible = false; return; }
        p.ActsV.IsVisible = true;
        float pill = CsPillWidth();
        bool fav = _csFav.Contains(g.Id);
        int focus = p.Acts ? p.Act : -1;
        const float pad = 8, r = ActH / 2;
        CsRepaint(p.ActsV, w + pad * 2, ActH + pad * 2, ds =>
        {
            float x = pad, y = pad;
            // 主按钮：白底黑字，三角 + 文字整体居中
            ds.FillRoundedRectangle(x, y, pill, ActH, r, r, Gfx.Rgba(242, 242, 242));
            float px = x + 40;
            using (var play = CanvasGeometry.CreatePolygon(ds, new[] { new Vector2(px, y + r - 13), new Vector2(px, y + r + 13), new Vector2(px + 22, y + r) }))
                ds.FillGeometry(play, Gfx.Rgba(17, 17, 17));
            using (var l = TextKit.Layout(ds, CsActNames[0], TextKit.Ui(600), ActFont, pill, CanvasHorizontalAlignment.Left))
                ds.DrawTextLayout(l, px + 26 + 14, y + r - (float)l.LayoutBounds.Height / 2 - (float)l.LayoutBounds.Y, Gfx.Rgba(17, 17, 17));
            if (focus == 0) ds.DrawRoundedRectangle(x - 5, y - 5, pill + 10, ActH + 10, r + 5, r + 5, Colors.White, 3.5f);
            float bx = x + pill + ActGap;
            for (int a = 1; a < CsActCount(g); a++)
            {
                bool on = a == 2 && fav;
                ds.FillCircle(bx + r, y + r, r, on ? Gfx.Rgba(242, 242, 242) : Gfx.Rgba(255, 255, 255, 0.14));
                var ink = on ? Gfx.Rgba(17, 17, 17) : Gfx.Rgba(242, 242, 242);
                var t0 = ds.Transform;
                ds.Transform = Matrix3x2.CreateScale(1.5f, 1.5f, new Vector2(12, 12)) * Matrix3x2.CreateTranslation(bx + r - 12, y + r - 12) * t0;
                if (a == 1) CsIconSliders(ds, 0, 0, ink); else CsIconStar(ds, 0, 0, ink, on);
                ds.Transform = t0;
                if (focus == a) ds.DrawCircle(bx + r, y + r, r + 5, Colors.White, 3.5f);
                bx += ActH + ActGap;
            }
        });
        // 位置只由 ActsAt 决定：焦点在按钮间移动只重画描边，不挪位置
        var o = p.ActsAt;
        p.ActsV.Offset = new Vector3(o.X - pad, o.Y - pad, 0);
        p.ActRects.Add((o.X, o.Y, pill, ActH, 0));
        float hx = o.X + pill + ActGap;
        for (int a = 1; a < CsActCount(g); a++) { p.ActRects.Add((hx, o.Y, ActH, ActH, a)); hx += ActH + ActGap; }
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
            sec.Offset = new Vector3(CsEdge, y + 16, 0);
            scroll.Children.InsertAtTop(sec);
            var row = G.Container(relative: false);
            row.Offset = new Vector3(CsEdge, y + 86 + 14, 0);
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
            y += 86 + 14 + 300 + 12 + CapH + 18 + 44;
        }
        var all = CsSection("全部", items.Count.ToString());
        all.Offset = new Vector3(CsEdge, y + 16 - (page.Recent.Count > 0 ? 44 : 0) + (page.Recent.Count > 0 ? 44 : 0), 0);
        scroll.Children.InsertAtTop(all);
        y += 16 + 46 + 22;
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
        return CsSprite(600, 46, ds =>
        {
            using var t = TextKit.Layout(ds, title, TextKit.Ui(600), 32, 600, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(t, 0, 0, Gfx.Rgba(242, 242, 242));
            if (count == null) return;
            using var c = TextKit.Layout(ds, count, TextKit.Num(400), 24, 200, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(c, (float)t.LayoutBounds.Width + 14, 8, Gfx.Rgba(242, 242, 242, 0.4));
        });
    }

    private float CsCatContentH(CsPage p) => p.GridTop + (float)Math.Ceiling(p.Items.Count / (double)GridCols) * GridPitch - GridGapY + 120;

    /// <summary>网格里第 i 格在滚动层里的位置。</summary>
    private static Vector3 GridCell(int i) => new(CsEdge + (i % GridCols) * (GridW + GridGapX), (i / GridCols) * GridPitch, 0);

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
            float l = CsEdge + p.Ai * 424 - p.RecentX, r = l + 400;
            float rx = p.RecentX;
            if (l < CsEdge) rx -= CsEdge - l; else if (r > CsW - CsEdge) rx += r - (CsW - CsEdge);
            rx = Math.Max(0, rx);
            p.RecentX = rx;
            var to = new Vector3(CsEdge - rx, p.RecentRow.Offset.Y, 0);
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
            p.StandCase?.Ring.StartAnimation("Opacity", CsS(0, CsMs(250), CsQuart));
            return;
        }
        if (dir == Direction.Down) { Cue(Sfx.Bump); return; }
        CsStep(p, dir == Direction.Left ? -1 : 1);
    }

    /// <summary>
    /// 叠放（11-05 修订，按摆放位置）：牌堆往右叠，← 上一张、→ 下一张（翻过去的牌往左下飞走，← 把它拿回来）。
    /// 在牌堆上 ← 永远只翻回上一张，绝不进按钮；已经是第一张 / 最后一张时牌堆轻轻顶一下回弹。
    /// 按钮行在最上面那张牌正下方：牌堆上 ↓ 进按钮（落在「开始游戏」），按钮之间 ← / →，↑ 或 B 回牌堆。牌堆上 ↑ 不做事。
    /// </summary>
    private void CsStackMove(CsPage p, Direction dir)
    {
        var g = p.Sel < p.Items.Count ? p.Items[p.Sel] : null;
        int n = g != null ? CsActCount(g) : 0;
        if (!p.Acts)
        {
            if (dir is Direction.Left or Direction.Right)
            {
                int d = dir == Direction.Left ? -1 : 1;
                int j = p.Sel + d;
                if (j < 0 || j >= CsCount(p)) { CsStackBounce(p, d); Cue(Sfx.Bump, pan: d < 0 ? -0.6f : 0.6f); return; }
                CsStep(p, d);
                return;
            }
            if (dir == Direction.Down && n > 0)
            {
                p.Acts = true; p.Act = 0;
                CsPaintActs(p, g, CsActsWidth(g));
                if (p.Cases.TryGetValue(p.Sel, out var c)) c.Ring.StartAnimation("Opacity", CsS(0, CsMs(250), CsQuart));
                return;
            }
            Cue(Sfx.None);
            return;
        }
        if (dir == Direction.Up) { CsStackBackToPile(p, g); return; }
        if (dir == Direction.Down) { Cue(Sfx.Bump); return; }
        int next = p.Act + (dir == Direction.Left ? -1 : 1);
        if (next < 0 || next >= n) { Cue(Sfx.Bump, pan: dir == Direction.Left ? -0.6f : 0.6f); return; }
        p.Act = next;
        CsPaintActs(p, g, CsActsWidth(g));
    }

    private void CsStackBackToPile(CsPage p, LibraryItem? g)
    {
        p.Acts = false;
        CsPaintActs(p, g, CsActsWidth(g));
        if (p.Cases.TryGetValue(p.Sel, out var c)) c.Ring.StartAnimation("Opacity", CsS(1, CsMs(300), CsQuart));
    }

    /// <summary>到头了：整个牌堆朝按键方向的反面顶一下再弹回（第一张按 ← 时往右顶）。</summary>
    private void CsStackBounce(CsPage p, int d)
    {
        if (Reduced || p.Row == null) return;
        var a = G.C.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, Vector3.Zero, G.Linear);
        a.InsertKeyFrame(0.3f, new Vector3(-d * 22, 0, 0), G.Ease(Bezier.EaseOut));
        a.InsertKeyFrame(1, Vector3.Zero, G.Ease(new Bezier(.34f, 1.56f, .64f, 1)));
        a.Duration = Gfx.Ms(360);
        p.Row.StartAnimation("Offset", a);
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
        if (p.Style == HomeStyle.Shelf) CsLayoutShelf(p, animate); else if (p.Style == HomeStyle.Stand) CsLayoutStand(p, animate); else CsLayoutStack(p, animate);
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
            if (p.Style == HomeStyle.Stand && p.StandCase is { } sc) CsLaunch(g, sc.Root, (StdX, StdY, StdW, StdH));
            else if (p.Cases.TryGetValue(p.Sel, out var c)) CsLaunch(g, c.Root, CsCaseRect(p, c));
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
        if (p.Acts && p.Style == HomeStyle.Stack) { CsStackBackToPile(p, p.Sel < p.Items.Count ? p.Items[p.Sel] : null); return true; }
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

    /// <summary>顶栏的标签（海报墙首页也能点）。点中了返回 true。</summary>
    private bool CsHeaderClick(double sx, double sy)
    {
        if (_cs == null || _csInsert != null || CsWallHome) return false;
        var (x, y) = CsLocal(sx, sy);
        if (y < HdTop - 16 || y > HdTop + HdLine + 20) return false;
        foreach (var t in _cs.Tabs)
            if (x >= t.X - 16 && x <= t.X + t.V.Size.X + 16) { CsGoTab(t.Tab); return true; }
        return false;
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
            if (p.Style == HomeStyle.Stand && x >= StdX && x <= StdX + StdW && y >= StdY && y <= StdY + StdH) hit = p.Cases.GetValueOrDefault(p.Sel) ?? p.StandCase;
            else if (p.Style is HomeStyle.Shelf or HomeStyle.Stand)
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
            int row = (int)Math.Floor(gy / GridPitch), col = (int)Math.Floor((x - CsEdge) / (GridW + GridGapX));
            float inCol = (x - CsEdge) - col * (GridW + GridGapX);
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
            bool row = p.Style is HomeStyle.Shelf or HomeStyle.Stand;
            // 叠放：横向拖动翻牌（左拖下一张、右拖上一张）
            float d = lx;
            float unit = row ? 120 : 90;
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
            float max = Math.Max(0, p.Recent.Count * 424 - 24 - (CsW - 2 * CsEdge));
            p.RecentX = Math.Clamp(p.RecentX - lx, 0, max);
            CsSet(p.RecentRow!, offset: new Vector3(CsEdge - p.RecentX, p.RecentRow!.Offset.Y, 0));
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
