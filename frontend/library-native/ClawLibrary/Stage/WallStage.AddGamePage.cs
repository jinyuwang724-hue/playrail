// 设置 › 游戏库 › 添加游戏 选中 exe 以后的整页（设计稿「游戏库 · 添加游戏」第二版），三步：
// ① 选定游戏：SteamGridDB 匹配到的游戏一排封面卡（中文名先经 Steam 商店找英文名），选一款；最后一张「只用名称，不配图」。
// ② 选图：左上名称（真输入框）/ 程序 / 匹配的游戏，左下只放右边当前页签选中的那张；右边 SteamGridDB 五种图各一个页签
//    （竖封面 / 横封面 / 背景 / 标志 / 图标，LB / RB 换），缩略图网格，A 选中打勾；底下 加入 Steam / 分类 / 添加。
// ③ 分类：单独一页。上面新建分类（输入框 + 「新建并选上」），下面已有的 Steam 分类大格子，可多选。
// 手柄、键盘、鼠标都能用：点一下就是选中并执行。搜索、下载、写 Steam 都在工具箱后台（plugin/claw_local_games.py）。
// 整页画在一张图上（Win2D），只有输入框是 XAML TextBox（中文输入法、触摸键盘），叠在画好的框里，框里不再画字。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Rect = Windows.Foundation.Rect;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum AgPage { Pick, Art, Category }

    private sealed class AgDraft
    {
        public string? Id;
        public string Name = "", Exe = "", Query = "";
        public bool Steam = true;
        public List<string> Collections = new(), NewCollections = new();
        public string? Sgdb;
        public List<JsonObject> Matches = new();
        /// <summary>选定的那款（-1 = 没选 / 只用名称）。</summary>
        public int Match = -1;
        public string MatchName = "";
        public string Status = "";
        public bool Searching, Loading, Saving;
        /// <summary>还在拿的图种。</summary>
        public readonly HashSet<string> Pending = new();
        /// <summary>每种图的候选（Url 为 null = 现在用着的那张），Pick 是选中的下标（-1 = 不用）。</summary>
        public readonly Dictionary<string, List<(string? Url, string Thumb)>> Cands = new();
        public readonly Dictionary<string, int> Pick = new();
        public int Version;
    }

    private static readonly (string Kind, string Title)[] AgKinds = { ("p", "竖封面"), ("header", "横封面"), ("hero", "背景"), ("logo", "标志"), ("icon", "图标") };

    // 舞台 1280×800 设计 px
    private const float AgLeftX = 40, AgLeftW = 380, AgRightX = 476, AgRightW = 764, AgGridTop = 164, AgGridBottom = 700;

    private ContainerVisual? _ag;
    private SpriteVisual _agSprite = null!;
    private bool _agOpen;
    private AgPage _agPage;
    private AgDraft? _agDraft;
    private int _agZone, _agIndex, _agTab, _agVersion;
    private float _agScroll;
    private string _agNewCat = "";
    private TextBox? _agBox;
    private readonly List<(Rect R, int Zone, int Index, Action Run)> _agHits = new();

    private Color AgAccent => PerfAccent;

    // ---------------- 开 / 关 ----------------

    private void AgOpenNew(string exe)
    {
        string dir = Path.GetDirectoryName(exe) ?? "";
        string name = Path.GetFileName(dir.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name) || dir.TrimEnd('\\').Length <= 2) name = Path.GetFileNameWithoutExtension(exe);
        var d = new AgDraft { Name = name, Exe = exe, Query = name };
        AgShow(d, AgPage.Pick);
        _ = AgSearch(d);
    }

    private void AgOpenEdit(JsonObject g)
    {
        var d = new AgDraft
        {
            Id = Str(g["id"]), Name = Str(g["name"]) ?? "", Exe = Str(g["exe"]) ?? "", Steam = Bool(g["steam"]), Sgdb = Str(g["sgdb"]),
            Collections = (g["collections"] as JsonArray)?.Select(Str).OfType<string>().ToList() ?? new(),
            NewCollections = (g["newCollections"] as JsonArray)?.Select(Str).OfType<string>().ToList() ?? new(),
        };
        d.Query = d.Name;
        d.MatchName = d.Sgdb != null ? "已配图" : "";
        foreach (var (kind, _) in AgKinds)
        {
            d.Cands[kind] = new();
            d.Pick[kind] = -1;
            if (g["art"] is JsonObject a && Str(a[kind]) is { Length: > 0 } path) { d.Cands[kind].Add((null, path)); d.Pick[kind] = 0; }
        }
        AgShow(d, AgPage.Art);
    }

    private void AgShow(AgDraft d, AgPage page)
    {
        if (_cc == null) return;
        if (_ag == null)
        {
            _ag = G.Container();
            _agSprite = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _ag.Children.InsertAtTop(_agSprite);
            _cc.Children.InsertAtTop(_ag);
        }
        _agDraft = d;
        _agOpen = true;
        _agVersion++;
        if (_st != null) _st.IsVisible = false;
        _ag.IsVisible = true;
        _ag.Opacity = 0;
        _ag.StartAnimation("Opacity", ToScalar(1, 200, EaseOutCss));
        AgGo(page);
        Log.Info($"add game page open ({page})");
    }

    private void AgClose()
    {
        if (!_agOpen || _ag == null) return;
        _agOpen = false;
        _agDraft = null;
        AgHideBox();
        _ag.IsVisible = false;
        if (_st != null && _stOpen) { _st.IsVisible = true; Repaint(); }
        _ = LgRefresh();
        Log.Info("add game page close");
    }

    private void AgGo(AgPage page)
    {
        _agPage = page;
        _agZone = page == AgPage.Pick ? 1 : page == AgPage.Art ? 1 : 1;
        _agIndex = 0;
        _agScroll = 0;
        if (page == AgPage.Pick && _agDraft != null && _agDraft.Match >= 0) _agIndex = _agDraft.Match;
        if (page == AgPage.Art && _agDraft != null)
        {
            _agTab = 0;
            _agIndex = Math.Max(0, _agDraft.Pick.TryGetValue("p", out var p) ? p : 0);
            if (AgItemCount() == 0) { _agZone = 2; _agIndex = 2; }
        }
        if (page == AgPage.Category) { _agNewCat = ""; _agZone = AgCollections().Count > 0 ? 1 : 0; }
        AgPlaceBox();
        AgPaint();
    }

    // ---------------- 后台 ----------------

    private async Task AgSearch(AgDraft d)
    {
        int v = ++d.Version;
        d.Searching = true;
        d.Status = "";
        d.Matches.Clear();
        AgRepaint(d);
        try
        {
            _agent ??= new AgentClient();
            var r = await _agent.CallLong("local_games_search", 60, JsonValue.Create(d.Query)) as JsonObject;
            if (v != d.Version) return;
            d.Matches = (r?["matches"] as JsonArray)?.OfType<JsonObject>().Select(m => (JsonObject)m.DeepClone()).ToList() ?? new();
            d.Status = Str(r?["status"]) ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn($"add game search: {ex.Message}");
            if (v == d.Version) d.Status = ex.Message.Contains("不支持的后台调用") ? "后台太旧" : "连不上";
        }
        if (v != d.Version) return;
        d.Searching = false;
        if (_agDraft == d && _agPage == AgPage.Pick && _agZone == 1) _agIndex = Math.Min(_agIndex, d.Matches.Count);
        AgRepaint(d);
    }

    private async Task AgLoadCandidates(AgDraft d)
    {
        int v = ++d.Version;
        d.Loading = true;
        foreach (var (kind, _) in AgKinds)
        {
            var keep = d.Cands.TryGetValue(kind, out var old) ? old.Where(c => c.Url == null).ToList() : new();
            d.Cands[kind] = keep;
            d.Pick[kind] = keep.Count > 0 ? 0 : -1;
        }
        if (d.Match < 0 || d.Match >= d.Matches.Count) { d.Loading = false; AgRepaint(d); return; }
        var match = d.Matches[d.Match];
        d.Sgdb = Str(match["source"]) == "sgdb" ? Str(match["id"]) : null;
        d.Pending.Clear();
        foreach (var (kind, _) in AgKinds) d.Pending.Add(kind);
        AgRepaint(d);
        // 先拿屏幕上这个页签的图，其余四种随后一起拿：第一批缩略图几秒内就出来
        string first = AgKinds[_agTab].Kind;
        await AgLoadKind(d, v, match, first);
        await Task.WhenAll(AgKinds.Where(k => k.Kind != first).Select(k => AgLoadKind(d, v, match, k.Kind)));
        if (v != d.Version) return;
        d.Loading = false;
        AgRepaint(d);
    }

    private async Task AgLoadKind(AgDraft d, int v, JsonObject match, string kind)
    {
        try
        {
            _agent ??= new AgentClient();
            var r = await _agent.CallLong("local_games_candidates", 60, match.DeepClone(), JsonValue.Create(kind), JsonValue.Create(false)) as JsonObject;
            if (v != d.Version) return;
            var found = (r?[kind] as JsonArray)?.OfType<JsonObject>()
                .Select(c => (Url: Str(c["url"]), Thumb: Str(c["thumb"]) ?? "")).Where(c => c.Url != null && c.Thumb.Length > 0)
                .Select(c => ((string?)c.Url, c.Thumb)).ToList() ?? new();
            bool hadOwn = d.Cands[kind].Count > 0;
            d.Cands[kind].AddRange(found);
            // 新加的游戏每种默认选第一张；改已有游戏时保留现在用着的那张
            if (!hadOwn) d.Pick[kind] = d.Cands[kind].Count > 0 ? 0 : -1;
        }
        catch (Exception ex) { Log.Warn($"add game candidates {kind}: {ex.Message}"); }
        if (v != d.Version) return;
        d.Pending.Remove(kind);
        // 屏幕上这一页刚有了图、焦点还停在底部按钮上：把焦点放回网格
        if (_agDraft == d && _agPage == AgPage.Art && AgKinds[_agTab].Kind == kind && _agZone == 2 && _agIndex == 2 && d.Cands[kind].Count > 0)
        { _agZone = 1; _agIndex = Math.Max(0, d.Pick[kind]); }
        AgRepaint(d);
    }

    private async Task AgSave(AgDraft d)
    {
        if (d.Saving) return;
        if (d.Name.Trim().Length == 0) { Toast("先填名称"); return; }
        d.Saving = true;
        AgRepaint(d);
        var art = new JsonObject();
        foreach (var (kind, _) in AgKinds)
        {
            var list = d.Cands.TryGetValue(kind, out var l) ? l : new();
            int pick = d.Pick.TryGetValue(kind, out var p) ? p : -1;
            if (pick >= 0 && pick < list.Count && list[pick].Url is string url) art[kind] = url;
            else if (pick < 0 && list.Any(c => c.Url == null)) art[kind] = "";   // 不用现在这张了
        }
        var game = new JsonObject
        {
            ["id"] = d.Id, ["name"] = d.Name.Trim(), ["exe"] = d.Exe, ["steam"] = d.Steam, ["sgdb"] = d.Sgdb, ["art"] = art,
            ["collections"] = new JsonArray(d.Collections.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["newCollections"] = new JsonArray(d.NewCollections.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        };
        try
        {
            _agent ??= new AgentClient();
            await _agent.CallLong("local_games_save", 180, game);
            Toast(d.Id == null ? $"已添加：{d.Name.Trim()}" : "已保存");
            _stNeedsReload = true;
            d.Saving = false;
            if (_agDraft == d) AgClose();
        }
        catch (Exception ex)
        {
            d.Saving = false;
            Toast(ex.Message);
            AgRepaint(d);
        }
    }

    private async Task AgRemove(AgDraft d)
    {
        try
        {
            _agent ??= new AgentClient();
            await _agent.Call("local_games_remove", JsonValue.Create(d.Id));
            Toast("已移除");
            _stNeedsReload = true;
            if (_agDraft == d) AgClose();
        }
        catch (Exception ex) { Toast(ex.Message); }
    }

    private void AgRepaint(AgDraft d)
    {
        if (_agOpen && _agDraft == d) _dq.TryEnqueue(AgPaint);
    }

    private List<(string Id, string Name)> AgCollections() =>
        (_lgState?["collections"] as JsonArray)?.OfType<JsonObject>().Select(c => (Str(c["id"]) ?? "", Str(c["name"]) ?? "")).Where(c => c.Item1.Length > 0).ToList() ?? new();

    // ---------------- 输入 ----------------

    private void AgMove(Direction dir)
    {
        var d = _agDraft;
        if (d == null) return;
        AgBlurBox();
        int dx = dir == Direction.Left ? -1 : dir == Direction.Right ? 1 : 0, dy = dir == Direction.Up ? -1 : dir == Direction.Down ? 1 : 0;
        switch (_agPage)
        {
            case AgPage.Pick:
                if (_agZone == 0)
                {
                    if (dx != 0) _agIndex = Math.Clamp(_agIndex + dx, 0, 1);
                    else if (dy > 0) { _agZone = 1; _agIndex = Math.Max(0, d.Match); }
                }
                else
                {
                    if (dx != 0) _agIndex = Math.Clamp(_agIndex + dx, 0, d.Matches.Count);
                    else if (dy < 0) { _agZone = 0; _agIndex = 0; }
                }
                break;
            case AgPage.Art:
                AgMoveArt(d, dx, dy);
                break;
            case AgPage.Category:
                AgMoveCategory(dx, dy);
                break;
        }
        NavHaptic("move");
        AgPaint();
    }

    private void AgMoveArt(AgDraft d, int dx, int dy)
    {
        int n = AgItemCount(), cols = AgGeo().Cols;
        int bottomCount = d.Id != null ? 4 : 3;
        switch (_agZone)
        {
            case 0:   // 名称 / 换游戏
                if (dy != 0) { int ni = _agIndex + dy; if (ni > 1) { _agZone = 2; _agIndex = 0; } else _agIndex = Math.Max(0, ni); }
                else if (dx > 0) { if (n > 0) { _agZone = 1; _agIndex = Math.Min(AgPickOf(d), n - 1); } else { _agZone = 2; _agIndex = 0; } }
                break;
            case 1:   // 网格
                if (dx != 0)
                {
                    int col = _agIndex % cols;
                    if (dx < 0 && col == 0) { _agZone = 0; _agIndex = 0; }
                    else _agIndex = Math.Clamp(_agIndex + dx, 0, n - 1);
                }
                else if (dy > 0) { if (_agIndex + cols < n) _agIndex += cols; else if (_agIndex / cols < (n - 1) / cols) _agIndex = n - 1; else { _agZone = 2; _agIndex = 0; } }
                else if (dy < 0 && _agIndex - cols >= 0) _agIndex -= cols;
                break;
            default:  // 底部：加入 Steam / 分类 / 添加 /（移除）
                if (dx != 0) _agIndex = Math.Clamp(_agIndex + dx, 0, bottomCount - 1);
                else if (dy < 0) { if (n > 0) { _agZone = 1; _agIndex = Math.Min(n - 1, ((n - 1) / cols) * cols); } else { _agZone = 0; _agIndex = 1; } }
                break;
        }
    }

    private void AgMoveCategory(int dx, int dy)
    {
        int n = AgCollections().Count, cols = 4;
        switch (_agZone)
        {
            case 0:
                if (dx != 0) _agIndex = Math.Clamp(_agIndex + dx, 0, 1);
                else if (dy > 0) { _agZone = n > 0 ? 1 : 2; _agIndex = 0; }
                break;
            case 1:
                if (dx != 0) _agIndex = Math.Clamp(_agIndex + dx, 0, n - 1);
                else if (dy > 0) { if (_agIndex + cols < n) _agIndex += cols; else { _agZone = 2; _agIndex = 0; } }
                else if (dy < 0) { if (_agIndex - cols >= 0) _agIndex -= cols; else { _agZone = 0; _agIndex = 0; } }
                break;
            default:
                if (dy < 0) { _agZone = n > 0 ? 1 : 0; _agIndex = n > 0 ? Math.Max(0, ((n - 1) / cols) * cols) : 0; }
                break;
        }
    }

    private void AgActivate()
    {
        var d = _agDraft;
        if (d == null) return;
        if (AgBoxFocused) { AgBoxEnter(); return; }
        NavHaptic("select");
        switch (_agPage)
        {
            case AgPage.Pick:
                if (_agZone == 0) { if (_agIndex == 0) AgFocusBox(); else _ = AgSearch(d); return; }
                if (d.Searching) return;
                AgChooseMatch(d, _agIndex < d.Matches.Count ? _agIndex : -1);
                return;
            case AgPage.Art:
                if (_agZone == 0) { if (_agIndex == 0) AgFocusBox(); else AgGo(AgPage.Pick); return; }
                if (_agZone == 1)
                {
                    var (kind, _) = AgKinds[_agTab];
                    int n = d.Cands.TryGetValue(kind, out var l) ? l.Count : 0;
                    d.Pick[kind] = _agIndex < n ? _agIndex : -1;
                    AgPaint();
                    return;
                }
                switch (_agIndex)
                {
                    case 0: d.Steam = !d.Steam; AgPaint(); break;
                    case 1: if (d.Steam) AgGo(AgPage.Category); else Toast("不加入 Steam 就没有分类"); break;
                    case 2: _ = AgSave(d); break;
                    default:
                        if (StConfirm("agremove")) _ = AgRemove(d); else AgPaint();
                        break;
                }
                return;
            case AgPage.Category:
                var cols = AgCollections();
                if (_agZone == 0) { if (_agIndex == 0) AgFocusBox(); else AgCreateCategory(); return; }
                if (_agZone == 1 && _agIndex < cols.Count)
                {
                    var id = cols[_agIndex].Id;
                    if (!d.Collections.Remove(id)) d.Collections.Add(id);
                    AgPaint();
                    return;
                }
                AgGo(AgPage.Art);
                return;
        }
    }

    private void AgChooseMatch(AgDraft d, int i)
    {
        d.Match = i;
        d.MatchName = i >= 0 ? Str(d.Matches[i]["name"]) ?? "" : "";
        if (i < 0) d.Sgdb = null;
        AgGo(AgPage.Art);
        _ = AgLoadCandidates(d);
    }

    private void AgCreateCategory()
    {
        var d = _agDraft;
        string name = _agNewCat.Trim();
        if (d == null) return;
        if (name.Length == 0) { AgFocusBox(); return; }
        var same = AgCollections().FirstOrDefault(c => c.Name == name);
        if (same.Id != null) { if (!d.Collections.Contains(same.Id)) d.Collections.Add(same.Id); }
        else if (!d.NewCollections.Contains(name)) d.NewCollections.Add(name);
        _agNewCat = "";
        if (_agBox != null) _agBox.Text = "";
        AgBlurBox();
        Toast($"已选上：{name}");
        AgPaint();
    }

    private void AgBack()
    {
        var d = _agDraft;
        if (AgBoxFocused) { AgBlurBox(); AgPaint(); return; }
        switch (_agPage)
        {
            case AgPage.Category: AgGo(AgPage.Art); return;
            case AgPage.Art when d is { Id: null }: AgGo(AgPage.Pick); return;
            default: AgClose(); return;
        }
    }

    /// <summary>LB / RB：选图页换图种。</summary>
    private void AgSwitchTab(int delta)
    {
        if (_agPage != AgPage.Art || _agDraft == null) return;
        AgBlurBox();
        _agTab = (_agTab + delta + AgKinds.Length) % AgKinds.Length;
        _agScroll = 0;
        int n = AgItemCount();
        if (_agZone == 1) { if (n == 0) { _agZone = 2; _agIndex = 0; } else _agIndex = Math.Clamp(AgPickOf(_agDraft), 0, n - 1); }
        NavHaptic("tab");
        AgPaint();
    }

    /// <summary>Y：选定游戏页 = 改搜索词；选图页 = 换游戏；分类页 = 新建分类。</summary>
    private void AgY()
    {
        if (_agDraft == null) return;
        switch (_agPage)
        {
            case AgPage.Pick: _agZone = 0; _agIndex = 0; AgFocusBox(); AgPaint(); break;
            case AgPage.Art: AgGo(AgPage.Pick); break;
            default: _agZone = 0; _agIndex = 0; AgFocusBox(); AgPaint(); break;
        }
    }

    /// <summary>X：选图页 = 分类。</summary>
    private void AgX()
    {
        if (_agPage == AgPage.Art && _agDraft is { Steam: true }) AgGo(AgPage.Category);
    }

    private void AgClick(double sx, double sy)
    {
        float x = (float)sx / _ccK, y = (float)sy / _ccK;
        foreach (var h in _agHits)
            if (h.R.Contains(new Point(x, y)))
            {
                _agZone = h.Zone;
                _agIndex = h.Index;
                h.Run();
                return;
            }
    }

    // ---------------- 选图页的网格 ----------------

    private (int Cols, float W, float H, bool Contain) AgGeo() => AgKinds[_agTab].Kind switch
    {
        "p" => (5, 128, 192, false),
        "header" => (3, 236, 110, false),
        "hero" => (2, 372, 120, false),
        "logo" => (3, 236, 132, true),
        _ => (6, 104, 104, true),
    };

    /// <summary>当前页签的格数：候选 +（标志、图标多一格「不用」）。</summary>
    private int AgItemCount()
    {
        var d = _agDraft;
        if (d == null) return 0;
        var kind = AgKinds[_agTab].Kind;
        int n = d.Cands.TryGetValue(kind, out var l) ? l.Count : 0;
        return n + (n > 0 && kind is "logo" or "icon" ? 1 : 0);
    }

    private int AgPickOf(AgDraft d)
    {
        var kind = AgKinds[_agTab].Kind;
        int p = d.Pick.TryGetValue(kind, out var v) ? v : -1;
        int n = d.Cands.TryGetValue(kind, out var l) ? l.Count : 0;
        return p >= 0 ? p : n;   // 「不用」那格在最后
    }

    // ---------------- 输入框 ----------------

    private bool AgBoxFocused => _agBox != null && _agBox.Visibility == Visibility.Visible && _agBox.FocusState != FocusState.Unfocused;

    /// <summary>这一页的输入框在哪（设计 px）。</summary>
    private Rect? AgFieldRect() => _agPage switch
    {
        AgPage.Pick => new Rect(40, 88, 520, 48),
        AgPage.Art => new Rect(AgLeftX, 114, AgLeftW, 48),
        _ => new Rect(40, 124, 1040, 52),
    };

    private void AgPlaceBox()
    {
        if (!_agOpen || AgFieldRect() is not Rect r) { AgHideBox(); return; }
        if (_agBox == null)
        {
            _agBox = new TextBox
            {
                MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0), RequestedTheme = ElementTheme.Dark,
                IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextAlignment = TextAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
            };
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
                _agBox.Resources[key] = clear;
            var ink = new SolidColorBrush(Theme.Ink);
            foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
                _agBox.Resources[key] = ink;
            var soft = new SolidColorBrush(Theme.SeamInkSoft);
            foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" })
                _agBox.Resources[key] = soft;
            _agBox.TextChanged += (_, _) =>
            {
                var d = _agDraft;
                if (d == null) return;
                switch (_agPage)
                {
                    case AgPage.Pick: d.Query = _agBox.Text; break;
                    case AgPage.Art: d.Name = _agBox.Text; break;
                    default: _agNewCat = _agBox.Text; break;
                }
            };
            _agBox.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; AgBoxEnter(); }
                else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; AgBlurBox(); AgPaint(); }
            };
            _agBox.GotFocus += (_, _) => { var f = AgFieldZone(); _agZone = f.Zone; _agIndex = f.Index; AgPaint(); };
            _agBox.LostFocus += (_, _) => AgPaint();
            // 系统输入框自带的「清空」× 不要
            _agBox.Loaded += (_, _) =>
            {
                if (FindChild(_agBox, "DeleteButton") is FrameworkElement x) { x.MaxWidth = 0; x.Opacity = 0; x.IsHitTestVisible = false; }
            };
            _rootEl.Overlay.Children.Add(_agBox);
        }
        float k = _ccK;
        var d = _agDraft;
        string text = _agPage switch { AgPage.Pick => d?.Query ?? "", AgPage.Art => d?.Name ?? "", _ => _agNewCat };
        if (_agBox.Text != text) _agBox.Text = text;
        _agBox.PlaceholderText = _agPage == AgPage.Category ? "输入新分类的名字" : "";
        _agBox.FontSize = 18 * k;
        _agBox.Width = r.Width * k;
        _agBox.Height = r.Height * k;
        _agBox.Padding = new Thickness(16 * k, (r.Height - 18 * 1.36) / 2 * k, 16 * k, 0);
        Canvas.SetLeft(_agBox, r.X * k);
        Canvas.SetTop(_agBox, r.Y * k);
        _agBox.Visibility = Visibility.Visible;
    }

    private (int Zone, int Index) AgFieldZone() => (0, 0);

    private void AgHideBox()
    {
        if (_agBox == null) return;
        if (AgBoxFocused) _rootEl.Focus(FocusState.Programmatic);
        _agBox.Visibility = Visibility.Collapsed;
    }

    private void AgFocusBox()
    {
        AgPlaceBox();
        if (_agBox == null) return;
        _agZone = 0; _agIndex = 0;
        _agBox.Focus(FocusState.Programmatic);
        _agBox.SelectAll();
        ShowTouchKeyboard();
        AgPaint();
    }

    private void AgBlurBox()
    {
        if (AgBoxFocused) _rootEl.Focus(FocusState.Programmatic);
    }

    private void AgBoxEnter()
    {
        var d = _agDraft;
        AgBlurBox();
        if (d == null) return;
        switch (_agPage)
        {
            case AgPage.Pick: _agZone = 1; _agIndex = 0; _ = AgSearch(d); break;
            case AgPage.Category: AgCreateCategory(); break;
            default: AgPaint(); break;
        }
    }

    // ---------------- 绘制 ----------------

    private void AgPaint()
    {
        if (!_agOpen || _ag == null || _agDraft == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        _agHits.Clear();
        CcSet(_agSprite, CcDraw(1280, 800, raster, ds =>
        {
            switch (_agPage)
            {
                case AgPage.Pick: AgPaintPick(ds, _agDraft); break;
                case AgPage.Art: AgPaintArt(ds, _agDraft); break;
                default: AgPaintCategory(ds, _agDraft); break;
            }
        }));
        _agSprite.Size = new Vector2(1280 * k, 800 * k);
        _agSprite.Offset = Vector3.Zero;
    }

    private static readonly Color AgField = Gfx.Rgba(0x25, 0x25, 0x27), AgPanel = Gfx.Rgba(0x15, 0x15, 0x17), AgKey = Gfx.Rgba(0x2e, 0x2e, 0x31);
    private Color AgMuted => Gfx.WithAlpha(CcText, 0.62f);

    private void AgTitle(CanvasDrawingSession ds, string title, string? sub)
    {
        using var tl = TextKit.Layout(ds, title, TextKit.Ui(700), 24, 600, CanvasHorizontalAlignment.Left);
        var bb = tl.LayoutBounds;
        ds.DrawTextLayout(tl, 40 - (float)bb.X, 32 - (float)bb.Y, CcText);
        if (sub != null) PText(ds, sub, TextKit.Ui(400), 14, AgMuted, 40 + (float)bb.Width + 16, 32 + (float)bb.Height / 2 + 2, 1200 - (float)bb.Width - 56);
    }

    private static void AgRing(CanvasDrawingSession ds, Rect r, float radius = 0)
    {
        var o = new Rect(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
        ds.DrawRoundedRectangle(new Rect(o.X - 1.5, o.Y - 1.5, o.Width + 3, o.Height + 3), radius + 4, radius + 4, Gfx.Rgba(0, 0, 0, 0.5), 1);
        ds.DrawRoundedRectangle(o, radius + 3, radius + 3, CcText, 3.5f);
    }

    private float AgKeycap(CanvasDrawingSession ds, string key, float x, float cy)
    {
        var f = TextKit.Ui(700);
        float w = Math.Max(22, PWidth(ds, key, f, 12) + 12);
        ds.FillRoundedRectangle(x, cy - 11, w, 22, 6, 6, AgKey);
        PText(ds, key, f, 12, CcText, x, cy, w, CanvasHorizontalAlignment.Center);
        return w;
    }

    private void AgHints(CanvasDrawingSession ds, params (string Key, string Text)[] hints)
    {
        float x = 40, cy = 776;
        foreach (var (key, text) in hints)
        {
            foreach (var part in key.Split(' ')) x += AgKeycap(ds, part, x, cy) + 4;
            x += 4;
            PText(ds, text, TextKit.Ui(700), 12, AgMuted, x, cy, 200);
            x += PWidth(ds, text, TextKit.Ui(700), 12) + 20;
        }
    }

    /// <summary>按钮：底色 + 文字（+ 键帽）；focused 画白环；primary 用强调色。返回宽度。</summary>
    private float AgButton(CanvasDrawingSession ds, string text, float x, float y, float h, bool focused, bool primary, string? key, int zone, int index, Action run, float minW = 0, bool alignRight = false)
    {
        var f = TextKit.Ui((ushort)(primary ? 700 : 500));
        float size = primary ? 18 : 16;
        float tw = PWidth(ds, text, f, size), kw = key != null ? Math.Max(22, PWidth(ds, key, TextKit.Ui(700), 12) + 12) + 10 : 0;
        float w = Math.Max(minW, tw + kw + 36);
        if (alignRight) x -= w;
        var r = new Rect(x, y, w, h);
        var bg = primary ? AgAccent : AgField;
        var fg = primary ? Theme.Bg : CcText;
        ds.FillRoundedRectangle(r, 12, 12, bg);
        PText(ds, text, f, size, fg, x + (w - tw - kw) / 2, y + h / 2, tw + 4);
        if (key != null)
        {
            float kx = x + (w - tw - kw) / 2 + tw + 10;
            float kwid = Math.Max(22, PWidth(ds, key, TextKit.Ui(700), 12) + 12);
            ds.FillRoundedRectangle(kx, y + h / 2 - 11, kwid, 22, 6, 6, primary ? Gfx.WithAlpha(Theme.Bg, 0.85f) : AgKey);
            PText(ds, key, TextKit.Ui(700), 12, primary ? AgAccent : CcText, kx, y + h / 2, kwid, CanvasHorizontalAlignment.Center);
        }
        if (focused) AgRing(ds, r, 12);
        _agHits.Add((r, zone, index, run));
        return w;
    }

    private void AgPaintField(CanvasDrawingSession ds, Rect r, bool focused)
    {
        ds.FillRectangle(r, AgField);
        if (AgBoxFocused) ds.FillRectangle((float)r.X, (float)(r.Y + r.Height - 2), (float)r.Width, 2, AgAccent);
        else if (focused) AgRing(ds, r);
        _agHits.Add((r, 0, 0, AgFocusBox));
    }

    private void AgPaintPick(CanvasDrawingSession ds, AgDraft d)
    {
        AgTitle(ds, "选定游戏", d.Exe);
        var field = AgFieldRect()!.Value;
        AgPaintField(ds, field, _agZone == 0 && _agIndex == 0);
        AgButton(ds, "搜索", 572, 88, 48, _agZone == 0 && _agIndex == 1, false, "Y", 0, 1, () => _ = AgSearch(d));
        string status = d.Searching ? "搜索中" : d.Status.Length > 0 ? d.Status : $"{d.Matches.Count} 款";
        PText(ds, status, TextKit.Ui(700), 12, AgMuted, 700, 112, 400);

        // 封面卡：一排 5 张，焦点走到边上时整排滚
        const float cw = 220, gap = 25, top = 168, ah = 330;
        int count = d.Matches.Count + 1;
        int focus = _agZone == 1 ? _agIndex : Math.Max(0, d.Match);
        int first = Math.Clamp((int)_agScroll, 0, Math.Max(0, count - 5));
        if (focus < first) first = focus;
        if (focus > first + 4) first = focus - 4;
        _agScroll = first;
        for (int i = first; i < Math.Min(count, first + 5); i++)
        {
            float x = 40 + (i - first) * (cw + gap);
            var art = new Rect(x, top, cw, ah);
            bool f = _agZone == 1 && _agIndex == i;
            int idx = i;
            if (i < d.Matches.Count)
            {
                var m = d.Matches[i];
                if (AgBitmapOf(Str(m["thumb"])) is { } bmp) DrawCovered(ds, bmp, art);
                else { ds.FillRectangle(art, AgField); PText(ds, m["covers"] is null ? "没取到预览" : "没有竖封面", TextKit.Ui(400), 13, Gfx.WithAlpha(CcText, 0.4f), x, top + ah / 2, cw, CanvasHorizontalAlignment.Center); }
                if (i == d.Match) AgTick(ds, art);
                using var nl = TextKit.Layout(ds, Str(m["name"]) ?? "", TextKit.Ui(500), 15, cw, CanvasHorizontalAlignment.Left, maxLines: 2);
                ds.DrawTextLayout(nl, x, top + ah + 12, CcText);
                string src = Str(m["source"]) == "steam" ? "Steam 商店" : "SteamGridDB";
                int year = Int(m["year"]), covers = Int(m["covers"]);
                string sub = string.Join(" · ", new[] { year > 0 ? year.ToString() : null, covers > 0 ? $"{covers} 张竖封面" : null, src }.Where(s => s != null));
                PText(ds, sub, TextKit.Ui(400), 13, AgMuted, x, top + ah + 12 + (float)nl.LayoutBounds.Height + 12, cw);
            }
            else
            {
                ds.FillRectangle(art, AgField);
                PText(ds, "不配图", TextKit.Ui(400), 15, Gfx.WithAlpha(CcText, 0.5f), x, top + ah / 2, cw, CanvasHorizontalAlignment.Center);
                PText(ds, "只用名称", TextKit.Ui(500), 15, CcText, x, top + ah + 22, cw);
                PText(ds, "以后可以再补", TextKit.Ui(400), 13, AgMuted, x, top + ah + 46, cw);
            }
            if (f) AgRing(ds, art);
            _agHits.Add((new Rect(x, top, cw, ah + 70), 1, idx, () => { if (!d.Searching) AgChooseMatch(d, idx < d.Matches.Count ? idx : -1); }));
        }
        if (d.Searching && d.Matches.Count == 0)
            PText(ds, "搜索中", TextKit.Ui(400), 15, AgMuted, 40, top + ah / 2, 1200, CanvasHorizontalAlignment.Center);
        AgHints(ds, ("A", "用这款"), ("Y", "改搜索词"), ("B", "返回"));
    }

    private void AgTick(CanvasDrawingSession ds, Rect r)
    {
        float x = (float)(r.X + r.Width - 30), y = (float)r.Y + 6;
        ds.FillRoundedRectangle(x, y, 24, 24, 6, 6, AgAccent);
        using var stroke = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        ds.DrawLine(x + 6, y + 12.5f, x + 10, y + 16.5f, Theme.Bg, 2.4f, stroke);
        ds.DrawLine(x + 10, y + 16.5f, x + 18, y + 7.5f, Theme.Bg, 2.4f, stroke);
    }

    private void AgPaintArt(CanvasDrawingSession ds, AgDraft d)
    {
        AgTitle(ds, d.Id == null ? "添加游戏" : "编辑游戏", null);
        // 左上：游戏信息
        PText(ds, "名称", TextKit.Ui(500), 14, AgMuted, AgLeftX, 102, 200);
        AgPaintField(ds, AgFieldRect()!.Value, _agZone == 0 && _agIndex == 0);
        PText(ds, "程序", TextKit.Ui(500), 14, AgMuted, AgLeftX, 184, 200);
        using (var pl = TextKit.Layout(ds, d.Exe, TextKit.Ui(400), 14, AgLeftW, CanvasHorizontalAlignment.Left, maxLines: 2))
            ds.DrawTextLayout(pl, AgLeftX, 196, CcText);
        PText(ds, "SteamGridDB", TextKit.Ui(500), 14, AgMuted, AgLeftX, 258, 200);
        string match = d.Match >= 0 ? d.MatchName : d.MatchName.Length > 0 ? d.MatchName : "不配图";
        float bw = AgButton(ds, "换游戏", AgLeftX + AgLeftW, 262, 44, _agZone == 0 && _agIndex == 1, false, "Y", 0, 1, () => AgGo(AgPage.Pick), alignRight: true);
        using (var ml = TextKit.Layout(ds, match, TextKit.Ui(400), 14, AgLeftW - bw - 12, CanvasHorizontalAlignment.Left, maxLines: 2))
            ds.DrawTextLayout(ml, AgLeftX, 272, CcText);

        // 左下：只放当前页签选中的那张
        var (kind, kindName) = AgKinds[_agTab];
        var box = new Rect(AgLeftX, 340, AgLeftW, 360);
        ds.FillRectangle(box, AgPanel);
        var list = d.Cands.TryGetValue(kind, out var l) ? l : new();
        int pick = d.Pick.TryGetValue(kind, out var pk) ? pk : -1;
        string caption;
        if (pick >= 0 && pick < list.Count)
        {
            var bmp = AgBitmapOf(list[pick].Thumb);
            if (bmp != null)
            {
                Rect target = kind switch
                {
                    "p" => new Rect(box.X + (box.Width - 220) / 2, box.Y + 15, 220, 330),
                    "header" => new Rect(box.X + 12, box.Y + (box.Height - 166) / 2, 356, 166),
                    "hero" => new Rect(box.X, box.Y + (box.Height - 123) / 2, 380, 123),
                    "logo" => new Rect(box.X + 30, box.Y + 80, 320, 200),
                    _ => new Rect(box.X + (box.Width - 128) / 2, box.Y + (box.Height - 128) / 2, 128, 128),
                };
                if (kind is "logo" or "icon") DrawContained(ds, bmp, target); else DrawCovered(ds, bmp, target);
            }
            caption = list[pick].Url == null ? $"{kindName} · 现在用的" : $"{kindName} · 已选第 {pick + 1} 张";
        }
        else caption = $"{kindName} · 不用";
        PText(ds, caption, TextKit.Ui(700), 12, AgMuted, AgLeftX, 716, AgLeftW);

        // 右：页签
        float tx = AgRightX;
        tx += AgKeycap(ds, "LB", tx, 112) + 16;
        var tabFont = TextKit.Ui(500);
        for (int i = 0; i < AgKinds.Length; i++)
        {
            var (kk, name) = AgKinds[i];
            int count = d.Cands.TryGetValue(kk, out var cl) ? cl.Count(c => c.Url != null) : 0;
            bool on = i == _agTab;
            float nw = PWidth(ds, name, tabFont, 17);
            string cnt = d.Pending.Contains(kk) ? "" : count.ToString();
            float cwid = cnt.Length > 0 ? PWidth(ds, cnt, TextKit.Ui(400), 12) + 6 : 0;
            PText(ds, name, tabFont, 17, on ? CcText : Gfx.WithAlpha(CcText, 0.55f), tx, 112, nw + 4);
            if (cnt.Length > 0) PText(ds, cnt, TextKit.Ui(400), 12, Gfx.WithAlpha(CcText, 0.42f), tx + nw + 6, 114, cwid + 4);
            if (on) ds.FillRectangle(tx, 132, nw, 2, AgAccent);
            int ti = i;
            _agHits.Add((new Rect(tx - 8, 92, nw + cwid + 16, 44), _agZone, _agIndex, () => AgSwitchTab(ti - _agTab)));
            tx += nw + cwid + 24;
        }
        AgKeycap(ds, "RB", tx - 8, 112);
        ds.FillRectangle(AgRightX, 140, AgRightW, 1, Gfx.WithAlpha(CcText, 0.07f));

        // 右：网格
        var (cols, w, h, contain) = AgGeo();
        int n = AgItemCount();
        float gx = cols > 1 ? (AgRightW - cols * w) / (cols - 1) : 0, gy = 20;
        float rowH = h + gy, viewH = AgGridBottom - AgGridTop;
        int focusRow = _agZone == 1 ? _agIndex / cols : (int)(_agScroll / rowH);
        float want = _agScroll;
        if (focusRow * rowH < want) want = focusRow * rowH;
        if (focusRow * rowH + h > want + viewH) want = focusRow * rowH + h - viewH;
        _agScroll = Math.Max(0, want);
        if (n == 0)
            PText(ds, d.Pending.Contains(kind) ? "加载中" : "没有", TextKit.Ui(400), 15, AgMuted, AgRightX, AgGridTop + 120, AgRightW, CanvasHorizontalAlignment.Center);
        using (ds.CreateLayer(1, new Rect(AgRightX - 8, AgGridTop - 8, AgRightW + 16, viewH + 16)))
        {
            for (int i = 0; i < n; i++)
            {
                float x = AgRightX + (i % cols) * (w + gx), y = AgGridTop + (i / cols) * rowH - _agScroll;
                if (y + h < AgGridTop - 8 || y > AgGridBottom + 8) continue;
                var r = new Rect(x, y, w, h);
                ds.FillRectangle(r, AgField);
                if (i < list.Count)
                {
                    if (AgBitmapOf(list[i].Thumb) is { } bmp)
                    {
                        if (contain) DrawContained(ds, bmp, new Rect(x + 10, y + 10, w - 20, h - 20)); else DrawCovered(ds, bmp, r);
                    }
                }
                else PText(ds, kindName == "图标" ? "不用图标" : "不用标志", TextKit.Ui(500), 15, AgMuted, x, y + h / 2, w, CanvasHorizontalAlignment.Center);
                bool chosen = i < list.Count ? i == pick : pick < 0;
                if (chosen) AgTick(ds, r);
                if (_agZone == 1 && _agIndex == i) AgRing(ds, r);
                int idx = i;
                double top = Math.Max(y, AgGridTop), bottom = Math.Min(y + h, AgGridBottom);
                if (bottom > top) _agHits.Add((new Rect(x, top, w, bottom - top), 1, idx, () => { d.Pick[kind] = idx < list.Count ? idx : -1; AgPaint(); }));
            }
        }

        // 底部：加入 Steam / 分类 / 添加 /（移除）
        ds.FillRectangle(AgRightX, 716, AgRightW, 1, Gfx.WithAlpha(CcText, 0.07f));
        float bx = AgRightX, by = 728;
        bx += AgToggle(ds, "加入 Steam", d.Steam, bx, by, _agZone == 2 && _agIndex == 0, () => { d.Steam = !d.Steam; AgPaint(); }) + 16;
        string cats = AgCategoryText(d);
        float cw = AgButton(ds, d.Steam ? $"分类 · {cats}" : "分类", bx, by, 44, _agZone == 2 && _agIndex == 1, false, "X", 2, 1,
            () => { if (d.Steam) AgGo(AgPage.Category); });
        if (!d.Steam) ds.FillRoundedRectangle(bx, by, cw, 44, 12, 12, Gfx.WithAlpha(Theme.Bg, 0.55f));
        float right = AgRightX + AgRightW;
        right -= AgButton(ds, d.Saving ? "保存中" : d.Id == null ? "添加" : "保存", right, by - 2, 48, _agZone == 2 && _agIndex == 2, true, null, 2, 2, () => _ = AgSave(d), 120, true) + 12;
        if (d.Id != null)
            AgButton(ds, _stArmed == "agremove" ? "再按一次" : "移除", right, by, 44, _agZone == 2 && _agIndex == 3, false, null, 2, 3,
                () => { if (StConfirm("agremove")) _ = AgRemove(d); else AgPaint(); }, 0, true);
        AgHints(ds, ("A", "选这张"), ("LB RB", "换图种"), ("Y", "换游戏"), ("X", "分类"), ("B", "返回"));
    }

    private string AgCategoryText(AgDraft d)
    {
        var names = AgCollections().Where(c => d.Collections.Contains(c.Id)).Select(c => c.Name).Concat(d.NewCollections).ToList();
        if (names.Count == 0) return "不加入";
        return names.Count <= 2 ? string.Join("、", names) : $"{names[0]} 等 {names.Count} 个";
    }

    private float AgToggle(CanvasDrawingSession ds, string label, bool on, float x, float y, bool focused, Action run)
    {
        var f = TextKit.Ui(500);
        float lw = PWidth(ds, label, f, 16);
        float w = lw + 16 + 44 + 24;
        var r = new Rect(x, y, w, 44);
        ds.FillRoundedRectangle(r, 12, 12, AgField);
        PText(ds, label, f, 16, CcText, x + 14, y + 22, lw + 4);
        float sx = x + 14 + lw + 12;
        ds.FillRoundedRectangle(sx, y + 10, 44, 24, 12, 12, on ? AgAccent : AgKey);
        ds.FillCircle(on ? sx + 32 : sx + 12, y + 22, 9, on ? Theme.Bg : Gfx.WithAlpha(CcText, 0.7f));
        if (focused) AgRing(ds, r, 12);
        _agHits.Add((r, 2, 0, run));
        return w;
    }

    private void AgPaintCategory(CanvasDrawingSession ds, AgDraft d)
    {
        AgTitle(ds, "分类", $"{d.Name.Trim()} · 放进 Steam 的哪些分类，可以多选，也可以都不选");
        PText(ds, "新建分类", TextKit.Ui(500), 14, AgMuted, 40, 108, 200);
        AgPaintField(ds, AgFieldRect()!.Value, _agZone == 0 && _agIndex == 0);
        AgButton(ds, "新建并选上", 1240, 124, 52, _agZone == 0 && _agIndex == 1, true, "Y", 0, 1, AgCreateCategory, 148, true);

        PText(ds, "已有的分类", TextKit.Ui(500), 14, AgMuted, 40, 222, 200);
        var cols = AgCollections();
        const int per = 4;
        const float tw = 291, th = 64, gap = 12, top = 240;
        if (cols.Count == 0) PText(ds, "Steam 里还没有分类", TextKit.Ui(400), 15, AgMuted, 40, top + 32, 600);
        for (int i = 0; i < cols.Count; i++)
        {
            float x = 40 + (i % per) * (tw + gap), y = top + (i / per) * (th + gap);
            if (y + th > 700) break;
            var r = new Rect(x, y, tw, th);
            bool on = d.Collections.Contains(cols[i].Id);
            ds.FillRectangle(r, on ? AgKey : AgField);
            if (on) ds.DrawRectangle(new Rect(x + 1, y + 1, tw - 2, th - 2), AgAccent, 2);
            var box = new Rect(x + 18, y + th / 2 - 11, 22, 22);
            if (on) { ds.FillRoundedRectangle(box, 6, 6, AgAccent); AgCheck(ds, box); }
            else ds.DrawRoundedRectangle(new Rect(box.X + 1, box.Y + 1, 20, 20), 5, 5, Gfx.WithAlpha(CcText, 0.45f), 2);
            PText(ds, cols[i].Name, TextKit.Ui(500), 17, CcText, x + 54, y + th / 2, tw - 70);
            if (_agZone == 1 && _agIndex == i) AgRing(ds, r);
            string id = cols[i].Id;
            int idx = i;
            _agHits.Add((r, 1, idx, () => { if (!d.Collections.Remove(id)) d.Collections.Add(id); AgPaint(); }));
        }

        ds.FillRectangle(40, 716, 1200, 1, Gfx.WithAlpha(CcText, 0.07f));
        var picked = AgCategoryText(d);
        PText(ds, picked == "不加入" ? "不加入分类" : $"已选：{string.Join("、", AgCollections().Where(c => d.Collections.Contains(c.Id)).Select(c => c.Name).Concat(d.NewCollections))}",
            TextKit.Ui(500), 16, CcText, 40, 740, 900);
        AgButton(ds, "完成", 1240, 726, 48, _agZone == 2, true, null, 2, 0, () => AgGo(AgPage.Art), 120, true);
        AgHints(ds, ("A", "选上 / 取消"), ("Y", "新建分类"), ("B", "返回"));
    }

    private static void AgCheck(CanvasDrawingSession ds, Rect box)
    {
        float x = (float)box.X, y = (float)box.Y;
        using var stroke = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        ds.DrawLine(x + 5, y + 11.5f, x + 9, y + 15.5f, Theme.Bg, 2.4f, stroke);
        ds.DrawLine(x + 9, y + 15.5f, x + 17, y + 6.5f, Theme.Bg, 2.4f, stroke);
    }

    private CanvasBitmap? AgBitmapOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_lgBitmaps.TryGetValue(path, out var bmp)) return bmp;
        _lgBitmaps[path] = null;
        _ = LoadAgBitmap(path);
        return null;
    }

    private static readonly System.Net.Http.HttpClient AgHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim AgDownloads = new(8);

    /// <summary>远程预览图下到游戏库自己的缓存里（同一张不重复下）；SteamGridDB 的图床偶尔断握手，试三次。</summary>
    private static async Task<string?> AgFetchThumb(string url)
    {
        var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..20];
        var ext = Path.GetExtension(new Uri(url).AbsolutePath) is { Length: > 1 and < 6 } e ? e : ".jpg";
        var file = Path.Combine(App.CacheDir, "add-thumbs", name + ext);
        if (File.Exists(file) && new FileInfo(file).Length > 0) return file;
        await AgDownloads.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var bytes = await AgHttp.GetByteArrayAsync(url);
                    if (bytes.Length == 0) return null;
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllBytesAsync(file, bytes);
                    return file;
                }
                catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode is null && attempt < 2) { await Task.Delay(400 * (attempt + 1)); }
            }
            return null;
        }
        catch (Exception ex) { Log.Warn($"thumb {url}: {ex.Message}"); return null; }
        finally { AgDownloads.Release(); }
    }

    private async Task LoadAgBitmap(string path)
    {
        try
        {
            string? local = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await AgFetchThumb(path) : path;
            if (local == null) return;
            var bmp = await CanvasBitmap.LoadAsync(G.Device, local);
            _dq.TryEnqueue(() =>
            {
                _lgBitmaps[path] = bmp;
                if (_lgBitmaps.Count > 160)
                    foreach (var k in _lgBitmaps.Keys.Where(k => k != path).Take(_lgBitmaps.Count - 120).ToList()) { _lgBitmaps[k]?.Dispose(); _lgBitmaps.Remove(k); }
                AgPaintSoon();
            });
        }
        catch (Exception ex) { Log.Warn($"preview {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>截图自测用（--state=add0..3 --exe=…）：打开设置页，直接进添加游戏。</summary>
    internal void AddGameTestState(string exe)
    {
        if (!_ccOpen) return;
        OpenSettingsPage();
        _ = LgRefresh();
        AgOpenNew(exe);
        if (Program.Args.FirstOrDefault(a => a.StartsWith("--name=")) is { } n && _agDraft is { } d)
        {
            d.Name = d.Query = n[7..];
            AgPlaceBox();
            _ = AgSearch(d);
        }
    }

    private bool _agPaintQueued;

    /// <summary>缩略图一张张到：攒一下再重画，不要每张都整页重画。</summary>
    private void AgPaintSoon()
    {
        if (_agPaintQueued || !_agOpen) return;
        _agPaintQueued = true;
        After(60, () => { _agPaintQueued = false; AgPaint(); });
    }
}
