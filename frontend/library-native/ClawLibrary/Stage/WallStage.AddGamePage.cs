// 设置 › 游戏库 › 添加游戏 / 编辑游戏。和性能、设置页同一套：左上 24 px 粗标题，标题右边页签（白字 + 2 px 下划线，其余 45 %），
// 左栏 40–388 是信息（共用的设置行：名称 / 匹配 / 加入 Steam / 分类，下面程序路径），右栏 428–1240 是封面，右下「完成」。
// 右栏页签：封面图片 / 背景 / 徽标 / 宽幅封面图片（各是一种图的候选网格，A 就换成这张，闪一下 + 提示）、
// Steam 官方封面（这款游戏在 Steam 商店的官方图，A 换成官方的）、封面概览（正在用的几张；图标在这里，可以从本地选）。
// 后两页同一种排法：从上到下 封面图片、背景、徽标、宽幅封面图片（概览多一个图标）。
// 选完 exe 直接进来，上面弹出「匹配游戏」弹窗（WallStage.AddGamePopup.cs）；之后「匹配」那一行或 Y 再打开。
// 分类是单独一页（设置行：新建分类，和每个 Steam 分类的 关 / 开）。「完成」立刻关页，保存在后台做完再提示。
// 整页画在一张图上（Win2D），只有打字时放一个 XAML TextBox。
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
    private enum AgPage { Art, Category }

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

    private static readonly (string Kind, string Title)[] AgKinds = { ("p", "封面图片"), ("hero", "背景"), ("logo", "徽标"), ("header", "宽幅封面图片"), ("icon", "图标") };

    private const float AgRowsW = 1200, AgCatTop = 112;

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
        var d = new AgDraft { Name = name, Exe = exe, Query = Path.GetFileNameWithoutExtension(exe) };
        foreach (var (kind, _) in AgKinds) { d.Cands[kind] = new(); d.Pick[kind] = -1; }
        AgShow(d, AgPage.Art);
        AgOpenPop();
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
        AgClosePop(instant: true);
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
            case AgPage.Art:
                _agTab = tab;
                // 右栏有图就落在右栏，没有就落在左栏第一行
                if (_agDraft != null && AgRightCount() > 0) { _agZone = 1; _agIndex = Math.Min(AgRightDefault(_agDraft), AgRightCount() - 1); }
                else { _agZone = 0; _agIndex = _agDraft != null ? AgFirstRow(AgArtRows(_agDraft)) : 0; }
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
        // 弹窗里：有结果就高亮第一款（直接按 A / 回车就用它），继续打字会重新搜
        if (_agDraft == d && _agPopOpen) _agPopIndex = d.Matches.Count > 0 ? 0 : -1;
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
        AgClosePop();
        AgGo(AgPage.Art, _agTab);
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
            new() { Title = "名称", Value = _agEditing == "name" ? "" : d.Name, Press = () => AgBeginEdit("name") },
            new() { Title = "匹配", Value = d.Match >= 0 && d.Match < d.Matches.Count ? Str(d.Matches[d.Match]["name"]) ?? "" : d.Sgdb != null ? "SteamGridDB" : "未匹配", Press = AgOpenPop },
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

    /// <summary>右栏的页签：四种图，Steam 官方封面，封面概览。</summary>
    private static readonly string[] AgTabNames = { "封面图片", "背景", "徽标", "宽幅封面图片", "Steam 官方封面", "封面概览" };
    private const int AgTabOfficial = 4, AgTabOverview = 5;

    /// <summary>四种图页签对应的图种。</summary>
    private static readonly string[] AgTabKinds = { "p", "hero", "logo", "header" };

    /// <summary>官方封面、封面概览两页从上到下的顺序（概览多一个图标）。</summary>
    private List<string> AgStackKinds() => _agTab == AgTabOverview ? new() { "p", "hero", "logo", "header", "icon" } : new() { "p", "hero", "logo", "header" };

    private static string AgKindTitle(string kind) => AgKinds.First(k => k.Kind == kind).Title;

    /// <summary>右栏当前页签能聚焦的格数。</summary>
    private int AgRightCount()
    {
        var d = _agDraft;
        if (d == null) return 0;
        if (_agTab >= AgTabOfficial) return AgStackKinds().Count;
        string kind = AgTabKinds[_agTab];
        int n = d.Cands[kind].Count;
        return n + (kind == "logo" && n > 0 ? 1 : 0);
    }

    private void AgMove(Direction dir)
    {
        var d = _agDraft;
        if (d == null) return;
        if (_agPopOpen) { AgPopMove(dir); return; }
        if (_agEditing != null) AgEndEdit(true);
        int dx = dir == Direction.Left ? -1 : dir == Direction.Right ? 1 : 0, dy = dir == Direction.Up ? -1 : dir == Direction.Down ? 1 : 0;
        switch (_agPage)
        {
            case AgPage.Art:
                AgMoveArt(d, dx, dy);
                break;
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

    /// <summary>左栏（信息行）← → 右栏（封面）；右栏底下是「完成」。</summary>
    private void AgMoveArt(AgDraft d, int dx, int dy)
    {
        var rows = AgArtRows(d);
        int n = AgRightCount();
        switch (_agZone)
        {
            case 0:   // 左栏的行
                if (dy != 0)
                {
                    int i = _agIndex + dy;
                    while (i >= 0 && i < rows.Count && rows[i].Section) i += dy;
                    if (i >= 0 && i < rows.Count) _agIndex = i;
                }
                else if (dx > 0)
                {
                    // 行里能左右改的（加入 Steam）先改值，其余往右栏走
                    if (_agIndex < rows.Count && rows[_agIndex].Choices != null && rows[_agIndex].Step != null && !rows[_agIndex].Disabled && rows[_agIndex].Choice == 0) rows[_agIndex].Step!(1);
                    else if (n > 0) { _agZone = 1; _agIndex = Math.Min(AgRightDefault(d), n - 1); }
                    else { _agZone = 2; _agIndex = 0; }
                }
                else if (dx < 0 && _agIndex < rows.Count && rows[_agIndex].Choices != null && rows[_agIndex].Step != null && !rows[_agIndex].Disabled) rows[_agIndex].Step!(-1);
                break;
            case 1:   // 右栏
                if (_agTab >= AgTabOfficial)
                {
                    if (dy > 0) { if (_agIndex < n - 1) _agIndex++; else { _agZone = 2; _agIndex = 0; } }
                    else if (dy < 0 && _agIndex > 0) _agIndex--;
                    else if (dx < 0) { _agZone = 0; _agIndex = AgFirstRow(rows); }
                    break;
                }
                var (cols, _, _, _) = AgGridGeo(AgTabKinds[_agTab]);
                if (dx != 0)
                {
                    if (dx < 0 && _agIndex % cols == 0) { _agZone = 0; _agIndex = AgFirstRow(rows); }
                    else _agIndex = Math.Clamp(_agIndex + dx, 0, n - 1);
                }
                else if (dy > 0) { if (_agIndex + cols < n) _agIndex += cols; else if (_agIndex / cols < (n - 1) / cols) _agIndex = n - 1; else { _agZone = 2; _agIndex = 0; } }
                else if (dy < 0 && _agIndex - cols >= 0) _agIndex -= cols;
                break;
            default:  // 完成
                if (dy < 0) { if (n > 0) { _agZone = 1; _agIndex = n - 1; } else { _agZone = 0; _agIndex = rows.FindLastIndex(r => !r.Section); } }
                else if (dx < 0) { _agZone = 0; _agIndex = rows.FindLastIndex(r => !r.Section); }
                break;
        }
    }

    /// <summary>进右栏时落在哪：图种页签落在正在用的那张。</summary>
    private int AgRightDefault(AgDraft d) => _agTab < AgTabOfficial ? Math.Max(0, d.Pick[AgTabKinds[_agTab]]) : 0;

    private void AgActivate()
    {
        var d = _agDraft;
        if (d == null) return;
        if (_agPopOpen) { AgPopEnter(); return; }
        if (_agEditing != null) { AgEndEdit(true); return; }
        NavHaptic("select");
        switch (_agPage)
        {
            case AgPage.Art:
                if (_agZone == 0) { var rows = AgArtRows(d); if (_agIndex < rows.Count && !rows[_agIndex].Disabled) rows[_agIndex].Press?.Invoke(); return; }
                if (_agZone == 1) { AgRightPress(d, _agIndex); return; }
                AgFinish(d);
                return;
            case AgPage.Category:
                if (_agZone == 0) { var rows = AgCategoryRows(); if (_agIndex < rows.Count) rows[_agIndex].Press?.Invoke(); return; }
                AgGo(AgPage.Art);
                return;
        }
    }

    /// <summary>右栏按 A：图种页 = 换成这张；官方页 = 换成官方的；概览 = 去那种图的页签（图标 = 从本地选）。</summary>
    private void AgRightPress(AgDraft d, int index)
    {
        if (_agTab < AgTabOfficial) { AgReplace(d, AgTabKinds[_agTab], index); return; }
        var kinds = AgStackKinds();
        if (index < 0 || index >= kinds.Count) return;
        string kind = kinds[index];
        if (_agTab == AgTabOfficial) { AgUseOfficial(d, kind, index); return; }
        if (kind == "icon") { _ = AgPickLocalIcon(d); return; }
        _agTab = Array.IndexOf(AgTabKinds, kind);
        _agScroll = 0;
        _agIndex = AgRightDefault(d);
        AgPaint();
    }

    /// <summary>换图：这一张马上用上，闪一下 + 提示。</summary>
    private void AgReplace(AgDraft d, string kind, int index)
    {
        var list = d.Cands[kind];
        if (list.Count == 0) return;
        int to = index < list.Count ? index : -1;
        if (to == d.Pick[kind]) return;
        d.Pick[kind] = to;
        AgFlash(AgGridTile(kind, index));
        Toast(to < 0 ? $"不用{AgKindTitle(kind)}了" : $"已换{AgKindTitle(kind)}");
        AgPaint();
    }

    private void AgUseOfficial(AgDraft d, string kind, int index)
    {
        if (!d.Official.TryGetValue(kind, out var o)) return;
        var list = d.Cands[kind];
        int at = list.FindIndex(c => c.Url == o.Url);
        if (at < 0) { list.Add((o.Url, o.Thumb)); at = list.Count - 1; }
        if (d.Pick[kind] == at) return;
        d.Pick[kind] = at;
        AgFlash(AgStackRects()[index].Image);
        Toast($"已换成 Steam 官方的{AgKindTitle(kind)}");
        AgPaint();
    }

    /// <summary>图标从本地选：系统的选文件窗口（触屏、鼠标都能用），选好就用上。</summary>
    private async Task AgPickLocalIcon(AgDraft d)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail };
            foreach (var ext in new[] { ".png", ".ico", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var file = await picker.PickSingleFileAsync();
            if (file == null || _agDraft != d) return;
            var list = d.Cands["icon"];
            string url = "file:" + file.Path;
            int at = list.FindIndex(c => c.Url == url);
            if (at < 0) { list.Add((url, file.Path)); at = list.Count - 1; }
            d.Pick["icon"] = at;
            int i = AgStackKinds().IndexOf("icon");
            if (i >= 0) AgFlash(AgStackRects()[i].Image);
            Toast("已换图标");
            AgPaint();
        }
        catch (Exception ex) { Log.Warn($"pick icon: {ex.Message}"); Toast("没能打开选文件窗口"); }
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
        if (_agPopOpen) { AgClosePop(); return; }
        if (_agEditing != null) { AgEndEdit(false); return; }
        if (_agPage == AgPage.Category) { AgGo(AgPage.Art); return; }
        AgClose();
    }

    /// <summary>LB / RB：右栏换页签。</summary>
    private void AgSwitchTab(int delta)
    {
        if (_agDraft == null || _agPopOpen || _agPage != AgPage.Art) return;
        if (_agEditing != null) AgEndEdit(true);
        _agTab = (_agTab + delta + AgTabNames.Length) % AgTabNames.Length;
        _agScroll = 0;
        if (_agZone == 1)
        {
            int n = AgRightCount();
            if (n == 0) { _agZone = 0; _agIndex = AgFirstRow(AgArtRows(_agDraft)); }
            else _agIndex = Math.Min(AgRightDefault(_agDraft), n - 1);
        }
        NavHaptic("tab");
        AgPaint();
    }

    /// <summary>Y：打开匹配弹窗（弹窗里 = 回到输入框）。</summary>
    private void AgY()
    {
        if (_agPopOpen) { _agPopIndex = -1; AgPopFocusBox(); AgPaintPop(); }
        else if (_agPage == AgPage.Art) AgOpenPop();
    }

    /// <summary>X：分类。</summary>
    private void AgX()
    {
        if (!_agPopOpen && _agPage == AgPage.Art && _agDraft is { Steam: true }) AgGo(AgPage.Category);
    }

    private void AgClick(double sx, double sy)
    {
        float x = (float)sx / _ccK, y = (float)sy / _ccK;
        var p = new Point(x, y);
        if (_agPopOpen) { AgPopClick(p); return; }
        foreach (var (r, i) in _agTabHits)
            if (r.Contains(p)) { AgSwitchTab(i - _agTab); return; }
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
        const bool right = true;
        switch (what)
        {
            case "name":
            {
                var rows = AgArtRows(d);
                int i = rows.FindIndex(x => x.Title == "名称");
                _agZone = 0; _agIndex = i;
                r = new Rect(40 + 110, AgRowsY + AgRowTop(rows, i), AgLeftW - 110, PerfRowH);
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
            void Page(CanvasDrawingSession s)
            {
                switch (_agPage)
                {
                    case AgPage.Art: AgPaintArt(s, d); break;
                    default: AgPaintCategory(s, d); break;
                }
            }
            if (!_agPopOpen) { Page(ds); return; }
            // 弹窗开着：页面糊一层（一次性画成模糊图，不是实时毛玻璃），点击只给弹窗
            using var cl = new CanvasCommandList(ds);
            using (var cds = cl.CreateDrawingSession()) Page(cds);
            using var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
            { Source = cl, BlurAmount = 10, BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Soft };
            ds.DrawImage(blur);
            _agHits.Clear();
            _agTabHits.Clear();
        }));
        _agSprite.Size = new Vector2(1280 * k, 800 * k);
        _agSprite.Offset = Vector3.Zero;
        if (_agPopOpen) AgPaintPop();
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

    // 两栏（和性能、设置页同一套位置）：左栏 40–388 放信息，右栏 428–1240 放封面；右栏内容到 708，下面是「完成」
    /// <summary>左栏第一行的顶：和设置页右栏第一行同一高度（标题下面空一个节头）。</summary>
    private const float AgRowsY = PerfTop + PerfSecH;
    private const float AgLeftW = PerfLeftW, AgRightX = PerfRightX, AgRightW = PerfRightW, AgContentTop = PerfTop + PerfSecH, AgContentBottom = 708;

    private void AgPaintArt(CanvasDrawingSession ds, AgDraft d)
    {
        float tx = AgTitle(ds, d.Id == null ? "添加游戏" : "编辑游戏");
        var counts = AgTabNames.Select((t, i) => (t, i < AgTabOfficial
            ? d.Pending.Contains(AgTabKinds[i]) ? (string?)"…" : d.Cands[AgTabKinds[i]].Count(c => c.Url != null && !c.Url.StartsWith("file:")).ToString()
            : null)).ToList();
        AgTabs(ds, counts, _agTab, tx);

        // 左栏：信息行 + 程序路径
        var rows = AgArtRows(d);
        var old = ds.Transform;
        // 行名左边和标题、程序路径对齐（行名在行里缩进 36，焦点三角落在页边距里，和设置页左栏一样）
        ds.Transform = Matrix3x2.CreateTranslation(40 - 36, AgRowsY) * old;
        PaintRowList(ds, rows, _agZone == 0 ? _agIndex : -1, null, false, AgLeftW + 36);
        ds.Transform = old;
        float y = AgRowsY;
        for (int i = 0; i < rows.Count; i++)
        {
            float h = rows[i].Section ? PerfSecH : PerfRowH;
            if (!rows[i].Section && !rows[i].Disabled) { var row = rows[i]; _agHits.Add((new Rect(40, y, AgLeftW, h), 0, i, () => row.Press?.Invoke())); }
            y += h;
        }
        PText(ds, "程序", TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.42), 40, y + 30, 200);
        using (var pl = TextKit.Layout(ds, d.Exe, TextKit.Ui(400), 14, AgLeftW, CanvasHorizontalAlignment.Left, maxLines: 3))
            ds.DrawTextLayout(pl, 40, y + 46, AgMuted);

        // 右栏
        using (ds.CreateLayer(1, new Rect(AgRightX - 8, PerfTop - 8, AgRightW + 16, AgContentBottom - PerfTop + 16)))
        {
            if (_agTab < AgTabOfficial) AgPaintGrid(ds, d, AgTabKinds[_agTab]);
            else AgPaintStack(ds, d);
        }
        AgDone(ds, _agZone == 2, () => AgFinish(d));
    }

    private static (int Cols, float W, float H, bool Contain) AgGridGeo(string kind) => kind switch
    {
        "p" => (5, 148, 222, false),
        "hero" => (2, 397, 128, false),
        "logo" => (3, 258, 120, true),
        _ => (3, 258, 121, false),
    };

    private Rect AgGridTile(string kind, int i)
    {
        var (cols, w, h, _) = AgGridGeo(kind);
        float gx = (AgRightW - cols * w) / (cols - 1);
        return new Rect(AgRightX + (i % cols) * (w + gx), AgContentTop + (i / cols) * (h + 20) - _agScroll, w, h);
    }

    /// <summary>一种图的候选网格：A 就换成这张；正在用的右上角白底勾；徽标多一格「不用」。</summary>
    private void AgPaintGrid(CanvasDrawingSession ds, AgDraft d, string kind)
    {
        var (cols, w, h, contain) = AgGridGeo(kind);
        var list = d.Cands[kind];
        int n = AgRightCount(), pick = d.Pick[kind];
        float rowH = h + 20, view = AgContentBottom - AgContentTop;
        if (_agZone == 1)
        {
            int frow = _agIndex / cols;
            if (frow * rowH < _agScroll) _agScroll = frow * rowH;
            if (frow * rowH + h > _agScroll + view) _agScroll = frow * rowH + h - view;
        }
        if (n == 0)
        {
            AgImage(ds, new Rect(AgRightX, AgContentTop, w, h), null, contain, false, d.Pending.Contains(kind));
            return;
        }
        for (int i = 0; i < n; i++)
        {
            var r = AgGridTile(kind, i);
            if (r.Y + r.Height < AgContentTop - 8 || r.Y > AgContentBottom + 8) continue;
            bool focused = _agZone == 1 && i == _agIndex;
            if (i < list.Count) AgImage(ds, r, list[i].Thumb, contain, focused);
            else
            {
                ds.FillRectangle(r, Gfx.WithAlpha(CcText, 0.04f));
                ds.DrawRectangle(r, Gfx.WithAlpha(CcText, 0.18f), 1);
                PText(ds, "不用", TextKit.Ui(400), 15, AgMuted, (float)r.X, (float)(r.Y + r.Height / 2), (float)r.Width, CanvasHorizontalAlignment.Center);
                if (focused) AgRing(ds, r);
            }
            if (i < list.Count ? i == pick : pick < 0) AgInUse(ds, r);
            int idx = i;
            double top = Math.Max(r.Y, AgContentTop), bottom = Math.Min(r.Y + r.Height, AgContentBottom);
            if (bottom > top) _agHits.Add((new Rect(r.X, top, r.Width, bottom - top), 1, idx, () => AgReplace(d, kind, idx)));
        }
    }

    /// <summary>官方封面 / 封面概览：从上到下 封面图片、背景、徽标、宽幅封面图片（概览再加图标），每种上面一行小字。</summary>
    private List<(string Kind, Rect Label, Rect Image)> AgStackRects()
    {
        var list = new List<(string, Rect, Rect)>();
        float y = AgContentTop - _agScroll;
        foreach (var kind in AgStackKinds())
        {
            var (w, h) = kind switch
            {
                "p" => (200f, 300f),
                "hero" => (AgRightW, AgRightW / 3.1f),
                "logo" => (420f, 160f),
                "header" => (420f, 196f),
                _ => (128f, 128f),
            };
            // 小字在图上方，第一行小字和设置页的节头同一高度，图和左栏第一行齐
            var label = new Rect(AgRightX, y - 24, AgRightW, 20);
            var image = new Rect(AgRightX, y, w, h);
            list.Add((kind, label, image));
            y += h + 60;
        }
        return list;
    }

    private void AgPaintStack(CanvasDrawingSession ds, AgDraft d)
    {
        bool official = _agTab == AgTabOfficial;
        var rects = AgStackRects();
        // 焦点那一块露出来
        if (_agZone == 1 && _agIndex < rects.Count)
        {
            var f = rects[_agIndex];
            double top = f.Label.Y, bottom = f.Image.Y + f.Image.Height;
            if (top < PerfTop + 8) _agScroll -= (float)(PerfTop + 8 - top);
            else if (bottom > AgContentBottom) _agScroll += (float)(bottom - AgContentBottom);
            rects = AgStackRects();
        }
        for (int i = 0; i < rects.Count; i++)
        {
            var (kind, label, image) = rects[i];
            if (image.Y > AgContentBottom + 8 || image.Y + image.Height < PerfTop - 30) continue;
            bool focused = _agZone == 1 && i == _agIndex;
            string? thumb;
            bool inUse = false;
            if (official)
            {
                thumb = d.Official.TryGetValue(kind, out var o) ? o.Thumb : null;
                inUse = thumb != null && d.Pick[kind] >= 0 && d.Pick[kind] < d.Cands[kind].Count && d.Cands[kind][d.Pick[kind]].Url == d.Official[kind].Url;
            }
            else thumb = d.Pick[kind] >= 0 && d.Pick[kind] < d.Cands[kind].Count ? d.Cands[kind][d.Pick[kind]].Thumb : null;
            PText(ds, AgKindTitle(kind), TextKit.Ui(500), 13, focused ? CcText : Gfx.WithAlpha(CcText, 0.42), (float)label.X, (float)label.Y + 10, (float)label.Width);
            AgImage(ds, image, thumb, kind is "logo" or "icon", focused, official ? d.SteamAppId != null && d.Official.Count == 0 : d.Pending.Contains(kind));
            if (inUse) AgInUse(ds, image);
            if (kind == "icon" && !official)
            {
                // 图标：本地上传（概览里唯一的按钮）
                var f = TextKit.Ui(500);
                float tw = PWidth(ds, "从本地选择", f, 15), bw = tw + 36, bh = 40;
                var b = new Rect(image.X + image.Width + 24, image.Y + (image.Height - bh) / 2, bw, bh);
                ds.FillRoundedRectangle(b, 12, 12, focused ? Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96) : Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.14));
                PText(ds, "从本地选择", f, 15, focused ? Gfx.Rgba(9, 9, 11) : CcText, (float)b.X, (float)(b.Y + bh / 2), (float)bw, CanvasHorizontalAlignment.Center);
                _agHits.Add((b, 1, i, () => _ = AgPickLocalIcon(d)));
            }
            int idx = i;
            _agHits.Add((image, 1, idx, () => AgRightPress(d, idx)));
        }
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
            d.Query = n[7..].Replace('_', ' ');
            if (_agPopBox != null) _agPopBox.Text = d.Query;
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
