// 舞台（照 src/stage/stage.ts）：一面常驻的墙 + 一道缝，全部画在合成视觉层上。
// - 相机与缝的开口是一个 CompositionPropertySet 里的几个数（cx、cy、open…），两半墙、缝、边缘标签的位置都是表达式动画，
//   相机补间 / 缝补间就是对这几个数做关键帧动画 —— 全在合成线程跑，UI 线程不逐帧干活。
// - UI 线程用同一条曲线解析地算出「此刻相机在哪」，供导航、裁剪、点击命中用。
// - 只有右摇杆 / 拖动平移时 UI 线程才逐帧写相机（CompositionTarget.Rendering，停了就退订）。
using System.Numerics;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Windows.UI;
using static ClawLibrary.Engine.Geometry;
using static ClawLibrary.Engine.ViewMath;

namespace ClawLibrary.Stage;

public sealed partial class WallStage : IDisposable
{
    /// <summary>视口外多渲染的世界距离；离已渲染范围边缘还剩这么多时重新裁剪。</summary>
    private const double Overscan = 420;
    private const double CullMargin = 160;
    private const int MaxTiles = 400;

    private sealed class Layer
    {
        public required string Id;
        public required string Title;
        public required List<LibraryItem> Items;
        public required WallLayout Layout;
        public required bool Home;
        /// <summary>缝里信息条的面包屑（合集：游戏库；搜索结果：游戏库 › 搜索）。</summary>
        public IReadOnlyList<string> Crumbs = new[] { "游戏库" };
        public (Center center, SlotKey? focus)? Saved;
    }

    private sealed class Tween
    {
        public double From, To, Ms; public long Start; public Bezier Ease;
        public double At(long now) => From + (To - From) * Ease.Ease(Math.Min(1, (now - Start) / Ms));
        public bool Done(long now) => now - Start >= Ms;
    }

    public readonly Gfx G;
    public readonly CardText Text;
    public readonly ImageCache Images = new();
    public readonly Chrome Chrome;
    public TitleStyle Style = TitleStyle.D;
    public long NowMs => Log.Clock.ElapsedMilliseconds;

    private readonly RootElement _rootEl;
    private readonly Window _window;
    private readonly nint _windowHandle;
    private readonly DispatcherQueue _dq;
    private Library _lib = new();
    private readonly ContainerVisual _root;
    private readonly ContainerVisual _field;
    private readonly ContainerVisual[] _worlds = new ContainerVisual[2];
    private readonly CompositionPropertySet _cam;
    private ContainerVisual _seamHost = null!, _seamInner = null!, _seamPivot = null!;
    private SpriteVisual _seamA = null!, _seamB = null!;
    private bool _seamFront;   // false = A 在前
    private SpriteVisual _edgeLeft = null!, _edgeRight = null!, _toast = null!, _hud = null!;
    private SpriteVisual _bgBaseR = null!, _glow1R = null!, _glow2R = null!;
    private SpriteVisual _bgBase = null!, _glow1 = null!, _glow2 = null!, _vignette = null!;

    public View View;
    private Center _center;
    private double _anchorX;
    private double _openWidth;
    private double _contentWidth = SeamHome;
    private readonly List<Layer> _stack = new();
    private int _tabIndex;
    private readonly Dictionary<SlotKey, Tile> _tiles = new();
    private Bounds? _culled;
    private readonly List<Slot> _slotScratch = new();

    public SlotKey? FocusKey;
    private SlotKey? _expandedKey;
    /// <summary>让位中（或已展开）那一块的实际矩形；块外的卡用模板矩形。</summary>
    private readonly Dictionary<SlotKey, Engine.Rect> _drawnOverride = new();
    private readonly Dictionary<SlotKey, long> _reflowEnds = new();

    private Tween? _camX, _camY, _seamTween;
    /// <summary>右摇杆 / 拖动的平移速度（屏幕 px/s）。</summary>
    public Vector2 PanVelocity;
    private (long start, double left, double top, long until)? _entrance;
    private bool _reducedLocal;
    private readonly Windows.UI.ViewManagement.UISettings _ui = new();
    private bool _hudOn;
    private bool _loopOn;
    private long _lastFrame;
    private int _version;
    private Input.GamepadPoller? _pad;
    public bool FirstFrameLogged;

    private Layer CurrentLayer => _stack[^1];
    public bool Reduced => _reducedLocal || !_ui.AnimationsEnabled;
    public int TileCount => _tiles.Count;

    public WallStage(RootElement rootEl, Window window)
    {
        _rootEl = rootEl;
        _window = window;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _dq = DispatcherQueue.GetForCurrentThread();
        var host = ElementCompositionPreview.GetElementVisual(rootEl.Surface);
        G = new Gfx(host.Compositor);
        Text = new CardText(G);
        Chrome = new Chrome(G);
        // _top：墙（_root）之上还有控制中心与提示条；控制中心对 _root 拍快照，所以它们不能在 _root 里
        _top = G.C.CreateContainerVisual();
        _top.RelativeSizeAdjustment = Vector2.One;
        _root = G.C.CreateContainerVisual();
        _root.RelativeSizeAdjustment = Vector2.One;
        _top.Children.InsertAtTop(_root);
        ElementCompositionPreview.SetElementChildVisual(rootEl.Surface, _top);
        _field = G.Container();
        _field.Clip = G.C.CreateInsetClip();
        _worlds[0] = G.C.CreateContainerVisual();
        _worlds[1] = G.C.CreateContainerVisual();
        _cam = G.C.CreatePropertySet();
        foreach (var n in new[] { "cx", "cy", "open", "anchor", "scale", "lineX", "vw", "vh", "cw", "sx", "sw", "pad", "bhome", "bsw" }) _cam.InsertScalar(n, 0);
        // fade：墙的底（底色、光、动态背景、暗角、缝）整体的不透明度；控制中心的出入场（folia 集合层的背景板）只动它，卡片另算
        _cam.InsertScalar("fade", 1);
        // lfade：关灯时缝里的内容与边缘标签压到 0.4（透明的缝不涂黑，见 WallStage.WallLook.cs）
        _seamLightsT = new CssTransition(G, _cam, "lfade", v => _cam.InsertScalar("lfade", v), 1);
        _backgroundLightsT = new CssTransition(G, _cam, "ldim", v => _cam.InsertScalar("ldim", v), 0);
        G.DeviceReplaced += () => _dq.TryEnqueue(() => { Text.Clear(); foreach (var t in _tiles.Values) t.Redraw(); RenderSeamContent(); RefreshScreensaverClock(force: true); });
    }

    public LibraryItem? Lookup(string id) => _lib.ById.TryGetValue(id, out var it) ? it : null;

    // ---------------- 启动 ----------------

    public void Start()
    {
        var t0 = NowMs;
        if (Program.Args.Contains("--style=b")) Style = TitleStyle.B;
        Measure();
        ApplyUiPrefsBeforeLoad();
        UpdateSeamLights(animate: false);
        LoadLibrary();
        ApplyTabPrefs();
        if (Program.Args.FirstOrDefault(a => a.StartsWith("--export-web=")) is { } exp)
        {
            WebExport.Write(_lib, exp["--export-web=".Length..]);
            Application.Current.Exit();
            return;
        }
        InitRail();
        if (!Program.Args.Contains("--no-backdrop")) _backdropOn = _prefs.Backdrop;
        BuildChrome();
        // 首页：控制中心，或者它下面的某一页
        if (_prefs.Boot != BootTarget.Library) After(900, () =>
        {
            if (!_ccOpen) OpenControlCenter();
            switch (_prefs.Boot)
            {
                case BootTarget.Perf: OpenPerf(); break;
                case BootTarget.Gamepad: OpenGamepad(); break;
                case BootTarget.Display: OpenDisplay(); break;
                case BootTarget.System: OpenSystem(); break;
                case BootTarget.Settings: OpenSettingsPage(); break;
            }
        });
        long tChrome = NowMs;
        Log.Info($"library: {_lib.Games.Count} games, {_lib.Collections.Count} collections, view {View.Width}x{View.Height} scale {View.Scale} dpi {Chrome.Dpi} ({NowMs - t0}ms)");

        var tab = _lib.HomeTabs[_tabIndex];
        var items = tab.Items();
        // 墙从世界原点附近开始：缝开在第 0 列块的左边界，相机让开口完整落在屏幕里。
        _center = new Center(BlockW * 0.35, 0);
        var plan = PlanSeam(_center, SeamHome, View);
        _anchorX = plan.AnchorX;
        _center.X = plan.CenterX;
        SetCam(_center);
        _cam.InsertScalar("anchor", (float)_anchorX);
        var start = SlotNearSeam();
        _stack.Add(new Layer { Id = $"home:{tab.Id}", Title = "游戏库", Items = items, Layout = Content.Infinite(items.Count, start), Home = true });
        _contentWidth = SeamHome;
        _cam.InsertScalar("cw", (float)_contentWidth);
        RenderSeamContent();
        long tSeam = NowMs;
        // 第一帧只建屏上的卡，视口外的预备区等第一帧画出来再补
        Recull(true, null, 0);
        Log.Info($"timing: chrome {tChrome - t0}ms, seam {tSeam - tChrome}ms, tiles {NowMs - tSeam}ms");
        // 首次进入：整墙按对角线落下，缝同时张开
        var vis = VisibleWorld(_center, View, 0, 0);
        long now = NowMs;
        _entrance = (now, vis.Left, vis.Top, now + (long)(Motion.Wave.MaxDelay + Motion.Wave.InMs));
        WallBusy(Motion.Wave.MaxDelay + Motion.Wave.InMs);
        if (!Reduced) foreach (var t in _tiles.Values) t.Land(Plans.WaveStagger(t.Slot.X, t.Slot.Y, vis.Left, vis.Top));
        TweenSeam(SeamHome, 0);
        Focus(start?.Key, false);
        ApplyLibraryRailVisibility(animate: false);
        CsStart();
        Log.Info($"wall built: {_tiles.Count} tiles ({NowMs - t0}ms in stage, {App.SinceProcessStartMs():F0}ms since process start)");

        if (ShouldShowOobe()) OpenOobe();
        PrepareStartupShield();
        // 第一帧真正画出来的时刻
        EventHandler<object>? first = null;
        first = (_, _) =>
        {
            CompositionTarget.Rendering -= first;
            FirstFrameLogged = true; PlayStartupSound();
            BeginStartupReadiness();
            _dq.TryEnqueue(DispatcherQueuePriority.Low, RenderEdgeTabs);
            After(1500, () => { try { PrewarmControlCenter(); } catch (Exception ex) { Log.Error("prewarm", ex); } });
            _dq.TryEnqueue(DispatcherQueuePriority.Low, () => { var t = NowMs; Recull(true); Log.Info($"overscan tiles built: {_tiles.Count} ({NowMs - t}ms)"); });
            Log.Info($"first frame: {App.SinceProcessStartMs():F0}ms since process start");
        };
        CompositionTarget.Rendering += first;
        Images.AllLoaded += () => Log.Info($"visible covers decoded: {App.SinceProcessStartMs():F0}ms since process start (cache {Images.Count})");

        HookInput();
        _pad = new Input.GamepadPoller(_dq, this, () => IsForeground());
        _pad.Start();
        StartRailTimers();
        StartScreensaverTimer();
        StartHomeAudio();
        UpdateBackdropRunning();
        _rootEl.Focus(FocusState.Programmatic);
        _rootEl.XamlRoot.Changed += (_, _) => { try { OnResize(); } catch (Exception ex) { Log.Error("resize", ex); } };
        _ = BuildMissingArtAsync();
        _ = CountShotsAsync();
        if (Program.Args.Contains("--probe")) StartFrameProbe();
        if (Program.Args.Contains("--services-probe")) _ = ServicesProbeAsync();
        if (Program.Args.Contains("--settings-edit-probe")) _ = SettingsEditProbeAsync();
        if (Program.Args.Contains("--copy-review-probe")) _ = CopyReviewProbeAsync();
        if (Program.Args.Contains("--touch-probe")) _ = TouchProbeAsync();
        if (Program.Args.Contains("--library-touch-probe")) _ = LibraryTouchProbeAsync();
        if (Program.Args.Contains("--osd-probe")) _ = OsdSettingsProbeAsync();
        if (_prefs.SourceXbox) After(500, () => _ = RefreshPlatformLibrary("xbox", quiet: true));
        if (Program.Args.Contains("--debug-backplate")) _root.IsVisible = false;
        if (Program.Args.Contains("--bench")) _ = Bench.RunAsync(this);
        if (Program.Args.Contains("--hud")) ToggleHud();
        if (Program.Args.FirstOrDefault(a => a.StartsWith("--state=")) is { } state) _ = Bench.StateAsync(this, state[8..]);
    }

    private void Measure()
    {
        var xr = _rootEl.XamlRoot;
        double w = Math.Max(1, xr.Size.Width), h = Math.Max(1, xr.Size.Height);
        View = new View(w, h, ScaleFor(w), w / 2);
        Text.ViewportWidth = (float)w;
        Chrome.Dpi = (float)xr.RasterizationScale;
        G.RasterScale = (float)(View.Scale * xr.RasterizationScale);
        _cam.InsertScalar("scale", (float)View.Scale);
        _cam.InsertScalar("lineX", (float)View.LineX);
        _cam.InsertScalar("vw", (float)View.Width);
        _cam.InsertScalar("vh", (float)View.Height);
    }

    private void OnResize()
    {
        var old = View;
        float oldRs = G.RasterScale;
        Measure();
        if (old.Width == View.Width && old.Height == View.Height && Math.Abs(oldRs - G.RasterScale) < 1e-4) return;
        Log.Info($"resize {View.Width}x{View.Height} scale {View.Scale}");
        if (_ccOpen) CloseControlCenter();
        LayoutChrome();
        ObResize();
        RefreshScreensaverClock(force: true);
        if (Math.Abs(oldRs - G.RasterScale) > 1e-4) { Text.Clear(); foreach (var t in _tiles.Values) t.Redraw(); }
        RenderSeamContent();
        Recull(true);
        CsResize();
    }

    private bool IsForeground()
    {
        if (_qsOpen && QuickPanelOwnWindow) return true;   // 快捷面板（自己的不激活窗口）开着：游戏在前台，手柄归面板
        return (_window as MainWindow)?.AcceptsGamepad != false && !ToolboxWindow.IsIconic(_windowHandle) && (GetForegroundWindow() == _windowHandle || (_window as MainWindow)?.OverlayAcceptsGamepad == true);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    // ---------------- 数据 ----------------

    private string ArtRoot => Path.Combine(App.CacheDir, "art");

    private void LoadLibrary()
    {
        var steam = Steam.Root();
        var user = steam != null ? _connections.Steam is { } account ? Steam.UserDirForAccount(steam, account.Id) : Steam.UserDir(steam) : null;
        var stats = Steam.ReadPlayStats(user);
        Log.Info($"steam root={steam ?? "-"} user={user ?? "-"} stats={stats.Count}");
        var seed = Path.Combine(AppContext.BaseDirectory, "Assets", "library.json");
        var collections = Steam.ReadCollections(user);
        var owned = steam != null ? Steam.ReadOwnedApps(steam) : null;
        var installed = steam != null ? Steam.ReadInstalled(steam) : null;
        Log.Info($"steam owned apps={owned?.Count ?? 0} installed={installed?.Count ?? 0}");
        // 设置页手动加的游戏文件夹：已经是 Steam 非 Steam 快捷方式的不重复加
        var shortcuts = Steam.ShortcutPaths(user);
        // 本地游戏不在文件夹扫描里重复出现；封面缓存按原图版本定位，启动和库内刷新共用。
        var local = LocalGames.Read();
        shortcuts.AddRange(local.Select(l => l.Exe));
        _localArtDirs = local.ToDictionary(g => g.Id, LocalArtDirectory);
        var played = FolderGames.ReadPlayed();
        Library.FolderGames = _prefs.Folders.SelectMany(f => FolderGames.Scan(f, shortcuts))
            .Select(g => (g.Title, g.Exe, played.TryGetValue(FolderGames.IdOf(g.Exe), out var t) ? (long?)t : null)).ToList();
        // 首帧之后后台扫描 Xbox，不在启动时等待包枚举。
        Library.XboxLibrary = new();
        if (Library.FolderGames.Count > 0) Log.Info($"folder games: {Library.FolderGames.Count}");
        _lib = Library.Load(seed, stats, id => ArtKitBuilder.TryLoad(ArtDirectory(id)), collections, owned, installed, Steam.ShortcutTargets(user), local);
        _steamRoot = steam; _steamUser = user;
        LoadSteamCloudCache();
        RestoreGameSession();
        if (_lib.Games.Count == 0)
        {
            Log.Warn("library empty");
            // 保留完整来源，安装范围切回「所有」时可以立即恢复被筛掉的游戏。
        }
    }

    private string? _steamRoot, _steamUser;

    /// <summary>缺封面套件的游戏：后台从 Steam 原图生成，生成一张就把墙上用到它的卡重画。</summary>
    private async Task BuildMissingArtBatchAsync()
    {
        if (_steamRoot == null) return;
        // 按墙上的顺序（最近玩过的在前）生成，先看得到的先有图
        var order = _lib.HomeTabs.FirstOrDefault(t => t.Id == "all")?.Items() ?? _lib.Games;
        var missing = order.Where(g => g.Kind == ItemKind.Game && g.Art == null && g.Platform != "folder" && g.Platform != "xbox").ToList();
        if (missing.Count == 0) return;
        Log.Info($"art kits missing: {missing.Count}; building");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var g in missing)
        {
            try
            {
                var (portrait, hero) = Steam.ArtSources(_steamRoot, _steamUser, g.Id);
                string dir = ArtDirectory(g.Id);
                var kit = await Task.Run(() => ArtKitBuilder.BuildAsync(dir, portrait, hero, g.SeedFx, g.SeedColor));
                if (kit == null) { Log.Warn($"no art for {g.Id} {g.Title}"); continue; }
                g.OwnArt = kit;
                RefreshItem(g.Id);
            }
            catch (Exception ex) { Log.Error($"art kit {g.Id}", ex); }
        }
        Log.Info($"art kits built in {sw.ElapsedMilliseconds}ms");
        _backdrop.SetFilmCovers(FilmCoverPaths(), a => _dq.TryEnqueue(() => { a(); EnsureBackdropLoop(); }));
        _ccBack?.SetFilmCovers(FilmCoverPaths(), a => _dq.TryEnqueue(() => { a(); CcBackLoop(); }));
    }

    private void RefreshItem(string id)
    {
        foreach (var t in _tiles.Values)
        {
            if (t.Shown == null || t.Target != t.Shown) continue;
            var it = Lookup(t.Shown);
            if (it == null) continue;
            if (it.Id == id || it.ArtFrom?.Id == id) t.Show(t.Shown);
        }
        UpdateBackdropKey(FocusKey, false);
        CsRefreshItem(id);
    }

    // ---------------- 墙以外的视觉 ----------------

    private void BuildChrome()
    {
        // 背景：底色 + 两团光（radial-gradient circle farthest-corner）
        // 关闭动态背景时用的底色分成缝左、缝右两半。动态背景开启时两半隐藏，让所有背景样式铺到游戏封面下方。
        _bgBase = G.Sprite(G.Solid(Theme.Bg));
        _glow1 = G.Sprite(null);
        _glow2 = G.Sprite(null);
        _bgBaseR = G.Sprite(G.Solid(Theme.Bg));
        _glow1R = G.Sprite(null);
        _glow2R = G.Sprite(null);
        var wallL = G.Container();
        var wallR = G.Container();
        _wallBackgroundL = wallL; _wallBackgroundR = wallR;
        _cam.InsertScalar("bpreview", 0);
        _cam.InsertScalar("bfull", 0);
        foreach (var v in new Visual[] { _bgBase, _glow1, _glow2 }) wallL.Children.InsertAtTop(v);
        foreach (var v in new Visual[] { _bgBaseR, _glow1R, _glow2R }) wallR.Children.InsertAtTop(v);
        var clipL = G.C.CreateInsetClip();
        var clipR = G.C.CreateInsetClip();
        wallL.Clip = clipL;
        wallR.Clip = clipR;
        Expr(clipL, "RightInset", "p.vw - (p.sx - p.sw / 2)");
        Expr(clipR, "LeftInset", "p.sx + p.sw / 2");
        _root.Children.InsertAtTop(wallL);
        _root.Children.InsertAtTop(wallR);
        Expr(wallL, "Opacity", "p.fade");
        Expr(wallR, "Opacity", "p.fade");
        _backdrop = new Backdrop(G) { Dpi = Chrome.Dpi, ShareModeTransitionFrame = App.Packaged };
        _backdrop.Configure(_backgroundSettings);
        _backdrop.SetFilmCovers(FilmCoverPaths(), a => _dq.TryEnqueue(() => { a(); EnsureBackdropLoop(); }));
        _root.Children.InsertAtBottom(_backdrop.Root);
        Expr(_backdrop.Root, "Opacity", "p.fade");
        // 所有开启的背景和全屏预览都固定在视口上，覆盖游戏封面之间的空隙；不随侧边栏开口平移。
        Expr(_backdrop.Root, "Offset", "p.bpreview > 0.5 || p.bfull > 0.5 ? Vector3(0, 0, 0) : Vector3(p.sx - Lerp(p.bhome, p.vw / 2, Clamp((p.sw - p.bsw) / Max(1, p.vw - p.bsw), 0, 1)), 0, 0)");
        // Dim the complete backdrop, including gutters and empty cells. Posters retain their
        // own Folia lights-out layer so a raised card remains fully lit without double dimming.
        var backgroundLights = G.Sprite(G.Solid(Microsoft.UI.Colors.Black));
        Expr(backgroundLights, "Opacity", "p.ldim * p.fade * (p.bpreview > 0.5 ? 0 : 1)");
        _root.Children.InsertAtTop(backgroundLights);
        _root.Children.InsertAtTop(_field);
        _field.Children.InsertAtTop(_worlds[0]);
        _field.Children.InsertAtTop(_worlds[1]);
        foreach (var w in _worlds)
        {
            var e = G.C.CreateExpressionAnimation(w == _worlds[0]
                ? "Vector3(p.lineX - p.cx * p.scale - p.sw / 2 - p.pad, p.vh / 2 - p.cy * p.scale, 0)"
                : "Vector3(p.lineX - p.cx * p.scale + p.sw / 2 + p.pad, p.vh / 2 - p.cy * p.scale, 0)");
            e.SetReferenceParameter("p", _cam);
            w.StartAnimation("Offset", e);
            var s = G.C.CreateExpressionAnimation("Vector3(p.scale, p.scale, 1)");
            s.SetReferenceParameter("p", _cam);
            w.StartAnimation("Scale", s);
        }
        // 缝的几何：sx（屏幕位置）、sw（实际开口）、pad（两半各让半个 gap）
        CamExpr("sx", "p.lineX + (p.anchor - p.cx) * p.scale");
        CamExpr("sw", "Max(0, Min(p.open, (Min(p.sx, p.vw - p.sx) - 14) * 2))");
        CamExpr("pad", "4 * p.scale * Min(1, p.sw / 16)");

        // 暗角：在卡片之上、缝之下
        _vignette = G.Sprite(null);
        _root.Children.InsertAtTop(_vignette);
        Expr(_vignette, "Opacity", "p.fade");

        // 缝：透明的纸条（folia「始终透明」的缝）——底色 64% 的纱 + 上沿一道很淡的主色渐变 + 两侧发丝线 + 两侧投影，
        // 透出后面的动态背景；里面的内容居中、随开口淡入，换层时翻转
        _seamHost = G.C.CreateContainerVisual();
        var shadowL = G.Sprite(G.LinearGradient(90, 32, 100, (0, Gfx.Rgba(0, 0, 0, 0)), (1, Gfx.Rgba(0, 0, 0, 0.32))), relative: false);
        var shadowR = G.Sprite(G.LinearGradient(90, 32, 100, (0, Gfx.Rgba(0, 0, 0, 0.32)), (1, Gfx.Rgba(0, 0, 0, 0))), relative: false);
        var veil = G.Sprite(G.Solid(Gfx.WithAlpha(Theme.Bg, 0.64)), relative: false);
        var sheen = G.Sprite(G.LinearGradient(180, 100, 100, (0, Gfx.WithAlpha(Theme.Ink, 0.06)), (0.22, Gfx.WithAlpha(Theme.Ink, 0))), relative: false);
        var hairL = G.Sprite(G.Solid(Gfx.WithAlpha(Theme.Ink, 0.12)), relative: false);
        var hairR = G.Sprite(G.Solid(Gfx.WithAlpha(Theme.Ink, 0.12)), relative: false);
        foreach (var v in new Visual[] { shadowL, shadowR, veil, sheen, hairL, hairR }) _seamHost.Children.InsertAtTop(v);
        Expr(_seamHost, "Offset", "Vector3(p.sx - p.sw / 2, 0, 0)");
        Expr(_seamHost, "Size", "Vector2(p.sw, p.vh)");
        Expr(_seamHost, "Opacity", "p.sw < 1 ? 0 : p.fade");
        Expr(veil, "Size", "Vector2(p.sw, p.vh)");
        Expr(sheen, "Size", "Vector2(p.sw, p.vh)");
        Expr(shadowL, "Size", "Vector2(32, p.vh)");
        shadowL.Offset = new Vector3(-32, 0, 0);
        Expr(shadowR, "Size", "Vector2(32, p.vh)");
        Expr(shadowR, "Offset", "Vector3(p.sw, 0, 0)");
        Expr(hairL, "Size", "Vector2(1, p.vh)");
        Expr(hairR, "Size", "Vector2(1, p.vh)");
        Expr(hairR, "Offset", "Vector3(p.sw - 1, 0, 0)");
        var clip = G.C.CreateContainerVisual();
        clip.Clip = G.C.CreateInsetClip();
        Expr(clip, "Size", "Vector2(p.sw, p.vh)");
        _seamHost.Children.InsertAtTop(clip);
        _seamInner = G.C.CreateContainerVisual();
        Expr(_seamInner, "Offset", "Vector3(p.sw / 2 - p.cw / 2, 0, 0)");
        Expr(_seamInner, "Size", "Vector2(p.cw, p.vh)");
        Expr(_seamInner, "Opacity", "(Min(1, p.sw / Max(1, p.cw)) < 0.6 ? 0 : (Min(1, p.sw / Max(1, p.cw)) - 0.6) / 0.4) * p.lfade");
        clip.Children.InsertAtTop(_seamInner);
        var persp = G.Container();
        persp.RelativeOffsetAdjustment = new Vector3(0.5f, 0.5f, 0);
        persp.TransformMatrix = Tile.Perspective((float)Motion.SeamFlip.Perspective);
        _seamPivot = G.Container();
        _seamPivot.RotationAxis = new Vector3(0, 1, 0);
        persp.Children.InsertAtTop(_seamPivot);
        _seamInner.Children.InsertAtTop(persp);
        _seamA = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _seamB = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _seamB.Opacity = 0;
        _seamPivot.Children.InsertAtTop(_seamA);
        _seamPivot.Children.InsertAtTop(_seamB);
        _root.Children.InsertAtTop(_seamHost);

        _edgeLeft = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _edgeRight = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        foreach (var e in new[] { _edgeLeft, _edgeRight })
        {
            var sh = G.C.CreateDropShadow();
            sh.Offset = new Vector3(0, 24, 0); sh.BlurRadius = 64; sh.Color = Gfx.Rgba(0, 0, 0, 0.42);
            sh.SourcePolicy = CompositionDropShadowSourcePolicy.InheritFromVisualContent;
            e.Shadow = sh;
            _root.Children.InsertAtTop(e);
        }
        Expr(_edgeLeft, "Opacity", "(p.open > 1 && p.sw < 2 && p.sx < p.lineX) ? p.fade * p.lfade : 0");
        Expr(_edgeRight, "Opacity", "(p.open > 1 && p.sw < 2 && p.sx >= p.lineX) ? p.fade * p.lfade : 0");

        _toast = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _toast.Opacity = 0;
        _top.Children.InsertAtTop(_toast);
        _hud = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _hud.IsVisible = false;
        _top.Children.InsertAtTop(_hud);
        LayoutChrome();
    }

    private void LayoutChrome()
    {
        float w = (float)View.Width, h = (float)View.Height;
        // radial-gradient(circle at 14% 12%, glow-a 16%, transparent 29%)（半径 = 到最远角）
        Glow(_glow1, 0.14f, 0.12f, Gfx.WithAlpha(Theme.GlowA, 0.16), 0.29f, w, h);
        Glow(_glow2, 0.86f, 0.82f, Gfx.WithAlpha(Theme.GlowB, 0.20), 0.32f, w, h);
        _glow1R.Brush = _glow1.Brush;
        _glow2R.Brush = _glow2.Brush;
        var vig = G.C.CreateRadialGradientBrush();
        vig.MappingMode = CompositionMappingMode.Absolute;
        vig.EllipseCenter = new Vector2(w / 2, h / 2);
        vig.EllipseRadius = new Vector2(w / 2 * MathF.Sqrt(2), h / 2 * MathF.Sqrt(2));
        vig.ColorStops.Add(G.C.CreateColorGradientStop(0.78f, Gfx.Rgba(0, 0, 0, 0)));
        vig.ColorStops.Add(G.C.CreateColorGradientStop(1f, Gfx.Rgba(0, 0, 0, 0.52)));
        _vignette.Brush = vig;
        _backdrop?.Build(w, h);
        RenderEdgeTabs();
    }

    private void Glow(SpriteVisual v, float fx, float fy, Color c, float stop, float w, float h)
    {
        float cx = fx * w, cy = fy * h;
        float r = MathF.Max(MathF.Max(MathF.Sqrt(cx * cx + cy * cy), MathF.Sqrt((w - cx) * (w - cx) + cy * cy)),
                            MathF.Max(MathF.Sqrt(cx * cx + (h - cy) * (h - cy)), MathF.Sqrt((w - cx) * (w - cx) + (h - cy) * (h - cy))));
        var b = G.C.CreateRadialGradientBrush();
        b.MappingMode = CompositionMappingMode.Absolute;
        b.EllipseCenter = new Vector2(cx, cy);
        b.EllipseRadius = new Vector2(r, r);
        b.ColorStops.Add(G.C.CreateColorGradientStop(0, c));
        b.ColorStops.Add(G.C.CreateColorGradientStop(stop, Gfx.WithAlpha(c, 0)));
        v.Brush = b;
    }

    private void CamExpr(string prop, string expr)
    {
        var e = G.C.CreateExpressionAnimation(expr);
        e.SetReferenceParameter("p", _cam);
        _cam.StartAnimation(prop, e);
    }

    private void Expr(CompositionObject target, string prop, string expr)
    {
        var e = G.C.CreateExpressionAnimation(expr);
        e.SetReferenceParameter("p", _cam);
        target.StartAnimation(prop, e);
    }

    // ---------------- 相机（合成线程动画 + UI 线程解析值） ----------------

    private void SetCam(Center c)
    {
        _cam.StopAnimation("cx");
        _cam.StopAnimation("cy");
        _cam.InsertScalar("cx", (float)c.X);
        _cam.InsertScalar("cy", (float)c.Y);
    }

    /// <summary>此刻的相机中心与开口（补间中按同一条曲线算）。</summary>
    private void Sync()
    {
        long now = NowMs;
        if (_camX != null) { _center.X = _camX.At(now); if (_camX.Done(now)) _camX = null; }
        if (_camY != null) { _center.Y = _camY.At(now); if (_camY.Done(now)) _camY = null; }
        if (_seamTween != null) { _openWidth = _seamTween.At(now); if (_seamTween.Done(now)) _seamTween = null; }
    }

    public Center CenterNow { get { Sync(); return _center; } }

    public void TweenCamera(Center to, double ms = Motion.CameraMs)
    {
        Sync();
        PanVelocity = Vector2.Zero;
        if (Math.Abs(to.X - _center.X) < 0.5 && Math.Abs(to.Y - _center.Y) < 0.5) return;
        // 先把目的地并进裁剪范围，飞行途中墙不会空
        Recull(true, VisibleWorld(to, View, _openWidth, 0));
        if (Reduced) { _camX = _camY = null; _center = to; SetCam(to); return; }
        long now = NowMs;
        _camX = new Tween { From = _center.X, To = to.X, Start = now, Ms = ms, Ease = Motion.CameraEase };
        _camY = new Tween { From = _center.Y, To = to.Y, Start = now, Ms = ms, Ease = Motion.CameraEase };
        _cam.StartAnimation("cx", G.Scalar(_center.X, to.X, ms, Motion.CameraEase));
        _cam.StartAnimation("cy", G.Scalar(_center.Y, to.Y, ms, Motion.CameraEase));
        WallBusy(ms);
        PublishStrip();
        int v = ++_version;
        After(ms + 20, () => { if (v == _version) { Sync(); Recull(false); } });
    }

    private void TweenSeam(double to, double? from = null)
    {
        if (!LibraryRailVisible) to = 0;
        Sync();
        double f = from ?? _openWidth;
        if (Reduced || Math.Abs(to - f) < 0.5)
        {
            _openWidth = to; _seamTween = null;
            _cam.StopAnimation("open"); _cam.InsertScalar("open", (float)to);
            return;
        }
        _seamTween = new Tween { From = f, To = to, Start = NowMs, Ms = Motion.SeamMs, Ease = Motion.SeamEase };
        _openWidth = f;
        _cam.StartAnimation("open", G.Scalar(f, to, Motion.SeamMs, Motion.SeamEase));
        WallBusy(Motion.SeamMs);
        PublishStrip();
        // 开口变宽，看得见的范围也变宽
        After(Motion.SeamMs + 20, () => { Sync(); Recull(false); });
        Recull(true, VisibleWorld(_center, View, Math.Max(to, f), 0));
    }

    private Engine.Rect Drawn(Slot slot) => _drawnOverride.TryGetValue(slot.Key, out var r) ? r : slot.Rect;

    private void Reveal(SlotKey key)
    {
        var slot = SlotByKey(key);
        if (slot == null) return;
        Sync();
        TweenCamera(RevealCenter(Drawn(slot), _center, View, _anchorX, LibrarySeamWidth));
    }

    // ---------------- 平移（右摇杆 / 拖动）：UI 线程逐帧 ----------------

    private void EnsureLoop()
    {
        if (_loopOn) return;
        _loopOn = true;
        _lastFrame = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private Vector2 _glide;

    private void OnRendering(object? sender, object e)
    {
        long now = NowMs;
        double dt = _lastFrame == 0 ? 16 : Math.Min(50, now - _lastFrame);
        _lastFrame = now;
        _pad?.ApplyPan(this);
        if (_ccOpen || _wsOpen || _qsOpen || CsActive) { PanVelocity = Vector2.Zero; _glide = Vector2.Zero; }
        var v = PanVelocity + _glide;
        if (v.X != 0 || v.Y != 0)
        {
            MarkInput();
            Sync();
            _camX = _camY = null;
            _version++;
            _center = new Center(_center.X + v.X * dt / 1000 / View.Scale, _center.Y + v.Y * dt / 1000 / View.Scale);
            SetCam(_center);
            Recull(false);
            WallBusy(120);
            PublishStrip();
        }
        if (_glide != Vector2.Zero)
        {
            float decay = (float)Math.Exp(-dt / 280);
            _glide *= decay;
            if (_glide.Length() <= 8) _glide = Vector2.Zero;
        }
        if (_hudOn) HudSample(dt);
        if (PanVelocity == Vector2.Zero && _glide == Vector2.Zero && !_hudOn && !(_pad?.Panning ?? false))
        {
            CompositionTarget.Rendering -= OnRendering;
            _loopOn = false;
        }
    }

    public void RequestPanLoop() { if (!ConsumeScreensaverWake()) EnsureLoop(); }

    /// <summary>拖动：直接平移相机（屏幕 px）。</summary>
    public void PanBy(double dx, double dy)
    {
        if (ConsumeScreensaverWake()) return;
        if (_backgroundSettingsOpen) return;
        if (_ccOpen || _wsOpen || _qsOpen || MorphBusy) return;
        if (CsActive) { MarkInput(); CsDrag(dx, dy); return; }
        MarkInput();
        Sync();
        _camX = _camY = null;
        _version++;
        _center = new Center(_center.X - dx / View.Scale, _center.Y - dy / View.Scale);
        SetCam(_center);
        Recull(false);
    }

    // ---------------- 裁剪 ----------------

    private Bounds Visible() { Sync(); return VisibleWorld(_center, View, _openWidth, 0); }

    private void Recull(bool force, Bounds? extra = null, double overscan = Overscan)
    {
        UpdateBackdropRunning();
        var vis = Visible();
        double pad = Overscan - CullMargin;
        if (!force && _culled is { } c && vis.Left >= c.Left - pad && vis.Right <= c.Right + pad && vis.Top >= c.Top - pad && vis.Bottom <= c.Bottom + pad) return;
        var want = new Bounds(vis.Left - overscan, vis.Right + overscan, vis.Top - overscan, vis.Bottom + overscan);
        if (extra is { } ex) want = want.Union(ex);
        _culled = vis;
        var slots = SlotsIn(want, MaxTiles, _slotScratch);
        var keep = new HashSet<SlotKey>();
        int anchorCol = BoundaryColumn(_anchorX);
        long now = NowMs;
        foreach (var slot in slots)
        {
            keep.Add(slot.Key);
            int side = slot.Column < anchorCol ? 0 : 1;
            if (!_tiles.TryGetValue(slot.Key, out var tile))
            {
                tile = new Tile(this, slot);
                if (_drawnOverride.TryGetValue(slot.Key, out var ov)) tile.Place(ov);
                tile.Show(ItemFor(slot));
                if (slot.Key == FocusKey) tile.SetFocused(true);
                if (slot.Key == _expandedKey) tile.SetExpanded(true);
                _tiles[slot.Key] = tile;
                if (_entrance is { } en && now < en.until && !Reduced)
                    tile.Land(Math.Max(0, en.start + Plans.WaveStagger(slot.X, slot.Y, en.left, en.top) - now));
                _worlds[side].Children.InsertAtBottom(tile.El);
                tile.Side = side;
            }
            else if (tile.Side != side)
            {
                _worlds[tile.Side].Children.Remove(tile.El);
                _worlds[side].Children.InsertAtBottom(tile.El);
                tile.Side = side;
            }
        }
        List<SlotKey>? drop = null;
        foreach (var (key, tile) in _tiles)
        {
            if (keep.Contains(key) || key == FocusKey || _drawnOverride.ContainsKey(key)) continue;
            (drop ??= new()).Add(key);
        }
        if (drop != null)
            foreach (var key in drop)
            {
                var tile = _tiles[key];
                _worlds[tile.Side].Children.Remove(tile.El);
                tile.Dispose();
                _tiles.Remove(key);
            }
        if (_focusTop != null && FocusKey is { } fk && _tiles.TryGetValue(fk, out var ft)) BringToFront(ft);
        RefreshScreensaverTiles();
    }

    private Tile? _focusTop;

    private void BringToFront(Tile t)
    {
        _focusTop = t;
        var w = _worlds[t.Side];
        w.Children.Remove(t.El);
        w.Children.InsertAtTop(t.El);
        // 展开的那张永远在最上面
        if (_expandedKey is { } ek && _tiles.TryGetValue(ek, out var et) && et != t && et.Side == t.Side)
        {
            w.Children.Remove(et.El);
            w.Children.InsertAtTop(et.El);
        }
        // Both halves meet when the rail closes. The raised card must also be above
        // the other half, otherwise its outward pop disappears behind a neighbour.
        _field.Children.Remove(w);
        _field.Children.InsertAtTop(w);
    }

    private string? ItemFor(Slot slot)
    {
        int idx = Content.ItemAt(CurrentLayer.Layout, slot.Key);
        return idx < 0 || idx >= CurrentLayer.Items.Count ? null : CurrentLayer.Items[idx].Id;
    }

    private bool HasContent(Slot slot) => ItemFor(slot) != null;

    /// <summary>缝右边离缝最近的一张卡（新层的起点）。</summary>
    private Slot? SlotNearSeam()
    {
        var vis = VisibleWorld(_center, View, 0, 0);
        var right = SlotsIn(vis).Where(s => s.X > _anchorX);
        return Nav.NearestTo(right, _anchorX + 200, _center.Y);
    }

    // ---------------- 焦点 ----------------

    public void Focus(SlotKey? key, bool reveal = true)
    {
        if (FocusKey is { } old && _tiles.TryGetValue(old, out var ot)) ot.SetFocused(false);
        FocusKey = key;
        UpdateBackdropKey(key, true);
        NoteFocusedGame(key);
        if (key is { } k)
        {
            if (_tiles.TryGetValue(k, out var t)) { t.SetFocused(!_railFocused); BringToFront(t); }
            if (reveal) Reveal(k);
        }
    }

    /// <summary>焦点不在屏上（摇杆平移走了）时，从视口中央选最近的一张。</summary>
    private bool EnsureFocusOnScreen()
    {
        var vis = Visible();
        var cur = FocusKey is { } fk ? SlotByKey(fk) : null;
        if (cur != null && Drawn(cur).Bounds.Overlaps(vis)) return false;
        var pick = Nav.NearestTo(SlotsIn(vis).Where(HasContent), _center.X, _center.Y);
        Focus(pick?.Key);
        return true;
    }

    public void Move(Direction dir) => WithCue(Sfx.Nav, () =>
    {
        if (ConsumeScreensaverWake()) return;
        MarkInput();
        NavHaptic(dir switch { Direction.Up => "moveUp", Direction.Down => "moveDown", Direction.Left => "moveLeft", _ => "moveRight" });
        if (_wsOpen) { WsMove(dir); return; }
        if (_qsOpen) { QsMove(dir); return; }
        if (_ob != null) { ObMove(dir); return; }
        if (LaunchMotionIntercept(false, dir)) { if (_lm?.Mode != LmMode.Playing) Cue(Sfx.None); return; }
        if (MorphIntercept()) return;
        if (_ccOpen) { CcMove(dir); return; }
        if (CsActive) { CsMove(dir); return; }
        if (RailMove(dir)) return;
        if (EnsureFocusOnScreen()) return;
        var from = SlotByKey(FocusKey!.Value);
        if (from == null) return;
        var next = Nav.FindAdjacentSlot(from, dir, HasContent, Drawn);
        if (TryEnterRailFrom(from, next, dir)) return;
        // 声音跟着位置走：撞到哪边的墙就在哪边响，移到哪张卡就从哪边响
        if (next == null) { Cue(Sfx.Bump, pan: dir == Direction.Left ? -0.6f : dir == Direction.Right ? 0.6f : PanFor(from)); return; }
        CuePan(PanFor(next));
        if (_expandedKey is { } ek && next.Key != ek) Collapse(false);
        Focus(next.Key);
    });

    /// <summary>A：游戏卡展开聚焦卡；聚焦卡上再按 = 启动；集合卡 = 进入。</summary>
    public void Activate() => WithCue(Sfx.Activate, () =>
    {
        if (!_wsOpen && NowMs < _wsLibraryConfirmUntil) { Cue(Sfx.None); return; }
        // 屏保里按 A：回到首页并选中屏幕上那款（只唤醒，不启动）
        if (_screensaverActive && CsSaverWakeWithGame()) return;
        if (ConsumeScreensaverWake()) return;
        MarkInput();
        NavHaptic("confirm");
        if (_wsOpen) { WsActivate(); return; }
        if (_qsOpen) { QsActivate(); return; }
        if (_ob != null) { ObActivate(); return; }
        if (LaunchMotionIntercept(false, activate: true)) { if (_lm?.Mode != LmMode.Playing) Cue(Sfx.None); return; }
        if (MorphIntercept()) return;
        if (_ccOpen) { CcActivate(); return; }
        if (CsActive) { CsActivate(); return; }
        if (RailActivate()) return;
        if (EnsureFocusOnScreen()) return;
        var slot = SlotByKey(FocusKey!.Value);
        var id = slot != null ? ItemFor(slot) : null;
        var item = id != null ? Lookup(id) : null;
        if (slot == null || item == null) return;
        if (item.Kind == ItemKind.Collection) { Cue(Sfx.DetailIn); Push(slot, item); return; }
        if (_expandedKey == slot.Key) { LaunchOrInstall(item, _tiles.GetValueOrDefault(slot.Key)); return; }
        Cue(Sfx.DetailIn);
        Expand(slot);
    });

    /// <summary>
    /// 交给 Steam：装好的 Steam 游戏 steam://rungameid/&lt;appid&gt;；没装的 steam://install/&lt;appid&gt;（Steam 弹出安装窗口，
    /// 按一下就开始下载）；本地游戏是 Steam 里的非 Steam 快捷方式，用 (appid &lt;&lt; 32) | 0x02000000 的游戏 ID 启动。
    /// </summary>
    private void LaunchOrInstall(LibraryItem item, Tile? tile = null)
    {
        _lmTile = tile;
        if (InterceptLaunchWhilePlaying(item)) return;
        (_window as MainWindow)?.HandOffForeground();
        if (item.XboxAumid is string aumid) { _ = LaunchXboxGame(item, aumid); return; }
        if (item.ExePath is string exe)
        {
            string dir = string.IsNullOrEmpty(item.ExeStartDir) ? Path.GetDirectoryName(exe) ?? "" : item.ExeStartDir;
            if (StartViaGameSession(item, exe, dir, item.ExeArgs ?? "")) { FolderGames.MarkPlayed(item.Id); AfterGameLaunch(item); }
            else
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Arguments = item.ExeArgs ?? "", WorkingDirectory = dir });
                    FolderGames.MarkPlayed(item.Id);
                    AfterGameLaunch(item);
                }
                catch (Exception ex) { Cue(Sfx.Error); Toast($"「{item.Title}」没能启动：{ex.Message}"); }
            Log.Info($"launch folder game {item.Id} {exe}");
            return;
        }
        if (!uint.TryParse(item.Id, out var appid)) { Toast($"「{item.Title}」没有可用的启动方式"); return; }
        if (item.Platform != "steam")
        {
            // 本地游戏（Steam 里的非 Steam 快捷方式）：照快捷方式里的程序和参数，经 ClawGameSession 直接启动，运行状态才可靠
            if (Steam.ShortcutLaunch(_steamUser, item.Id) is { } sc && StartViaGameSession(item, sc.Exe, sc.StartDir, sc.Options))
                AfterGameLaunch(item);
            else
            {
                ulong gameId = ((ulong)appid << 32) | 0x02000000UL;
                SystemInfo.Open($"steam://rungameid/{gameId}");
                AfterGameLaunch(item);
            }
        }
        else if (item.Installed)
        {
            SystemInfo.Open($"steam://rungameid/{appid}");
            BeginLaunchSession(item, SessionSource.Steam);
            AfterGameLaunch(item);
        }
        else
        {
            SystemInfo.Open($"steam://install/{appid}");
            Toast($"在 Steam 里安装「{item.Title}」");
        }
        Log.Info($"launch {item.Id} {item.Platform} installed={item.Installed}");
    }

    private async Task LaunchXboxGame(LibraryItem item, string aumid)
    {
        try
        {
            await XboxGames.LaunchAsync(aumid);
            AfterGameLaunch(item);
        }
        catch (Exception ex) { SoundFx.Play(Sfx.Error); Toast($"「{item.Title}」没能启动：{ex.Message}"); }
    }

    /// <summary>B：收起聚焦卡 → 返回上一层。</summary>
    public void Back() => WithCue(Sfx.Back, () =>
    {
        if (ConsumeScreensaverWake()) return;
        MarkInput();
        NavHaptic("back");
        if (_wsOpen) { WsBack(); return; }
        if (_qsOpen) { QsBack(); return; }
        if (_ob != null) { ObBack(); return; }
        if (LaunchMotionIntercept(true)) return;
        if (MorphIntercept()) return;
        if (_ccOpen) { if (!CcChromeBack()) CloseControlCenter(); return; }
        if (CsActive)
        {
            if (CsBack()) return;
            if (_npBar != null && _game is { } now) { ResumeGame(now); return; }
            Cue(Sfx.Bump);
            return;
        }
        if (RailBack()) return;
        if (_expandedKey != null) { Cue(Sfx.DetailOut); Collapse(true); return; }
        if (_stack.Count > 1) { Cue(Sfx.DetailOut); Pop(); return; }
        if (_npBar != null && _game is { } playing) { ResumeGame(playing); return; }
        Cue(Sfx.Bump);
    });

    // ---------------- 聚焦卡（块内让位） ----------------

    private void Expand(Slot slot)
    {
        if (_expandedKey != null) Collapse(false);
        var to = FocusedBlockSlots(slot.Column, slot.Row, slot.Index).ToDictionary(s => s.Key, s => s.Rect);
        StartReflow(to);
        _expandedKey = slot.Key;
        if (_tiles.TryGetValue(slot.Key, out var t)) { t.SetExpanded(true); BringToFront(t); }
        Sync();
        TweenCamera(RevealCenter(to[slot.Key], _center, View, _anchorX, LibrarySeamWidth));
    }

    private void Collapse(bool reveal)
    {
        if (_expandedKey is not { } key) return;
        var slot = SlotByKey(key)!;
        if (_tiles.TryGetValue(key, out var t)) t.SetExpanded(false);
        _expandedKey = null;
        var baseRects = new Dictionary<SlotKey, Engine.Rect>();
        foreach (var s in BlockSlots(slot.Column, slot.Row)) baseRects[s.Key] = s.Rect;
        StartReflow(baseRects, clearAfter: true);
        if (reveal)
        {
            Sync();
            TweenCamera(RevealCenter(slot.Rect, _center, View, _anchorX, LibrarySeamWidth));
        }
    }

    /// <summary>
    /// 让位：每张卡按弹簧曲线从当前矩形走到新矩形（合成线程）。
    /// 与网页版不同的一点：每张卡各走各的，新的让位不会把上一次还没走完的卡冻在半路。
    /// </summary>
    private void StartReflow(Dictionary<SlotKey, Engine.Rect> to, bool clearAfter = false)
    {
        long now = NowMs;
        if (Reduced)
        {
            foreach (var (key, r) in to) if (_tiles.TryGetValue(key, out var t)) { t.Place(r); t.FitArt(); }
            if (clearAfter) foreach (var key in to.Keys) _drawnOverride.Remove(key);
            else foreach (var (key, r) in to) _drawnOverride[key] = r;
            return;
        }
        int ms = Motion.ReflowMs;
        WallBusy(ms);
        foreach (var (key, r) in to)
        {
            var from = _tiles.TryGetValue(key, out var t) ? t.CurrentRect(now) : Drawn(SlotByKey(key)!);
            _drawnOverride[key] = r;
            _reflowEnds[key] = now + ms;
            if (t != null)
            {
                if (from != r) t.AnimateRect(from, r, ms, now);
                else t.Place(r);
            }
        }
        var keys = to.Keys.ToList();
        After(ms + 10, () =>
        {
            long n = NowMs;
            foreach (var key in keys)
            {
                if (!_reflowEnds.TryGetValue(key, out long end) || n < end) continue;   // 被更新的让位接管了
                _reflowEnds.Remove(key);
                if (_tiles.TryGetValue(key, out var t)) { t.FinishRect(); t.FitArt(); }
                if (clearAfter && _drawnOverride.TryGetValue(key, out var r) && SlotByKey(key) is { } s && r == s.Rect) _drawnOverride.Remove(key);
            }
        });
    }

    // ---------------- 换层 ----------------

    private void SwapContent(FlipOrigin? origin)
    {
        var changes = new List<Change>();
        foreach (var t in _tiles.Values) changes.Add(new Change(t.Slot, t.Target, ItemFor(t.Slot)));
        Sync();
        var visible = VisibleWorld(_center, View, _openWidth, Overscan);
        bool reduced = Reduced;
        double duration;
        if (origin != null)
        {
            var plan = Plans.PlanFlip(changes, origin, visible);
            foreach (var key in plan.Instant) { var t = _tiles[key]; t.Show(ItemFor(t.Slot)); }
            foreach (var st in plan.Steps) { var t = _tiles[st.Key]; t.Flip(ItemFor(t.Slot), reduced ? 0 : st.Delay, st.Direction, reduced); }
            duration = reduced ? Motion.ReducedFadeMs : plan.DurationMs;
        }
        else
        {
            var viewport = VisibleWorld(_center, View, _openWidth, 0);
            var plan = Plans.PlanWave(changes, viewport, visible);
            foreach (var key in plan.Instant) { var t = _tiles[key]; t.Show(ItemFor(t.Slot)); }
            foreach (var st in plan.Steps) { var t = _tiles[st.Key]; t.Wave(ItemFor(t.Slot), st.OutDelay, st.InDelay, reduced); }
            duration = reduced ? Motion.ReducedFadeMs : plan.DurationMs;
        }
        WallBusy(duration);
        After(duration + 80, SweepTiles);
    }

    private void SweepTiles()
    {
        long now = NowMs;
        bool busy = false;
        foreach (var t in _tiles.Values) { t.Sweep(now); busy |= t.Busy(now); }
        if (busy) After(150, SweepTiles);
    }

    /// <summary>缝内容换层：转到 90° 换成新内容（新宽度），再从另一侧转回来。</summary>
    private void FlipSeam(double width)
    {
        if (Reduced) { _contentWidth = width; _cam.InsertScalar("cw", (float)width); RenderSeamContent(); return; }
        var front = _seamFront ? _seamB : _seamA;
        var back = _seamFront ? _seamA : _seamB;
        _contentWidth = width;
        PaintSeam(back);
        double outMs = Motion.SeamFlip.OutMs, inMs = Motion.SeamFlip.InMs, total = outMs + inMs;
        float p = (float)(outMs / total);
        var rot = G.C.CreateScalarKeyFrameAnimation();
        rot.InsertKeyFrame(0, 0, G.Linear);
        rot.InsertKeyFrame(p, 90, G.Ease(Motion.SeamFlip.OutEase));
        rot.InsertKeyFrame(p + 0.001f, -90, G.Linear);
        rot.InsertKeyFrame(1, 0, G.Ease(Motion.SeamFlip.InEase));
        rot.Duration = Gfx.Ms(total);
        _seamPivot.StartAnimation("RotationAngleInDegrees", rot);
        front.StartAnimation("Opacity", Step(1, 0, p, total));
        back.StartAnimation("Opacity", Step(0, 1, p, total));
        _cam.TryGetScalar("cw", out float cwNow);
        _cam.StartAnimation("cw", Step(cwNow, (float)width, p, total));
        _seamFront = !_seamFront;
    }

    private ScalarKeyFrameAnimation Step(float a, float b, float at, double total)
    {
        var s = G.C.CreateScalarKeyFrameAnimation();
        s.InsertKeyFrame(0, a, G.Linear);
        s.InsertKeyFrame(at, a, G.Linear);
        s.InsertKeyFrame(at + 0.001f, b, G.Linear);
        s.InsertKeyFrame(1, b, G.Linear);
        s.Duration = Gfx.Ms(total);
        return s;
    }

    /// <summary>缝被摇杆推出了屏幕：换层时在视野里重新裂开（此时开口是 0，换锚点不会跳）。</summary>
    private Center ReopenSeamIfGone(double width)
    {
        if (!LibraryRailVisible) return _center;
        Sync();
        var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
        if (!geo.Collapsed && geo.Width > 1) return _center;
        var plan = PlanSeam(_center, width, View);
        _anchorX = plan.AnchorX;
        _cam.InsertScalar("anchor", (float)_anchorX);
        _openWidth = 0;
        _seamTween = null;
        _cam.StopAnimation("open");
        _cam.InsertScalar("open", 0);
        Recull(true);
        return new Center(plan.CenterX, _center.Y);
    }

    private void Push(Slot slot, LibraryItem item)
    {
        var items = (item.Children ?? new()).Select(Lookup).Where(i => i != null).Cast<LibraryItem>().ToList();
        PushLayer(slot, new Layer { Id = item.Id, Title = item.Title, Items = items, Layout = Content.Infinite(items.Count, slot), Home = false });
    }

    /// <summary>进一层（合集、搜索结果）：缝展开成信息条并翻面，墙从 slot 处翻牌换成这一层的内容。</summary>
    private void PushLayer(Slot slot, Layer layer)
    {
        if (_expandedKey != null) Collapse(false);
        if (_searchOpen) { _searchOpen = false; _searchTyping = false; HideSearchBox(); }
        var cur = CurrentLayer;
        Sync();
        cur.Saved = (_center, FocusKey);
        _stack.Add(layer);
        var target = ReopenSeamIfGone(SeamFull);
        var plan = PlanSeam(target, LibrarySeamWidth, View, _anchorX);
        target = new Center(plan.CenterX, target.Y);
        TweenSeam(SeamFull);
        FlipSeam(SeamFull);
        SwapContent(new FlipOrigin.Point(slot.Cx, slot.Cy));
        Focus(slot.Key, false);
        TweenCamera(RevealCenter(slot.Rect, target, View, _anchorX, LibrarySeamWidth));
        RenderEdgeTabs();
    }

    private void Pop()
    {
        if (_expandedKey != null) Collapse(false);
        _stack.RemoveAt(_stack.Count - 1);
        var layer = CurrentLayer;
        var saved = layer.Saved;
        ReopenSeamIfGone(SeamHome);
        TweenSeam(SeamHome);
        FlipSeam(SeamHome);
        SwapContent(new FlipOrigin.Line(_anchorX));
        if (saved is { } s)
        {
            Focus(s.focus, false);
            TweenCamera(s.center);
        }
        RenderEdgeTabs();
    }

    /// <summary>LB / RB：首页换页签，整墙出场 → 入场。</summary>
    public void SwitchTab(int delta) => WithCue(Sfx.Tab, () => { if (_wsOpen) { WsMove(delta < 0 ? Direction.Left : Direction.Right); return; } if (_qsOpen) { Cue(Sfx.None); QsTab(delta); return; } if (_ob != null || LaunchMotionIntercept(false)) { Cue(Sfx.None); return; } SwitchTabCore(delta); });

    private void SwitchTabCore(int delta)
    {
        if (ConsumeScreensaverWake()) return;
        if (_backgroundSettingsOpen) return;
        MarkInput();
        if (_ccOpen && _agOpen) { AgSwitchTab(delta); return; }   // 添加游戏页换图种
        if (_ccOpen && _gpOpen) { GpSwitchTab(delta); return; }   // 手柄页「选择指令」里 LB / RB 换页签
        if (_ccOpen && _stOpen && GsOverlayTab(delta)) return;     // 游戏设置：按键选择换页签 / 封面换图种
        if (_ccOpen && _shOpen) { if (!MorphIntercept()) ShSwitchTab(delta); return; }   // 截图页换游戏
        if (CsActive || (CsWallHome && !_csRailTab && _stack.Count <= 1)) { if (!MorphIntercept()) CsSwitchTab(delta); return; }
        if (_stack.Count > 1 || _ccOpen || MorphIntercept()) { Cue(Sfx.Bump); return; }
        if (_expandedKey != null) Collapse(false);
        if (_searchOpen) CloseSearch();
        _tabIndex = (_tabIndex + delta + _lib.HomeTabs.Count) % _lib.HomeTabs.Count;
        var tab = _lib.HomeTabs[_tabIndex];
        var items = tab.Items();
        Sync();
        var start = SlotNearSeam();
        _stack[0] = new Layer { Id = $"home:{tab.Id}", Title = "游戏库", Items = items, Layout = Content.Infinite(items.Count, start), Home = true };
        RenderSeamContent();
        SwapContent(null);
        Focus(start?.Key, false);
        AfterTabSwitch();
    }

    // ---------------- 缝的内容 ----------------

    private void PaintSeam(SpriteVisual target)
    {
        var layer = CurrentLayer;
        float h = (float)View.Height;
        ChromeArt art;
        if (layer.Home && _searchOpen)
        {
            var (a, lay) = Chrome.SeamSearch(h, _railFocused ? _searchFocus : -1, _searchTyping);
            art = a;
            _searchLayout = lay;
        }
        else art = layer.Home
            ? _rail.Draw(RailModelNow(), h, _railFocused ? _railFocus : -1)
            : Chrome.SeamFull(layer.Title, layer.Crumbs, layer.Items.Count, layer.Items.Sum(i => i.PlaytimeH ?? 0), h, _railFocused);
        var brush = (CompositionSurfaceBrush)target.Brush;
        (brush.Surface as IDisposable)?.Dispose();
        brush.Surface = art.Surface;
        target.Size = art.Size;
        // 内容在缝里水平居中（left: 50%; translate: -50%），以转轴（缝内容中心）为原点
        target.Offset = new Vector3(-art.Size.X / 2, -art.Size.Y / 2, 0);
        UpdateSeamLights();
    }

    private void RenderSeamContent()
    {
        var front = _seamFront ? _seamB : _seamA;
        if (CurrentLayer.Home && !_searchOpen && _seamTween == null) { _contentWidth = SeamHome; _cam.StopAnimation("cw"); _cam.InsertScalar("cw", (float)SeamHome); }
        var t = NowMs;
        PaintSeam(front);
        var t2 = NowMs;
        // 边缘标签只在缝被挤出屏幕时出现：启动时放到第一帧之后再画
        if (FirstFrameLogged) RenderEdgeTabs();
        if (!FirstFrameLogged) Log.Info($"seam paint {t2 - t}ms");
    }

    private void RenderEdgeTabs()
    {
        if (_stack.Count == 0) return;
        var text = CurrentLayer.Home ? $"游戏库 · {_lib.HomeTabs[_tabIndex].Label}" : CurrentLayer.Title;
        foreach (var (v, left) in new[] { (_edgeLeft, true), (_edgeRight, false) })
        {
            var art = Chrome.EdgeTab(text, left, (float)View.Height * 0.6f);
            var brush = (CompositionSurfaceBrush)v.Brush;
            (brush.Surface as IDisposable)?.Dispose();
            brush.Surface = art.Surface;
            v.Size = art.Size;
            v.Offset = new Vector3(left ? 0 : (float)View.Width - art.Size.X, (float)View.Height / 2 - art.Size.Y / 2, 0);
        }
    }

    // ---------------- 其他 ----------------

    public void Toast(string text)
    {
        if (_cueDepth == 0) SoundFx.Play(Sfx.Toast);   // 输入引起的提示已经有那次输入的声音
        _ = ShowGlobalNotice(text);
        Log.Info($"toast: {text}");
    }

    private async Task ShowGlobalNotice(string text)
    {
        string kind = NoticeKind(text);
        try
        {
            _agent ??= new AgentClient();
            await _agent.Call("show_notice", JsonValue.Create(text), JsonValue.Create(kind));
        }
        catch (Exception ex)
        {
            Log.Warn($"global notice: {ex.Message}");
            // The same renderer remains available while the agent is restarting.
            try
            {
                string app = Path.Combine(BackendSetup.Home, "app");
                string current = Path.Combine(BackendSetup.Home, "current.json");
                if (File.Exists(current) && JsonNode.Parse(File.ReadAllText(current))?["agent"]?.GetValue<string>() is string script)
                    app = Path.GetDirectoryName(script) ?? app;
                string version = JsonNode.Parse(File.ReadAllText(Path.Combine(app, "plugin.json")))?["version"]?.GetValue<string>() ?? "";
                if (version.Length == 0 || version.Any(c => !char.IsAsciiDigit(c) && c != '.')) return;
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(app, "bin", $"ClawInput-{version}.exe")) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--notice");
                start.ArgumentList.Add(new JsonObject { ["kind"] = kind, ["title"] = text.Length > 300 ? text[..300] : text, ["detail"] = "" }.ToJsonString());
                System.Diagnostics.Process.Start(start)?.Dispose();
            }
            catch (Exception fallback) { Log.Warn($"notice renderer: {fallback.Message}"); }
        }
    }

    private static string NoticeKind(string text)
    {
        bool Has(params string[] words) => words.Any(text.Contains);
        if (Has("失败", "错误", "没能", "没有保存", "没有写进", "连不上", "无法", "不支持", "未连接")) return "error";
        if (Has("请先", "至少", "最多", "不能", "再按一次", "请稍", "需要", "未安装", "没有找到")) return "warning";
        if (Has("更新", "下载", "安装")) return "update";
        if (Has("账户", "连接 Steam", "连接 Xbox", "密钥", "API Key", "绑定", "登录")) return "account";
        if (Has("封面", "截图", "图片")) return "image";
        if (Has("音量", "声音", "麦克风", "静音")) return "sound";
        if (Has("游戏库", "识别", "文件夹", "集合", "列表")) return "library";
        if (Has("功耗", "电源", "充电", "电池", "休眠", "睡眠")) return "power";
        if (Has("桌面模式")) return "desktop";
        if (Has("手柄", "摇杆", "背键", "扳机", "映射", "震动")) return "gamepad";
        if (Has("游戏", "启动")) return "game";
        if (Has("设置", "恢复", "动态", "还原", "后台")) return "settings";
        if (Has("已", "完成", "成功")) return "success";
        return "info";
    }

    public void ToggleReducedMotion()
    {
        _reducedLocal = !_reducedLocal;
        _prefs = _prefs with { Reduced = _reducedLocal }; _prefs.Save();
        Toast(_reducedLocal ? "已降低动态效果" : "已恢复完整动效");
    }

    /// <summary>鼠标：点卡 = 聚焦并激活。</summary>
    public void ClickAt(double sx, double sy)
    {
        if (!_wsOpen && NowMs < _wsLibraryConfirmUntil) return;
        if (ConsumeScreensaverWake()) return;
        MarkInput();
        if (_wsOpen) { WsClick(sx, sy); return; }
        if (_qsOpen) { QsClick(sx, sy); return; }   // 快设面板开着：只有面板能点，左上角的触摸导航和墙都点不到
        if (TouchNavigationClick(sx, sy)) return;
        if (_ob != null) { ObClick(sx, sy); return; }
        // 启动 / 正在游戏画面开着：点它自己的按钮，别的地方吞掉（以前会点穿到下面的墙，点到这款游戏的卡就反复「继续游戏」）
        if (_lm is { Leaving: false }) { LaunchMotionClick(sx, sy); return; }
        if (_backgroundSettingsOpen) { MarkInput(); RailClick(sx, sy); return; }
        if (MorphIntercept()) return;
        if (_ccOpen) { CcClick(sx, sy); return; }
        if (CsActive && CsClick(sx, sy)) return;
        if (CsWallHome && CsHeaderClick(sx, sy)) return;
        if (RailClick(sx, sy)) return;
        Sync();
        var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
        var off = SplitOffsets(geo.Width, View.Scale);
        bool left = sx < geo.ScreenX;
        double baseX = View.LineX - _center.X * View.Scale + (left ? off.Left : off.Right);
        double wx = (sx - baseX) / View.Scale, wy = (sy - (View.Height / 2 - _center.Y * View.Scale)) / View.Scale;
        long now = NowMs;
        Tile? hit = null;
        foreach (var t in _tiles.Values)
            if (t.CurrentRect(now).Contains(wx, wy) && (hit == null || t.Expanded || (t.Focused && !hit.Expanded))) hit = t;
        if (hit == null || !HasContent(hit.Slot)) return;
        if (hit.HitsEndGame(wx, wy) && RunningGame(hit.Shown) is { } running)
        { _ = ForceQuitAsync(running); return; }
        var key = hit.Slot.Key;
        bool fromRail = _railFocused;
        if (fromRail)
        {
            // A card hit transfers input ownership before Activate dispatches the action.
            _railFocused = false;
            _railFocus = -1;
            RepaintRail();
        }
        if (FocusKey != key)
        {
            if (_expandedKey is { } ek && ek != key) Collapse(false);
            Focus(key);
        }
        else if (fromRail) Focus(key);
        Activate();
    }

    // ---------------- 性能读数 ----------------

    private readonly double[] _frames = new double[60];
    private int _frameCount, _frameIdx;
    private long _hudNext;
    private Dwm.Sample _dwmLast;

    public void ToggleHud()
    {
        _hudOn = !_hudOn;
        _hud.IsVisible = _hudOn;
        _frameCount = 0;
        _dwmLast = Dwm.Read();
        if (_hudOn) EnsureLoop();
    }

    private void HudSample(double dt)
    {
        _frames[_frameIdx] = dt; _frameIdx = (_frameIdx + 1) % _frames.Length; _frameCount = Math.Min(_frameCount + 1, _frames.Length);
        long now = NowMs;
        if (now < _hudNext || _frameCount == 0) return;
        _hudNext = now + 500;
        double sum = 0, max = 0;
        for (int i = 0; i < _frameCount; i++) { sum += _frames[i]; max = Math.Max(max, _frames[i]); }
        double avg = sum / _frameCount;
        var d = Dwm.Read();
        var (fps, missed) = Dwm.Rate(_dwmLast, d);
        _dwmLast = d;
        var art = Chrome.Hud($"帧 {avg:F1} ms · 最长 {max:F1} ms · {Math.Round(1000 / avg)} fps · DWM {fps:F0} fps 掉帧 {missed} · 卡片 {TileCount} · 图 {Images.Count}");
        var brush = (CompositionSurfaceBrush)_hud.Brush;
        (brush.Surface as IDisposable)?.Dispose();
        brush.Surface = art.Surface;
        _hud.Size = art.Size;
        _hud.Offset = new Vector3(16, (float)View.Height - 14 - art.Size.Y, 0);
    }

    // ---------------- 输入：键盘、鼠标 ----------------

    private void HookInput()
    {
        _rootEl.KeyDown += (_, e) =>
        {
            try { OnKey(e); } catch (Exception ex) { Log.Error("key", ex); }
        };
        _rootEl.KeyUp += (_, e) => { if (_screensaverWakeKey == e.Key) { _screensaverWakeKey = null; e.Handled = true; } };
        _rootEl.PointerPressed += OnPointerPressed;
        _rootEl.PointerMoved += OnPointerMoved;
        _rootEl.PointerReleased += OnPointerReleased;
        _rootEl.PointerCanceled += OnPointerCanceled;
        _rootEl.PointerCaptureLost += OnPointerCanceled;
        CompositionTarget.Rendering += TouchNavigationFrame;
        _rootEl.PointerWheelChanged += (_, e) =>
        {
            if (ConsumeScreensaverWake()) { e.Handled = true; return; }
            MarkInput();
            var p = e.GetCurrentPoint(_rootEl).Properties;
            if (_qsOpen) { e.Handled = true; return; }   // 快设面板开着：滚轮不滚下面的墙
            if (SettingsPageOpen && !_agOpen && !_shOpen && !_wsOpen && !GsArtMode)
            {
                if (!p.IsHorizontalMouseWheel) SettingsWheel(p.MouseWheelDelta);
                e.Handled = true;
                return;
            }
            if (_ccOpen)
            {
                CcWheel(p.MouseWheelDelta, p.IsHorizontalMouseWheel);
                e.Handled = true;
                return;
            }
            if (_railModel.SettingsOpen && _railFocused && !p.IsHorizontalMouseWheel)
            {
                Move(p.MouseWheelDelta > 0 ? Direction.Up : Direction.Down);
                e.Handled = true;
                return;
            }
            // 网页滚轮一格约 100 px
            double d = p.MouseWheelDelta / 120.0 * 100;
            if (p.IsHorizontalMouseWheel) PanBy(d, 0); else PanBy(0, d);
            e.Handled = true;
        };
    }

    private void OnKey(KeyRoutedEventArgs e)
    {
        if (_screensaverWakeKey != null) { e.Handled = true; return; }
        if (ConsumeScreensaverWake()) { _screensaverWakeKey = e.Key; e.Handled = true; return; }
        MarkInput();
        if (!_wsOpen && !_qsOpen && e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox) return;   // 搜索框里打字不当快捷键
        bool repeat = e.KeyStatus.RepeatCount > 1 || e.KeyStatus.WasKeyDown;
        switch (e.Key)
        {
            case VirtualKey.Up: Move(Direction.Up); break;
            case VirtualKey.Down: Move(Direction.Down); break;
            case VirtualKey.Left: Move(Direction.Left); break;
            case VirtualKey.Right: Move(Direction.Right); break;
            default:
                if (repeat) return;
                switch (e.Key)
                {
                    case VirtualKey.Enter: case VirtualKey.Space: Activate(); break;
                    case VirtualKey.Escape: case VirtualKey.Back: Back(); break;
                    case VirtualKey.M: case VirtualKey.Application: ToggleControlCenter(); break;
                    case VirtualKey.Y: PadY(); break;
                    case VirtualKey.Q: case VirtualKey.PageUp: SwitchTab(-1); break;
                    case VirtualKey.E: case VirtualKey.PageDown: SwitchTab(1); break;
                    case VirtualKey.F: ToggleHud(); break;
                    case VirtualKey.V: PadView(); break;
                    case VirtualKey.X: PadX(); break;
                    case VirtualKey.R: ToggleReducedMotion(); break;
            case VirtualKey.L: ToggleLights(); break;
                    default: return;
                }
                break;
        }
        e.Handled = true;
    }

    private (double x, double y, ulong t, double vx, double vy, double moved, uint id)? _drag;
    /// <summary>这次拖动从哪按下的（滑动看总位移）。</summary>
    private (double x, double y) _dragFrom;
    /// <summary>手指从屏幕左边 64 DIP 以内往右划 = 返回（控制中心各页，和 B 一样）。贴着边框划会被 Windows 拿去开小组件，所以区域放宽一些。</summary>
    private (double x, double y, uint id)? _edge;
    private TouchSurface _edgeSurface;
    private const double EdgeBackPx = 64, EdgeBackMin = 80;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_rootEl);
        if (PointerEditor(e.OriginalSource)) return;
        if (_pagePointer.Active || _settingsGesture.Active || _edge != null) { e.Handled = true; return; }
        if ((!pt.IsInContact && !pt.Properties.IsLeftButtonPressed) || pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed) return;
        if (ConsumeScreensaverWake())
        {
            _screensaverWakePointers.Add(pt.PointerId);
            _rootEl.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        MarkInput();
        if (!_wsOpen && _ob == null && !_qsOpen && _ccOpen && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch && pt.Position.X <= EdgeBackPx && !TouchNavigationHit(pt.Position.X, pt.Position.Y))
        {
            MarkInput();
            _edge = (pt.Position.X, pt.Position.Y, pt.PointerId);
            _edgeSurface = PointerSurface;
            if (!_rootEl.CapturePointer(e.Pointer)) _edge = null;
            e.Handled = true;
            return;
        }
        // 添加游戏页叠在设置页上面：它的点按、拖动走下面通用那条（AgClick），不能让设置页的手势接走
        if (!_wsOpen && _ob == null && !_qsOpen && _lm is not { Leaving: false } && UseSettingsPointer(pt.Position.X, pt.Position.Y))
        {
            e.Handled = true;
            if ((!pt.IsInContact && !pt.Properties.IsLeftButtonPressed) || pt.Properties.IsRightButtonPressed || pt.Properties.IsMiddleButtonPressed) return;
            MarkInput();
            if (SettingsPointerBegin(pt.PointerId, pt.Position.X, pt.Position.Y))
            {
                if (!_rootEl.CapturePointer(e.Pointer)) { SettingsPointerCancel(pt.PointerId); return; }
                _rootEl.Focus(FocusState.Pointer);
            }
            return;
        }
        if (!_pagePointer.Begin(pt.PointerId, pt.Position.X, pt.Position.Y, PointerSurface)) return;
        var quickSlider = _qsOpen ? QsDirectSliderAt(pt.Position.X / QsK, pt.Position.Y / QsK) : null;
        _quickTouchId = quickSlider ?? (_qsOpen ? QsTouchItemAt(pt.Position.X, pt.Position.Y)?.Id : null);
        _shFilterDragRemainder = 0;
        _drag = (pt.Position.X, pt.Position.Y, pt.Timestamp, 0, 0, 0, pt.PointerId);
        _dragFrom = (pt.Position.X, pt.Position.Y);
        CsDragEnd();
        _glide = Vector2.Zero;
        if (!_rootEl.CapturePointer(e.Pointer)) { _pagePointer.Reset(); _drag = null; return; }
        if (quickSlider != null) { QsSetFocus(quickSlider); QsTouchDrag(quickSlider, pt.Position.X); }
        _rootEl.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(_rootEl);
        bool pointerMoved = _lastPointerPosition is not { } last ||
            (Math.Abs(current.Position.X - last.x) + Math.Abs(current.Position.Y - last.y) > 2);
        _lastPointerPosition = (current.Position.X, current.Position.Y);
        if (_screensaverWakePointers.Contains(current.PointerId)) { e.Handled = true; return; }
        if (pointerMoved)
        {
            if (ConsumeScreensaverWake())
            {
                if (current.IsInContact || current.Properties.IsLeftButtonPressed) _screensaverWakePointers.Add(current.PointerId);
                e.Handled = true; return;
            }
            MarkInput();
        }
        if (_ob != null) ObHover(current.Position.X, current.Position.Y);
        if (_lm is { Leaving: false }) LaunchMotionHover(current.Position.X, current.Position.Y);
        if (_edge?.id == e.Pointer.PointerId) { e.Handled = true; return; }
        if (_settingsGesture.Owns(e.Pointer.PointerId))
        {
            var point = e.GetCurrentPoint(_rootEl);
            SettingsPointerMove(point.PointerId, point.Position.X, point.Position.Y);
            e.Handled = true;
            return;
        }
        if (_drag is not { } d) return;
        var pt = e.GetCurrentPoint(_rootEl);
        if (pt.PointerId != d.id) return;
        var intent = _pagePointer.Move(pt.PointerId, pt.Position.X, pt.Position.Y, PointerSurface);
        if (intent is TouchResult.Canceled or TouchResult.None) { e.Handled = true; return; }
        double dx = pt.Position.X - d.x, dy = pt.Position.Y - d.y;
        double dt = Math.Max(1, (pt.Timestamp - d.t) / 1000.0);
        double moved = Math.Max(d.moved, Math.Max(Math.Abs(pt.Position.X - _dragFrom.x), Math.Abs(pt.Position.Y - _dragFrom.y)));
        _drag = (pt.Position.X, pt.Position.Y, pt.Timestamp, dx / dt * 1000, dy / dt * 1000, moved, d.id);
        if (_qsOpen && QsDirectSliderAt(_dragFrom.x / QsK, _dragFrom.y / QsK) == _quickTouchId && _quickTouchId != null)
        {
            QsTouchDrag(_quickTouchId, pt.Position.X);
            e.Handled = true;
            return;
        }
        if (intent != TouchResult.Tap)
        {
            if (_wsOpen) { }
            else if (_qsOpen) { if (intent == TouchResult.Horizontal && _quickTouchId == "tdp") QsTouchDrag(_quickTouchId, pt.Position.X); }
            else if (_ob != null || _lm is { Leaving: false } || _searchOpen || _backgroundSettingsOpen || TouchNavigationHit(_dragFrom.x, _dragFrom.y)) { }
            else if (_ccOpen) { if (intent == TouchResult.Vertical) CcDrag(dx, dy); }
            else PanBy(dx, dy);
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_screensaverWakePointers.Remove(e.Pointer.PointerId))
        {
            _rootEl.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_edge is { } edge && edge.id == e.Pointer.PointerId)
        {
            var at = e.GetCurrentPoint(_rootEl).Position;
            _edge = null;
            _rootEl.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            if (PointerSurface != _edgeSurface) return;
            double ex = at.X - edge.x, ey = at.Y - edge.y;
            if (ex >= EdgeBackMin && Math.Abs(ey) < ex) Back();
            else if (Math.Abs(ex) + Math.Abs(ey) <= 8) ClickAt(at.X, at.Y);
            return;
        }
        if (_settingsGesture.Owns(e.Pointer.PointerId))
        {
            var point = e.GetCurrentPoint(_rootEl);
            SettingsPointerEnd(point.PointerId, point.Position.X, point.Position.Y);
            _rootEl.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_drag is not { } d) return;
        var pt = e.GetCurrentPoint(_rootEl);
        if (pt.PointerId != d.id) return;
        var result = _pagePointer.End(pt.PointerId, pt.Position.X, pt.Position.Y, PointerSurface);
        _drag = null;
        _rootEl.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (result is TouchResult.None or TouchResult.Canceled) return;
        if (_wsOpen)
        {
            if (result == TouchResult.Tap) WsClick(pt.Position.X, pt.Position.Y);
            else if (result == TouchResult.Horizontal) WsMove(pt.Position.X < _dragFrom.x ? Direction.Right : Direction.Left);
            return;
        }
        if (_qsOpen)
        {
            if (_quickTouchId != null && QsDirectSliderAt(_dragFrom.x / QsK, _dragFrom.y / QsK) == _quickTouchId)
                QsTouchDrag(_quickTouchId, pt.Position.X);
            else if (result == TouchResult.Tap && _quickTouchId == QsTouchItemAt(pt.Position.X, pt.Position.Y)?.Id) ClickAt(pt.Position.X, pt.Position.Y);
            return;
        }
        if (result == TouchResult.Tap) { ClickAt(pt.Position.X, pt.Position.Y); return; }
        if (_ob != null || _lm is { Leaving: false } || _searchOpen || _backgroundSettingsOpen || TouchNavigationHit(_dragFrom.x, _dragFrom.y)) return;
        if (_ccOpen) { if (result == TouchResult.Horizontal) CcSwipe(pt.Position.X - _dragFrom.x, pt.Position.Y - _dragFrom.y); return; }
        if (CsActive) { CsDragEnd(); return; }
        if ((pt.Timestamp - d.t) / 1000.0 > 80) return;
        // 惯性：滑行距离 = 松手速度 × 0.3（power），按时间常数 280ms 指数衰减；速度上限 4000 px/s
        double speed = Math.Sqrt(d.vx * d.vx + d.vy * d.vy);
        if (speed < 40 || _ccOpen) return;
        double k = Math.Min(speed, 4000) / speed * (0.3 / 0.28);
        // 注意方向：拖动是「把墙往手指方向推」，相机反向
        _glide = new Vector2((float)(-d.vx * k), (float)(-d.vy * k));
        EnsureLoop();
    }

    // ---------------- 计时 ----------------

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _screensaverWakePointers.Remove(e.Pointer.PointerId);
        _pagePointer.Cancel(e.Pointer.PointerId);
        SettingsPointerCancel(e.Pointer.PointerId);
        if (_drag?.id == e.Pointer.PointerId) _drag = null;
        if (_edge?.id == e.Pointer.PointerId) _edge = null;
    }

    public DispatcherQueueTimer After(double ms, Action action)
    {
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms));
        t.IsRepeating = false;
        // 计时器没人引用会被回收、到点也不响：响之前一直挂在这里
        _pendingTimers.Add(t);
        t.Tick += (s, _) =>
        {
            s.Stop();
            _pendingTimers.Remove(s);
            try { action(); } catch (Exception ex) { Log.Error("timer", ex); }
        };
        t.Start();
        return t;
    }

    private readonly HashSet<DispatcherQueueTimer> _pendingTimers = new();

    public void Dispose()
    {
        CompositionTarget.Rendering -= TouchNavigationFrame;
        _pagePointer.Reset();
        DisposeStartupReadiness();
        DisposeShortcutMotion();
        CloseSpecialSurfaces();
        foreach (var session in _networkBtSessions.Values) session.Dispose();
        _networkBtSessions.Clear();
        _servicesDisposed = true;
        _connectCancel?.Cancel();
        _steamCloudLifetime.Cancel();
        StopScreensaver();
        foreach (var timer in new[] { _ccTimer, _stTimer, _perfTimer, _dsTimer, _syTimer, _gpLive, _railTimer, _morphTimer }) timer?.Stop();
        _trayTimers.Clear();
        // 先取消帧回调，再释放背景资源；显式退出不能留下轮询去访问已销毁的窗口。
        if (_backdropLoop) { CompositionTarget.Rendering -= OnBackdropFrame; _backdropLoop = false; }
        if (_ccBackLoop) { CompositionTarget.Rendering -= OnCcBackFrame; _ccBackLoop = false; }
        if (_loopOn) { CompositionTarget.Rendering -= OnRendering; _loopOn = false; }
        DisposeHomeAudio();
        _pad?.Dispose();
        _backdrop?.Dispose();
        _agent?.Dispose();
        _cpuFreq?.Dispose();
        _ccBack?.Dispose();
        foreach (var timer in _pendingTimers) timer.Stop();
        _pendingTimers.Clear();

        // The prewarmed QAM is a separate WinUI window. Release it on real exit so
        // the process cannot keep receiving activations after the main window closes.
        var quickPanel = _qsWin;
        _qsWin = null;
        quickPanel?.Win.Close();
    }

    // 给自动测试用
    internal bool FocusFirstCollection()
    {
        var vis = Visible();
        var pick = Nav.NearestTo(SlotsIn(vis).Where(sl => ItemFor(sl) is { } id && Lookup(id)?.Kind == ItemKind.Collection && vis.Left < sl.Cx && sl.Cx < vis.Right && vis.Top < sl.Cy && sl.Cy < vis.Bottom), _center.X, _center.Y);
        if (pick == null) return false;
        Focus(pick.Key);
        return true;
    }

    internal int TabIndex => _tabIndex;
    internal Window HostWindow => _window;
    internal Input.GamepadPoller? Pad => _pad;
    internal int Depth => _stack.Count;
    internal SlotKey? ExpandedKey => _expandedKey;
}
