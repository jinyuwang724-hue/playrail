// 控制中心 → 设置：工具箱自己的设置，一个独立的整页（功能点见 docs/settings-page-features.md）。
// 不套 3:7 读数页：左栏是大字分类目录（每个分类下一行摘要），右栏是这个分类的全部选项。
// 改过、和默认不同的项前面有一个小点；Y 把焦点这一项恢复默认。
// 焦点在两栏之间：目录里 ↑↓ 换分类，→ / A 进右栏；右栏 ↑↓ 选项、←→ 改值、A 执行，B（或在没有可调值的行按 ←）回目录，目录里 B 回控制中心。
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
    private static readonly string[] StCats = { "通用", "游戏库", "外观", "声音与反馈", "游戏中", "开机自启", "第三方软件", "维护", "关于" };
    private const float StCatH = 52;

    private UiPrefs _prefs = new();
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
        Library.IncludeShortcuts = _prefs.SourceShortcuts;
        if (!Program.Args.Contains("--style=b")) Style = _prefs.StyleB ? TitleStyle.B : TitleStyle.D;
        _reducedLocal = _prefs.Reduced;
        SoundFx.Enabled = _prefs.Sounds;
        SoundFx.SetVolume(_prefs.SoundVolume);
        // 封面重新生成：上次在设置里点了，这次启动先删掉旧的封面套件（缺的会在后台重新生成）
        var flag = Path.Combine(App.CacheDir, "art.reset");
        if (File.Exists(flag))
        {
            try { if (Directory.Exists(ArtRoot)) Directory.Delete(ArtRoot, true); File.Delete(flag); Log.Info("art cache cleared"); }
            catch (Exception ex) { Log.Warn($"art reset: {ex.Message}"); }
        }
    }

    /// <summary>游戏库载入后：集合的顺序和显示隐藏、开机落在的集合。</summary>
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
    private void AfterGameLaunch()
    {
        if (_prefs.AfterLaunch != AfterLaunch.Hide) return;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        After(1500, () => ShowWindow(hwnd, 6));   // SW_MINIMIZE
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

    private void SetTitleStyle(bool b)
    {
        Style = b ? TitleStyle.B : TitleStyle.D;
        foreach (var t in _tiles.Values) t.Show(t.Target);
        SaveUi(_prefs with { StyleB = b });
    }

    // ---------------- 开 / 关 ----------------

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
        _stOpen = false;
        _stBrowse = null;
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
        string later = _stAgentDown ? "连不上工具箱后台" : "正在读取…";
        switch (_stCat)
        {
            case 0: BuildStGeneral(v, later); break;
            case 1: BuildStLibrary(); break;
            case 2: BuildStLook(); break;
            case 3: BuildStSound(v, later); break;
            case 4: BuildStInGame(v, later); break;
            case 5: BuildStStartup(v, later); break;
            case 6: BuildStVendors(v, later); break;
            case 7: BuildStMaintenance(v, later); break;
            default: BuildStAbout(v); break;
        }
        if (_stFocus >= _stRows.Count || (_stFocus >= 0 && _stFocus < _stRows.Count && (_stRows[_stFocus].Section || _stRows[_stFocus].Skip)))
        {
            int f = Math.Min(_stFocus, _stRows.Count - 1);
            while (f > 0 && (_stRows[f].Section || _stRows[f].Skip)) f--;
            _stFocus = Math.Max(0, f);
        }
    }

    private void Repaint() { BuildStRows(); PaintSt(); }

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
                Toast(n > 0 ? $"已添加：找到 {n} 款游戏（重新载入游戏库后出现）" : "已添加，但里面没找到带 exe 的子文件夹");
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
    private void CcRepaintPages() { if (_stOpen) PaintSt(); }

    private void BuildStGeneral(JsonObject? v, string later)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "开机" });
        if (v?["home"] is JsonObject home && home["choices"] is JsonArray choices)
        {
            var ids = choices.Select(c => Str(c?["id"]) ?? "").ToArray();
            var names = choices.Select(c => Str(c?["name"]) ?? "").ToArray();
            int cur = Array.IndexOf(ids, Str(home["current"]) ?? "");
            _stRows.Add(StPick("全屏体验主页", names, cur < 0 ? 0 : cur, i =>
            {
                home["current"] = ids[i];
                Repaint();
                _ = StRun("set_home_app", _ => $"全屏体验主页：{names[i]}（下次进全屏体验时生效）", JsonValue.Create(ids[i]));
            }));
        }
        else _stRows.Add(StInfo("全屏体验主页", later));
        _stRows.Add(StPick("打开后落在", new[] { "游戏库", "控制中心" }, (int)_prefs.Boot, i => { SaveUi(_prefs with { Boot = (BootTarget)i }); Repaint(); }, 0));
        if (v?["autostart"] is JsonValue)
        {
            bool on = Bool(v["autostart"]);
            _stRows.Add(StToggle("开机自动运行工具箱后台", on, to =>
            {
                v["autostart"] = to;
                Repaint();
                _ = StRun("set_autostart", _ => to ? "工具箱后台开机会自动运行" : "工具箱后台开机不再自动运行", JsonValue.Create(to));
            }, true));
        }
        else _stRows.Add(StInfo("开机自动运行工具箱后台", v == null ? later : "没有找到登录任务"));
        _stRows.Add(new PerfRow { Section = true, Title = "语言" });
        _stRows.Add(StValue("界面语言", "简体中文"));
    }

    private void BuildStLibrary()
    {
        _stRows.Add(new PerfRow { Section = true, Title = "显示" });
        _stRows.Add(StPick("标题样式", new[] { "电影字幕", "极简黑体" }, Style == TitleStyle.B ? 1 : 0, i => { SetTitleStyle(i == 1); Repaint(); }, 0));
        var tabs = _allTabs ?? _lib.HomeTabs;
        var visible = _lib.HomeTabs;
        int start = Math.Max(0, visible.FindIndex(t => t.Id == _prefs.StartTab));
        _stRows.Add(StPick("开机落在的集合", visible.Select(t => t.Label).ToArray(), start, i => { SaveUi(_prefs with { StartTab = visible[i].Id }); Repaint(); }, 0));

        _stRows.Add(new PerfRow { Section = true, Title = "集合" });
        var ordered = tabs.OrderBy(t => _prefs.TabOrder.ToList().IndexOf(t.Id) is int i && i >= 0 ? i : 99 + tabs.IndexOf(t)).ToList();
        for (int k = 0; k < ordered.Count; k++)
        {
            var tab = ordered[k];
            int pos = k;
            bool hidden = _prefs.TabsHidden.Contains(tab.Id);
            _stRows.Add(new PerfRow
            {
                Title = tab.Label, Numeric = true,
                Value = $"第 {pos + 1} 个 · {(hidden ? "隐藏" : "显示")}",
                Changed = hidden || (_prefs.TabOrder.Length > 0 && !_prefs.TabOrder.Take(ordered.Count).SequenceEqual(tabs.Select(t => t.Id))),
                Step = d =>
                {
                    int to = Math.Clamp(pos + d, 0, ordered.Count - 1);
                    if (to == pos) return;
                    var ids = ordered.Select(t => t.Id).ToList();
                    ids.RemoveAt(pos); ids.Insert(to, tab.Id);
                    SaveUi(_prefs with { TabOrder = ids.ToArray() });
                    ApplyTabPrefs(keepCurrent: true); RepaintRail();
                    _stFocus += to - pos;
                    Repaint();
                },
                Press = () =>
                {
                    var hiddenNow = _prefs.TabsHidden.ToList();
                    if (hidden) hiddenNow.Remove(tab.Id);
                    else if (tabs.Count - hiddenNow.Count <= 1) { Toast("至少要留一个集合"); return; }
                    else hiddenNow.Add(tab.Id);
                    SaveUi(_prefs with { TabsHidden = hiddenNow.ToArray() });
                    ApplyTabPrefs(keepCurrent: true); RepaintRail();
                    Repaint();
                },
                Reset = () => { SaveUi(_prefs with { TabOrder = Array.Empty<string>(), TabsHidden = Array.Empty<string>() }); ApplyTabPrefs(keepCurrent: true); RepaintRail(); Repaint(); },
            });
        }

        string reload = "";
        _stRows.Add(new PerfRow { Section = true, Title = "游戏来源" });
        _stRows.Add(StToggle("Steam 游戏", _prefs.SourceSteam, on =>
        {
            if (!on && !_prefs.SourceShortcuts) { Toast("至少要留一种来源"); return; }
            SaveUi(_prefs with { SourceSteam = on }); _stNeedsReload = true; Repaint();
        }, true, reload));
        _stRows.Add(StToggle("非 Steam 快捷方式", _prefs.SourceShortcuts, on =>
        {
            if (!on && !_prefs.SourceSteam) { Toast("至少要留一种来源"); return; }
            SaveUi(_prefs with { SourceShortcuts = on }); _stNeedsReload = true; Repaint();
        }, true, reload));
        foreach (var folder in _prefs.Folders)
        {
            string key = "folder:" + folder;
            int count = _stFolderCounts.TryGetValue(folder, out var c) ? c : -1;
            _stRows.Add(StAction(folder, key, count < 0 ? "" : $"{count} 款", () =>
            {
                if (!StConfirm(key)) return;
                SaveUi(_prefs with { Folders = _prefs.Folders.Where(f => f != folder).ToArray() });
                _stNeedsReload = true;
                Toast($"已移除：{folder}（重新载入游戏库后生效）");
                Repaint();
            }));
            if (count < 0) _ = StCountFolder(folder);
        }
        _stRows.Add(StAction("添加游戏文件夹…", "addfolder", "", () => StBrowseTo("")));
        _stRows.Add(StToggle("显示在 Steam 里隐藏的游戏", _prefs.ShowHiddenGames, on => { SaveUi(_prefs with { ShowHiddenGames = on }); _stNeedsReload = true; Repaint(); }, false, reload));
        _stRows.Add(StAction("重新载入游戏库", "reload", _stNeedsReload ? "有改动待生效" : "", RestartLibrary));

        _stRows.Add(new PerfRow { Section = true, Title = "封面" });
        long bytes = 0;
        try { if (Directory.Exists(ArtRoot)) bytes = new DirectoryInfo(ArtRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
        _stRows.Add(StAction("重新生成全部封面", "art", $"{bytes >> 20} MB", () =>
        {
            if (!StConfirm("art")) return;
            try { Directory.CreateDirectory(App.CacheDir); File.WriteAllText(Path.Combine(App.CacheDir, "art.reset"), "1"); } catch { }
            RestartLibrary();
        }));

        _stRows.Add(new PerfRow { Section = true, Title = "启动游戏后" });
        _stRows.Add(StPick("游戏库", new[] { "保持", "收到后台并暂停" }, (int)_prefs.AfterLaunch, i => { SaveUi(_prefs with { AfterLaunch = (AfterLaunch)i }); Repaint(); }, 0));
    }

    private void BuildStLook()
    {
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
                break;
            case BackgroundKind.Monet:
                _stRows.Add(StToggle("色块漂移", b.MonetDrift, on => SetBg(b with { MonetDrift = on }), true));
                _stRows.Add(StToggle("光带", b.MonetStreaks, on => SetBg(b with { MonetStreaks = on }), true));
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

        _stRows.Add(new PerfRow { Section = true, Title = "界面" });
        _stRows.Add(StPick("控制中心卡片", new[] { "拍立得", "卡片" }, _ccCards ? 1 : 0, i => { SetCcCardStyle(i == 1); Repaint(); }, 0));
        var accentNames = new[] { "跟随封面" }.Concat(UiPrefs.Accents.Select(a => a.Name)).ToArray();
        _stRows.Add(StPick("强调色", accentNames, _prefs.Accent + 1, i => { SaveUi(_prefs with { Accent = i - 1 }); Repaint(); CcRepaintPages(); }, 0));
        _stRows.Add(StToggle("降低动态效果", _reducedLocal, on => { _reducedLocal = on; SaveUi(_prefs with { Reduced = on }); Repaint(); }, false));
    }

    private PerfRow StNumber(string title, int value, int min, int max, int step, Func<int, string> fmt, Action<int> set, int def) => new()
    {
        Title = title, Value = fmt(value), Numeric = true, Changed = value != def,
        Reset = () => { if (value != def) set(def); },
        Step = d => { int nv = Math.Clamp(value + d * step, min, max); if (nv != value) set(nv); },
    };

    private void BuildStSound(JsonObject? v, string later)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "界面音效" });
        _stRows.Add(StToggle("界面音效", _prefs.Sounds, on => { SaveUi(_prefs with { Sounds = on }); SoundFx.Enabled = on; SoundFx.Play(Sfx.Confirm); Repaint(); }, false));
        var vol = StNumber("音效音量", _prefs.SoundVolume, 0, 100, 10, n => $"{n} %", n => { SaveUi(_prefs with { SoundVolume = n }); SoundFx.SetVolume(n); SoundFx.Play(Sfx.Move); Repaint(); }, 40);
        vol.Disabled = !_prefs.Sounds;
        _stRows.Add(vol);
        _stRows.Add(new PerfRow { Section = true, Title = "震动" });
        if (v?["haptics"] is JsonValue)
        {
            bool on = Bool(v["haptics"]);
            _stRows.Add(StToggle("导航震动", on, to => { v["haptics"] = to; Repaint(); _ = StRun("set_haptics_enabled", null, JsonValue.Create(to)); }, true));
        }
        else _stRows.Add(StInfo("导航震动", later));
    }

    private void BuildStInGame(JsonObject? v, string later)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "兼容修复" });
        if (v?["compatibility"] is JsonObject c && c["enabled"] is JsonObject en)
        {
            var items = c["items"] as JsonObject;
            bool busy = Bool(c["busy"]);
            foreach (var (id, title) in new[] { ("steam", "Steam 以管理员身份启动"), ("gamebar", "Xbox 键打开 Game Bar 首页") })
            {
                bool on = Bool(en[id]);
                var row = StToggle(title, on, to =>
                {
                    en[id] = to;
                    Repaint();
                    _ = StRun("manage_compatibility", r => Str(r?["items"]?[id]) ?? "已更新", JsonValue.Create("set"), JsonValue.Create(id), JsonValue.Create(to));
                }, false, busy ? "处理中" : (Str(items?[id]) ?? "").StartsWith("需要处理") ? "需要处理" : "");
                row.Disabled = busy;
                _stRows.Add(row);
            }
            if (Str(c["error"]) is string err && err.Length > 0) _stRows.Add(StInfo("兼容修复出错", err));
        }
        else _stRows.Add(StInfo("兼容修复", later));
        _stRows.Add(new PerfRow { Section = true, Title = "调试" });
        _stRows.Add(StToggle("性能读数（帧时间）", _hudOn, on => { if (on != _hudOn) ToggleHud(); Repaint(); }, false));
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
        _stRows.Add(new PerfRow { Section = true, Title = "已被工具箱替代" });
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

    private void BuildStMaintenance(JsonObject? v, string later)
    {
        _stRows.Add(new PerfRow { Section = true, Title = "诊断" });
        _stRows.Add(StAction("导出日志", "logs", "", () => _ = StRun("export_logs", r => $"日志已存到桌面：{Str(r?["name"])}")));
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
                Title = "还原设置", Numeric = true,
                Value = _stArmed == "restore" ? "再按一次确认" : Str(backups[_stBackupPick]["time"]) ?? name,
                Step = d => { int ni = Math.Clamp(_stBackupPick + d, 0, backups.Length - 1); if (ni != _stBackupPick) { _stBackupPick = ni; _stArmed = null; Repaint(); } },
                Press = () => { if (StConfirm("restore")) { Toast("正在还原，工具箱后台会重启一下"); _ = StRun("restore_settings", _ => "已还原，后台正在重启", JsonValue.Create(name)); } },
            });
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
        if (_stZone == 0)
        {
            if (dir is Direction.Up or Direction.Down)
            {
                int next = Math.Clamp(_stCat + (dir == Direction.Up ? -1 : 1), 0, StCats.Length - 1);
                if (next == _stCat) return;
                _stCat = next;
                _stArmed = null;
                _stBrowse = null;
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
        if (r != null && !r.Disabled && r.Step != null) { r.Step(dir == Direction.Left ? -1 : 1); return; }
        if (dir == Direction.Left) { _stZone = 0; PaintSt(); }
    }

    private void StActivate()
    {
        SettingsResumeKeyboard();
        if (_stZone == 0) { _stZone = 1; PaintSt(); return; }
        if (_stFocus < 0 || _stFocus >= _stRows.Count) return;
        var r = _stRows[_stFocus];
        if (r.Disabled) return;
        r.Press?.Invoke();
    }

    private void StBack()
    {
        if (_stBrowse != null)
        {
            // 上一级；在磁盘列表里再按 B 退出浏览
            StBrowseTo(_stBrowse.Length == 0 ? null : Path.GetDirectoryName(_stBrowse.TrimEnd('\\')) is string up && up.Length > 0 ? up : "");
            return;
        }
        if (_stZone == 1) { _stZone = 0; _stArmed = null; Repaint(); return; }
        CloseSettingsPage();
    }

    /// <summary>Y：焦点这一项恢复默认。</summary>
    private void StResetFocused()
    {
        if (_stZone != 1 || _stFocus < 0 || _stFocus >= _stRows.Count) return;
        var r = _stRows[_stFocus];
        if (r.Reset == null || r.Disabled) { Toast("这一项没有默认值"); return; }
        r.Reset();
        Toast($"「{r.Title}」已恢复默认");
    }

    /// <summary>点左栏的分类。</summary>
    private bool StCategoryClick(double sx, double sy)
    {
        double x = sx / _ccK - PerfLeftX, y = sy / _ccK - PerfTop;
        if (x < 0 || x > PerfLeftW || y < 0) return false;
        int i = (int)(y / StCatH);
        if (i < 0 || i >= StCats.Length) return false;
        MarkInput();
        _stCat = i; _stZone = 0; _stArmed = null; _stBrowse = null;
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
        float k = _ccK, raster = k * Chrome.Dpi;
        float h = RowsHeight(_stRows), vh = PerfViewH;
        using (var tl = TextKit.Layout(G.Device, "设置", TextKit.Ui(700), 24, 400, CanvasHorizontalAlignment.Left))
        {
            var bb = tl.LayoutBounds;
            float w = (float)bb.Width + 4, th = (float)bb.Height + 4;
            CcSet(_stTitle, CcDraw(w, th, raster, ds => ds.DrawTextLayout(tl, -(float)bb.X, -(float)bb.Y, CcText)));
            _stTitle.Size = new Vector2(w * k, th * k);
            _stTitle.Offset = new Vector3(40 * k, 32 * k, 0);
        }
        _settingsTargets.Clear();
        CcSet(_stRight, CcDraw(PerfRightW, h, raster, ds => StPaintRows(ds, _stRows, _stZone == 1 ? _stFocus : -1, _settingsTargets)));
        _stRight.Size = new Vector2(PerfRightW * k, h * k);
        _stRightView.Size = new Vector2(PerfRightW * k, vh * k);
        _stRightView.Offset = new Vector3(PerfRightX * k, PerfTop * k, 0);
        RowScrollTo(_stRight, _stRows, _settingsManualScroll || _stZone == 0 ? -1 : _stFocus, ref _stScroll, ref _stScrollY, h, vh, false);
        CcSet(_stLeft, CcDraw(PerfLeftW, vh, raster, ds => PaintStNav(ds, vh)));
        _stLeft.Size = new Vector2(PerfLeftW * k, vh * k);
        _stLeft.Offset = new Vector3(PerfLeftX * k, PerfTop * k, 0);
    }
}
