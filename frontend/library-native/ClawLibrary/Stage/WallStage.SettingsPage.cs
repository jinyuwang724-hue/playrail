// 控制中心 → 设置：工具箱自己的设置，一个独立的整页（功能点见 docs/settings-page-features.md）。
// 不套 3:7 读数页：左栏是大字分类目录（每个分类下一行摘要），右栏是这个分类的全部选项。
// 改过、和默认不同的项前面有一个小点；Y 把焦点这一项恢复默认。
// 目录里 ↑↓ 换分类，→ / A 进右栏；带箭头的值先 A 进入调整，再 ←→ 改值，A / B 结束调整；右栏 B 回目录，目录里 B 回控制中心。
// 游戏库自己的选项存在 ui.ini（UiPrefs）；Windows 和后台的（主页、自启、第三方软件、震动、兼容修复、备份）走工具箱后台。
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Windows.UI;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static readonly string[] StCatsMain = { "通用", "游戏库", "外观", "Magpie", "小黄鸭", "声音与反馈", "账户与服务", "开机自启", "禁用第三方软件", "维护", "Steam 插件", "性能显示", "关于" };
    private const int StOsdCat = 11;
    /// <summary>左栏的分类：游戏设置时换成游戏的那几类（WallStage.GameSettings.cs）。</summary>
    private string[] StCats => _gsItem != null ? GsCats : StCatsMain;
    private const float StCatH = PerfRowH;
    /// <summary>
    /// 两栏的位置：设置页和性能页一样（左栏 40–388、右栏 428–1240）；游戏设置照手柄页的比例 ——
    /// 左栏只放分类（40–230），右栏从 250 起到 1240。
    /// </summary>
    private float StLeftW => _gsItem != null ? 190 : PerfLeftW;
    private float StRightX => _gsItem != null ? 250 : PerfRightX;
    private float StRightW => _gsItem != null ? 990 : PerfRightW;
    /// <summary>首页：游戏库打开后先到哪（控制中心，或它下面的六个页面之一）。顺序和 BootTarget 一致。</summary>
    private static readonly string[] BootNames = { "游戏库", "控制中心", "性能", "手柄", "显示与声音", "系统", "设置" };

    private UiPrefs _prefs = new();
    internal bool WindowManagerOn => _prefs.WindowManager;
    private ContainerVisual? _st;
    private ContainerVisual _stRightView = null!;
    private SpriteVisual _stTitle = null!, _stLeft = null!, _stRight = null!;
    private bool _stOpen, _stBusy, _stAgentDown;
    private int _stCat, _stZone, _stFocus, _stVersion;
    private float _stScroll, _stScrollY = float.NaN;
    private JsonObject? _stView;
    private readonly List<PerfRow> _stRows = new();
    private DispatcherQueueTimer? _stTimer;
    private string? _stArmed;
    private long _stArmedAt;
    private int _stBackupPick;
    /// <summary>改了只在重新载入游戏库后才生效的选项（来源、隐藏的游戏）。</summary>
    private bool _stNeedsReload;

    // ---------------- 启动时套用 ----------------

    /// <summary>游戏库载入前：来源和隐藏游戏的开关交给 Library.Build。</summary>
    private void ApplyUiPrefsBeforeLoad()
    {
        _prefs = UiPrefs.Load();
        Library.ShowHidden = _prefs.ShowHiddenGames;
        Library.IncludeSteam = _prefs.SourceSteam;
        _connections = new PlatformConnectionStore(App.CacheDir);
        _connections.Load();
        _steamCloud = new SteamCloudStore(App.CacheDir);
        if (_prefs.PreloadSteam && !Program.Args.Contains("--no-platform-startup"))
            _ = Task.Run(() =>
            {
                try { Log.Info(PlatformClients.StartSteamInTray()); }
                catch (Exception ex) { Log.Warn($"Steam preload: {ex.Message}"); }
            });
        Library.SteamInstalledOnly = _prefs.SteamInstalledOnly;
        if (!Program.Args.Contains("--style=b"))
            Style = Enum.TryParse<TitleStyle>(_prefs.TitleStyleKey, out var style) && Enum.IsDefined(style)
                ? style : _prefs.StyleB ? TitleStyle.B : TitleStyle.D;
        _reducedLocal = _prefs.Reduced;
        SoundFx.Enabled = _prefs.Sounds;
        SoundFx.Configure(_prefs.SoundVolume);
        // 封面重新生成：上次在设置里点了，这次启动先删掉旧的封面套件（缺的会在后台重新生成）
        var flag = Path.Combine(App.CacheDir, "art.reset");
        if (File.Exists(flag))
        {
            try { if (Directory.Exists(ArtRoot)) Directory.Delete(ArtRoot, true); File.Delete(flag); Log.Info("art cache cleared"); }
            catch (Exception ex) { Log.Warn($"art reset: {ex.Message}"); }
        }
    }

    /// <summary>游戏库载入后：集合的顺序和显示隐藏、默认集合。</summary>
    private void ApplyTabPrefs(bool keepCurrent = false)
    {
        string? current = keepCurrent && _tabIndex < _lib.HomeTabs.Count ? _lib.HomeTabs[_tabIndex].Id : null;
        _allTabs ??= _lib.HomeTabs.ToList();
        var order = _prefs.TabOrder.ToList();
        var tabs = _allTabs.OrderBy(t => order.IndexOf(t.Id) is int i && i >= 0 ? i : order.Count + _allTabs.IndexOf(t)).ToList();
        var shown = tabs.Where(t => !_prefs.TabsHidden.Contains(t.Id)).ToList();
        _lib.HomeTabs = shown.Count > 0 ? shown : tabs.Take(1).ToList();
        int want = _lib.HomeTabs.FindIndex(t => t.Id == (current ?? _prefs.StartTab));
        _tabIndex = want >= 0 ? want : 0;
    }

    private List<HomeTab>? _allTabs;

    private void SaveUi(UiPrefs next)
    {
        _prefs = next;
        _prefs.Save();
    }

    private Color? FixedAccent => _prefs.Accent >= 0 ? Gfx.Rgba((byte)(UiPrefs.Accents[_prefs.Accent].Rgb >> 16), (byte)(UiPrefs.Accents[_prefs.Accent].Rgb >> 8), (byte)UiPrefs.Accents[_prefs.Accent].Rgb) : null;

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);

    /// <summary>启动游戏后：设置成「隐藏游戏库」时，游戏起来前把窗口收到任务栏，渲染随之停下。</summary>
    private void AfterGameLaunch(LibraryItem item)
    {
        HoldScreensaverForLaunch();
        Cue(Sfx.Launch);
        _homeMusic?.HoldForLaunch();
        // 游戏设置里开了 Magpie 自动铺满的游戏：后台等它的窗口到前台（最多 90 秒）就套上这款游戏的画面效果
        _ = Task.Run(async () => {
            try {
                _agent ??= new AgentClient();
                await _agent.Call("note_game_launch", new System.Text.Json.Nodes.JsonObject {
                    ["title"] = item.Title, ["exe"] = item.ExePath ?? "", ["appId"] = item.Id,
                    ["aumid"] = item.XboxAumid ?? ""
                });
            } catch (Exception ex) { Log.Warn($"game session launch: {ex.Message}"); }
            try { _agent ??= new AgentClient(); await _agent.Call("magpie_auto_start"); }
            catch (Exception ex) { Log.Warn($"magpie auto start: {ex.Message}"); }
        });
        StartLaunchMotion(item, _lmTile);
        _lmTile = null;
        // 「隐藏游戏库」：等启动画面交接给游戏再收（见 LaunchForegroundChanged）；画面没开起来才按老办法 1.5 秒后收
        if (_prefs.AfterLaunch == AfterLaunch.Hide && _lm == null) After(1500, HideAfterLaunch);
    }

    private Tile? _lmTile;

    private void HideAfterLaunch()
    {
        if (_prefs.BackgroundRun && _window is MainWindow main) main.RunInBackground();
        else ShowWindow(_windowHandle, 6);
    }

    /// <summary>重新载入游戏库：整个前端重启一次（来源、隐藏的游戏、封面重新生成都要从头载入）。</summary>
    private void RestartLibrary()
    {
        Toast("正在重新载入游戏库…");
        Log.Info("library restart from settings");
        After(400, () =>
        {
            try { Microsoft.Windows.AppLifecycle.AppInstance.Restart(""); }
            catch (Exception ex) { Log.Warn($"restart: {ex.Message}"); Toast("没能自动重启，请关掉游戏库再打开"); }
        });
    }

    internal void SetTitleStyle(TitleStyle style)
    {
        if (!Enum.IsDefined(style)) return;
        Style = style;
        foreach (var t in _tiles.Values) t.Show(t.Target);
        SaveUi(_prefs with { TitleStyleKey = style.ToString(), StyleB = style == TitleStyle.B });
    }

    // ---------------- 开 / 关 ----------------

    /// <summary>
    /// 设置页是从游戏库（竖条 ⚙）直接进来的：B 退到底就回游戏库，不经过控制中心主页。
    /// </summary>
    private bool _stFromLibrary;

    /// <summary>
    /// 游戏库 → 设置页：照 folia 设置面板的进场（SettingsModal 的 shell：240 ms ease-out，墙淡出、
    /// 面板从下面 20 px、0.98 倍升起并淡入），不走游戏库卡飞进控制中心的形变。
    /// </summary>
    private void OpenSettingsFromLibrary()
    {
        if (_ccOpen) { OpenSettingsPage(); return; }
        OpenControlCenter(quiet: true);
        OpenSettingsPage();
        _stFromLibrary = true;
        if (_st == null || Reduced) return;
        float k = _ccK;
        _st.CenterPoint = new Vector3((float)View.Width / 2, (float)View.Height / 2, 0);
        _st.Offset = new Vector3(0, 20 * k, 0);
        _st.Scale = new Vector3(0.98f, 0.98f, 1);
        _st.StartAnimation("Offset", ToV3(Vector3.Zero, 240, EaseOutCss, 40));
        _st.StartAnimation("Scale", ToV3(Vector3.One, 240, EaseOutCss, 40));
    }

    /// <summary>从游戏库进来的设置页按 B 回去：面板往下 16 px、0.985 倍淡出，墙淡入（folia 的 exit），控制中心收起。</summary>
    private void CloseSettingsToLibrary()
    {
        _stFromLibrary = false;
        if (_cc == null || _st == null || !_ccOpen) { CloseSettingsPage(); return; }
        var st = _st;
        float k = _ccK;
        // 游戏设置的横幅留到墙淡进来盖住为止（墙下面就是同一张横幅）
        _gsBannerHold = true;
        CloseSettingsPage();
        _gsBannerHold = false;
        foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); vis.Opacity = 0; }
        double ms = Reduced ? 1 : 240;
        if (!Reduced)
        {
            st.CenterPoint = new Vector3((float)View.Width / 2, (float)View.Height / 2, 0);
            st.StartAnimation("Offset", ToV3(new Vector3(0, 16 * k, 0), 220, EaseOutCss));
            st.StartAnimation("Scale", ToV3(new Vector3(0.985f, 0.985f, 1), 220, EaseOutCss));
        }
        _ccOpen = false;
        int v = ++_ccVersion;
        _ccTimer?.Stop();
        _root.StopAnimation("Opacity");
        _root.IsVisible = true;
        _root.Opacity = 0;
        _root.StartAnimation("Opacity", G.Scalar(0, 1, ms, EaseOutCss));
        After(ms + 20, () =>
        {
            if (v != _ccVersion) return;
            HideControlCenter();
            GsBannerClose(instant: true);
            foreach (var p in new[] { "Offset", "Scale" }) st.StopAnimation(p);
            st.Offset = Vector3.Zero;
            st.Scale = Vector3.One;
            foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); vis.Opacity = 1; }
        });
        UpdateBackdropRunning();
        Log.Info("settings page close → library");
    }

    private void OpenSettingsPage()
    {
        if (_cc == null) return;
        EnsureStVisuals();
        _stOpen = true;
        _stArmed = null;
        SettingsResetPointer();
        ++_stVersion;
        _stZone = 0;
        BuildStRows();
        _stFocus = FirstStRow();
        _stScroll = 0;
        _stScrollY = float.NaN;
        PaintSt();
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        _st!.IsVisible = true;
        _st.Opacity = 0;
        _st.StartAnimation("Opacity", ToScalar(1, 240, EaseOutCss, 80));
        _stTimer ??= MakeStTimer();
        _stTimer.Start();
        _ = StRefresh();
        Log.Info("settings page open");
    }

    private void CloseSettingsPage(bool instant = false)
    {
        if (!_stOpen || _st == null) return;
        if (!instant) Cue(Sfx.ModalHide);
        if (_agOpen) { _agOpen = false; _agDraft = null; AgHideBox(); if (_ag != null) _ag.IsVisible = false; }
        _stOpen = false;
        GsArtReset();
        GsPadReset();
        if (_stOver != null) _stOver.IsVisible = false;
        if (!_gsBannerHold) GsBannerClose();
        _gsItem = null;
        _gsState = null;
        _stBrowsePick = null;
        _stBrowseFromLibrary = false;
        _stBrowse = null;
        _stBrowseExe = false;
        _stBrowseMusic = false;
        _stBrowseLossless = false;
        MgCloseEditor();
        _lgPage = LgPage.None;
        StEditEnd(false);
        SettingsResetPointer();
        _stTimer?.Stop();
        int v = ++_stVersion;
        if (instant || Reduced)
        {
            _st.StopAnimation("Opacity"); _st.IsVisible = false; _st.Opacity = 1;
            foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); vis.Opacity = 1; }
            return;
        }
        _st.StartAnimation("Opacity", ToScalar(0, 140, EaseOutCss));
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(1, 220, EaseOutCss, 60));
        After(160, () => { if (v == _stVersion && !_stOpen && _st != null) { _st.IsVisible = false; _st.StopAnimation("Opacity"); _st.Opacity = 1; } });
        Log.Info("settings page close");
    }

    private void EnsureStVisuals()
    {
        if (_st != null) return;
        _st = G.Container();
        _st.IsVisible = false;
        _stTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _stLeft = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _stRight = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _stRightView = G.Container(relative: false);
        _stRightView.Clip = G.C.CreateInsetClip();
        _stRightView.Children.InsertAtTop(_stRight);
        foreach (var s in new Visual[] { _stTitle, _stLeft, _stRightView }) _st.Children.InsertAtTop(s);
        _cc!.Children.InsertAtTop(_st);
    }

    /// <summary>三秒读一次后台（第三方软件开着没、自启有没有被别处改）；两下确认三秒后自动取消。</summary>
    private DispatcherQueueTimer MakeStTimer()
    {
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(3);
        t.IsRepeating = true;
        t.Tick += (_, _) =>
        {
            if (_stArmed != null && NowMs - _stArmedAt > 3000) { _stArmed = null; BuildStRows(); PaintSt(); }
            if (_stOpen && !_settingsGesture.Active) _ = StRefresh();
        };
        return t;
    }

    // ---------------- 和 agent 打交道 ----------------

    private async Task StRefresh()
    {
        if (_stBusy) return;
        _stBusy = true;
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.Call("get_settings_state") is JsonObject o) _stView = (JsonObject)o.DeepClone();
            _stAgentDown = false;
        }
        catch (Exception ex)
        {
            if (!_stAgentDown) Log.Warn($"settings refresh: {ex.Message}");
            _stAgentDown = true;
        }
        finally { _stBusy = false; }
        if (!_stOpen) return;
        BuildStRows();
        PaintSt();
        if (_gsItem == null && _stCat == 1 && !_lgPolling) await LgRefresh();
        if (_gsItem == null && _stCat == 3 && _mgPrefs == null && !_mgLoading) await MgLoad();
        if (_gsItem == null && _stCat == 4 && !_lsBusy) await LsLoad();
        if (_gsItem == null && _stCat == StOsdCat) await OsdLoad();
    }

    /// <summary>交给后台的动作：等它做完，按结果给提示，再读一次。</summary>
    private async Task StRun(string method, Func<JsonNode?, string>? done, params JsonNode?[] args)
    {
        try
        {
            _agent ??= new AgentClient();
            var r = await _agent.Call(method, args);
            if (done != null) Toast(done(r));
        }
        catch (Exception ex) { Toast(ex.Message.Contains("不支持的后台调用") ? "工具箱后台版本太旧，先更新后台" : $"没有做成：{ex.Message}"); }
        if (_stOpen) await StRefresh();
    }

    private bool StConfirm(string key)
    {
        if (_stArmed == key && NowMs - _stArmedAt <= 3000) { _stArmed = null; return true; }
        _stArmed = key;
        _stArmedAt = NowMs;
        BuildStRows();
        PaintSt();
        return false;
    }

    // ---------------- 右栏的行 ----------------

    private int FirstStRow() => Math.Max(0, _stRows.FindIndex(r => !r.Section && !r.Skip));

    private PerfRow StToggle(string title, bool on, Action<bool> set, bool? def = null, string note = "") => new()
    {
        Title = title, Choices = new[] { "关", "开" }, Choice = on ? 1 : 0, Value = note,
        Changed = def != null && on != def, Reset = def is bool d ? () => { if (on != d) set(d); } : null,
        Step = d => { if ((d > 0) != on) set(d > 0); },
        Press = () => set(!on),
        Select = i => set(i == 1),
    };

    private PerfRow StPick(string title, string[] names, int idx, Action<int> set, int? def = null, string note = "")
    {
        idx = Math.Clamp(idx, 0, names.Length - 1);
        var row = new PerfRow
        {
            Title = title, Value = note, Changed = def != null && idx != def,
            Reset = def is int d ? () => { if (idx != d) set(d); } : null,
            Step = d => { int ni = Math.Clamp(idx + d, 0, names.Length - 1); if (ni != idx) set(ni); },
        };
        if (names.Length <= 4) { row.Choices = names; row.Choice = idx; row.Press = () => set((idx + 1) % names.Length); row.Select = set; }
        else { row.Value = names[idx]; row.Numeric = true; row.Press = () => set((idx + 1) % names.Length); }
        return row;
    }

    private PerfRow StAction(string title, string key, string idle, Action run) =>
        new() { Title = title, Value = _stArmed == key ? "再按一次确认" : idle, Press = run };

    private static PerfRow StInfo(string title, string value) => new() { Title = title, Value = value, Disabled = true };
    private static PerfRow StValue(string title, string value) => new() { Title = title, Value = value };

    private void BuildStRows()
    {
        _stRows.Clear();
        var v = _stView;
        if (_stBrowse != null) { BuildStBrowse(); return; }
        if (_lgPage != LgPage.None) { BuildLgRows(); return; }
        string later = _stAgentDown ? "连不上工具箱后台" : "正在读取…";
        if (_gsItem != null) BuildGsRows();
        else switch (_stCat)
        {
            case 0: BuildStGeneral(v, later); break;
            case 1: BuildStLibrary(); break;
            case 2: BuildStLook(); break;
            case 3: BuildStMagpie(); break;
            case 4: BuildStLossless(); break;
            case 5: BuildStSound(v, later); break;
            case 6: BuildStServices(); break;
            case 7: BuildStStartup(v, later); break;
            case 8: BuildStVendors(v, later); break;
            case 9: BuildStMaintenance(v, later); break;
            case 10: BuildStSteam(v, later); break;
            case StOsdCat: BuildStOsd(); break;
            default: BuildStAbout(v); break;
        }
        if (_stEditTitle != null)
            foreach (var r in _stRows)
                if (r.Title == _stEditTitle) { r.Value = ""; r.Choices = null; }
        if (_stFocus >= _stRows.Count || (_stFocus >= 0 && _stFocus < _stRows.Count && (_stRows[_stFocus].Section || _stRows[_stFocus].Skip)))
        {
            int f = Math.Min(_stFocus, _stRows.Count - 1);
            while (f > 0 && (_stRows[f].Section || _stRows[f].Skip)) f--;
            _stFocus = f >= 0 && f < _stRows.Count && !_stRows[f].Section && !_stRows[f].Skip ? f : FirstStRow();
        }
    }

    private void Repaint() { BuildStRows(); PaintSt(); }

    private long _navHapticAt;

    /// <summary>游戏库里的导航震动（和 Steam 大屏幕同一套样式，后台出震动）；连按时 40 ms 内只震一次。</summary>
    private void NavHaptic(string pattern)
    {
        if (!_prefs.NavHaptics || NowMs - _navHapticAt < 40) return;
        _navHapticAt = NowMs;
        _ = Task.Run(async () =>
        {
            try { _agent ??= new AgentClient(); await _agent.Call("library_haptic", JsonValue.Create(pattern)); }
            catch { }
        });
    }

    // ---------------- 选游戏文件夹（手柄能用的文件夹浏览） ----------------

    /// <summary>null = 没在浏览；"" = 磁盘列表；其余 = 当前文件夹。</summary>
    private string? _stBrowse;
    private readonly Dictionary<string, int> _stFolderCounts = new();

    private async Task StCountFolder(string folder)
    {
        _stFolderCounts[folder] = await Task.Run(() => FolderGames.Count(folder));
        if (_stOpen && _stCat == 1 && _stBrowse == null) Repaint();
    }

    private void StBrowseTo(string? path)
    {
        _stBrowse = path;
        if (path == null) { _stBrowseExe = false; _stBrowseMusic = false; _stBrowsePick = null; _stBrowseLossless = false; }
        _stZone = 1;
        _stArmed = null;
        BuildStRows();
        _stFocus = FirstStRow();
        _stScroll = 0; _stScrollY = float.NaN;
        PaintSt();
    }

    private void BuildStBrowse()
    {
        string path = _stBrowse!;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (_stBrowsePick != null && Directory.Exists(desktop) && !string.Equals(path, desktop, StringComparison.OrdinalIgnoreCase))
            _stRows.Add(new PerfRow { Title = "桌面快捷方式", Value = "浏览  ›", Press = () => StBrowseTo(desktop) });
        _stRows.Add(new PerfRow { Section = true, Title = path.Length == 0 ? "选择磁盘" : path });
        if (path.Length == 0)
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
            {
                string root = d.Name;
                string label = string.IsNullOrEmpty(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "可移动磁盘" : "本地磁盘") : d.VolumeLabel;
                _stRows.Add(new PerfRow { Title = root.TrimEnd('\\'), Value = $"{label} · 剩 {d.AvailableFreeSpace >> 30} GB", Press = () => StBrowseTo(root) });
            }
            return;
        }
        if (_stBrowseMusic) { BuildMusicBrowse(path); return; }
        if (_stBrowseExe || _stBrowseLossless)
        {
            // 添加游戏：子文件夹在前，下面是这一层的 exe，选中一个就进确认页
            string[] subs;
            try { subs = Directory.GetDirectories(path).Where(d => (new DirectoryInfo(d).Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0).OrderBy(d => d, StringComparer.CurrentCulture).Take(400).ToArray(); }
            catch (Exception ex) { _stRows.Add(StInfo("打不开这个文件夹", ex.Message)); return; }
            foreach (var d in subs) _stRows.Add(new PerfRow { Title = Path.GetFileName(d), Value = "›", Press = () => StBrowseTo(d) });
            int before = _stRows.Count;
            if (_stBrowseLossless) BuildLsBrowseExes(path); else BuildStBrowseExes(path);
            if (subs.Length == 0 && _stRows.Count == before) _stRows.Add(StInfo("（空）", ""));
            return;
        }
        bool added = _prefs.Folders.Any(f => string.Equals(f.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        _stRows.Add(new PerfRow
        {
            Title = "✓ 用这个文件夹", Value = added ? "已添加" : "",
            Press = () =>
            {
                if (added) return;
                string folder = path.TrimEnd('\\');
                if (folder.Length == 2) folder += "\\";   // 整个盘
                SaveUi(_prefs with { Folders = _prefs.Folders.Append(folder).ToArray() });
                _stNeedsReload = true;
                int n = FolderGames.Count(folder);
                _stFolderCounts[folder] = n;
                Toast(n > 0 ? $"已添加：找到 {n} 款游戏" : "已添加，但里面没找到带 exe 的子文件夹");
                StBrowseTo(null);
            },
        });
        string[] dirs;
        try { dirs = Directory.GetDirectories(path).Where(d => (new DirectoryInfo(d).Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0).OrderBy(d => d, StringComparer.CurrentCulture).Take(400).ToArray(); }
        catch (Exception ex) { _stRows.Add(StInfo("打不开这个文件夹", ex.Message)); return; }
        foreach (var d in dirs) _stRows.Add(new PerfRow { Title = Path.GetFileName(d), Value = "›", Press = () => StBrowseTo(d) });
        if (dirs.Length == 0) _stRows.Add(StInfo("（没有子文件夹）", ""));
    }

    /// <summary>强调色变了：开着的页重画。</summary>
    private void CcRepaintPages() { if (_stOpen) PaintSt(); if (_shOpen) ShPaintAll(); }

    private void BuildStGeneral(JsonObject? v, string later)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "开机" });
        BuildStBootMode();
        if (ShellMode.Available)
            _stRows.Add(StToggle("游戏模式窗口管理（试验）", _prefs.WindowManager, on => { SaveUi(_prefs with { WindowManager = on }); Repaint(); }, false));
        if (ShellMode.Available)
            _stRows.Add(StToggle("游戏模式保留系统输入法（试验）", ShellMode.KeepExplorer, on =>
            {
                if (_shellBusy) return;
                _shellBusy = true;
                _ = ShellMode.Run("set-covered", on ? "on" : "off").ContinueWith(t => _dq.TryEnqueue(() =>
                {
                    _shellBusy = false;
                    Toast(t.Result == 0 ? (on ? "下次进入游戏模式时保留输入法" : "下次进入游戏模式时关闭桌面") : ShellMode.Why());
                    if (_stOpen) Repaint();
                }));
            }, false));
        // 游戏模式不经过 explorer，全屏体验只在桌面模式下有意义
        if (ShellMode.Available && ShellMode.DefaultGame) { }
        else if (v?["home"] is JsonObject home && home["choices"] is JsonArray choices)
        {
            // 关 / Claw 游戏库 / Steam 大屏幕 / Xbox（/ 现在装着的其他主页应用，比如 AnyFSE）
            const string own = "ClawToolbox.Library_ba3r68zr466za!App";
            var ids = new List<string> { "off" };
            var names = new List<string> { "关" };
            if (choices.OfType<JsonObject>().Any(c => Str(c["id"]) == own))
            {
                ids.AddRange(new[] { own, "steam" });
                names.AddRange(new[] { "Claw 游戏库", "Steam 大屏幕" });
            }
            foreach (var c in choices.OfType<JsonObject>())
                if (Str(c["id"]) is string id && id != own) { ids.Add(id); names.Add(Str(c["name"]) ?? id); }
            string current = Str(home["current"]) ?? "";
            string sel = home["startup"] is JsonValue sv && !Bool(sv) ? "off" : current == own ? (_prefs.FseSteam ? "steam" : own) : current;
            if (sel != "off" && !ids.Contains(sel))
            {
                ids.Add("unavailable"); names.Add("未生效"); sel = "unavailable";
            }
            int idx = Math.Max(0, ids.IndexOf(sel));
            _stRows.Add(StPick("全屏体验", names.ToArray(), idx, i =>
            {
                string pick = ids[i];
                if (pick == "unavailable") return;
                string aumid = pick == "steam" ? own : pick;
                _ = StRun("set_home_app", r =>
                {
                    SaveUi(_prefs with { FseSteam = pick == "steam" });
                    if (_stView != null && r is JsonObject updated) _stView["home"] = updated.DeepClone();
                    return pick == "off" ? "开机不再进入全屏体验" : $"全屏体验主页：{names[i]}";
                }, JsonValue.Create(aumid));
            }));
            if (Str(home["error"]) is string error) _stRows.Add(StInfo("全屏体验状态", error));
        }
        else _stRows.Add(StInfo("全屏体验", later));
        _stRows.Add(StPick("首页", BootNames, (int)_prefs.Boot, i => { SaveUi(_prefs with { Boot = (BootTarget)i }); Repaint(); }, 0));
        if (v?["autostart"] is JsonValue)
        {
            bool on = Bool(v["autostart"]);
            _stRows.Add(StToggle("工具箱后台自启动", on, to =>
            {
                v["autostart"] = to;
                Repaint();
                _ = StRun("set_autostart", _ => to ? "工具箱后台开机会自动运行" : "工具箱后台开机不再自动运行", JsonValue.Create(to));
            }, true));
        }
        else _stRows.Add(StInfo("工具箱后台自启动", v == null ? later : "没有找到登录任务"));
        _stRows.Add(new PerfRow { Section = true, Title = "打开游戏库时" });
        _stRows.Add(StToggle("在托盘启动 Steam", _prefs.PreloadSteam, on =>
        {
            SaveUi(_prefs with { PreloadSteam = on }); Repaint();
            if (on) _ = RunServiceAction(() => PlatformClients.StartSteamInTray());
        }, true));
        _stRows.Add(StAction("启动 Steam 至托盘", "steam-tray", _serviceActionBusy ? "处理中…" : "", () =>
            _ = RunServiceAction(() => PlatformClients.StartSteamInTray())));
        _stRows.Add(new PerfRow { Section = true, Title = "后台运行" });
        _stRows.Add(StToggle("关闭窗口后留在托盘", _prefs.BackgroundRun,
            on => { SaveUi(_prefs with { BackgroundRun = on }); Repaint(); }, true));
        _stRows.Add(StAction("转入后台", "background", "", () => (_window as MainWindow)?.RunInBackground()));
        // 游戏模式下游戏库就是外壳：退出会被 ClawShell 立刻拉回来，不给这一项
        if (!ShellMode.Available || !ShellMode.GameMode) _stRows.Add(StAction("退出游戏库", "exit-library", "", () => (_window as MainWindow)?.ExitLibrary()));
        _stRows.Add(new PerfRow { Section = true, Title = "语言" });
        // 换语言要重排所有已经画好的文字：存下来以后整个游戏库重启一次
        _stRows.Add(StPick("界面语言", new[] { "简体中文", "English" }, I18n.En ? 1 : 0, i =>
        {
            SaveUi(_prefs with { Lang = i == 1 ? "en-US" : "zh-CN" });
            if ((i == 1) != I18n.En) RestartLibrary();
        }, 0));
    }

    private void BuildStLibrary()
    {
        _stRows.Add(new PerfRow { Section = true, Title = "显示" });
        _stRows.Add(StPick("首页样式", CsStyleOrder.Select(CsStyleName).ToArray(), Array.IndexOf(CsStyleOrder, _prefs.Home), i => { CsSetHomeStyle(CsStyleOrder[i]); Repaint(); }, 0));
        _stRows.Add(StToggle("插卡音效", _prefs.InsertSound, on => { SaveUi(_prefs with { InsertSound = on }); Repaint(); }, true));
        _stRows.Add(StToggle("竖条信息栏", _prefs.LibraryRail, on => { SetLibraryRail(on); Repaint(); }, true));
        _stRows.Add(StPick("竖栏 Steam 按钮", new[] { "Steam 库", "Steam 大屏幕模式" }, (int)_prefs.SteamShortcut,
            i => { SaveUi(_prefs with { SteamShortcut = (SteamShortcutTarget)i }); Repaint(); }, 0));
        _stRows.Add(StPick("Steam 游戏显示范围", new[] { "所有游戏", "已安装游戏" }, _prefs.SteamInstalledOnly ? 1 : 0,
            i => SetSteamInstalledOnly(i == 1), 0));
        var tabs = _allTabs ?? _lib.HomeTabs;
        var visible = _lib.HomeTabs;
        int start = Math.Max(0, visible.FindIndex(t => t.Id == _prefs.StartTab));
        _stRows.Add(StPick("默认集合", visible.Select(t => t.Label).ToArray(), start, i => { SaveUi(_prefs with { StartTab = visible[i].Id }); Repaint(); }, 0));

        _stRows.Add(new PerfRow { Section = true, Title = "集合" });
        var ordered = tabs.OrderBy(t => _prefs.TabOrder.ToList().IndexOf(t.Id) is int i && i >= 0 ? i : 99 + tabs.IndexOf(t)).ToList();
        for (int k = 0; k < ordered.Count; k++)
        {
            var tab = ordered[k];
            int pos = k;
            bool hidden = _prefs.TabsHidden.Contains(tab.Id);
            _stRows.Add(new PerfRow
            {
                Title = tab.Label + "位置", Numeric = true, EditId = "collection-order:" + tab.Id,
                Value = $"第 {pos + 1} 个",
                Changed = hidden || (_prefs.TabOrder.Length > 0 && !_prefs.TabOrder.Take(ordered.Count).SequenceEqual(tabs.Select(t => t.Id))),
                Step = d =>
                {
                    int to = Math.Clamp(pos + d, 0, ordered.Count - 1);
                    if (to == pos) return;
                    var ids = ordered.Select(t => t.Id).ToList();
                    ids.RemoveAt(pos); ids.Insert(to, tab.Id);
                    SaveUi(_prefs with { TabOrder = ids.ToArray() });
                    ApplyTabPrefs(keepCurrent: true); RepaintRail();
                    _stFocus += (to - pos) * 2;
                    Repaint();
                },
                Reset = () => { SaveUi(_prefs with { TabOrder = Array.Empty<string>(), TabsHidden = Array.Empty<string>() }); ApplyTabPrefs(keepCurrent: true); RepaintRail(); Repaint(); },
            });
            _stRows.Add(StPick(tab.Label + "可见性", new[] { "显示", "隐藏" }, hidden ? 1 : 0, choice =>
            {
                var hiddenNow = _prefs.TabsHidden.ToList();
                if (choice == 0) hiddenNow.Remove(tab.Id);
                else if (tabs.Count - hiddenNow.Count <= 1) { Toast("至少要留一个集合"); return; }
                else if (!hiddenNow.Contains(tab.Id)) hiddenNow.Add(tab.Id);
                SaveUi(_prefs with { TabsHidden = hiddenNow.ToArray() });
                ApplyTabPrefs(keepCurrent: true); RepaintRail(); Repaint();
            }, 0));
        }

        string reload = "";
        _stRows.Add(new PerfRow { Section = true, Title = "游戏来源 · 重新载入后生效" });
        _stRows.Add(StAction("账户与服务", "services", "Steam · Xbox", () =>
        {
            _stCat = 6; _stFocus = 0; _stScroll = 0; _stScrollY = float.NaN;
            BuildStRows(); _stFocus = FirstStRow(); PaintSt();
        }));
        // 加进 Steam 的非 Steam 游戏就是本地游戏（和设置里添加的一样），一直显示，不再单独开关
        foreach (var folder in _prefs.Folders)
        {
            string key = "folder:" + folder;
            int count = _stFolderCounts.TryGetValue(folder, out var c) ? c : -1;
            _stRows.Add(StAction(folder, key, count < 0 ? "" : $"{count} 款", () =>
            {
                if (!StConfirm(key)) return;
                SaveUi(_prefs with { Folders = _prefs.Folders.Where(f => f != folder).ToArray() });
                _stNeedsReload = true;
                Toast($"已移除：{folder}");
                Repaint();
            }));
            if (count < 0) _ = StCountFolder(folder);
        }
        _stRows.Add(StAction("添加游戏文件夹", "addfolder", "", () => StBrowseTo("")));
        _stRows.Add(StToggle("显示 Steam 隐藏游戏", _prefs.ShowHiddenGames, on => { SaveUi(_prefs with { ShowHiddenGames = on }); _stNeedsReload = true; Repaint(); }, false, reload));
        _stRows.Add(StAction("重新载入游戏库", "reload", _stNeedsReload ? "待重新载入" : "", RestartLibrary));

        BuildLgEntryRows();

        _stRows.Add(new PerfRow { Section = true, Title = "封面 · 将重新载入游戏库" });
        long bytes = 0;
        try { if (Directory.Exists(ArtRoot)) bytes = new DirectoryInfo(ArtRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
        _stRows.Add(StAction("重新生成全部封面", "art", $"{bytes >> 20} MB", () =>
        {
            if (!StConfirm("art")) return;
            try { Directory.CreateDirectory(App.CacheDir); File.WriteAllText(Path.Combine(App.CacheDir, "art.reset"), "1"); } catch { }
            RestartLibrary();
        }));

        _stRows.Add(new PerfRow { Section = true, Title = "启动游戏后" });
        _stRows.Add(StPick("游戏库", new[] { "保持", "转入后台并暂停" }, (int)_prefs.AfterLaunch, i => { SaveUi(_prefs with { AfterLaunch = (AfterLaunch)i }); Repaint(); }, 0));
        _stRows.Add(StPick("游戏中回到游戏库", new[] { "正在游戏面板", "墙面 + 顶部提示" }, (int)_prefs.NowPlaying, i => { SaveUi(_prefs with { NowPlaying = (NowPlayingStyle)i }); Repaint(); }, 0));
    }

    /// <summary>
    /// 莫奈 / 漫游的图：跟着游戏封面换，或者固定用一张本地壁纸。选壁纸用 Windows 自己的选文件窗口（手柄、触屏、鼠标都能用）；
    /// 第一次切到「本地壁纸」还没选过图就直接打开它。
    /// </summary>
    private void AddWallpaperRows(BackgroundSettings b, Action<BackgroundSettings> setBg)
    {
        _stRows.Add(StPick("背景图片", new[] { "游戏封面", "本地壁纸" }, b.UseWallpaper ? 1 : 0, i =>
        {
            if (i == 1 && (b.Wallpaper.Length == 0 || !File.Exists(b.Wallpaper))) _ = PickWallpaper(setBg);
            else setBg(b with { UseWallpaper = i == 1 });
        }, 0));
        if (b.UseWallpaper)
            _stRows.Add(new PerfRow { Title = "选择壁纸…", Value = b.Wallpaper.Length > 0 ? Path.GetFileName(b.Wallpaper) : "", Press = () => _ = PickWallpaper(setBg) });
    }

    private async Task PickWallpaper(Action<BackgroundSettings> setBg)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };
            foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            setBg(_backgroundSettings with { UseWallpaper = true, Wallpaper = file.Path });
            Toast("已换成本地壁纸");
        }
        catch (Exception ex) { Log.Warn($"pick wallpaper: {ex.Message}"); Toast("没能打开选文件窗口"); }
    }

    private void BuildStLook()
    {
        _stRows.Add(new PerfRow { Section = true, Title = "标题" });
        _stRows.Add(StPick("标题样式", TitleStyles.Names, Array.IndexOf(TitleStyles.Values, Style),
            i => { SetTitleStyle(TitleStyles.Values[i]); Repaint(); }, 0));
        var b = _backgroundSettings;
        void SetBg(BackgroundSettings next) { _backgroundSettings = next; ApplyBackgroundSettings(); Repaint(); }
        _stRows.Add(new PerfRow { Section = true, Title = "背景" });
        var kinds = new[] { BackgroundKind.Latent, BackgroundKind.Monet, BackgroundKind.Nomand, BackgroundKind.Filmstrip };
        _stRows.Add(StPick("背景样式", new[] { "隐现", "莫奈", "漫游", "胶片封面墙" }, Array.IndexOf(kinds, b.Kind), i => SetBg(b with { Kind = kinds[i] }), 0));
        switch (b.Kind)
        {
            case BackgroundKind.Latent:
                _stRows.Add(StPick("显示模式", new[] { "混合", "像素", "流体" }, (int)b.Latent, i => SetBg(b with { Latent = (LatentLayers)i }), 0));
                break;
            case BackgroundKind.Nomand:
                _stRows.Add(StPick("背景效果", new[] { "镜头畸变", "纸张纹理" }, (int)b.Nomand, i => SetBg(b with { Nomand = (NomandEffect)i }), 0));
                AddWallpaperRows(b, SetBg);
                break;
            case BackgroundKind.Monet:
                _stRows.Add(StToggle("色块漂移", b.MonetDrift, on => SetBg(b with { MonetDrift = on }), true));
                _stRows.Add(StToggle("光带", b.MonetStreaks, on => SetBg(b with { MonetStreaks = on }), true));
                AddWallpaperRows(b, SetBg);
                break;
            case BackgroundKind.Filmstrip:
                _stRows.Add(StPick("封面排列", new[] { "横向条带", "竖向条带" }, (int)b.FilmLayout, i => SetBg(b with { FilmLayout = (FilmLayout)i }), 0));
                _stRows.Add(StNumber("条带数量", b.FilmStripCount, 0, 16, 1, n => n == 0 ? "自动" : $"{n} 条", n => SetBg(b with { FilmStripCount = n == 0 ? 0 : Math.Max(3, n) }), 0));
                _stRows.Add(StNumber("滚动速度", b.FilmSpeed, 25, 200, 25, n => $"{n} %", n => SetBg(b with { FilmSpeed = n }), 100));
                _stRows.Add(StNumber("倾斜角度", b.FilmAngle, 0, 45, 5, n => $"{n}°", n => SetBg(b with { FilmAngle = n }), 15));
                _stRows.Add(StNumber("压暗", b.FilmDarkness, 0, 90, 10, n => $"{n} %", n => SetBg(b with { FilmDarkness = n }), 70));
                break;
        }
        _stRows.Add(StToggle("动态背景", _backdropOn, on => { _backdropOn = on; UpdateBackdropRunning(); SaveUi(_prefs with { Backdrop = on }); Repaint(); }, true));
        // 卡盒版首页、分类页、游戏设置、屏保用当前游戏的横幅；关掉后它们也用上面的样式
        if (_cs != null) _stRows.Add(StToggle("游戏页面用横幅", _backgroundSettings.GameBanner, on => { SetGameBanner(on); Repaint(); }, true));

        // 墙面（folia 的海报灯光、海报叠色）
        void SetLook(UiPrefs next) { SaveUi(next); ApplyWallLook(); Repaint(); }
        _stRows.Add(new PerfRow { Section = true, Title = "墙面" });
        _stRows.Add(StToggle("竖条信息栏", _prefs.LibraryRail, on => { SetLibraryRail(on); Repaint(); }, true));
        _stRows.Add(StPick("海报灯光", new[] { "开灯", "关灯" }, _prefs.LightsOn ? 0 : 1, i => SetLook(_prefs with { LightsOn = i == 0 }), 0));
        _stRows.Add(StToggle("海报叠色", _prefs.PosterTint, on => SetLook(_prefs with { PosterTint = on }), true));
        if (_prefs.PosterTint)
        {
            _stRows.Add(StToggle("自定义叠色", _prefs.PosterTintCustom, on => SetLook(_prefs with { PosterTintCustom = on }), false));
            if (_prefs.PosterTintCustom)
            {
                int sw = Math.Max(0, Array.FindIndex(TintSwatches, s => s.Hex == _prefs.PosterTintColor));
                _stRows.Add(StPick("叠色颜色", TintSwatches.Select(s => s.Name).ToArray(), sw, i => SetLook(_prefs with { PosterTintColor = TintSwatches[i].Hex }), 0));
            }
            _stRows.Add(StNumber("叠色强度", _prefs.PosterTintIntensity, 0, 100, 5, n => $"{n} %", n => SetLook(_prefs with { PosterTintIntensity = n }), 50));
        }

        _stRows.Add(new PerfRow { Section = true, Title = "屏幕保护" });
        _stRows.Add(StToggle("封面灯光屏保", _prefs.Screensaver, on =>
        {
            SaveUi(_prefs with { Screensaver = on }); MarkInput(); Repaint();
        }, false, ""));
        if (_prefs.Screensaver)
        {
            _stRows.Add(StNumber("闲置后进入", _prefs.ScreensaverIdleSeconds, 15, 900, 15,
                n => n < 60 ? $"{n} 秒" : $"{n / 60} 分 {n % 60} 秒",
                n => { SaveUi(_prefs with { ScreensaverIdleSeconds = n }); MarkInput(); Repaint(); }, 60));
            _stRows.Add(StNumber("封面切换间隔", _prefs.ScreensaverIntervalSeconds, 3, 60, 1,
                n => $"每 {n} 秒", n => { SaveUi(_prefs with { ScreensaverIntervalSeconds = n }); Repaint(); }, 6));
            _stRows.Add(StAction("预览屏保", "screensaver-preview", "任意输入退出", () =>
            {
                CloseControlCenter(); After(1400, () => EnterScreensaver());
            }));
        }

        _stRows.Add(new PerfRow { Section = true, Title = "界面" });
        _stRows.Add(StPick("控制中心卡片", new[] { "拍立得", "卡片" }, _ccCards ? 1 : 0, i => { SetCcCardStyle(i == 1); Repaint(); }, 0));
        _stRows.Add(StPick("截图页排版", ShStyleNames, _prefs.ShotsStyle, i => { ShSetStyle((ShStyle)i); Repaint(); }, 0));
        _stRows.Add(StToggle("游戏库显示封面", _prefs.LibraryCover, on =>
        {
            SaveUi(_prefs with { LibraryCover = on });
            CcReadData();
            foreach (var blk in _ccBlocks) CcPaintCard(blk);
            CcSetCover(_ccBlocks[0]);
            Repaint();
        }, false));
        var accentNames = new[] { "跟随封面" }.Concat(UiPrefs.Accents.Select(a => a.Name)).ToArray();
        _stRows.Add(StPick("强调色", accentNames, _prefs.Accent + 1, i => { SaveUi(_prefs with { Accent = i - 1 }); Repaint(); CcRepaintPages(); }, 0));
        _stRows.Add(StToggle("降低动态效果", _reducedLocal, on => { _reducedLocal = on; SaveUi(_prefs with { Reduced = on }); Repaint(); }, false));
    }

    private PerfRow StNumber(string title, int value, int min, int max, int step, Func<int, string> fmt, Action<int> set, int def) => new()
    {
        Title = title, Value = fmt(value), Numeric = true, Changed = value != def,
        Number = value, Minimum = min, Maximum = max, Increment = step, SetNumber = set,
        Reset = () => { if (value != def) set(def); },
        Step = d => { int nv = Math.Clamp(value + d * step, min, max); if (nv != value) set(nv); },
    };

    private void BuildStSound(JsonObject? v, string later)
    {
        BuildAudioRows();
        _stRows.Add(new PerfRow { Section = true, Title = "震动" });
        _stRows.Add(StToggle("导航震动", _prefs.NavHaptics, on => { SaveUi(_prefs with { NavHaptics = on }); if (on) NavHaptic("confirm"); Repaint(); }, true));
        _stRows.Add(new PerfRow { Section = true, Title = "全局提示" });
        if (v?["notifications"] is JsonObject notices)
        {
            foreach (var (key, title) in new[] { ("power", "充电与电池供电提示"), ("mode", "手柄与桌面模式切换提示") })
                _stRows.Add(StToggle(title, Bool(notices[key]), on =>
                {
                    notices[key] = on; Repaint();
                    _ = StRun("set_notification", null, JsonValue.Create(key), JsonValue.Create(on));
                }, true));
        }
        else _stRows.Add(StInfo("全局提示", later));
    }

    /// <summary>Steam 插件（Decky 里的工具箱）管的事：大屏幕里快设键、Claw 键接管成左右侧栏，Steam 以管理员身份启动，大屏幕导航震动。</summary>
    private void BuildStSteam(JsonObject? v, string later)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "大屏幕按键" });
        if (v?["steamKeys"] is JsonObject keys)
        {
            bool on = Bool(keys["enabled"]);
            string qam = Str(keys["qamAction"]) ?? "MainMenu", msi = Str(keys["msiAction"]) ?? "QuickAccess";
            void Save(bool en, string q, string m)
            {
                keys["enabled"] = en; keys["qamAction"] = q; keys["msiAction"] = m;
                Repaint();
                _ = StRun("set_steam_keys", null, JsonValue.Create(en), JsonValue.Create(q), JsonValue.Create(m));
            }
            string[] sides = { "MainMenu", "QuickAccess" }, sideNames = { "左侧栏", "右侧栏" };
            _stRows.Add(StToggle("接管快设键和 Claw 键", on, to => Save(to, qam, msi), true));
            var q = StPick("快设键", sideNames, Math.Max(0, Array.IndexOf(sides, qam)), i => Save(on, sides[i], msi), 0);
            var m = StPick("Claw 键", sideNames, Math.Max(0, Array.IndexOf(sides, msi)), i => Save(on, qam, sides[i]), 1);
            q.Disabled = m.Disabled = !on;
            _stRows.Add(q);
            _stRows.Add(m);
        }
        else _stRows.Add(StInfo("接管快设键和 Claw 键", later));

        _stRows.Add(new PerfRow { Section = true, Title = "Steam" });
        if (v?["compatibility"] is JsonObject c && c["enabled"] is JsonObject en)
        {
            bool busy = Bool(c["busy"]);
            bool on = Bool(en["steam"]);
            string status = Str((c["items"] as JsonObject)?["steam"]) ?? "";
            string note = busy ? "处理中" : status.StartsWith("需要处理") ? "没生效" : "";
            var row = StToggle("以管理员身份启动", on, to =>
            {
                en["steam"] = to;
                Repaint();
                _ = StRun("manage_compatibility", null, JsonValue.Create("set"), JsonValue.Create("steam"), JsonValue.Create(to));
            }, false, note.Length > 0 ? note : "重启 Steam 后生效");
            row.Disabled = busy;
            _stRows.Add(row);
            if (status.StartsWith("需要处理")) _stRows.Add(StInfo("没生效的原因", status.Replace("需要处理：", "")));
        }
        else _stRows.Add(StInfo("以管理员身份启动", later));
        if (v?["haptics"] is JsonValue)
        {
            bool on = Bool(v["haptics"]);
            _stRows.Add(StToggle("大屏幕导航震动", on, to => { v["haptics"] = to; Repaint(); _ = StRun("set_haptics_enabled", null, JsonValue.Create(to)); }, true));
        }
        else _stRows.Add(StInfo("大屏幕导航震动", later));
    }

    private void BuildStStartup(JsonObject? v, string later)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "开机自启" });
        if (v?["startup"] is not JsonArray items) { _stRows.Add(StInfo("自启列表", later)); return; }
        foreach (var it in items.OfType<JsonObject>())
        {
            string id = Str(it["id"]) ?? "", name = Str(it["name"]) ?? id;
            bool on = Bool(it["enabled"]);
            if (Str(it["protected"]) is string why) { _stRows.Add(StValue(name, "开")); continue; }
            _stRows.Add(StToggle(name, on, to =>
            {
                it["enabled"] = to;
                Repaint();
                _ = StRun("set_startup_item", _ => to ? $"{name}：开机自动运行" : $"{name}：开机不再自动运行", JsonValue.Create(id), JsonValue.Create(to));
            }));
        }
    }

    private void BuildStVendors(JsonObject? v, string later)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "第三方软件" });
        if (v?["vendors"] is not JsonArray apps) { _stRows.Add(StInfo("第三方软件", later)); return; }
        foreach (var app in apps.OfType<JsonObject>())
        {
            string id = Str(app["id"]) ?? "", name = Str(app["name"]) ?? id;
            if (!Bool(app["installed"])) { _stRows.Add(StValue(name, "没有安装")); continue; }
            bool off = Bool(app["disabled"]);
            int running = Int(app["running"]);
            string state = off ? "已禁用" : running > 0 ? "运行中" : "未禁用";
            _stRows.Add(new PerfRow
            {
                Title = name, Value = state, Changed = off,
                Press = () =>
                {
                    app["disabled"] = !off;
                    Repaint();
                    _ = StRun("set_vendor_app", _ => off ? $"{name} 已恢复（服务和计划任务打开）" : $"{name} 已禁用，没有卸载", JsonValue.Create(id), JsonValue.Create(!off));
                },
            });
        }
    }

    private bool _backendSetupRunning;

    private async Task UpdateBackendAsync()
    {
        if (_backendSetupRunning) return;
        _backendSetupRunning = true;
        try
        {
            string run = Guid.NewGuid().ToString("N");
            using var process = BackendSetup.Start(run);
            Toast("正在安装或更新后台组件");
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                Toast("后台组件已就绪");
                return;
            }
            string? detail = null;
            if (File.Exists(BackendSetup.StatusPath(run))
                && JsonNode.Parse(File.ReadAllText(BackendSetup.StatusPath(run))) is JsonObject report)
                detail = Str(report["error"]) ?? report.Select(item => item.Value).OfType<JsonObject>()
                    .Where(step => Str(step["state"]) == "failed").Select(step => Str(step["detail"])).FirstOrDefault();
            Toast(detail == null ? "后台更新失败，请查看安装日志" : $"后台更新失败：{detail}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Toast("已取消后台更新");
        }
        catch (Exception ex)
        {
            Log.Warn($"backend setup: {ex.Message}");
            Toast("后台更新失败，请查看安装日志");
        }
        finally { _backendSetupRunning = false; }
    }

    private void BuildStMaintenance(JsonObject? v, string later)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "诊断" });
        _stRows.Add(StToggle("帧时间读数", _hudOn, on => { if (on != _hudOn) ToggleHud(); Repaint(); }, false));
        _stRows.Add(StAction("导出日志", "logs", "", () => _ = StRun("export_logs", r => $"日志已存到桌面：{Str(r?["name"])}")));
        _stRows.Add(StAction("重新运行首次设置", "oobe", "", OpenOobe));
        _stRows.Add(StAction("安装或更新后台", "backend-setup", BackendSetup.InstalledRelease() ?? "", () => _ = UpdateBackendAsync()));
        string version = Str(v?["version"]) ?? "—";
        _stRows.Add(StAction("重启工具箱后台", "restart", _stAgentDown ? "未连接" : "", () =>
        {
            if (!StConfirm("restart")) return;
            Toast("工具箱后台正在重启，几秒后自动连回");
            _ = StRun("restart_agent", null);
        }));

        _stRows.Add(new PerfRow { Section = true, Title = "设置" });
        var backups = (v?["backups"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? Array.Empty<JsonObject>();
        _stRows.Add(StAction("备份设置", "backup", backups.Length > 0 ? $"上次 {Str(backups[0]["time"])}" : "还没有备份",
            () => _ = StRun("backup_settings", _ => "已备份到 文档\\ClawToolbox 备份")));
        if (backups.Length > 0)
        {
            _stBackupPick = Math.Clamp(_stBackupPick, 0, backups.Length - 1);
            string name = Str(backups[_stBackupPick]["name"]) ?? "";
            _stRows.Add(new PerfRow
            {
                Title = "选择备份", Numeric = true,
                Value = Str(backups[_stBackupPick]["time"]) ?? name,
                Step = d => { int ni = Math.Clamp(_stBackupPick + d, 0, backups.Length - 1); if (ni != _stBackupPick) { _stBackupPick = ni; _stArmed = null; Repaint(); } },
            });
            _stRows.Add(StAction("还原所选备份", "restore", "", () =>
            {
                if (StConfirm("restore")) { Toast("正在还原，工具箱后台会重启一下"); _ = StRun("restore_settings", _ => "已还原，后台正在重启", JsonValue.Create(name)); }
            }));
        }
        else _stRows.Add(StInfo("还原设置", v == null ? later : "还没有备份"));
        _stRows.Add(StAction("恢复默认", "reset", "", () =>
        {
            if (!StConfirm("reset")) return;
            Toast("正在恢复默认，工具箱后台会重启一下");
            _ = StRun("reset_settings", r => $"已恢复默认（原来的设置备份在 {Str(r?["backup"])}）");
        }));
    }

    private void BuildStAbout(JsonObject? v)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "版本" });
        _stRows.Add(StValue("游戏库", SyOwnVersion()));
        _stRows.Add(StValue("工具箱后台", Str(v?["version"]) ?? "—"));
        _stRows.Add(StValue("Game Bar 小组件", Str(v?["widget"]) ?? "—"));
        _stRows.Add(new PerfRow { Section = true, Title = "开源许可" });
        _stRows.Add(StValue("工具箱", "MIT"));
        _stRows.Add(StValue("Magpie", "GPL-3.0"));
        _stRows.Add(StValue("Python", "PSF"));
    }

    // ---------------- 输入 ----------------

    private void StMove(Direction dir)
    {
        SettingsResumeKeyboard();
        if (StEditing) return;
        if (_stZone == 1 && GsOverlayMove(dir)) return;
        if (_stZone == 0 && dir == Direction.Right && GsOverlayEnter()) return;
        if (_stZone == 0)
        {
            if (dir is Direction.Up or Direction.Down)
            {
                int next = Math.Clamp(_stCat + (dir == Direction.Up ? -1 : 1), 0, StCats.Length - 1);
                if (next == _stCat) return;
                _stCat = next;
                _stArmed = null;
                _stBrowse = null;
                _stBrowseExe = false;
                _stBrowseMusic = false;
                _stBrowsePick = null;
                _stBrowseLossless = false;
                _lgPage = LgPage.None;
                BuildStRows();
                _stFocus = FirstStRow();
                _stScroll = 0; _stScrollY = float.NaN;
                PaintSt();
            }
            else if (dir == Direction.Right) { _stZone = 1; PaintSt(); }
            return;
        }
        if (dir is Direction.Up or Direction.Down)
        {
            int d = dir == Direction.Up ? -1 : 1;
            for (int i = _stFocus + d; i >= 0 && i < _stRows.Count; i += d)
                if (!_stRows[i].Section && !_stRows[i].Skip) { _stFocus = i; if (_stArmed != null) { _stArmed = null; BuildStRows(); } PaintSt(); return; }
            return;
        }
        var r = _stFocus >= 0 && _stFocus < _stRows.Count ? _stRows[_stFocus] : null;
        if (r != null && !r.Disabled && r.Step != null) { RowStep(r, dir == Direction.Left ? -1 : 1); return; }
        if (dir == Direction.Left) { _stZone = 0; PaintSt(); }
    }

    private void StActivate()
    {
        SettingsResumeKeyboard();
        if (StEditing) { StEditEnd(true); return; }
        if (GsOverlayActivate()) return;
        if (_stZone == 0) { _stZone = 1; PaintSt(); return; }
        if (_stFocus < 0 || _stFocus >= _stRows.Count) return;
        var r = _stRows[_stFocus];
        if (r.Disabled) return;
        RowPress(r);
    }

    private void StBack()
    {
        if (StEditing) { StEditEnd(false); return; }
        if (SettingsFinishEdit()) return;
        if (GsOverlayBack()) return;
        if (_stBrowse != null)
        {
            if (_stBrowse.Length == 0 && _stBrowseFromLibrary) { _stBrowseFromLibrary = false; CloseSettingsToLibrary(); return; }
            // 上一级；在磁盘列表里再按 B 退出浏览
            StBrowseTo(_stBrowse.Length == 0 ? null : Path.GetDirectoryName(_stBrowse.TrimEnd('\\')) is string up && up.Length > 0 ? up : "");
            return;
        }
        if (LgBack()) return;
        if (_gsItem == null && _stCat == 3 && MgBack()) return;
        if (_stZone == 1) { _stZone = 0; _stArmed = null; Repaint(); return; }
        if (_stFromLibrary) CloseSettingsToLibrary(); else CloseSettingsPage();
    }

    /// <summary>Y：焦点这一项恢复默认。</summary>
    private void StResetFocused()
    {
        if (GsPadPicking && GpGroupY()) return;   // 游戏设置 › 手柄的分组页：Y 进这个键的触发设置
        if (_stZone != 1 || _stFocus < 0 || _stFocus >= _stRows.Count || GsOverlay) return;
        var r = _stRows[_stFocus];
        if (r.Reset == null || r.Disabled) { Toast("这一项没有默认值"); return; }
        r.Reset();
        Toast($"「{r.Title}」已恢复默认");
    }

    /// <summary>点左栏的分类。</summary>
    private bool StCategoryClick(double sx, double sy)
    {
        double x = sx / _ccK - PerfLeftX + StGutter, y = sy / _ccK - PerfTop;
        if (x < 0 || x > StLeftW + StGutter || y < PerfSecH) return false;
        int i = (int)Math.Floor((y - PerfSecH) / StCatH);
        if (i < 0 || i >= StCats.Length) return false;
        _settingsEdit.End();
        MarkInput();
        _stCat = i; _stZone = 0; _stArmed = null; _stBrowse = null; _stBrowseExe = false; _stBrowseMusic = false; _stBrowsePick = null; _lgPage = LgPage.None;
        _stBrowseLossless = false;
        if (_gsItem != null && !_gpOpen) { _gpPicker = null; _gsMapFocus = -1; }
        BuildStRows();
        _stFocus = FirstStRow();
        _stScroll = 0; _stScrollY = float.NaN;
        PaintSt();
        return true;
    }

    // ---------------- 绘制 ----------------

    private void PaintSt()
    {
        if (_st == null) return;
        var stSw = System.Diagnostics.Stopwatch.StartNew();
        float k = _ccK, raster = k * Chrome.Dpi;
        float h = RowsHeight(_stRows), vh = PerfViewH;
        using (var tl = TextKit.Layout(G.Device, _gsItem?.Title ?? (_stBrowseFromLibrary ? "添加游戏" : "设置"), TextKit.Ui(700), 24, 900, CanvasHorizontalAlignment.Left))
        {
            var bb = tl.LayoutBounds;
            float w = (float)bb.Width + 4, th = (float)bb.Height + 4;
            CcSet(_stTitle, CcDraw(w, th, raster, ds => ds.DrawTextLayout(tl, -(float)bb.X, -(float)bb.Y, CcText)));
            _stTitle.Size = new Vector2(w * k, th * k);
            _stTitle.Offset = new Vector3(40 * k, 32 * k, 0);
        }
        _settingsTargets.Clear();
        float rw = StRightW;
        CcSet(_stRight, CcDraw(rw, h, raster, ds => PaintRowList(ds, _stRows, _stZone == 1 && _gsMapFocus < 0 ? _stFocus : -1, _settingsTargets, false, rw)));
        _stRight.Size = new Vector2(rw * k, h * k);
        _stRightView.Size = new Vector2(rw * k, vh * k);
        _stRightView.Offset = new Vector3(StRightX * k, PerfTop * k, 0);
        RowScrollTo(_stRight, _stRows, _settingsManualScroll || _stZone == 0 ? -1 : _stFocus, ref _stScroll, ref _stScrollY, h + _stKbLift, vh, false);
        // 左栏往左多出 StGutter：焦点三角画在页边距里，分类名的左边和标题「设置」对齐
        float lw = StLeftW;
        CcSet(_stLeft, CcDraw(lw + StGutter, vh, raster, ds => PaintStNav(ds, vh)));
        _stLeft.Size = new Vector2((lw + StGutter) * k, vh * k);
        _stLeft.Offset = new Vector3((PerfLeftX - StGutter) * k, PerfTop * k, 0);
        PaintGsOverlay(k, raster);
        if (stSw.ElapsedMilliseconds > 40) Log.Info($"settings paint {stSw.ElapsedMilliseconds}ms (cat {_stCat}, {_stRows.Count} rows, {h:F0}px)");
    }
}
