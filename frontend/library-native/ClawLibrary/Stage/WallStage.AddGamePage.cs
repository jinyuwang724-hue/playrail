// 设置 › 游戏库 › 添加游戏 选中 exe 以后的几页。和截图页、设置页同一套：左上 24 px 粗标题，标题右边页签（白字 + 2 px 下划线，
// 其余 45 %），设置项用共用的行（PaintRowList），图片直角、焦点白框，没有卡片底、没有按键提示栏。
// - 选定游戏：标题右边是搜索词（像页签一样带下划线，Y / 点一下改），下面一排 SteamGridDB 匹配到的游戏封面，名字下只写来源。A 选定。
// - 添加游戏：页签「封面 / 游戏库里 / Steam 官方」（Steam 官方只在这款游戏在 Steam 商店有官方图时出现）。
//   封面：五种图（竖封面 / 横封面 / 背景 / 标志 / 图标）按它们本来的形状摆开，A 进「更换」；Steam 官方：同样摆开，A 直接换成官方的。
//   游戏库里：在本应用里会是什么样（墙上的小卡、展开的大卡、竖封面）。下面几行：名称 / 加入 Steam / 分类，右下「完成」。
// - 更换：页签是五种图，网格里 A 就替换（闪一下 + 提示），不用再统一保存。
// - 分类：设置行：新建分类，和 Steam 里每个分类的 关 / 开。
// 「完成」立刻关页，保存（下载原图）在后台做完再提示。整页画在一张图上（Win2D），只有打字时放一个 XAML TextBox。
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
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum AgPage { Pick, Art, Change, Category }

    private sealed class AgDraft
    {
        public string? Id;
        public string Name = "", Exe = "", Query = "";
        public bool Steam = true;
        public List<string> Collections = new(), NewCollections = new();
        public string? Sgdb, SteamAppId;
        public List<JsonObject> Matches = new();
        public int Match = -1;
        public string Status = "";
        public bool Searching;
        /// <summary>每种图的候选（Url 为 null = 现在用着的那张），Pick 是用着的下标（-1 = 不用）。</summary>
        public readonly Dictionary<string, List<(string? Url, string Thumb)>> Cands = new();
        public readonly Dictionary<string, int> Pick = new();
        /// <summary>Steam 商店里的官方图（每种一张）。</summary>
        public readonly Dictionary<string, (string Url, string Thumb)> Official = new();
        public readonly HashSet<string> Pending = new();
        public int Version;
    }

    private static readonly (string Kind, string Title)[] AgKinds = { ("p", "竖封面"), ("header", "横封面"), ("hero", "背景"), ("logo", "标志"), ("icon", "图标") };

    /// <summary>封面页上五种图的位置（设计 px）：按各自本来的比例摆。</summary>
    private static readonly Dictionary<string, Rect> AgSlots = new()
    {
        ["p"] = new Rect(40, 112, 240, 360),
        ["header"] = new Rect(312, 112, 382, 179),
        ["hero"] = new Rect(726, 112, 514, 166),
        ["logo"] = new Rect(312, 335, 382, 137),
        ["icon"] = new Rect(726, 330, 142, 142),
    };

    private const float AgRowsTop = 520, AgRowsW = 1200, AgTabsX = 116, AgCatTop = 112;

    private ContainerVisual? _ag;
    private SpriteVisual _agSprite = null!, _agFlash = null!;
    private bool _agOpen;
    private AgPage _agPage;
    private AgDraft? _agDraft;
    private int _agZone, _agIndex, _agTab, _agKind, _agVersion;
    private float _agScroll;
    private TextBox? _agBox;
    /// <summary>正在打字的东西：query（搜索词）/ name（名称）/ newcat（新分类）。</summary>
    private string? _agEditing;
    private readonly List<(Rect R, int Zone, int Index, Action Run)> _agHits = new();
    private readonly List<(Rect R, int Index)> _agTabHits = new();

    // ---------------- 开 / 关 ----------------

    private void AgOpenNew(string exe)
    {
        string dir = Path.GetDirectoryName(exe) ?? "";
        string name = Path.GetFileName(dir.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name) || dir.TrimEnd('\\').Length <= 2) name = Path.GetFileNameWithoutExtension(exe);
        var d = new AgDraft { Name = name, Exe = exe, Query = name };
        foreach (var (kind, _) in AgKinds) { d.Cands[kind] = new(); d.Pick[kind] = -1; }
        AgShow(d, AgPage.Pick);
        _ = AgSearch(d);
    }

    private void AgOpenEdit(JsonObject g)
    {
        var d = new AgDraft
        {
            Id = Str(g["id"]), Name = Str(g["name"]) ?? "", Exe = Str(g["exe"]) ?? "", Steam = Bool(g["steam"]), Sgdb = Str(g["sgdb"]),
            SteamAppId = Str(g["steamAppId"]),
            Collections = (g["collections"] as JsonArray)?.Select(Str).OfType<string>().ToList() ?? new(),
            NewCollections = (g["newCollections"] as JsonArray)?.Select(Str).OfType<string>().ToList() ?? new(),
        };
        d.Query = d.Name;
        foreach (var (kind, _) in AgKinds)
        {
            d.Cands[kind] = new();
            d.Pick[kind] = -1;
            if (g["art"] is JsonObject a && Str(a[kind]) is { Length: > 0 } path) { d.Cands[kind].Add((null, path)); d.Pick[kind] = 0; }
        }
        AgShow(d, AgPage.Art);
        if (d.Sgdb != null) _ = AgLoadCandidates(d, new JsonObject { ["source"] = "sgdb", ["id"] = d.Sgdb });
        if (d.SteamAppId != null) _ = AgLoadOfficial(d);
    }

    private void AgShow(AgDraft d, AgPage page)
    {
        if (_cc == null) return;
        if (_ag == null)
        {
            _ag = G.Container();
            _agSprite = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _agFlash = G.Sprite(G.Solid(Microsoft.UI.Colors.White), relative: false);
            _agFlash.Opacity = 0;
            _ag.Children.InsertAtTop(_agSprite);
            _ag.Children.InsertAtTop(_agFlash);
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
        AgEndEdit(false);
        _agOpen = false;
        _agDraft = null;
        _ag.IsVisible = false;
        if (_st != null && _stOpen) { _st.IsVisible = true; Repaint(); }
        _ = LgRefresh();
        Log.Info("add game page close");
    }

    private void AgGo(AgPage page, int tab = 0)
    {
        AgEndEdit(false);
        _agPage = page;
        _agScroll = 0;
        switch (page)
        {
            case AgPage.Pick:
                _agZone = 1; _agIndex = Math.Max(0, _agDraft?.Match ?? 0);
                break;
            case AgPage.Art:
                _agTab = tab;
                _agZone = 0; _agIndex = 0;
                if (!AgArtTabHasCovers()) { _agZone = 1; _agIndex = _agDraft != null ? AgFirstRow(AgArtRows(_agDraft)) : 0; }
                break;
            case AgPage.Change:
                _agKind = tab;
                _agZone = 0;
                _agIndex = Math.Max(0, _agDraft?.Pick[AgKinds[tab].Kind] ?? 0);
                break;
            case AgPage.Category:
                _agZone = 0; _agIndex = AgFirstRow(AgCategoryRows());
                break;
        }
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
            if (v == d.Version) d.Status = "连不上";
        }
        if (v != d.Version) return;
        d.Searching = false;
        if (_agDraft == d && _agPage == AgPage.Pick && _agEditing == null) { _agZone = 1; _agIndex = 0; _agScroll = 0; }
        AgRepaint(d);
    }

    /// <summary>选定一款：换掉 SteamGridDB 的候选（现在用着的保留），每种默认用第一张；有 Steam 商店的就拿官方图。</summary>
    private void AgChooseMatch(AgDraft d, int i)
    {
        d.Match = i;
        var match = i >= 0 && i < d.Matches.Count ? d.Matches[i] : null;
        d.Sgdb = match != null && Str(match["source"]) == "sgdb" ? Str(match["id"]) : null;
        d.SteamAppId = match != null ? Str(match["steamAppId"]) : null;
        d.Official.Clear();
        AgGo(AgPage.Art);
        if (match != null) _ = AgLoadCandidates(d, match);
        if (d.SteamAppId != null) _ = AgLoadOfficial(d);
    }

    private async Task AgLoadCandidates(AgDraft d, JsonObject match)
    {
        int v = ++d.Version;
        foreach (var (kind, _) in AgKinds)
        {
            var keep = d.Cands[kind].Where(c => c.Url == null).ToList();
            d.Cands[kind].Clear();
            d.Cands[kind].AddRange(keep);
            d.Pick[kind] = keep.Count > 0 ? 0 : -1;
            d.Pending.Add(kind);
        }
        AgRepaint(d);
        await Task.WhenAll(AgKinds.Select(k => AgLoadKind(d, v, match, k.Kind)));
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
            if (!hadOwn) d.Pick[kind] = d.Cands[kind].Count > 0 ? 0 : -1;
        }
        catch (Exception ex) { Log.Warn($"add game candidates {kind}: {ex.Message}"); }
        if (v != d.Version) return;
        d.Pending.Remove(kind);
        AgRepaint(d);
    }

    private async Task AgLoadOfficial(AgDraft d)
    {
        string? appid = d.SteamAppId;
        if (appid == null) return;
        try
        {
            _agent ??= new AgentClient();
            var r = await _agent.CallLong("local_games_official", 30, JsonValue.Create(appid)) as JsonObject;
            if (d.SteamAppId != appid) return;
            foreach (var (kind, _) in AgKinds)
                if ((r?[kind] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() is { } o && Str(o["url"]) is { } url)
                    d.Official[kind] = (url, Str(o["thumb"]) ?? url);
        }
        catch (Exception ex) { Log.Warn($"add game official: {ex.Message}"); }
        AgRepaint(d);
    }

    /// <summary>完成：页面立刻关，原图下载和写清单在后台做，做完提示。</summary>
    private void AgFinish(AgDraft d)
    {
        if (d.Name.Trim().Length == 0) { Toast("名称是空的"); return; }
        var art = new JsonObject();
        foreach (var (kind, _) in AgKinds)
        {
            var list = d.Cands[kind];
            int pick = d.Pick[kind];
            if (pick >= 0 && pick < list.Count && list[pick].Url is string url) art[kind] = url;
            else if (pick < 0 && list.Any(c => c.Url == null)) art[kind] = "";
        }
        var game = new JsonObject
        {
            ["id"] = d.Id, ["name"] = d.Name.Trim(), ["exe"] = d.Exe, ["steam"] = d.Steam, ["sgdb"] = d.Sgdb, ["steamAppId"] = d.SteamAppId, ["art"] = art,
            ["collections"] = new JsonArray(d.Collections.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["newCollections"] = new JsonArray(d.NewCollections.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        };
        string name = d.Name.Trim();
        bool isNew = d.Id == null;
        AgClose();
        _ = Task.Run(async () =>
        {
            try
            {
                _agent ??= new AgentClient();
                await _agent.CallLong("local_games_save", 180, game);
                _dq.TryEnqueue(() => { _stNeedsReload = true; Toast(isNew ? $"已添加：{name}" : $"已保存：{name}"); _ = LgRefresh(); });
            }
            catch (Exception ex) { _dq.TryEnqueue(() => Toast($"「{name}」没有存上：{ex.Message}")); }
        });
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

    // ---------------- 行（和设置页同一套） ----------------

    private List<PerfRow> AgArtRows(AgDraft d)
    {
        var rows = new List<PerfRow>
        {
            new() { Section = true, Title = "游戏" },
            new() { Title = "名称", Value = _agEditing == "name" ? "" : d.Name, Press = () => AgBeginEdit("name") },
            new()
            {
                Title = "加入 Steam", Choices = new[] { "关", "开" }, Choice = d.Steam ? 1 : 0,
                Press = () => { d.Steam = !d.Steam; AgPaint(); }, Step = s => { if ((s > 0) != d.Steam) { d.Steam = s > 0; AgPaint(); } },
                Select = i => { d.Steam = i == 1; AgPaint(); },
            },
            new() { Title = "分类", Value = d.Steam ? AgCategoryText(d) : "", Disabled = !d.Steam, Press = () => AgGo(AgPage.Category) },
        };
        if (d.Id != null)
            rows.Add(new PerfRow { Title = "从游戏库移除", Value = _stArmed == "agremove" ? "再按一次" : "", Press = () => { if (StConfirm("agremove")) _ = AgRemove(d); else AgPaint(); } });
        return rows;
    }

    private List<PerfRow> AgCategoryRows()
    {
        var d = _agDraft;
        var rows = new List<PerfRow>();
        if (d == null) return rows;
        rows.Add(new PerfRow { Section = true, Title = "新建" });
        rows.Add(new PerfRow { Title = "新建分类", Value = _agEditing == "newcat" ? "" : "›", Press = () => AgBeginEdit("newcat") });
        rows.Add(new PerfRow { Section = true, Title = "Steam 分类" });
        foreach (var name in d.NewCollections.ToList())
        {
            var n = name;
            rows.Add(new PerfRow
            {
                Title = n, Choices = new[] { "关", "开" }, Choice = 1, Value = "新建",
                Press = () => { d.NewCollections.Remove(n); AgPaint(); }, Step = s => { if (s < 0) { d.NewCollections.Remove(n); AgPaint(); } },
                Select = i => { if (i == 0) { d.NewCollections.Remove(n); AgPaint(); } },
            });
        }
        foreach (var (id, name) in AgCollections())
        {
            string cid = id;
            bool on = d.Collections.Contains(cid);
            void Set(bool to) { if (to) { if (!d.Collections.Contains(cid)) d.Collections.Add(cid); } else d.Collections.Remove(cid); AgPaint(); }
            rows.Add(new PerfRow
            {
                Title = name, Choices = new[] { "关", "开" }, Choice = on ? 1 : 0,
                Press = () => Set(!on), Step = s => Set(s > 0), Select = i => Set(i == 1),
            });
        }
        return rows;
    }

    private static int AgFirstRow(List<PerfRow> rows) => Math.Max(0, rows.FindIndex(r => !r.Section && !r.Disabled));

    private static float AgRowTop(List<PerfRow> rows, int index)
    {
        float y = 0;
        for (int i = 0; i < index; i++) y += rows[i].Section ? PerfSecH : PerfRowH;
        return y;
    }

    private string AgCategoryText(AgDraft d)
    {
        var names = AgCollections().Where(c => d.Collections.Contains(c.Id)).Select(c => c.Name).Concat(d.NewCollections).ToList();
        if (names.Count == 0) return "不加入";
        return names.Count <= 2 ? string.Join("、", names) : $"{names[0]} 等 {names.Count} 个";
    }

    // ---------------- 输入 ----------------

    /// <summary>添加页的页签：封面 / 游戏库里 /（Steam 官方）。</summary>
    private string[] AgArtTabs() => _agDraft?.Official.Count > 0 ? new[] { "封面", "游戏库里", "Steam 官方" } : new[] { "封面", "游戏库里" };

    private bool AgArtTabHasCovers() => _agTab is 0 or 2;

    /// <summary>当前页签上能聚焦的图（官方页只有有图的几种）。</summary>
    private List<string> AgCoverKinds()
    {
        var d = _agDraft;
        if (d == null || !AgArtTabHasCovers()) return new();
        return _agTab == 2 ? AgKinds.Select(k => k.Kind).Where(d.Official.ContainsKey).ToList() : AgKinds.Select(k => k.Kind).ToList();
    }

    private void AgMove(Direction dir)
    {
        var d = _agDraft;
        if (d == null) return;
        if (_agEditing != null) AgEndEdit(true);
        int dx = dir == Direction.Left ? -1 : dir == Direction.Right ? 1 : 0, dy = dir == Direction.Up ? -1 : dir == Direction.Down ? 1 : 0;
        switch (_agPage)
        {
            case AgPage.Pick:
                if (_agZone == 1 && dx != 0) _agIndex = Math.Clamp(_agIndex + dx, 0, Math.Max(0, d.Matches.Count - 1));
                else if (dy < 0) { _agZone = 0; _agIndex = 0; }
                else if (dy > 0) { _agZone = 1; _agIndex = Math.Max(0, d.Match); }
                break;
            case AgPage.Art:
                AgMoveArt(d, dx, dy);
                break;
            case AgPage.Change:
            {
                var (cols, _, _) = AgGridGeo(AgKinds[_agKind].Kind);
                var list = d.Cands[AgKinds[_agKind].Kind];
                int n = list.Count + (AgKinds[_agKind].Kind is "logo" or "icon" && list.Count > 0 ? 1 : 0);
                if (n == 0) break;
                if (dx != 0) _agIndex = Math.Clamp(_agIndex + dx, 0, n - 1);
                else if (dy > 0 && _agIndex + cols < n) _agIndex += cols;
                else if (dy > 0 && _agIndex / cols < (n - 1) / cols) _agIndex = n - 1;
                else if (dy < 0 && _agIndex - cols >= 0) _agIndex -= cols;
                break;
            }
            case AgPage.Category:
            {
                var rows = AgCategoryRows();
                if (_agZone == 0 && dy != 0)
                {
                    int i = _agIndex + dy;
                    while (i >= 0 && i < rows.Count && rows[i].Section) i += dy;
                    if (i >= rows.Count) { _agZone = 1; _agIndex = 0; }
                    else if (i >= 0) _agIndex = i;
                }
                else if (_agZone == 0 && dx != 0 && _agIndex < rows.Count) rows[_agIndex].Step?.Invoke(dx);
                else if (_agZone == 1 && dy < 0) { _agZone = 0; _agIndex = rows.FindLastIndex(r => !r.Section); }
                break;
            }
        }
        NavHaptic("move");
        AgPaint();
    }

    private void AgMoveArt(AgDraft d, int dx, int dy)
    {
        var kinds = AgCoverKinds();
        var rows = AgArtRows(d);
        switch (_agZone)
        {
            case 0:   // 图：按位置走
            {
                if (kinds.Count == 0) { _agZone = 1; _agIndex = AgFirstRow(rows); break; }
                var cur = AgSlots[kinds[Math.Clamp(_agIndex, 0, kinds.Count - 1)]];
                if (dy > 0)
                {
                    // 下面还有图（横封面 → 标志、背景 → 图标）就去图，否则下到行
                    var below = kinds.Select((k, i) => (k, i)).Where(t => AgSlots[t.k].Y > cur.Y + cur.Height - 1 && Math.Abs(AgSlots[t.k].X - cur.X) < 40).ToList();
                    if (below.Count > 0) _agIndex = below[0].i; else { _agZone = 1; _agIndex = AgFirstRow(rows); }
                }
                else if (dy < 0)
                {
                    var above = kinds.Select((k, i) => (k, i)).Where(t => AgSlots[t.k].Y + AgSlots[t.k].Height < cur.Y + 1 && Math.Abs(AgSlots[t.k].X - cur.X) < 40).ToList();
                    if (above.Count > 0) _agIndex = above[0].i;
                }
                else if (dx != 0)
                {
                    // 左右：按中心点找这一边最近的
                    double cxc = cur.X + cur.Width / 2, cyc = cur.Y + cur.Height / 2;
                    int best = -1; double bestD = double.MaxValue;
                    for (int i = 0; i < kinds.Count; i++)
                    {
                        var r = AgSlots[kinds[i]];
                        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                        if (dx > 0 ? cx <= cxc + 1 : cx >= cxc - 1) continue;
                        double dist = Math.Abs(cx - cxc) + Math.Abs(cy - cyc) * 2;
                        if (dist < bestD) { bestD = dist; best = i; }
                    }
                    if (best >= 0) _agIndex = best;
                }
                break;
            }
            case 1:   // 行
                if (dy != 0)
                {
                    int i = _agIndex + dy;
                    while (i >= 0 && i < rows.Count && rows[i].Section) i += dy;
                    if (i < 0) { if (kinds.Count > 0) { _agZone = 0; _agIndex = 0; } }
                    else if (i >= rows.Count) { _agZone = 2; _agIndex = 0; }
                    else _agIndex = i;
                }
                else if (dx != 0 && _agIndex < rows.Count && !rows[_agIndex].Disabled) rows[_agIndex].Step?.Invoke(dx);
                break;
            default:  // 完成
                if (dy < 0) { _agZone = 1; _agIndex = rows.FindLastIndex(r => !r.Section); }
                break;
        }
    }

    private void AgActivate()
    {
        var d = _agDraft;
        if (d == null) return;
        if (_agEditing != null) { AgEndEdit(true); return; }
        NavHaptic("select");
        switch (_agPage)
        {
            case AgPage.Pick:
                if (_agZone == 0) { AgBeginEdit("query"); return; }
                if (d.Searching) return;
                if (d.Matches.Count == 0) { AgGo(AgPage.Art); return; }   // 一款也没找到：不配图直接往下
                AgChooseMatch(d, Math.Clamp(_agIndex, 0, d.Matches.Count - 1));
                return;
            case AgPage.Art:
                if (_agZone == 0)
                {
                    var kinds = AgCoverKinds();
                    if (_agIndex >= kinds.Count) return;
                    string kind = kinds[_agIndex];
                    if (_agTab == 2) AgUseOfficial(d, kind);
                    else AgGo(AgPage.Change, Array.FindIndex(AgKinds, k => k.Kind == kind));
                    return;
                }
                if (_agZone == 1) { var rows = AgArtRows(d); if (_agIndex < rows.Count && !rows[_agIndex].Disabled) rows[_agIndex].Press?.Invoke(); return; }
                AgFinish(d);
                return;
            case AgPage.Change:
                AgReplace(d, _agIndex);
                return;
            case AgPage.Category:
                if (_agZone == 0) { var rows = AgCategoryRows(); if (_agIndex < rows.Count) rows[_agIndex].Press?.Invoke(); return; }
                AgGo(AgPage.Art);
                return;
        }
    }

    /// <summary>更换页按 A：这一张马上用上，闪一下 + 提示。</summary>
    private void AgReplace(AgDraft d, int index)
    {
        var (kind, title) = AgKinds[_agKind];
        var list = d.Cands[kind];
        if (list.Count == 0) return;
        int to = index < list.Count ? index : -1;
        if (to == d.Pick[kind]) return;
        d.Pick[kind] = to;
        AgFlash(AgChangeTile(index));
        Toast(to < 0 ? $"不用{title}了" : $"已换{title}");
        AgPaint();
    }

    private void AgUseOfficial(AgDraft d, string kind)
    {
        if (!d.Official.TryGetValue(kind, out var o)) return;
        var list = d.Cands[kind];
        int at = list.FindIndex(c => c.Url == o.Url);
        if (at < 0) { list.Add((o.Url, o.Thumb)); at = list.Count - 1; }
        if (d.Pick[kind] == at) return;
        d.Pick[kind] = at;
        AgFlash(AgSlots[kind]);
        Toast($"已换成 Steam 官方的{AgKinds.First(k => k.Kind == kind).Title}");
        AgPaint();
    }

    private void AgFlash(Rect r)
    {
        float k = _ccK;
        _agFlash.Size = new Vector2((float)r.Width * k, (float)r.Height * k);
        _agFlash.Offset = new Vector3((float)r.X * k, (float)r.Y * k, 0);
        if (Reduced) return;
        _agFlash.StopAnimation("Opacity");
        _agFlash.Opacity = 0.55f;
        _agFlash.StartAnimation("Opacity", ToScalar(0, 320, EaseOutCss));
    }

    private void AgBack()
    {
        if (_agEditing != null) { AgEndEdit(false); return; }
        switch (_agPage)
        {
            case AgPage.Change:
            case AgPage.Category: AgGo(AgPage.Art); return;
            case AgPage.Art when _agDraft is { Id: null }: AgGo(AgPage.Pick); return;
            default: AgClose(); return;
        }
    }

    /// <summary>LB / RB：添加页换页签，更换页换图种。</summary>
    private void AgSwitchTab(int delta)
    {
        if (_agDraft == null) return;
        if (_agEditing != null) AgEndEdit(true);
        if (_agPage == AgPage.Art)
        {
            int n = AgArtTabs().Length;
            _agTab = (_agTab + delta + n) % n;
            if (AgArtTabHasCovers()) { _agZone = 0; _agIndex = 0; }
            else if (_agZone == 0) { _agZone = 1; _agIndex = AgFirstRow(AgArtRows(_agDraft)); }
        }
        else if (_agPage == AgPage.Change)
        {
            _agKind = (_agKind + delta + AgKinds.Length) % AgKinds.Length;
            _agScroll = 0;
            _agIndex = Math.Max(0, _agDraft.Pick[AgKinds[_agKind].Kind]);
        }
        else return;
        NavHaptic("tab");
        AgPaint();
    }

    /// <summary>Y：选定游戏页 = 改搜索词。</summary>
    private void AgY()
    {
        if (_agPage == AgPage.Pick) AgBeginEdit("query");
    }

    /// <summary>X：添加页 = 分类。</summary>
    private void AgX()
    {
        if (_agPage == AgPage.Art && _agDraft is { Steam: true }) AgGo(AgPage.Category);
    }

    private void AgClick(double sx, double sy)
    {
        float x = (float)sx / _ccK, y = (float)sy / _ccK;
        var p = new Point(x, y);
        foreach (var (r, i) in _agTabHits)
            if (r.Contains(p))
            {
                if (_agPage == AgPage.Art) AgSwitchTab(i - _agTab);
                else if (_agPage == AgPage.Change) AgSwitchTab(i - _agKind);
                return;
            }
        foreach (var h in _agHits)
            if (h.R.Contains(p))
            {
                if (_agEditing != null) AgEndEdit(true);
                _agZone = h.Zone;
                _agIndex = h.Index;
                h.Run();
                return;
            }
    }

    // ---------------- 打字 ----------------

    /// <summary>输入框放在要改的那段文字上（那段文字先不画）：搜索词左对齐，行里的值右对齐。</summary>
    private void AgBeginEdit(string what)
    {
        var d = _agDraft;
        if (d == null) return;
        Rect r;
        string text;
        bool right = true;
        switch (what)
        {
            case "query":
                if (_agPage != AgPage.Pick) return;
                r = AgQueryRect(); text = d.Query; right = false;
                _agZone = 0; _agIndex = 0;
                break;
            case "name":
            {
                var rows = AgArtRows(d);
                int i = rows.FindIndex(x => x.Title == "名称");
                _agZone = 1; _agIndex = i;
                r = new Rect(40 + 400, AgRowsTop + AgRowTop(rows, i), AgRowsW - 400, PerfRowH);
                text = d.Name;
                break;
            }
            case "newcat":
            {
                var rows = AgCategoryRows();
                int i = rows.FindIndex(x => x.Title == "新建分类");
                _agZone = 0; _agIndex = i;
                r = new Rect(40 + 400, AgCatTop + AgRowTop(rows, i), AgRowsW - 400, PerfRowH);
                text = "";
                break;
            }
            default: return;
        }
        if (_agBox == null)
        {
            _agBox = new TextBox
            {
                MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0), RequestedTheme = ElementTheme.Dark,
                IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, Visibility = Visibility.Collapsed,
                FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            };
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
                _agBox.Resources[key] = clear;
            var ink = new SolidColorBrush(Theme.Ink);
            foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
                _agBox.Resources[key] = ink;
            _agBox.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; AgEndEdit(true); }
                else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; AgEndEdit(false); }
            };
            _agBox.LostFocus += (_, _) => { if (_agEditing != null) AgEndEdit(true); };
            _agBox.Loaded += (_, _) =>
            {
                if (FindChild(_agBox, "DeleteButton") is FrameworkElement x) { x.MaxWidth = 0; x.Opacity = 0; x.IsHitTestVisible = false; }
            };
            _rootEl.Overlay.Children.Add(_agBox);
        }
        float k = _ccK;
        _agEditing = what;
        _agBox.Text = text;
        _agBox.TextAlignment = right ? TextAlignment.Right : TextAlignment.Left;
        _agBox.FontSize = 17 * k;
        _agBox.Width = r.Width * k;
        _agBox.Height = r.Height * k;
        // 字的垂直位置和画出来的字一样：17 px 字，行高约 1.33 倍
        _agBox.Padding = new Thickness(0, Math.Max(0, (r.Height - 17 * 1.33) / 2 * k - 1), 0, 0);
        Canvas.SetLeft(_agBox, r.X * k);
        Canvas.SetTop(_agBox, r.Y * k);
        _agBox.Visibility = Visibility.Visible;
        AgPaint();
        _agBox.Focus(FocusState.Programmatic);
        _agBox.SelectAll();
        ShowTouchKeyboard();
    }

    private void AgEndEdit(bool commit)
    {
        var what = _agEditing;
        if (what == null || _agBox == null) return;
        _agEditing = null;
        string text = _agBox.Text.Trim();
        _agBox.Visibility = Visibility.Collapsed;
        _rootEl.Focus(FocusState.Programmatic);
        var d = _agDraft;
        if (commit && d != null)
            switch (what)
            {
                case "query":
                    if (text.Length > 0 && (text != d.Query || d.Matches.Count == 0) && !d.Searching) { d.Query = text; _ = AgSearch(d); }
                    break;
                case "name":
                    if (text.Length > 0) d.Name = text;
                    break;
                case "newcat":
                    if (text.Length > 0)
                    {
                        var same = AgCollections().FirstOrDefault(c => c.Name == text);
                        if (same.Id != null) { if (!d.Collections.Contains(same.Id)) d.Collections.Add(same.Id); }
                        else if (!d.NewCollections.Contains(text)) d.NewCollections.Add(text);
                    }
                    break;
            }
        if (_agOpen) AgPaint();
    }

    private void AgHideBox() => AgEndEdit(false);

    // ---------------- 绘制 ----------------

    private Color AgMuted => Gfx.WithAlpha(CcText, 0.55f);
    private static readonly FontSpec AgMono = new("Cascadia Mono", 400);

    private void AgPaint()
    {
        if (!_agOpen || _ag == null || _agDraft == null) return;
        float k = _ccK, raster = k * Chrome.Dpi;
        _agHits.Clear();
        _agTabHits.Clear();
        var d = _agDraft;
        CcSet(_agSprite, CcDraw(1280, 800, raster, ds =>
        {
            switch (_agPage)
            {
                case AgPage.Pick: AgPaintPick(ds, d); break;
                case AgPage.Art: AgPaintArt(ds, d); break;
                case AgPage.Change: AgPaintChange(ds, d); break;
                default: AgPaintCategory(ds, d); break;
            }
        }));
        _agSprite.Size = new Vector2(1280 * k, 800 * k);
        _agSprite.Offset = Vector3.Zero;
    }

    /// <summary>左上标题（截图页同款：24 px 粗体，40, 32）。返回页签起点：和截图页一样，页签字离标题右边 40。</summary>
    private float AgTitle(CanvasDrawingSession ds, string title)
    {
        using var tl = TextKit.Layout(ds, title, TextKit.Ui(700), 24, 600, CanvasHorizontalAlignment.Left);
        var bb = tl.LayoutBounds;
        ds.DrawTextLayout(tl, 40 - (float)bb.X, 32 - (float)bb.Y, CcText);
        return 40 + (float)bb.Width + 28;
    }

    /// <summary>标题右边的页签（截图页同款）：名字 17，后面小号数字；当前白字 + 2 px 下划线，其余 45 %。</summary>
    private void AgTabs(CanvasDrawingSession ds, IReadOnlyList<(string Name, string? Count)> tabs, int current, float x0)
    {
        const float cy = 48, Pad = 12, Gap = 6;
        var ui = TextKit.Ui(400);
        float x = x0;
        for (int i = 0; i < tabs.Count; i++)
        {
            bool cur = i == current;
            float nw = PWidth(ds, tabs[i].Name, ui, 17);
            float cw = tabs[i].Count is { } c ? PWidth(ds, c, AgMono, 13) : 0;
            var col = cur ? CcText : Gfx.WithAlpha(CcText, 0.45);
            PText(ds, tabs[i].Name, ui, 17, col, x + Pad, cy, nw + 4);
            if (tabs[i].Count is { } count) PText(ds, count, AgMono, 13, Gfx.WithAlpha(CcText, cur ? 0.5 : 0.4), x + Pad + nw + Gap, cy + 1, cw + 4);
            if (cur) ds.FillRectangle(x + Pad, cy + 13, nw, 2, CcText);
            float w = Pad + nw + (cw > 0 ? Gap + cw : 0) + Pad;
            _agTabHits.Add((new Rect(x, 26, w, 44), i));
            x += w + 8;
        }
    }

    /// <summary>一张图：有图就画（cover 铺满 / contain 放进去），没图是一块淡底 + 图片图标；焦点白框。</summary>
    private void AgImage(CanvasDrawingSession ds, Rect r, string? thumb, bool contain, bool focused, bool pending = false)
    {
        var bmp = AgBitmapOf(thumb);
        if (bmp != null && !contain) DrawCovered(ds, bmp, r);
        else
        {
            ds.FillRectangle(r, Gfx.WithAlpha(CcText, 0.06f));
            if (bmp != null) DrawContained(ds, bmp, new Rect(r.X + r.Width * 0.08, r.Y + r.Height * 0.1, r.Width * 0.84, r.Height * 0.8));
            else if (thumb == null && !pending) AgPictureIcon(ds, r);
        }
        if (focused) AgRing(ds, r);
    }

    /// <summary>没有图：图片图标（框 + 山 + 太阳），淡色。</summary>
    private void AgPictureIcon(CanvasDrawingSession ds, Rect r)
    {
        float s = (float)Math.Min(40, Math.Min(r.Width, r.Height) * 0.3);
        float cx = (float)(r.X + r.Width / 2), cy = (float)(r.Y + r.Height / 2);
        var c = Gfx.WithAlpha(CcText, 0.32f);
        using var stroke = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        ds.DrawRoundedRectangle(cx - s / 2, cy - s * 0.4f, s, s * 0.8f, 3, 3, c, 1.6f, stroke);
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(cx - s * 0.4f, cy + s * 0.3f);
        pb.AddLine(cx - s * 0.12f, cy - s * 0.02f);
        pb.AddLine(cx + s * 0.06f, cy + s * 0.16f);
        pb.AddLine(cx + s * 0.18f, cy + s * 0.06f);
        pb.AddLine(cx + s * 0.4f, cy + s * 0.3f);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var g = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(g, c, 1.6f, stroke);
        ds.DrawCircle(cx + s * 0.2f, cy - s * 0.16f, s * 0.07f, c, 1.4f);
    }

    /// <summary>焦点：白框（墙上卡片同款），外面一圈淡黑边让它在亮图上也看得清。</summary>
    private void AgRing(CanvasDrawingSession ds, Rect r)
    {
        ds.DrawRectangle(new Rect(r.X - 3.5, r.Y - 3.5, r.Width + 7, r.Height + 7), Gfx.Rgba(0, 0, 0, 0.45), 1);
        ds.DrawRectangle(new Rect(r.X - 1.5, r.Y - 1.5, r.Width + 3, r.Height + 3), CcText, 3);
    }

    /// <summary>搜索词的位置（在「选定游戏」标题后面，页签的位置）。</summary>
    private float _agQueryX = AgTabsX;
    private Rect AgQueryRect() => new(_agQueryX + 12 + 26, 34, 420, 28);

    private void AgPaintPick(CanvasDrawingSession ds, AgDraft d)
    {
        _agQueryX = AgTitle(ds, "选定游戏");
        // 搜索词：放在页签的位置，放大镜 + 字 + 下划线；焦点在它上面时是白字白线
        var q = AgQueryRect();
        bool qf = _agZone == 0;
        var ui = TextKit.Ui(400);
        AgSearchIcon(ds, _agQueryX + 12, 48, qf ? CcText : Gfx.WithAlpha(CcText, 0.6f));
        float qw = Math.Min(420, Math.Max(40, PWidth(ds, d.Query, ui, 17)));
        if (_agEditing != "query")
        {
            PText(ds, d.Query, ui, 17, qf ? CcText : Gfx.WithAlpha(CcText, 0.75f), (float)q.X, 48, 420);
            ds.FillRectangle((float)q.X, 61, qw, 2, qf ? CcText : Gfx.WithAlpha(CcText, 0.25f));
        }
        else { qw = 420; ds.FillRectangle((float)q.X, 61, qw, 2, CcText); }
        string count = d.Searching ? "…" : d.Matches.Count.ToString();
        PText(ds, count, AgMono, 13, Gfx.WithAlpha(CcText, 0.45), (float)q.X + qw + 10, 49, 60);
        _agHits.Add((new Rect(_agQueryX, 26, 26 + 12 + 440, 44), 0, 0, () => AgBeginEdit("query")));

        // 一排封面（墙上卡片的样子：直角、焦点白框），名字下面只写来源
        const float cw = 220, ch = 330, gap = 25, top = 112;
        int n = d.Matches.Count;
        int focus = _agZone == 1 ? _agIndex : -1;
        int first = Math.Clamp((int)_agScroll, 0, Math.Max(0, n - 5));
        if (focus >= 0 && focus < first) first = focus;
        if (focus > first + 4) first = focus - 4;
        _agScroll = first;
        for (int i = first; i < Math.Min(n, first + 5); i++)
        {
            var m = d.Matches[i];
            float x = 40 + (i - first) * (cw + gap);
            var r = new Rect(x, top, cw, ch);
            AgImage(ds, r, Str(m["thumb"]), false, i == focus);
            using (var nl = TextKit.Layout(ds, Str(m["name"]) ?? "", TextKit.Ui(400), 17, cw, CanvasHorizontalAlignment.Left, maxLines: 2))
            {
                ds.DrawTextLayout(nl, x, top + ch + 14, i == focus ? CcText : Gfx.WithAlpha(CcText, 0.85f));
                string src = Str(m["source"]) == "steam" ? "Steam 商店" : "SteamGridDB";
                PText(ds, src, TextKit.Ui(400), 13, AgMuted, x, top + ch + 14 + (float)nl.LayoutBounds.Height + 14, cw);
            }
            int idx = i;
            _agHits.Add((new Rect(x, top, cw, ch + 70), 1, idx, () => { if (!d.Searching) AgChooseMatch(d, idx); }));
        }
        if (n == 0 && !d.Searching)
        {
            // 一个也没找到：一块空的卡位，A 不配图往下走
            var r = new Rect(40, top, cw, ch);
            AgImage(ds, r, null, false, _agZone == 1);
            _agHits.Add((r, 1, 0, () => AgGo(AgPage.Art)));
        }
    }

    private static void AgSearchIcon(CanvasDrawingSession ds, float x, float cy, Color c)
    {
        ds.DrawCircle(x + 7, cy - 1.5f, 6, c, 1.6f);
        using var stroke = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        ds.DrawLine(x + 11.5f, cy + 3, x + 15.5f, cy + 7, c, 1.6f, stroke);
    }

    private void AgPaintArt(CanvasDrawingSession ds, AgDraft d)
    {
        float tx = AgTitle(ds, d.Id == null ? "添加游戏" : "编辑游戏");
        var tabs = AgArtTabs();
        _agTab = Math.Clamp(_agTab, 0, tabs.Length - 1);
        AgTabs(ds, tabs.Select(t => (t, (string?)null)).ToList(), _agTab, tx);

        if (_agTab == 1) AgPaintLibraryPreview(ds, d);
        else
        {
            var kinds = AgCoverKinds();
            foreach (var (kind, title) in AgKinds)
            {
                var r = AgSlots[kind];
                string? thumb;
                if (_agTab == 2) thumb = d.Official.TryGetValue(kind, out var o) ? o.Thumb : null;
                else thumb = d.Pick[kind] >= 0 && d.Pick[kind] < d.Cands[kind].Count ? d.Cands[kind][d.Pick[kind]].Thumb : null;
                int fi = kinds.IndexOf(kind);
                bool focused = _agZone == 0 && fi == _agIndex;
                bool inUse = _agTab == 2 && thumb != null && d.Pick[kind] >= 0 && d.Pick[kind] < d.Cands[kind].Count && d.Cands[kind][d.Pick[kind]].Url == d.Official[kind].Url;
                AgImage(ds, r, thumb, kind is "logo" or "icon", focused, _agTab == 0 ? d.Pending.Contains(kind) : d.SteamAppId != null && d.Official.Count == 0);
                if (inUse) AgInUse(ds, r);
                PText(ds, title, TextKit.Ui(400), 13, focused ? CcText : AgMuted, (float)r.X, (float)(r.Y + r.Height + 16), (float)r.Width);
                if (fi >= 0) _agHits.Add((r, 0, fi, AgActivate));
            }
        }

        // 下面的设置行（设置页同款，铺满宽度）
        var rows = AgArtRows(d);
        AgPaintRows(ds, rows, AgRowsTop, _agZone == 1 ? _agIndex : -1, 1);
        AgDone(ds, _agZone == 2, () => AgFinish(d));
    }

    private void AgPaintRows(CanvasDrawingSession ds, List<PerfRow> rows, float top, int focus, int zone)
    {
        var old = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(40, top) * old;
        PaintRowList(ds, rows, focus, null, true, AgRowsW);
        ds.Transform = old;
        float y = top;
        for (int i = 0; i < rows.Count; i++)
        {
            float h = rows[i].Section ? PerfSecH : PerfRowH;
            if (!rows[i].Section && !rows[i].Disabled)
            {
                var row = rows[i];
                _agHits.Add((new Rect(40, y, AgRowsW, h), zone, i, () => row.Press?.Invoke()));
            }
            y += h;
        }
    }

    /// <summary>右下「完成」：和展开卡上的操作同一种圆角按钮；焦点是白底黑字。</summary>
    private void AgDone(CanvasDrawingSession ds, bool focused, Action run)
    {
        var f = TextKit.Ui(600);
        float tw = PWidth(ds, "完成", f, 17), w = Math.Max(112, tw + 48), h = 44;
        var r = new Rect(1240 - w, 724, w, h);
        ds.FillRoundedRectangle(r, 12, 12, focused ? Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96) : Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.14));
        PText(ds, "完成", f, 17, focused ? Gfx.Rgba(9, 9, 11) : CcText, (float)r.X, (float)(r.Y + h / 2), w, CanvasHorizontalAlignment.Center);
        _agHits.Add((r, 2, 0, run));
    }

    /// <summary>「游戏库里」：这套图在本应用里的样子——墙上的小卡（方）、展开的大卡（背景 + 标题）、竖封面、图标。</summary>
    private void AgPaintLibraryPreview(CanvasDrawingSession ds, AgDraft d)
    {
        string? Use(string kind) => d.Pick[kind] >= 0 && d.Pick[kind] < d.Cands[kind].Count ? d.Cands[kind][d.Pick[kind]].Thumb : null;
        string? hero = Use("hero") ?? Use("header") ?? Use("p"), portrait = Use("p") ?? hero;
        var small = new Rect(40, 112, 220, 220);
        AgImage(ds, small, hero, false, false);
        AgCardTitle(ds, small, d.Name, 15, null);
        PText(ds, "墙上", TextKit.Ui(400), 13, AgMuted, 40, 112 + 220 + 16, 220);
        var big = new Rect(292, 112, 360, 360);
        AgImage(ds, big, hero, false, false);
        AgCardTitle(ds, big, d.Name, 24, "本地游戏");
        PText(ds, "展开", TextKit.Ui(400), 13, AgMuted, 292, 112 + 360 + 16, 360);
        var tall = new Rect(684, 112, 240, 360);
        AgImage(ds, tall, portrait, false, false);
        PText(ds, "封面墙", TextKit.Ui(400), 13, AgMuted, 684, 112 + 360 + 16, 240);
        var icon = new Rect(956, 112, 96, 96);
        AgImage(ds, icon, Use("icon"), true, false);
        PText(ds, "图标", TextKit.Ui(400), 13, AgMuted, 956, 112 + 96 + 16, 96);
    }

    /// <summary>卡片上的标题（墙上「电影字幕」样式的简化：衬线、居中、下方暗角）。</summary>
    private void AgCardTitle(CanvasDrawingSession ds, Rect r, string title, float size, string? sub)
    {
        float bottom = (float)(r.Y + r.Height), gh = (float)r.Height * 0.45f;
        using (var grad = new Microsoft.Graphics.Canvas.Brushes.CanvasLinearGradientBrush(ds, Gfx.Rgba(0, 0, 0, 0), Gfx.Rgba(0, 0, 0, 0.7))
        { StartPoint = new Vector2(0, bottom - gh), EndPoint = new Vector2(0, bottom) })
            ds.FillRectangle(new Rect(r.X, bottom - gh, r.Width, gh), grad);
        float cy = bottom - (sub != null ? 52 : 26);
        PText(ds, title, TextKit.TitleCjk, size, CcText, (float)r.X + 12, cy, (float)r.Width - 24, CanvasHorizontalAlignment.Center);
        if (sub != null) PText(ds, sub, TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.75f), (float)r.X, bottom - 22, (float)r.Width, CanvasHorizontalAlignment.Center);
    }

    private static (int Cols, float W, float H) AgGridGeo(string kind) => kind switch
    {
        "p" => (6, 180, 270),
        "header" => (4, 285, 133),
        "hero" => (3, 384, 124),
        "logo" => (4, 285, 120),
        _ => (8, 132, 132),
    };

    private Rect AgChangeTile(int i)
    {
        var (cols, w, h) = AgGridGeo(AgKinds[_agKind].Kind);
        float gx = (1200 - cols * w) / (cols - 1);
        return new Rect(40 + (i % cols) * (w + gx), 112 + (i / cols) * (h + 24) - _agScroll, w, h);
    }

    private void AgPaintChange(CanvasDrawingSession ds, AgDraft d)
    {
        float tx = AgTitle(ds, "更换封面");
        AgTabs(ds, AgKinds.Select(k => (k.Title, d.Pending.Contains(k.Kind) ? (string?)"…" : d.Cands[k.Kind].Count(c => c.Url != null).ToString())).ToList(), _agKind, tx);
        var (kind, _) = AgKinds[_agKind];
        var (cols, w, h) = AgGridGeo(kind);
        var list = d.Cands[kind];
        bool contain = kind is "logo" or "icon";
        int n = list.Count + (contain && list.Count > 0 ? 1 : 0);
        int pick = d.Pick[kind];
        // 滚到焦点那一行露出来
        float rowH = h + 24, view = 800 - 112 - 40;
        int frow = _agIndex / cols;
        if (frow * rowH < _agScroll) _agScroll = frow * rowH;
        if (frow * rowH + h > _agScroll + view) _agScroll = frow * rowH + h - view;
        if (n == 0) { if (!d.Pending.Contains(kind)) AgImage(ds, new Rect(40, 112, w, h), null, contain, false); return; }
        using (ds.CreateLayer(1, new Rect(30, 102, 1220, view + 20)))
            for (int i = 0; i < n; i++)
            {
                var r = AgChangeTile(i);
                if (r.Y + r.Height < 100 || r.Y > 800) continue;
                bool focused = i == _agIndex;
                if (i < list.Count) AgImage(ds, r, list[i].Thumb, contain, focused);
                else
                {
                    // 「不用」：空的一格
                    ds.FillRectangle(r, Gfx.WithAlpha(CcText, 0.04f));
                    ds.DrawRectangle(r, Gfx.WithAlpha(CcText, 0.18f), 1);
                    PText(ds, "不用", TextKit.Ui(400), 15, AgMuted, (float)r.X, (float)(r.Y + r.Height / 2), (float)r.Width, CanvasHorizontalAlignment.Center);
                    if (focused) AgRing(ds, r);
                }
                bool used = i < list.Count ? i == pick : pick < 0;
                if (used) AgInUse(ds, r);
                int idx = i;
                _agHits.Add((r, 0, idx, () => AgReplace(d, idx)));
            }
    }

    /// <summary>正在用的那张：右上角一个白底小勾。</summary>
    private static void AgInUse(CanvasDrawingSession ds, Rect r)
    {
        float x = (float)(r.X + r.Width - 30), y = (float)r.Y + 8;
        ds.FillCircle(x + 11, y + 11, 11, Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96));
        using var stroke = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        ds.DrawLine(x + 6, y + 11.5f, x + 9.5f, y + 15, Gfx.Rgba(9, 9, 11), 2.2f, stroke);
        ds.DrawLine(x + 9.5f, y + 15, x + 16, y + 7.5f, Gfx.Rgba(9, 9, 11), 2.2f, stroke);
    }

    private void AgPaintCategory(CanvasDrawingSession ds, AgDraft d)
    {
        AgTitle(ds, "分类");
        var rows = AgCategoryRows();
        AgPaintRows(ds, rows, AgCatTop, _agZone == 0 ? _agIndex : -1, 0);
        AgDone(ds, _agZone == 1, () => AgGo(AgPage.Art));
    }

    // ---------------- 图 ----------------

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
                if (_lgBitmaps.Count > 200)
                    foreach (var k in _lgBitmaps.Keys.Where(k => k != path).Take(_lgBitmaps.Count - 150).ToList()) { _lgBitmaps[k]?.Dispose(); _lgBitmaps.Remove(k); }
                AgPaintSoon();
            });
        }
        catch (Exception ex) { Log.Warn($"preview {Path.GetFileName(path)}: {ex.Message}"); }
    }

    /// <summary>截图自测用（--state=add0..5 --exe=… --name=…，名字里的 _ 当空格）：打开设置页，直接进添加游戏。</summary>
    internal void AddGameTestState(string exe)
    {
        if (!_ccOpen) return;
        OpenSettingsPage();
        _ = LgRefresh();
        AgOpenNew(exe);
        if (Program.Args.FirstOrDefault(a => a.StartsWith("--name=")) is { } n && _agDraft is { } d)
        {
            d.Name = d.Query = n[7..].Replace('_', ' ');
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
