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
    private BackgroundSettings _backgroundSettings = new();
    private bool _backgroundSettingsOpen;
    private ContainerVisual _wallBackgroundL = null!, _wallBackgroundR = null!;
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

    // ---------------- 初始化 ----------------

    private void InitRail()
    {
        _rail = new Rail(G) { Dpi = Chrome.Dpi };
        _railModel.User = SystemInfo.UserName();
        _railModel.Battery = SystemInfo.Battery();
        _railModel.Now = DateTime.Now;
        _backgroundSettings = BackgroundSettings.Load();
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
            bool was = _windowActive;
            _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
            OnScreensaverWindowActivation(_windowActive);
            UpdateBackdropRunning();
            if (was != _windowActive) GpOnWindowActive(_windowActive);
        };
        _ = GpCheckDesktopMode();   // 启动时手柄就在桌面模式：同样先切成手柄模式
    }

    /// <summary>竖条要画的东西（页签、合集入口、设置项）。</summary>
    private RailModel RailModelNow()
    {
        var m = _railModel;
        m.Home = true;
        m.Tabs = _lib.HomeTabs.Select(t => t.Label).ToList();
        m.ActiveTab = _tabIndex;
        m.Shortcuts = new();   // 竖条里只放平台页签；Steam 的分类铺在 Steam 页签的墙上
        m.Settings = new()
        {
            ("外观设置", TitleStyles.Name(Style)),
            ("背景样式", _backgroundSettings.Name),
            ("控制中心卡片", _ccCards ? "卡片" : "拍立得"),
            ("动态背景", _backdropOn ? "开" : "关"),
            ("降低动态效果", _reducedLocal ? "开" : "关"),
            ("性能读数", _hudOn ? "开" : "关"),
            ("MSI Center M", _msiCenterText),
            ("导出日志", "存到桌面"),
            ("恢复默认", _resetArmed && NowMs - _resetArmedAt <= 3000 ? "再按一次确认" : "工具箱设置回到默认"),
            ("完成", "返回竖条"),
        };
        m.SettingsTitle = _backgroundSettingsOpen ? "背景" : "设置";
        if (_backgroundSettingsOpen)
        {
            m.Settings = new() { ("背景样式", _backgroundSettings.Name) };
            if (_backgroundSettings.Kind == BackgroundKind.Monet)
            {
                m.Settings.Add(("缓慢漂移", _backgroundSettings.MonetDrift ? "开" : "关"));
                m.Settings.Add(("竖向纹理", _backgroundSettings.MonetStreaks ? "开" : "关"));
            }
            else if (_backgroundSettings.Kind == BackgroundKind.Filmstrip)
            {
                m.Settings.Add(("封面排列", _backgroundSettings.FilmLayout == FilmLayout.Horizontal ? "横向条带" : "竖向条带"));
                m.Settings.Add(("条带数量", _backgroundSettings.FilmStripCount == 0 ? "自动" : $"{_backgroundSettings.FilmStripCount} 条"));
                m.Settings.Add(("滑动速度", $"{_backgroundSettings.FilmSpeed}%"));
                m.Settings.Add(("倾斜角度", $"{_backgroundSettings.FilmAngle}°"));
                m.Settings.Add(("背景暗化", $"{_backgroundSettings.FilmDarkness}%"));
            }
            else m.Settings.Add((_backgroundSettings.Kind == BackgroundKind.Latent ? "显示模式" : "背景效果", _backgroundSettings.Variant));
            m.Settings.Add(("完成", "返回设置"));
        }
        return m;
    }

    private bool _searchOpen;

    /// <summary>只重画竖条（不翻转）。</summary>
    private void RepaintRail()
    {
        CsPaintStatus();
        if (_stack.Count == 0) return;
        var front = _seamFront ? _seamB : _seamA;
        PaintSeam(front);
        PlaceSearchBox();
    }

    // ---------------- 焦点：墙 ⇄ 竖条 ----------------

    private bool SeamUsable()
    {
        if (!LibraryRailVisible) return false;
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
        if (_searchOpen) { _searchFocus = 1; RepaintRail(); return; }
        RepaintRail();   // 先排一遍拿到各项的位置
        _railFocus = item ?? (CurrentLayer.Home ? _rail.NearestTo((float)screenY) : 0);
        if (_railFocus < 0) _railFocus = CurrentLayer.Home ? _rail.DefaultFocus(_railModel) : 0;
        RepaintRail();
    }

    /// <summary>出竖条：回到墙上那一侧离这一项最近的卡。</summary>
    private void ExitRail(bool toLeft)
    {
        float y = 0;
        if (CurrentLayer.Home && !_searchOpen && _railFocus >= 0 && _railFocus < _rail.Items.Count)
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
        if (_backgroundSettingsOpen && dir is Direction.Left or Direction.Right)
        {
            if (_railFocus >= 0 && _railFocus < _rail.Items.Count && _rail.Items[_railFocus].Kind == RailKind.Setting)
                ToggleSetting(_rail.Items[_railFocus].Index, dir == Direction.Left ? -1 : 1);
            return true;
        }
        if (_searchOpen) return SearchMove(dir);
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
        if (_searchOpen) { SearchActivate(); return true; }
        if (!CurrentLayer.Home) { Pop(); return true; }
        if (_railFocus < 0 || _railFocus >= _rail.Items.Count) return true;
        var it = _rail.Items[_railFocus];
        if (_backgroundSettingsOpen && it.Kind != RailKind.Setting) SetBackgroundPreview(false);
        switch (it.Kind)
        {
            case RailKind.Tab:
                if (it.Index != _tabIndex) { _csRailTab = true; try { SwitchTab(it.Index - _tabIndex); } finally { _csRailTab = false; } }
                break;
            case RailKind.Shortcut:
                if (it.Index < _lib.Collections.Count) OpenCollection(_lib.Collections[it.Index]);
                break;
            case RailKind.Account:
                SystemInfo.Open("ms-settings:yourinfo");
                break;
            case RailKind.Steam:
                string uri = _prefs.SteamShortcut == SteamShortcutTarget.BigPicture ? "steam://open/bigpicture" : "steam://open/games";
                try { PlatformClients.Open(uri); Log.Info($"rail Steam shortcut: {uri}"); }
                catch (Exception ex) { Log.Warn($"rail Steam shortcut: {ex.Message}"); Toast("无法打开 Steam，请确认已安装 Steam"); }
                break;
            case RailKind.Search:
                OpenSearch();
                break;
            case RailKind.AddGame:
                _ = AddGameFromLibrary();
                break;
            case RailKind.Settings:
                // ⚙：打开设置页（独立的整页，在控制中心里）
                _backgroundSettingsOpen = false;
                OpenSettingsFromLibrary();
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
        if (_backgroundSettingsOpen) { CloseBackgroundSettings(); return true; }
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

    private void ToggleSetting(int i, int step = 1)
    {
        if (_backgroundSettingsOpen)
        {
            bool monet = _backgroundSettings.Kind == BackgroundKind.Monet;
            if (i == 0) _backgroundSettings = _backgroundSettings with { Kind = (BackgroundKind)(((int)_backgroundSettings.Kind + step + 4) % 4) };
            else if (_backgroundSettings.Kind == BackgroundKind.Filmstrip)
            {
                _backgroundSettings = i switch
                {
                    1 => _backgroundSettings with { FilmLayout = _backgroundSettings.FilmLayout == FilmLayout.Horizontal ? FilmLayout.Vertical : FilmLayout.Horizontal },
                    2 => _backgroundSettings with { FilmStripCount = StepFilmStripCount(step) },
                    3 => _backgroundSettings with { FilmSpeed = Math.Clamp(_backgroundSettings.FilmSpeed + step * 25, 25, 200) },
                    4 => _backgroundSettings with { FilmAngle = Math.Clamp(_backgroundSettings.FilmAngle + step * 5, 0, 45) },
                    5 => _backgroundSettings with { FilmDarkness = Math.Clamp(_backgroundSettings.FilmDarkness + step * 10, 0, 90) },
                    _ => _backgroundSettings,
                };
                if (i is < 1 or > 5) { CloseBackgroundSettings(); return; }
            }
            else if (i == 1)
            {
                _backgroundSettings = _backgroundSettings.Kind switch
                {
                    BackgroundKind.Latent => _backgroundSettings with { Latent = (LatentLayers)(((int)_backgroundSettings.Latent + step + 3) % 3) },
                    BackgroundKind.Nomand => _backgroundSettings with { Nomand = _backgroundSettings.Nomand == NomandEffect.Lens ? NomandEffect.Paper : NomandEffect.Lens },
                    _ => _backgroundSettings with { MonetDrift = !_backgroundSettings.MonetDrift },
                };
            }
            else if (i == 2 && monet) _backgroundSettings = _backgroundSettings with { MonetStreaks = !_backgroundSettings.MonetStreaks };
            else { CloseBackgroundSettings(); return; }
            ApplyBackgroundSettings();
            RepaintRail();
            return;
        }
        switch (i)
        {
            case 0: OpenSettingsFromLibrary(); _stCat = 2; Repaint(); break;
            case 1: SetBackgroundPreview(true); _railFocus = 0; break;
            case 2: SetCcCardStyle(!_ccCards); break;
            case 3: _backdropOn = !_backdropOn; UpdateBackdropRunning(); _prefs = _prefs with { Backdrop = _backdropOn }; _prefs.Save(); Toast(_backdropOn ? "动态背景：开" : "动态背景：关"); break;
            case 4: ToggleReducedMotion(); UpdateBackdropRunning(); break;
            case 5: ToggleHud(); break;
            case 6: _ = RailMsiCenterToggle(); break;
            case 7: _ = RailAgentRun("export_logs", r => $"日志已存到桌面：{(string?)r?["name"]}"); break;
            case 8:
                // 收不回来：第一下只把这一行变成「再按一次确认」，3 秒内再按才做（先自动备份，后台会重启一下）
                if (!_resetArmed || NowMs - _resetArmedAt > 3000) { _resetArmed = true; _resetArmedAt = NowMs; After(3100, RepaintRail); break; }
                _resetArmed = false;
                Toast("正在恢复默认，工具箱后台会重启一下");
                _ = RailAgentRun("reset_settings", r => $"已恢复默认（原来的设置备份在 {(string?)r?["backup"]}）");
                break;
            case 9:
                _railModel.SettingsOpen = false;
                RepaintRail();
                _railFocus = _rail.Items.FindIndex(it => it.Kind == RailKind.Settings);
                break;
        }
        RepaintRail();
    }

    private string _msiCenterText = "—";
    private bool _msiCenterOff;

    /// <summary>
    /// MSI Center M：工具箱已经替代了它。禁用 = 它的 Foundation 服务设为禁用并停掉、两个计划任务禁用、进程全部关掉；
    /// 不卸载，再按一下恢复（服务自动、计划任务打开）。打开设置时读一次状态。
    /// </summary>
    private async Task RailMsiCenterRefresh()
    {
        try
        {
            _agent ??= new ClawLibrary.Data.AgentClient();
            RailMsiCenterShow(await _agent.Call("get_msi_center"));
        }
        catch (Exception ex) { _msiCenterText = ex.Message.Contains("不支持的后台调用") ? "后台版本太旧" : "读不到"; }
        RepaintRail();
    }

    private void RailMsiCenterShow(System.Text.Json.Nodes.JsonNode? r)
    {
        if (r is not System.Text.Json.Nodes.JsonObject o || o["installed"]?.GetValue<bool>() != true) { _msiCenterText = "没有安装"; _msiCenterOff = true; return; }
        bool service = o["serviceDisabled"]?.GetValue<bool>() == true;
        bool tasks = (o["tasks"] as System.Text.Json.Nodes.JsonArray)?.All(t => t?["enabled"]?.GetValue<bool>() != true) ?? true;
        int running = (int?)o["running"]?.GetValue<double>() ?? 0;
        _msiCenterOff = service && tasks && running == 0;
        _msiCenterText = _msiCenterOff ? "已禁用" : running > 0 ? "正在运行 · 按 A 禁用" : "没禁用 · 按 A 禁用";
    }

    private async Task RailMsiCenterToggle()
    {
        bool disable = !_msiCenterOff;
        _msiCenterText = disable ? "正在禁用…" : "正在恢复…";
        RepaintRail();
        try
        {
            _agent ??= new ClawLibrary.Data.AgentClient();
            RailMsiCenterShow(await _agent.Call("set_msi_center", System.Text.Json.Nodes.JsonValue.Create(disable)));
            Toast(disable ? "MSI Center M 已禁用（没有卸载，可以再按一下恢复）" : "MSI Center M 已恢复自启");
        }
        catch (Exception ex) { Toast($"没有改成：{ex.Message}"); await RailMsiCenterRefresh(); }
        RepaintRail();
    }

    private bool _resetArmed;
    private long _resetArmedAt;

    /// <summary>设置里交给工具箱后台的动作（导出日志、恢复默认）：等后台做完，按结果给提示。</summary>
    private async Task RailAgentRun(string method, Func<System.Text.Json.Nodes.JsonNode?, string> done)
    {
        try
        {
            _agent ??= new ClawLibrary.Data.AgentClient();
            Toast(done(await _agent.Call(method)));
        }
        catch (Exception ex) { Toast(ex.Message.Contains("不支持的后台调用") ? "工具箱后台版本太旧" : $"没有做成：{ex.Message}"); }
    }

    private void CloseBackgroundSettings()
    {
        SetBackgroundPreview(false);
        _railFocus = 1;
        RepaintRail();
    }

    private int StepFilmStripCount(int step)
    {
        int current = _backgroundSettings.FilmStripCount;
        if (current == 3 && step < 0) return 0; // 最少条带之后可回到原来的自动布局。
        return Math.Clamp((current == 0 ? _backdrop.AutoFilmStripCount : current) + step, 3, 16);
    }

    // The background page itself is a full-screen preview; the existing settings rail stays usable.
    private void SetBackgroundPreview(bool on)
    {
        _backgroundSettingsOpen = on;
        _cam.InsertScalar("bpreview", on ? 1 : 0);
        UpdateWallBackgroundVisibility();
        UpdateBackdropKey(FocusKey, false);
        UpdateBackdropRunning();
    }

    private bool BackdropBehindCovers => _backdropOn;

    private void UpdateWallBackgroundVisibility()
    {
        bool showWall = !_backgroundSettingsOpen && !BackdropBehindCovers;
        _wallBackgroundL.IsVisible = _wallBackgroundR.IsVisible = showWall;
        _field.IsVisible = _vignette.IsVisible = !_backgroundSettingsOpen && !CsHidesWall;
        if (CsHidesWall) _wallBackgroundL.IsVisible = _wallBackgroundR.IsVisible = false;
        _cam.InsertScalar("bfull", BackdropBehindCovers ? 1 : 0);
    }

    private void ApplyBackgroundSettings()
    {
        _backgroundSettings.Save();
        _backdrop.Configure(_backgroundSettings);
        _backdrop.Build((float)View.Width, (float)View.Height);
        UpdateBackdropKey(FocusKey, false);
        if (_ccBack != null)
        {
            _ccDim.IsVisible = _backgroundSettings.Kind != BackgroundKind.Filmstrip;
            _ccBack.Configure(_backgroundSettings);
            _ccBack.Build((float)View.Width, (float)View.Height);
            CcBackRun();
        }
        UpdateBackdropRunning();
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
        if (!LibraryRailVisible) return false;
        Sync();
        var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
        if (geo.Width < 2 || sx < geo.ScreenX - geo.Width / 2 || sx > geo.ScreenX + geo.Width / 2) return false;
        if (_searchOpen) { SearchClick(sx - (geo.ScreenX - SeamFull / 2), sy); return true; }
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

    // ---------------- 搜索（照 folia 首页缝里的全局搜索框） ----------------
    // 放大镜把首页窄缝临时展开成完整宽度的搜索面板（与进合集同一套开口补间 + 翻面），输入框拿焦点。
    // 输入框不跟着打字过滤：提交才搜，结果作为一层「游戏库 › 搜索」压进来，铺成一面封面墙（不是列表）；
    // ×、Esc / B 只关面板，缝翻回竖条。没有结果时面板留着，提示一下。

    /// <summary>搜索面板里手柄焦点在哪一项：1 输入框、2 搜索按钮。</summary>
    private int _searchFocus = 1;
    /// <summary>输入框此刻有键盘焦点（描边换强调色）。</summary>
    private bool _searchTyping;
    private Chrome.SearchPanelLayout? _searchLayout;
    private bool _placePending;

    private void EnsureSearchBox()
    {
        if (_searchBox != null) return;
        _searchBox = new TextBox
        {
            PlaceholderText = Chrome.SearchPlaceholder,
            FontSize = 16,
            Height = 42,
            MinHeight = 0,
            Padding = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(0),
            RequestedTheme = ElementTheme.Dark,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            Visibility = Visibility.Collapsed,
        };
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var ink = new SolidColorBrush(Theme.Ink);
        var soft = new SolidColorBrush(Theme.SeamInkSoft);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            _searchBox.Resources[key] = clear;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            _searchBox.Resources[key] = ink;
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" })
            _searchBox.Resources[key] = soft;
        _searchBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Enter: e.Handled = true; SubmitSearch(); break;
                case Windows.System.VirtualKey.Escape: e.Handled = true; CloseSearch(); break;
                case Windows.System.VirtualKey.Down:
                    // 离开输入框，焦点落到「搜索」按钮
                    e.Handled = true;
                    _rootEl.Focus(FocusState.Programmatic);
                    _railFocused = true; _searchFocus = 2; RepaintRail();
                    break;
            }
        };
        // 系统输入框自带的「清空」× 不要：面板上画的 × 是 folia 的「关闭搜索」
        // （模板里的按钮要等第一次布局才有，打字 / 拿焦点时都再藏一次）
        void HideClearButton()
        {
            if (FindChild(_searchBox!, "DeleteButton") is FrameworkElement clearButton && clearButton.MaxWidth != 0)
            {
                clearButton.MaxWidth = 0;
                clearButton.Opacity = 0;
                clearButton.IsHitTestVisible = false;
            }
        }
        _searchBox.Loaded += (_, _) => HideClearButton();
        _searchBox.TextChanged += (_, _) => HideClearButton();
        _searchBox.GotFocus += (_, _) => { HideClearButton(); _searchTyping = true; RepaintRail(); };
        _searchBox.LostFocus += (_, _) => { _searchTyping = false; RepaintRail(); };
        HookTouchKeyboard(_searchBox);
        _rootEl.Overlay.Children.Add(_searchBox);
    }

    private static DependencyObject? FindChild(DependencyObject root, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is FrameworkElement fe && fe.Name == name) return c;
            if (FindChild(c, name) is { } hit) return hit;
        }
        return null;
    }

    private void OpenSearch()
    {
        if (_searchOpen || _stack.Count != 1) return;
        try
        {
            EnsureSearchBox();
            if (_expandedKey != null) Collapse(false);
            _railModel.SettingsOpen = false;
            if (FocusKey is { } fk && _tiles.TryGetValue(fk, out var t)) t.SetFocused(false);
            _searchOpen = true;
            _searchFocus = 1;
            _railFocused = true;
            _searchBox!.Text = "";
            // 窄缝展开成完整宽度（贴边时相机让一让），缝翻面换成搜索面板
            var target = ReopenSeamIfGone(SeamFull);
            var plan = PlanSeam(target, SeamFull, View, _anchorX);
            TweenSeam(SeamFull);
            FlipSeam(SeamFull);
            if (Math.Abs(plan.CenterX - target.X) > 0.5 || Math.Abs(target.X - _center.X) > 0.5) TweenCamera(new Center(plan.CenterX, target.Y));
            PlaceSearchBox();
            // 翻面完了再把输入框放上去、给它键盘焦点（folia：打开即聚焦）
            After(Motion.SeamFlip.OutMs + Motion.SeamFlip.InMs + 20, () =>
            {
                if (!_searchOpen) return;
                PlaceSearchBox();
                FocusSearchBox();
            });
        }
        catch (Exception ex) { Log.Error("open search", ex); }
    }

    private void FocusSearchBox()
    {
        if (_searchBox == null || !_searchOpen) return;
        _searchFocus = 1;
        _railFocused = true;
        PlaceSearchBox();
        (_window as MainWindow)?.RequestTextInputFocus();
        _searchBox.Focus(FocusState.Programmatic);
        RepaintRail();
    }

    /// <summary>只关面板：缝翻回竖条（焦点在竖条里就落在放大镜上）。</summary>
    private void CloseSearch()
    {
        if (!_searchOpen) return;
        _searchOpen = false;
        _searchTyping = false;
        HideSearchBox();
        _rootEl.Focus(FocusState.Programmatic);
        if (_stack.Count == 1 && CurrentLayer.Home)
        {
            TweenSeam(SeamHome);
            FlipSeam(SeamHome);
            if (_railFocused) { _railFocus = _rail.Items.FindIndex(i => i.Kind == RailKind.Search); RepaintRail(); }
        }
    }

    private void HideSearchBox()
    {
        if (_searchBox != null) _searchBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>提交：在整个游戏库里按名字找（标题、主标题、原名、副标题、开发商），结果压成一层封面墙。</summary>
    private void SubmitSearch()
    {
        if (!_searchOpen || _searchBox == null) return;
        var q = _searchBox.Text.Trim();
        if (q.Length == 0) { FocusSearchBox(); return; }
        var items = SearchItems(q);
        Log.Info($"search \"{q}\": {items.Count}");
        if (items.Count == 0) { Toast($"没有找到「{q}」"); FocusSearchBox(); return; }
        Sync();
        var start = SlotNearSeam();
        if (start == null) return;
        _searchOpen = false;
        _searchTyping = false;
        HideSearchBox();
        _rootEl.Focus(FocusState.Programmatic);
        _railFocused = false;
        _railFocus = -1;
        // 结果墙是有限的方块矩阵（不像合集那样循环铺满）：缝右边的格子按离起点的远近排名，第 0 名就是起点那格，
        // 往外一圈圈放，放完为止，其余格子空着
        // 面板已经是完整宽度：开口不用再动，缝直接翻面成「游戏库 › 搜索」信息条，墙从缝边翻牌换成结果
        PushLayer(start, new Layer
        {
            Id = $"search:{q}", Title = q, Items = items, Layout = SearchLayout(items.Count, start), Home = false,
            Crumbs = new[] { "游戏库", "搜索" },
        });
    }

    private List<LibraryItem> SearchItems(string query) => _lib.Games.OrderByDescending(g => g.LastPlayed)
        .Where(i => Contains(i.Title, query) || Contains(i.Main, query) || Contains(i.Kicker, query) || Contains(i.Tail, query) || Contains(i.Subtitle, query)).ToList();

    private FiniteLayout SearchLayout(int count, Slot start)
    {
        int anchorCol = BoundaryColumn(_anchorX);
        var candidates = new List<Slot>();
        for (double span = 1600; ; span *= 1.6)
        {
            candidates = SlotsIn(new Bounds(_anchorX, _anchorX + span * 2, start.Cy - span, start.Cy + span), 6000, new List<Slot>())
                .Where(s => s.Column >= anchorCol).ToList();
            if (candidates.Count >= count * 2 || span > 60000) break;
        }
        return Content.Finite(candidates, count, start.Cx, start.Cy, start.Key);
    }

    private static bool Contains(string? s, string q) => s != null && s.Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>搜索面板里的手柄操作（焦点在面板里时）。</summary>
    private bool SearchMove(Direction dir)
    {
        if (dir is Direction.Left or Direction.Right) { ExitRail(dir == Direction.Left); return true; }
        int next = Math.Clamp(_searchFocus + (dir == Direction.Down ? 1 : -1), 1, 2);
        if (next != _searchFocus) { _searchFocus = next; RepaintRail(); }
        return true;
    }

    private void SearchActivate()
    {
        switch (_searchFocus)
        {
            case 1: FocusSearchBox(); break;
            default: SubmitSearch(); break;
        }
    }

    /// <summary>鼠标点在搜索面板上。</summary>
    private void SearchClick(double lx, double ly)
    {
        if (_searchLayout is not { } l) return;
        var p = new Windows.Foundation.Point(lx, ly);
        if (l.Clear.Contains(p)) CloseSearch();
        else if (l.Field.Contains(p)) FocusSearchBox();
        else if (l.Submit.Contains(p)) { _railFocused = true; _searchFocus = 2; SubmitSearch(); }
    }

    /// <summary>输入框（XAML TextBox，为了中文输入法）叠在面板画好的框里；开口还在动 / 相机还在走的时候先藏着，停了再放。</summary>
    private void PlaceSearchBox()
    {
        if (_searchBox == null) return;
        if (!_searchOpen || _searchLayout is not { } l) { HideSearchBox(); return; }
        Sync();
        if (_seamTween != null || _camX != null || _camY != null)
        {
            HideSearchBox();
            if (!_placePending)
            {
                _placePending = true;
                After(Math.Max(Motion.SeamMs, Motion.CameraMs) + 30, () => { _placePending = false; PlaceSearchBox(); });
            }
            return;
        }
        var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
        if (geo.Width < SeamFull * 0.95) { HideSearchBox(); return; }
        _searchBox.Width = l.Input.Width;
        Canvas.SetLeft(_searchBox, geo.ScreenX - SeamFull / 2 + l.Input.X);
        Canvas.SetTop(_searchBox, l.Input.Y);
        _searchBox.Visibility = Visibility.Visible;
    }

    // ---------------- 动态背景 ----------------

    private string[] FilmCoverPaths() => _lib.Games
        .Where(g => g.Art?.Has('q') == true)
        .OrderByDescending(g => g.Installed)
        .ThenBy(g => g.LastPlayedDays ?? int.MaxValue)
        .Select(g => g.Art!.File('q'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

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

    /// <summary>动态背景铺在整面游戏封面下方；窗口在前台、没降低动态效果时跑循环动画。</summary>
    private void UpdateBackdropRunning()
    {
        if (_backdrop == null) return;
        UpdateWallBackgroundVisibility();
        _backdrop.Visible = _backdropOn || _backgroundSettingsOpen;
        bool recent = NowMs - _lastInput < BackdropIdleMs;
        bool run = _backdropOn && _windowActive && !Reduced && recent && !_ccOpen && _ob == null && (!CsHidesWall || !BannerOn);   // 整屏可见，不再依赖侧边栏开口；控制中心 / 首次启动引导开着时墙冻住
        _backdrop.Run(run);
        CcBackRun();
        ObBackRun();
        EnsureBackdropLoop();
        // 背景在动时，角落里那个 1 px 的点也一起动：实测只有缝里一条在动时，展开 / 换页签时 DWM 会隔帧合成（约 8 % 的帧 33 ms），
        // 加上这个点就每帧都准时；背景本来就让合成每帧都在跑，它不额外花什么
        if (run) StartFrameProbe(); else if (!Program.Args.Contains("--probe")) _pacer?.TryGetAnimationController("Opacity")?.Pause();
    }
}

public sealed partial class WallStage
{
    /// <summary>截图自测用（--state=rail|settings|search|searchresults|railtab）：把竖条摆到某个状态。</summary>
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
                After(700, () => { if (_searchBox != null) _searchBox.Text = "命运"; });
                break;
            case "searchresults":
                EnterRail(0, null);
                _railFocus = _rail.Items.FindIndex(i => i.Kind == RailKind.Search);
                Activate();
                After(700, () => { if (_searchBox != null) _searchBox.Text = "命运"; SubmitSearch(); });
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
        if (_backgroundSettingsOpen || BackdropBehindCovers)
        {
            _backdrop.SetStrip(0, View.Width);
            StartBackdropLoop();
            return;
        }
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
        if (_searchOpen) PlaceSearchBox();   // 缝挪了，输入框跟着（在动时先藏起来）
        StartBackdropLoop();   // 背景停着时开口变了，也要把新露出来的那块补画上
    }
}
