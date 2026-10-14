// 舞台里与竖条（首页窄缝）有关的部分：焦点在墙与竖条之间走、竖条各项的动作、设置、搜索、时钟与电池刷新、动态背景。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static ClawLibrary.Engine.Geometry;
using static ClawLibrary.Engine.ViewMath;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private Rail _rail = null!;
    private readonly RailModel _railModel = new();
    /// <summary>焦点在竖条里（_railFocus 是竖条里的第几项）；墙上的 FocusKey 保留，出来时回到它附近。</summary>
    private bool _railFocused;
    private int _railFocus = -1;
    private DispatcherQueueTimer? _railTimer;
    private Backdrop _backdrop = null!;
    private bool _backdropOn = true;
    private bool _windowActive = true;
    /// <summary>多久没操作就把动态背景停下（省电；有操作立刻恢复）。</summary>
    private const long BackdropIdleMs = 60_000;
    private long _lastInput;

    /// <summary>有输入：记时间，背景停着就让它动起来。</summary>
    public void MarkInput()
    {
        bool wasIdle = NowMs - _lastInput >= BackdropIdleMs;
        _lastInput = NowMs;
        if (wasIdle) UpdateBackdropRunning();
    }
    private TextBox? _searchBox;
    private string _searchQuery = "";

    // ---------------- 初始化 ----------------

    private void InitRail()
    {
        _rail = new Rail(G) { Dpi = Chrome.Dpi };
        _railModel.User = SystemInfo.UserName();
        _railModel.Battery = SystemInfo.Battery();
        _railModel.Now = DateTime.Now;
        if (Program.Args.Contains("--no-backdrop")) _backdropOn = false;
    }

    private void StartRailTimers()
    {
        // 时钟按分钟、电池按 10 秒检查一次，变了才重画竖条（一张 120 px 宽的图，约 1–2 ms）
        _railTimer = _dq.CreateTimer();
        _railTimer.Interval = TimeSpan.FromSeconds(10);
        _railTimer.IsRepeating = true;
        _railTimer.Tick += (_, _) =>
        {
            try
            {
                var now = DateTime.Now;
                var bat = SystemInfo.Battery();
                UpdateBackdropRunning();
                if (now.Minute != _railModel.Now.Minute || now.Hour != _railModel.Now.Hour || bat != _railModel.Battery)
                {
                    _railModel.Now = now;
                    _railModel.Battery = bat;
                    RepaintRail();
                }
            }
            catch (Exception ex) { Log.Error("rail timer", ex); }
        };
        _railTimer.Start();
        // 头像后台解码，好了再画
        var path = SystemInfo.AvatarPath();
        if (path != null)
            _ = Task.Run(async () =>
            {
                try
                {
                    var bmp = await CanvasBitmap.LoadAsync(G.Device, path);
                    _dq.TryEnqueue(() => { _railModel.Avatar = bmp; RepaintRail(); });
                }
                catch (Exception ex) { Log.Warn($"avatar load: {ex.Message}"); }
            });
        _window.Activated += (_, e) =>
        {
            _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
            UpdateBackdropRunning();
        };
    }

    /// <summary>竖条要画的东西（页签、合集入口、设置项）。</summary>
    private RailModel RailModelNow()
    {
        var m = _railModel;
        m.Home = true;
        m.Tabs = _lib.HomeTabs.Select(t => t.Label).ToList();
        m.ActiveTab = _tabIndex;
        m.Shortcuts = new();   // 竖条里只放平台页签；Steam 的分类铺在 Steam 页签的墙上
        m.SearchOpen = _searchBox != null && _searchOpen;
        m.Query = _searchQuery;
        m.Settings = new()
        {
            ("标题样式", Style == TitleStyle.B ? "极简黑体" : "电影字幕"),
            ("动态背景", _backdropOn ? "开" : "关"),
            ("降低动态效果", _reducedLocal ? "开" : "关"),
            ("性能读数", _hudOn ? "开" : "关"),
            ("完成", "返回竖条"),
        };
        return m;
    }

    private bool _searchOpen;

    /// <summary>只重画竖条（不翻转）。</summary>
    private void RepaintRail()
    {
        if (_stack.Count == 0) return;
        var front = _seamFront ? _seamB : _seamA;
        PaintSeam(front);
        PlaceSearchBox();
    }

    // ---------------- 焦点：墙 ⇄ 竖条 ----------------

    private bool SeamUsable()
    {
        Sync();
        var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
        return !geo.Collapsed && geo.Width > _contentWidth * 0.6;
    }

    /// <summary>屏幕纵坐标 → 竖条里最近的一项，进竖条。</summary>
    private void EnterRail(double screenY, int? item = null)
    {
        if (_expandedKey != null) Collapse(false);
        _railFocused = true;
        if (FocusKey is { } fk && _tiles.TryGetValue(fk, out var t)) t.SetFocused(false);
        RepaintRail();   // 先排一遍拿到各项的位置
        _railFocus = item ?? (CurrentLayer.Home ? _rail.NearestTo((float)screenY) : 0);
        if (_railFocus < 0) _railFocus = CurrentLayer.Home ? _rail.DefaultFocus(_railModel) : 0;
        RepaintRail();
    }

    /// <summary>出竖条：回到墙上那一侧离这一项最近的卡。</summary>
    private void ExitRail(bool toLeft)
    {
        float y = 0;
        if (CurrentLayer.Home && _railFocus >= 0 && _railFocus < _rail.Items.Count)
        {
            var b = _rail.Items[_railFocus].Bounds;
            y = (float)(b.Y + b.Height / 2);
        }
        else y = (float)View.Height / 2;
        _railFocused = false;
        _railFocus = -1;
        RepaintRail();
        Sync();
        int anchorCol = BoundaryColumn(_anchorX);
        double wy = _center.Y + (y - View.Height / 2) / View.Scale;
        var vis = Visible();
        var side = SlotsIn(vis).Where(s => HasContent(s) && (toLeft ? s.Column < anchorCol : s.Column >= anchorCol)).ToList();
        var pick = Nav.NearestTo(side, _anchorX + (toLeft ? -200 : 200), wy);
        if (pick == null) { Focus(FocusKey); return; }
        Focus(pick.Key);
    }

    /// <summary>十字键在竖条里：走一步；横着走出边界就回到墙上。</summary>
    private bool RailMove(Direction dir)
    {
        if (!_railFocused) return false;
        if (!CurrentLayer.Home)
        {
            if (dir is Direction.Left or Direction.Right) ExitRail(dir == Direction.Left);
            return true;
        }
        int next = _rail.Step(_railFocus, dir);
        if (next == -1 || next == -2) { ExitRail(next == -1); return true; }
        if (next != _railFocus) { _railFocus = next; RepaintRail(); }
        return true;
    }

    /// <summary>墙上横着走、下一张跨过了缝（或这边已经没有卡）：进竖条。</summary>
    private bool TryEnterRailFrom(Slot from, Slot? next, Direction dir)
    {
        if (dir is not (Direction.Left or Direction.Right)) return false;
        int anchorCol = BoundaryColumn(_anchorX);
        int fromSide = from.Column < anchorCol ? 0 : 1;
        bool towardSeam = (fromSide == 1 && dir == Direction.Left) || (fromSide == 0 && dir == Direction.Right);
        if (!towardSeam) return false;
        if (next != null && (next.Column < anchorCol ? 0 : 1) == fromSide) return false;
        if (!SeamUsable()) return false;
        Sync();
        var r = Drawn(from);
        double screenY = View.Height / 2 + (r.Y + r.Height / 2 - _center.Y) * View.Scale;
        EnterRail(screenY);
        return true;
    }

    // ---------------- 竖条里的动作 ----------------

    private bool RailActivate()
    {
        if (!_railFocused) return false;
        if (!CurrentLayer.Home) { Pop(); return true; }
        if (_railFocus < 0 || _railFocus >= _rail.Items.Count) return true;
        var it = _rail.Items[_railFocus];
        switch (it.Kind)
        {
            case RailKind.Tab:
                if (it.Index != _tabIndex) SwitchTab(it.Index - _tabIndex);
                break;
            case RailKind.Shortcut:
                if (it.Index < _lib.Collections.Count) OpenCollection(_lib.Collections[it.Index]);
                break;
            case RailKind.Account:
                SystemInfo.Open("ms-settings:yourinfo");
                break;
            case RailKind.Explorer:
                SystemInfo.Open("explorer.exe");
                break;
            case RailKind.Search:
                if (_searchOpen) CloseSearch(); else OpenSearch();
                break;
            case RailKind.SearchField:
                _searchBox?.Focus(FocusState.Programmatic);
                break;
            case RailKind.Settings:
                _railModel.SettingsOpen = !_railModel.SettingsOpen;
                RepaintRail();
                _railFocus = _railModel.SettingsOpen ? _rail.DefaultFocus(_railModel) : _rail.Items.FindIndex(i => i.Kind == RailKind.Settings);
                RepaintRail();
                break;
            case RailKind.Setting:
                ToggleSetting(it.Index);
                break;
        }
        return true;
    }

    /// <summary>B：先关设置 / 搜索，再回到墙上。</summary>
    private bool RailBack()
    {
        if (_railModel.SettingsOpen)
        {
            _railModel.SettingsOpen = false;
            RepaintRail();
            if (_railFocused) { _railFocus = _rail.Items.FindIndex(i => i.Kind == RailKind.Settings); RepaintRail(); }
            return true;
        }
        if (_searchOpen) { CloseSearch(); return true; }
        if (_railFocused)
        {
            if (!CurrentLayer.Home) { Pop(); return true; }
            ExitRail(false);
            return true;
        }
        return false;
    }

    private void ToggleSetting(int i)
    {
        switch (i)
        {
            case 0: ToggleTitleStyle(); break;
            case 1: _backdropOn = !_backdropOn; UpdateBackdropRunning(); Toast(_backdropOn ? "动态背景：开" : "动态背景：关"); break;
            case 2: ToggleReducedMotion(); UpdateBackdropRunning(); break;
            case 3: ToggleHud(); break;
            case 4:
                _railModel.SettingsOpen = false;
                RepaintRail();
                _railFocus = _rail.Items.FindIndex(it => it.Kind == RailKind.Settings);
                break;
        }
        RepaintRail();
    }

    /// <summary>从竖条打开一个合集：起点取缝右边离缝最近的那张卡（与网页版换层同一套翻牌）。</summary>
    private void OpenCollection(LibraryItem item)
    {
        Sync();
        var start = SlotNearSeam();
        if (start == null) return;
        _railFocused = false;
        _railFocus = -1;
        Push(start, item);
        // 进了合集，焦点留在竖条的「返回」上
        _railFocused = true;
        _railFocus = 0;
        if (FocusKey is { } fk && _tiles.TryGetValue(fk, out var t)) t.SetFocused(false);
    }

    /// <summary>换页签之后：焦点在竖条里就跟着落在新的页签上。</summary>
    private void AfterTabSwitch()
    {
        if (!_railFocused) return;
        if (FocusKey is { } fk && _tiles.TryGetValue(fk, out var t)) t.SetFocused(false);
        RepaintRail();
        int i = _rail.Items.FindIndex(it => it.Kind == RailKind.Tab && it.Index == _tabIndex);
        if (i >= 0) { _railFocus = i; RepaintRail(); }
    }

    /// <summary>鼠标点在竖条上：聚焦那一项并执行。</summary>
    private bool RailClick(double sx, double sy)
    {
        Sync();
        var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
        if (geo.Width < 2 || sx < geo.ScreenX - geo.Width / 2 || sx > geo.ScreenX + geo.Width / 2) return false;
        if (!CurrentLayer.Home)
        {
            if (sy < 80 && sx < geo.ScreenX - geo.Width / 2 + 80) Pop();
            return true;
        }
        float lx = (float)(sx - (geo.ScreenX - Rail.HomeWidth / 2));
        int hit = _rail.HitTest(lx, (float)sy);
        if (hit < 0) return true;
        if (!_railFocused) EnterRail(sy, hit); else { _railFocus = hit; RepaintRail(); }
        RailActivate();
        return true;
    }

    // ---------------- 搜索 ----------------

    private void OpenSearch()
    {
        try
        {
            if (_searchBox == null)
            {
                _searchBox = new TextBox
                {
                    PlaceholderText = "输入游戏名",
                    FontSize = 12,
                    Width = Rail.HomeWidth - 28,
                    Height = 32,
                    MinHeight = 0,
                    Padding = new Thickness(8, 6, 4, 4),
                    BorderThickness = new Thickness(0),
                    RequestedTheme = ElementTheme.Dark,
                    IsSpellCheckEnabled = false,
                    IsTextPredictionEnabled = false,
                };
                var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                var ink = new SolidColorBrush(Theme.Ink);
                foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
                    _searchBox.Resources[key] = clear;
                foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
                    _searchBox.Resources[key] = ink;
                _searchBox.TextChanged += (_, _) => { _searchQuery = _searchBox.Text.Trim(); ApplySearch(); };
                _searchBox.KeyDown += (_, e) =>
                {
                    if (e.Key is Windows.System.VirtualKey.Escape) { CloseSearch(); e.Handled = true; }
                    else if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Down)
                    {
                        // 回到竖条（焦点留在搜索框那一项），可以直接往右走进结果
                        _rootEl.Focus(FocusState.Programmatic);
                        e.Handled = true;
                    }
                };
                _rootEl.Overlay.Children.Add(_searchBox);
            }
            _searchOpen = true;
            _searchBox.Visibility = Visibility.Visible;
            _searchBox.Text = _searchQuery;
            RepaintRail();
            if (!_railFocused) EnterRail(0);
            _railFocus = _rail.Items.FindIndex(i => i.Kind == RailKind.SearchField);
            RepaintRail();
            _searchBox.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) { Log.Error("open search", ex); }
    }

    private void CloseSearch()
    {
        _searchOpen = false;
        if (_searchBox != null) { _searchBox.Visibility = Visibility.Collapsed; _searchBox.Text = ""; }
        _searchQuery = "";
        ApplySearch();
        _rootEl.Focus(FocusState.Programmatic);
        RepaintRail();
        if (_railFocused) { _railFocus = _rail.Items.FindIndex(i => i.Kind == RailKind.Search); RepaintRail(); }
    }

    private string _appliedQuery = "";

    /// <summary>按名字过滤当前页签（标题、主标题、原名、副标题里包含即算），整面墙翻牌换成结果。</summary>
    private void ApplySearch()
    {
        if (_stack.Count == 0 || _searchQuery == _appliedQuery) { RepaintRail(); return; }
        _appliedQuery = _searchQuery;
        while (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1);
        var all = _lib.HomeTabs[_tabIndex].Items();
        var q = _searchQuery;
        var items = q.Length == 0 ? all : all.Where(i =>
            Contains(i.Title, q) || Contains(i.Main, q) || Contains(i.Kicker, q) || Contains(i.Tail, q) || Contains(i.Subtitle, q)).ToList();
        _railModel.Matches = q.Length == 0 ? -1 : items.Count;
        if (_expandedKey != null) Collapse(false);
        Sync();
        var start = SlotNearSeam();
        _stack[0] = new Layer { Id = $"home:{_lib.HomeTabs[_tabIndex].Id}", Title = "游戏库", Items = items, Layout = Content.Infinite(items.Count, start), Home = true };
        SwapContent(new FlipOrigin.Line(_anchorX));
        FocusKey = start?.Key;
        RepaintRail();
    }

    private static bool Contains(string? s, string q) => s != null && s.Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>搜索框（XAML TextBox，为了中文输入法）放在竖条里画好的框上。</summary>
    private void PlaceSearchBox()
    {
        if (_searchBox == null || !_searchOpen) return;
        var it = _rail.Items.FirstOrDefault(i => i.Kind == RailKind.SearchField);
        Sync();
        var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
        if (it == null || geo.Width < Rail.HomeWidth * 0.6) { _searchBox.Visibility = Visibility.Collapsed; return; }
        _searchBox.Visibility = Visibility.Visible;
        double left = geo.ScreenX - Rail.HomeWidth / 2 + it.Bounds.X + 4;
        Canvas.SetLeft(_searchBox, left);
        Canvas.SetTop(_searchBox, it.Bounds.Y);
    }

    // ---------------- 动态背景 ----------------

    private void UpdateBackdropKey(SlotKey? key, bool animate)
    {
        if (_backdrop == null || key is not { } k) return;
        var slot = SlotByKey(k);
        var id = slot != null ? ItemFor(slot) : null;
        var item = id != null ? Lookup(id) : null;
        // folia 取的是封面（专辑图）：这里用竖封面，没有就用大背景
        var art = item?.Art;
        string? path = art == null ? null : art.Has('p') ? art.File('p') : art.Has('x') ? art.File('x') : art.Has('h') ? art.File('h') : null;
        _backdrop.SetCover(path, a => _dq.TryEnqueue(() => { a(); EnsureBackdropLoop(); }));
        EnsureBackdropLoop();
        _ = animate;
    }

    /// <summary>只在缝开着、窗口在前台、开着动态背景、没降低动态效果时跑循环动画。</summary>
    private void UpdateBackdropRunning()
    {
        if (_backdrop == null) return;
        bool seamOpen;
        try { Sync(); seamOpen = SeamGeometry(_anchorX, _center, _openWidth, View).Width > 1 || _seamTween != null; }
        catch { seamOpen = true; }
        _backdrop.Visible = _backdropOn;
        bool recent = NowMs - _lastInput < BackdropIdleMs;
        bool run = _backdropOn && _windowActive && !Reduced && seamOpen && recent;
        _backdrop.Run(run);
        EnsureBackdropLoop();
        // 背景在动时，角落里那个 1 px 的点也一起动：实测只有缝里一条在动时，展开 / 换页签时 DWM 会隔帧合成（约 8 % 的帧 33 ms），
        // 加上这个点就每帧都准时；背景本来就让合成每帧都在跑，它不额外花什么
        if (run) StartFrameProbe(); else if (!Program.Args.Contains("--probe")) _pacer?.TryGetAnimationController("Opacity")?.Pause();
    }
}

public sealed partial class WallStage
{
    /// <summary>截图自测用（--state=rail|settings|search|railtab）：把竖条摆到某个状态。</summary>
    internal void RailTestState(string s)
    {
        switch (s)
        {
            case "rail":
                Move(Direction.Left); Move(Direction.Down); Move(Direction.Down);
                break;
            case "railtab":
                Move(Direction.Left);
                _railFocus = _rail.Items.FindIndex(i => i.Kind == RailKind.Tab && i.Index == 4);
                RepaintRail();
                Activate();
                break;
            case "settings":
                EnterRail(0, null);
                _railFocus = _rail.Items.FindIndex(i => i.Kind == RailKind.Settings);
                Activate();
                Move(Direction.Down);
                break;
            case "search":
                EnterRail(0, null);
                _railFocus = _rail.Items.FindIndex(i => i.Kind == RailKind.Search);
                Activate();
                if (_searchBox != null) _searchBox.Text = "命运";
                break;
        }
    }
}

public sealed partial class WallStage
{
    /// <summary>自测用（--probe）：角落里一个 1 px、肉眼看不见的点一直在动，让合成器每帧都出帧——这样量到的 33 ms 间隔才都是真掉帧。</summary>
    private SpriteVisual? _pacer;

    internal void StartFrameProbe()
    {
        if (_pacer != null) { _pacer.TryGetAnimationController("Opacity")?.Resume(); return; }
        var dot = G.Sprite(G.Solid(Gfx.Rgba(9, 9, 11)), relative: false);
        dot.Size = new Vector2(1, 1);
        var a = G.C.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, 0.98f, G.Linear);
        a.InsertKeyFrame(0.5f, 1f, G.Linear);
        a.InsertKeyFrame(1, 0.98f, G.Linear);
        a.Duration = TimeSpan.FromSeconds(1);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        _root.Children.InsertAtTop(dot);
        dot.StartAnimation("Opacity", a);
        _pacer = dot;
    }
}

public sealed partial class WallStage
{
    /// <summary>动态背景在合成器的帧上出帧；这里把缝的开口宽度报过去（补间途中取起止两头里宽的那个）。</summary>
    private void EnsureBackdropLoop()
    {
        PublishStrip();
        StartBackdropLoop();
    }

    private void StartBackdropLoop()
    {
        if (_backdropLoop || _backdrop == null || !_backdrop.NeedsFrames) return;
        _backdropLoop = true;
        _backdropLast = 0;
        CompositionTarget.Rendering += OnBackdropFrame;
    }

    private bool _backdropLoop;
    /// <summary>缝第一次打开时在屏幕上的中心和宽度（DIP）：背景画布按缝在这里来画。</summary>
    private double? _backdropHome;
    private double _backdropHomeWidth;
    private long _backdropLast;
    /// <summary>墙面动画进行到什么时候（这之前背景停帧）。</summary>
    private long _wallBusyUntil;

    /// <summary>墙面开始一段动画：背景先停到它结束（背景整块跟着缝平移，停的只是它自己那点极慢的流动；
    /// 实测停帧期间也出帧会让 DWM 隔帧合成，墙面动画从 60 掉到 51–57 fps）。</summary>
    private void WallBusy(double ms) => _wallBusyUntil = Math.Max(_wallBusyUntil, NowMs + (long)ms + 30);

    private void OnBackdropFrame(object? sender, object e)
    {
        try
        {
            long now = NowMs;
            if (now < _wallBusyUntil)
            {
                // 墙面在动：背景的流动停着，但缝变宽 / 挪动露出来的新区域要立刻补上（进合集时缝从竖条张到整屏）
                _backdropLast = 0;
                _backdrop.FrameNow(0, hold: true);
                return;
            }
            double dt = _backdropLast == 0 ? 16.7 : Math.Min(100, now - _backdropLast);
            _backdropLast = now;
            _backdrop.FrameNow(dt);
            if (!_backdrop.NeedsFrames)
            {
                CompositionTarget.Rendering -= OnBackdropFrame;
                _backdropLoop = false;
            }
        }
        catch (Exception ex) { Log.Error("backdrop frame", ex); }
    }

    private void PublishStrip()
    {
        if (_backdrop == null || _stack.Count == 0) return;
        Sync();
        // 画布里缝的位置固定（视觉跟着缝平移，见 BuildChrome 里的 Offset 表达式），这里只看宽度：补间途中取起止两头里宽的那个
        var g = SeamGeometry(_anchorX, _center, _openWidth, View);
        if (_backdropHome == null && g.Width > 1)
        {
            _backdropHome = g.ScreenX; _backdropHomeWidth = g.Width;
            _cam.InsertScalar("bhome", (float)g.ScreenX);
            _cam.InsertScalar("bsw", (float)g.Width);
        }
        double home = _backdropHome ?? View.Width / 2, homeW = _backdropHomeWidth;
        double width = g.Width;
        if (_seamTween != null || _camX != null || _camY != null)
        {
            // 补间途中：开口终点宽度 × 相机起止两头，取最宽的（开口受屏幕边距限制，相机走到哪宽度就不一样）
            var end = new Center(_camX?.To ?? _center.X, _camY?.To ?? _center.Y);
            double to = _seamTween?.To ?? _openWidth;
            width = Math.Max(width, Math.Max(SeamGeometry(_anchorX, _center, to, View).Width, SeamGeometry(_anchorX, end, to, View).Width));
        }
        // 与 Offset 表达式同一个公式：缝越宽，画布里的中心越往正中靠（铺满全屏时正好是整张画布）
        double t = Math.Clamp((width - homeW) / Math.Max(1, View.Width - homeW), 0, 1);
        double center = home + (View.Width / 2 - home) * t;
        _backdrop.SetStrip(center - width / 2, width);
        StartBackdropLoop();   // 背景停着时开口变了，也要把新露出来的那块补画上
    }
}
