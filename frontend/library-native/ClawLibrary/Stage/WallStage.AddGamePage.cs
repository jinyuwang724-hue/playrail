// 设置 › 游戏库 › 添加游戏 / 编辑游戏。和性能、设置页同一套：左上 24 px 粗标题；左栏 40–388 是信息（共用的设置行：
// 名称 / 匹配 / 加入 Steam / 分类，下面程序路径），右栏 428–1240 是封面，右下「完成」。
// 右栏顶上一行页签（截图页同款）：封面图片 / 背景 / 徽标 / 宽幅封面图片（各是一种图的候选网格，A 就换成这张，闪一下 + 提示）、
// 封面概览（五种图一屏放下，每种都能「从本地选择」）。四种图的页右头有「Steam 官方封面」按钮（商店里有这款才有），
// 按下弹出这种图的官方版本，A 用它。
// 选完 exe 先进「匹配游戏」子页面（WallStage.AddGameMatch.cs），选定一款再到这页；之后「匹配」那一行或 Y 再进去。
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
    private enum AgPage { Art, Category, Match }

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
        /// <summary>Steam 游戏的封面（游戏设置 › 封面）：只换图，完成时通过 Steam 设成它的自定义封面，不进本地游戏。</summary>
        public bool SteamArt;
        /// <summary>每种图的候选（Url 为 null = 现在用着的那张），Pick 是用着的下标（-1 = 不用）。</summary>
        public readonly Dictionary<string, List<(string? Url, string Thumb)>> Cands = new();
        public readonly Dictionary<string, int> Pick = new();
        /// <summary>Steam 商店里的官方图（每种一张）。</summary>
        public readonly Dictionary<string, (string Url, string Thumb)> Official = new();
        public readonly HashSet<string> Pending = new();
        /// <summary>候选图的原图尺寸（「600 × 900」），按 Url 记。</summary>
        public readonly Dictionary<string, string> Sizes = new();
        public int Version;
    }

    private static readonly (string Kind, string Title)[] AgKinds = { ("p", "封面图片"), ("hero", "背景"), ("logo", "徽标"), ("header", "宽幅封面图片"), ("icon", "图标") };

    private const float AgRowsW = 1200, AgCatTop = 112;

    private ContainerVisual? _ag;
    private SpriteVisual _agSprite = null!, _agFlash = null!;
    private bool _agOpen;
    /// <summary>封面页的右栏嵌在游戏设置 › 封面里（WallStage.GameSettingsEmbed.cs）：同一份草稿和网格，换一张就马上存。</summary>
    private bool _agEmbed;
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
        AgShow(d, AgPage.Match);
    }

    private void AgOpenEdit(JsonObject g)
    {
        var d = AgDraftOf(g);
        AgShow(d, AgPage.Art);
        if (d.Sgdb != null) _ = AgLoadCandidates(d, new JsonObject { ["source"] = "sgdb", ["id"] = d.Sgdb });
        if (d.SteamAppId != null) _ = AgLoadOfficial(d);
    }

    /// <summary>本地游戏（或 Steam 里的非 Steam 游戏）的草稿：现有的图是每种的第一张。</summary>
    private static AgDraft AgDraftOf(JsonObject g)
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
        return d;
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
        AgMatchSearching();
        _agDraft = null;
        _ag.IsVisible = false;
        _ = LgRefresh();
        Log.Info("add game page close");
        if (_agFromLibrary)
        {
            // 从游戏库的 ＋ 进来的：直接回游戏库（新游戏在库里重新载入后出现）
            _agFromLibrary = false;
            _stNeedsReload = true;
            if (_stFromLibrary) { CloseSettingsToLibrary(); return; }
        }
        if (_st != null && _stOpen) { _st.IsVisible = true; Repaint(); }
    }

    private void AgGo(AgPage page, int tab = 0)
    {
        AgEndEdit(false);
        _agPage = page;
        _agScroll = 0;
        _agManualScroll = false;
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
            case AgPage.Match:
                // 有结果就落在用着的那款上（A 就用它），没有就落在输入框；还没搜过就先用现在的搜索词搜一次
                if (_agDraft is { } m && m.Matches.Count > 0) { _agZone = 1; _agIndex = Math.Clamp(m.Match, 0, m.Matches.Count - 1); }
                else { _agZone = 0; _agIndex = 0; }
                if (_agDraft is { Matches.Count: 0, Searching: false } s && s.Query.Trim().Length > 0) _ = AgSearch(s);
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
        // 匹配页：有结果焦点就落到第一款（直接按 A 就用它），没有就回输入框
        if (_agDraft == d && _agPage == AgPage.Match && _agEditing == null) { _agZone = d.Matches.Count > 0 ? 1 : 0; _agIndex = 0; }
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
            var items = (r?[kind] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
            foreach (var c in items)
                if (Str(c["url"]) is { } u && Int(c["w"]) is > 0 and var w && Int(c["h"]) is > 0 and var h) d.Sizes[u] = $"{w} × {h}";
            var found = items
                .Select(c => (Url: Str(c["url"]), Thumb: Str(c["thumb"]) ?? "")).Where(c => c.Url != null && c.Thumb.Length > 0)
                .Select(c => ((string?)c.Url, c.Thumb)).ToList();
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
        if (d.SteamArt)
        {
            var req = new JsonObject { ["id"] = d.Id, ["platform"] = "steam", ["title"] = d.Name };
            string title = d.Name;
            AgClose();
            _ = Task.Run(async () =>
            {
                try
                {
                    _agent ??= new AgentClient();
                    await _agent.CallLong("game_art_save", 180, req, art);
                    _dq.TryEnqueue(() => Toast($"已换「{title}」的封面"));
                }
                catch (Exception ex) { _dq.TryEnqueue(() => Toast($"「{title}」的封面没有换上：{ex.Message}")); }
            });
            return;
        }
        var game = new JsonObject
        {
            ["id"] = d.Id, ["name"] = d.Name.Trim(), ["exe"] = d.Exe, ["steam"] = d.Steam, ["sgdb"] = d.Sgdb, ["steamAppId"] = d.SteamAppId, ["art"] = art,
            ["collections"] = new JsonArray(d.Collections.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["newCollections"] = new JsonArray(d.NewCollections.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        };
        string name = d.Name.Trim();
        bool isNew = d.Id == null;
        bool fromLibrary = _agFromLibrary;
        AgClose();
        _ = Task.Run(async () =>
        {
            try
            {
                _agent ??= new AgentClient();
                await _agent.CallLong("local_games_save", 180, game);
                _dq.TryEnqueue(() =>
                {
                    _stNeedsReload = true;
                    Toast(isNew ? $"已添加：{name}" : $"已保存：{name}");
                    _ = LgRefresh();
                    // 从游戏库的 ＋ 加的、人已经回到墙上：重新载入一次，新游戏马上上墙
                    if (fromLibrary && isNew && !_ccOpen) After(1200, () => { if (!_ccOpen) RestartLibrary(); });
                });
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
        if ((_agOpen || _agEmbed) && _agDraft == d) _dq.TryEnqueue(AgPaint);
    }

    private List<(string Id, string Name)> AgCollections() =>
        (_lgState?["collections"] as JsonArray)?.OfType<JsonObject>().Select(c => (Str(c["id"]) ?? "", Str(c["name"]) ?? "")).Where(c => c.Item1.Length > 0).ToList() ?? new();

    // ---------------- 行（和设置页同一套） ----------------

    /// <summary>
    /// 游戏设置 › 封面的草稿（嵌在设置页里，不开封面页）。本地游戏和 Steam 里的非 Steam 游戏用本地游戏的那份；
    /// Steam 游戏只换图：现有的图从 Steam 的缓存 / 自定义图读，按游戏名到 SteamGridDB 找候选，换一张就设成 Steam 的自定义封面。
    /// </summary>
    private async void GsArtLoad(LibraryItem item)
    {
        _agEmbed = true;
        _agDraft = null;
        await LgRefresh();
        if (_gsItem != item || !_agEmbed) return;
        var local = LgGames().FirstOrDefault(g => Str(g["id"]) == item.Id);
        if (local != null)
        {
            GsArtShow(AgDraftOf(local));
            return;
        }
        var d = new AgDraft { Id = item.Id, Name = item.Title, Exe = "", Query = item.Title, SteamArt = true, Steam = true };
        foreach (var (kind, _) in AgKinds) { d.Cands[kind] = new(); d.Pick[kind] = -1; }
        try
        {
            _agent ??= new AgentClient();
            if (await _agent.CallLong("game_art_get", 20, new JsonObject { ["id"] = item.Id, ["platform"] = item.Platform, ["title"] = item.Title }) is JsonObject r
                && r["art"] is JsonObject a)
                foreach (var (kind, _) in AgKinds)
                    if (Str(a[kind]) is { Length: > 0 } path) { d.Cands[kind].Add((null, path)); d.Pick[kind] = 0; }
        }
        catch (Exception ex) { Log.Warn($"game art: {ex.Message}"); }
        if (_gsItem != item || !_agEmbed) return;
        GsArtShow(d);
    }

    /// <summary>嵌在游戏设置里换了一张：只存这一种图（Steam 游戏设成 Steam 的自定义图，本地游戏存进本地游戏再同步到 Steam）。</summary>
    private void AgEmbedSave(AgDraft d, string kind)
    {
        var list = d.Cands[kind];
        int pick = d.Pick[kind];
        string? url = pick >= 0 && pick < list.Count ? list[pick].Url : "";
        if (url == null) return;   // 换回了本来那张
        var art = new JsonObject { [kind] = url };
        string title = d.Name;
        if (d.SteamArt)
        {
            var req = new JsonObject { ["id"] = d.Id, ["platform"] = "steam", ["title"] = d.Name };
            _ = Task.Run(async () =>
            {
                try { _agent ??= new AgentClient(); await _agent.CallLong("game_art_save", 180, req, art); }
                catch (Exception ex) { _dq.TryEnqueue(() => Toast($"「{title}」的{AgKindTitle(kind)}没有换上：{ex.Message}")); }
            });
            return;
        }
        var game = new JsonObject
        {
            ["id"] = d.Id, ["name"] = d.Name.Trim(), ["exe"] = d.Exe, ["steam"] = d.Steam, ["sgdb"] = d.Sgdb, ["steamAppId"] = d.SteamAppId, ["art"] = art,
            ["collections"] = new JsonArray(d.Collections.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
            ["newCollections"] = new JsonArray(d.NewCollections.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        };
        _ = Task.Run(async () =>
        {
            try
            {
                _agent ??= new AgentClient();
                await _agent.CallLong("local_games_save", 180, game);
                _dq.TryEnqueue(() => { _stNeedsReload = true; _ = LgRefresh(); });
            }
            catch (Exception ex) { _dq.TryEnqueue(() => Toast($"「{title}」的{AgKindTitle(kind)}没有存上：{ex.Message}")); }
        });
    }

    private List<PerfRow> AgArtRows(AgDraft d)
    {
        if (d.SteamArt)
            return new List<PerfRow>
            {
                new() { Title = "名称", Value = d.Name, Disabled = true },
                new() { Title = "匹配", Value = d.Match >= 0 && d.Match < d.Matches.Count ? Str(d.Matches[d.Match]["name"]) ?? "" : d.Searching ? "正在找…" : "未匹配", Press = () => AgGo(AgPage.Match) },
            };
        var rows = new List<PerfRow>
        {
            new() { Title = "名称", Value = _agEditing == "name" ? "" : d.Name, Press = () => AgBeginEdit("name") },
            new() { Title = "匹配", Value = d.Match >= 0 && d.Match < d.Matches.Count ? Str(d.Matches[d.Match]["name"]) ?? "" : d.Sgdb != null ? "SteamGridDB" : "未匹配", Press = () => AgGo(AgPage.Match) },
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

    /// <summary>右栏顶上的页签：四种图 + 封面概览。</summary>
    private static readonly string[] AgTabNames = { "封面图片", "背景", "徽标", "宽幅封面图片", "封面概览" };
    private const int AgTabOverview = 4;
    /// <summary>右栏焦点在「Steam 官方封面」按钮上（四种图的页里，商店有这款才有）。</summary>
    private const int AgOffBtn = -1;

    /// <summary>四种图页签对应的图种。</summary>
    private static readonly string[] AgTabKinds = { "p", "hero", "logo", "header" };

    private static string AgKindTitle(string kind) => AgKinds.First(k => k.Kind == kind).Title;

    /// <summary>商店里有这款（拿到了官方图）才显示「Steam 官方封面」按钮。</summary>
    private bool AgHasOfficial(AgDraft d) => d.SteamAppId != null && d.Official.Count > 0;

    /// <summary>右栏当前页签能聚焦的格数（不算官方按钮）。</summary>
    private int AgRightCount()
    {
        var d = _agDraft;
        if (d == null) return 0;
        if (_agTab == AgTabOverview) return AgOverviewTargets(d).Count;
        string kind = AgTabKinds[_agTab];
        int n = d.Cands[kind].Count;
        return n + (kind == "logo" && n > 0 ? 1 : 0);
    }

    private void AgMove(Direction dir)
    {
        var d = _agDraft;
        if (d == null) return;
        if (_agOffKind != null) return;
        if (_agEditing != null) AgEndEdit(true);
        _agManualScroll = false;
        int dx = dir == Direction.Left ? -1 : dir == Direction.Right ? 1 : 0, dy = dir == Direction.Up ? -1 : dir == Direction.Down ? 1 : 0;
        switch (_agPage)
        {
            case AgPage.Art:
                AgMoveArt(d, dx, dy);
                break;
            case AgPage.Match:
                AgMoveMatch(d, dx, dy);
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
        int LastRow() => rows.FindLastIndex(r => !r.Section);
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
                    else if (_agTab != AgTabOverview && AgHasOfficial(d)) { _agZone = 1; _agIndex = AgOffBtn; }
                    else { _agZone = 2; _agIndex = 0; }
                }
                else if (dx < 0 && _agIndex < rows.Count && rows[_agIndex].Choices != null && rows[_agIndex].Step != null && !rows[_agIndex].Disabled) rows[_agIndex].Step!(-1);
                break;
            case 1:   // 右栏
                if (_agTab == AgTabOverview)
                {
                    var targets = AgOverviewTargets(d);
                    int next = AgSpatial(targets.Select(t => t.R).ToList(), _agIndex, dx, dy);
                    if (next >= 0) _agIndex = next;
                    else if (dx < 0) { _agZone = 0; _agIndex = AgFirstRow(rows); }
                    else if (dy > 0) { _agZone = 2; _agIndex = 0; }
                    break;
                }
                if (_agIndex == AgOffBtn)
                {
                    if (dy > 0) { if (n > 0) _agIndex = 0; else { _agZone = 2; _agIndex = 0; } }
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
                else if (dy < 0) { if (_agIndex - cols >= 0) _agIndex -= cols; else if (AgHasOfficial(d)) _agIndex = AgOffBtn; }
                break;
            default:  // 完成
                if (dy < 0) { if (n > 0) { _agZone = 1; _agIndex = n - 1; } else { _agZone = 0; _agIndex = LastRow(); } }
                else if (dx < 0) { _agZone = 0; _agIndex = LastRow(); }
                break;
        }
    }

    /// <summary>按位置走：这个方向上中心点最近的一个（-1 = 这边没有了）。</summary>
    private static int AgSpatial(List<Rect> rects, int from, int dx, int dy)
    {
        if (from < 0 || from >= rects.Count) return rects.Count > 0 ? 0 : -1;
        var c = rects[from];
        double cx = c.X + c.Width / 2, cy = c.Y + c.Height / 2;
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < rects.Count; i++)
        {
            if (i == from) continue;
            var r = rects[i];
            double x = r.X + r.Width / 2, y = r.Y + r.Height / 2, ddx = x - cx, ddy = y - cy;
            bool ok = dx > 0 ? ddx > 4 : dx < 0 ? ddx < -4 : dy > 0 ? ddy > 4 : ddy < -4;
            if (!ok) continue;
            // 主方向的距离 + 2 倍的偏离
            double dist = dx != 0 ? Math.Abs(ddx) + 2 * Math.Abs(ddy) : Math.Abs(ddy) + 2 * Math.Abs(ddx);
            if (dist < bestD) { bestD = dist; best = i; }
        }
        return best;
    }

    /// <summary>进右栏时落在哪：图种页签落在正在用的那张。</summary>
    private int AgRightDefault(AgDraft d) => _agTab < AgTabOverview ? Math.Max(0, d.Pick[AgTabKinds[_agTab]]) : 0;

    private void AgActivate()
    {
        var d = _agDraft;
        if (d == null) return;
        if (_agOffKind != null) { AgUseOfficial(d, _agOffKind); return; }
        if (_agEditing != null) { AgEndEdit(true); return; }
        NavHaptic("select");
        switch (_agPage)
        {
            case AgPage.Art:
                if (_agZone == 0) { var rows = AgArtRows(d); if (_agIndex < rows.Count && !rows[_agIndex].Disabled) rows[_agIndex].Press?.Invoke(); return; }
                if (_agZone == 1) { AgRightPress(d, _agIndex); return; }
                AgFinish(d);
                return;
            case AgPage.Match:
                AgActivateMatch(d);
                return;
            case AgPage.Category:
                if (_agZone == 0) { var rows = AgCategoryRows(); if (_agIndex < rows.Count) rows[_agIndex].Press?.Invoke(); return; }
                AgGo(AgPage.Art);
                return;
        }
    }

    /// <summary>右栏按 A：图种页 = 换成这张（官方按钮 = 弹出官方图）；概览 = 图去那种图的页签，「从本地选择」= 选文件。</summary>
    private void AgRightPress(AgDraft d, int index)
    {
        if (_agTab < AgTabOverview)
        {
            if (index == AgOffBtn) AgOpenOfficial(AgTabKinds[_agTab]);
            else AgReplace(d, AgTabKinds[_agTab], index);
            return;
        }
        var targets = AgOverviewTargets(d);
        if (index < 0 || index >= targets.Count) return;
        var t = targets[index];
        if (t.Local || _agEmbed) { _ = AgPickLocal(d, t.Kind); return; }   // 游戏设置里没有候选页签：图本身也是从本地换
        int tab = Array.IndexOf(AgTabKinds, t.Kind);
        if (tab < 0) { _ = AgPickLocal(d, t.Kind); return; }   // 图标只有本地这一种来源
        _agTab = tab;
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
        Toast(to < 0 ? $"不用{AgKindTitle(kind)}了" : $"已换{AgKindTitle(kind)}");
        AgPaint();
        AgBounce(AgGridTile(kind, index));
        if (_agEmbed) AgEmbedSave(d, kind);
    }

    /// <summary>「Steam 官方封面」弹窗：这一种图在 Steam 商店里的官方版本，A 用它，B 关。</summary>
    private void AgOpenOfficial(string kind)
    {
        _agOffKind = kind;
        NavHaptic("select");
        AgPaint();
    }

    private void AgCloseOfficial()
    {
        _agOffKind = null;
        AgPaint();
    }

    private void AgUseOfficial(AgDraft d, string kind)
    {
        _agOffKind = null;
        if (!d.Official.TryGetValue(kind, out var o)) { AgPaint(); return; }
        var list = d.Cands[kind];
        int at = list.FindIndex(c => c.Url == o.Url);
        if (at < 0) { list.Add((o.Url, o.Thumb)); at = list.Count - 1; }
        d.Pick[kind] = at;
        _agZone = 1;
        _agIndex = at;
        AgPaint();
        AgBounce(AgGridTile(kind, at));
        Toast($"已换成 Steam 官方的{AgKindTitle(kind)}");
        if (_agEmbed) AgEmbedSave(d, kind);
    }

    /// <summary>从本地选一张（系统的选文件窗口，触屏、鼠标都能用），选好就用上。</summary>
    private async Task AgPickLocal(AgDraft d, string kind)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail };
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".ico" }) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
            var file = await picker.PickSingleFileAsync();
            if (file == null || _agDraft != d) return;
            var list = d.Cands[kind];
            string url = "file:" + file.Path;
            int at = list.FindIndex(c => c.Url == url);
            if (at < 0) { list.Add((url, file.Path)); at = list.Count - 1; }
            d.Pick[kind] = at;
            Toast($"已换{AgKindTitle(kind)}");
            AgPaint();
            if (_agEmbed) AgEmbedSave(d, kind);
            if (_agTab == AgTabOverview && AgOverviewSlots().TryGetValue(kind, out var slot)) AgBounce(slot);
            else if (_agTab < AgTabOverview && AgTabKinds[_agTab] == kind) AgBounce(AgGridTile(kind, at));
        }
        catch (Exception ex) { Log.Warn($"pick local {kind}: {ex.Message}"); Toast("没能打开选文件窗口"); }
    }

    private SpriteVisual? _agBounce;
    private ContainerVisual? _agBounceHost;

    /// <summary>
    /// 换图时那张图 Q 弹一下：把页面上这一块（图、勾、焦点框）单独画到一层上，从中心放大到 1.1 倍再弹回、
    /// 再小弹一次落定。只往大里弹，下面那张原图始终被盖住，看不出两层。
    /// </summary>
    private void AgBounce(Rect r)
    {
        var d = _agDraft;
        ContainerVisual? host = _agEmbed && !_agOpen ? _st : _ag;
        if (host == null || d == null || Reduced || r.Width <= 0) return;
        if (_agBounce == null || _agBounceHost != host)
        {
            if (_agBounce != null) { _agBounceHost?.Children.Remove(_agBounce); _agBounce.Dispose(); }
            _agBounce = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            _agBounceHost = host;
            host.Children.InsertAtTop(_agBounce);
        }
        float k = _ccK, raster = k * Chrome.Dpi, pad = 8;
        var hits = _agHits.ToList();
        var tabHits = _agTabHits.ToList();
        bool embed = _agEmbed && !_agOpen;
        // 嵌在游戏设置里：封面概览按 GsArtMatrix 挪过去、放大了，弹的那块也照它算
        var m = embed ? GsArtMatrix : Matrix3x2.Identity;
        var tl = Vector2.Transform(new Vector2((float)r.X, (float)r.Y), m);
        var br = Vector2.Transform(new Vector2((float)(r.X + r.Width), (float)(r.Y + r.Height)), m);
        var area = new Rect(tl.X - pad, tl.Y - pad, br.X - tl.X + pad * 2, br.Y - tl.Y + pad * 2);
        CcSet(_agBounce, CcDraw((float)area.Width, (float)area.Height, raster, ds =>
        {
            ds.Transform = m * Matrix3x2.CreateTranslation(-(float)area.X, -(float)area.Y) * ds.Transform;
            if (embed) GsPaintArt(ds, d);
            else using (ds.CreateLayer(1, new Rect(r.X - pad, r.Y - pad, r.Width + pad * 2, r.Height + pad * 2))) AgPaintArt(ds, d);
        }));
        _agHits.Clear(); _agHits.AddRange(hits);
        _agTabHits.Clear(); _agTabHits.AddRange(tabHits);
        _agBounce.Size = new Vector2((float)area.Width * k, (float)area.Height * k);
        _agBounce.Offset = new Vector3((float)area.X * k, (float)area.Y * k, 0);
        _agBounce.CenterPoint = new Vector3((float)area.Width * k / 2, (float)area.Height * k / 2, 0);
        _agBounce.IsVisible = true;
        var a = G.C.CreateVector3KeyFrameAnimation();
        var outE = G.C.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1));
        var inOut = G.C.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1));
        a.InsertKeyFrame(0, Vector3.One);
        a.InsertKeyFrame(0.28f, new Vector3(1.1f, 1.1f, 1), outE);
        a.InsertKeyFrame(0.55f, Vector3.One, inOut);
        a.InsertKeyFrame(0.76f, new Vector3(1.035f, 1.035f, 1), inOut);
        a.InsertKeyFrame(1, Vector3.One, inOut);
        a.Duration = TimeSpan.FromMilliseconds(560);
        _agBounce.StartAnimation("Scale", a);
        var b = _agBounce;
        After(600, () => { if (_agBounce == b) b.IsVisible = false; });
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
        if (_agOffKind != null) { AgCloseOfficial(); return; }
        if (_agEditing != null) { AgEndEdit(false); return; }
        if (_agPage is AgPage.Category or AgPage.Match) { AgGo(AgPage.Art, _agTab); return; }
        AgClose();
    }

    /// <summary>LB / RB：右栏换页签。</summary>
    private void AgSwitchTab(int delta)
    {
        if (_agDraft == null || _agOffKind != null || _agPage != AgPage.Art) return;
        if (_agEditing != null) AgEndEdit(true);
        _agTab = (_agTab + delta + AgTabNames.Length) % AgTabNames.Length;
        _agScroll = 0;
        _agManualScroll = false;
        if (_agZone == 1)
        {
            int n = AgRightCount();
            if (n == 0) { if (_agTab != AgTabOverview && AgHasOfficial(_agDraft)) _agIndex = AgOffBtn; else { _agZone = 0; _agIndex = AgFirstRow(AgArtRows(_agDraft)); } }
            else _agIndex = Math.Min(AgRightDefault(_agDraft), n - 1);
        }
        NavHaptic("tab");
        AgPaint();
    }

    /// <summary>Y：进匹配页（匹配页里 = 改搜索词）。</summary>
    private void AgY()
    {
        if (_agPage == AgPage.Match) { _agZone = 0; _agIndex = 0; AgBeginEdit("query"); }
        else if (_agPage == AgPage.Art && _agOffKind == null) AgGo(AgPage.Match);
    }

    /// <summary>X：分类。</summary>
    private void AgX()
    {
        if (_agOffKind == null && _agPage == AgPage.Art && _agDraft is { Steam: true }) AgGo(AgPage.Category);
    }

    private void AgClick(double sx, double sy)
    {
        float x = (float)sx / _ccK, y = (float)sy / _ccK;
        var p = new Point(x, y);
        if (_agOffKind != null)
        {
            if (_agOffUse.Contains(p) && _agDraft != null) AgUseOfficial(_agDraft, _agOffKind);
            else if (!_agOffPanel.Contains(p)) AgCloseOfficial();
            return;
        }
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
        bool right = true;
        float size = 17;
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
            case "query":
                r = AgMatchMap(AgFieldInput);
                text = d.Query;
                right = false;
                size = 16 * AgMatchS;
                break;
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
                if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _agEditEnter = true; AgEndEdit(true); }
                else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; AgEndEdit(false); }
            };
            _agBox.LostFocus += (_, _) => { if (_agEditing != null) AgEndEdit(true); };
            HookTouchKeyboard(_agBox);
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
        _agBox.FontSize = size * k;
        _agBox.Width = r.Width * k;
        _agBox.Height = r.Height * k;
        // 字的垂直位置和画出来的字一样：17 px 字，行高约 1.33 倍
        _agBox.Padding = new Thickness(0, Math.Max(0, (r.Height - size * 1.33) / 2 * k - 1), 0, 0);
        Canvas.SetLeft(_agBox, r.X * k);
        Canvas.SetTop(_agBox, r.Y * k);
        _agBox.Visibility = Visibility.Visible;
        AgPaint();
        FocusTextBox(_agBox, () => _agEditing == what);
    }

    private void AgEndEdit(bool commit)
    {
        var what = _agEditing;
        bool enter = _agEditEnter;
        _agEditEnter = false;
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
                case "query":
                    // 回车才搜（和 folia 一样打字不搜）；点别处只记下字
                    d.Query = text;
                    if (enter && text.Length > 0) { _agZone = 0; _agIndex = 1; _ = AgSearch(d); }
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
        if (_agEmbed && !_agOpen) { if (_stOpen) PaintSt(); return; }
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
                    case AgPage.Match: AgPaintMatch(s, d); break;
                    default: AgPaintCategory(s, d); break;
                }
            }
            if (_agOffKind == null) { Page(ds); return; }
            // 弹窗开着：页面糊一层（一次性画成模糊图，不是实时毛玻璃），点击只给弹窗
            using var cl = new CanvasCommandList(ds);
            using (var cds = cl.CreateDrawingSession()) Page(cds);
            using var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
            { Source = cl, BlurAmount = 10, BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Soft };
            ds.DrawImage(blur);
            _agHits.Clear();
            _agTabHits.Clear();
            AgPaintOfficial(ds, d);
        }));
        _agSprite.Size = new Vector2(1280 * k, 800 * k);
        _agSprite.Offset = Vector3.Zero;
        AgMatchSearching();
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

    // 两栏（和性能、设置页同一套位置）：左栏 40–388 放信息，右栏 428–1240 放封面；右栏内容到 708，下面是「完成」。
    // 两栏顶上同一行：左边小字「游戏」，右边是封面页签（和截图页的页签同款）＋右头「Steam 官方封面」按钮。
    private const float AgBandY = 112, AgRowsY = 150;
    private const float AgLeftW = PerfLeftW, AgRightX = PerfRightX, AgRightW = PerfRightW, AgContentTop = 150, AgContentBottom = 708;

    /// <summary>「Steam 官方封面」弹窗开着时是哪种图（null = 没开）；弹窗面板和「用这张」按钮的位置（点击用）。</summary>
    private string? _agOffKind;
    private Rect _agOffPanel, _agOffUse;

    private void AgPaintArt(CanvasDrawingSession ds, AgDraft d)
    {
        AgTitle(ds, d.SteamArt ? "更换封面" : d.Id == null ? "添加游戏" : "编辑游戏");

        // 左栏：小字 + 信息行 + 程序路径
        PText(ds, "游戏", TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.42), 40, AgBandY, 200);
        var rows = AgArtRows(d);
        var old = ds.Transform;
        // 行名左边和标题对齐（行名在行里缩进 36，焦点三角落在页边距里，和设置页左栏一样）
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
        if (d.Exe.Length > 0)
        {
            PText(ds, "程序", TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.42), 40, y + 30, 200);
            using var pl = TextKit.Layout(ds, d.Exe, TextKit.Ui(400), 14, AgLeftW, CanvasHorizontalAlignment.Left, maxLines: 2);
            ds.DrawTextLayout(pl, 40, y + 46, AgMuted);
        }
        AgPaintPreview(ds, d, 466);

        // 右栏：页签 + 官方按钮
        var counts = AgTabNames.Select((t, i) => (t, i < AgTabOverview
            ? d.Pending.Contains(AgTabKinds[i]) ? (string?)"…" : d.Cands[AgTabKinds[i]].Count(c => c.Url != null && !c.Url.StartsWith("file:")).ToString()
            : null)).ToList();
        AgTabsAt(ds, counts, _agTab, AgRightX - 12, AgBandY);
        if (_agTab < AgTabOverview && AgHasOfficial(d))
        {
            var f = TextKit.Ui(500);
            float tw = PWidth(ds, "Steam 官方封面", f, 14), bw = tw + 32;
            var b = new Rect(AgRightX + AgRightW - bw, AgBandY - 16, bw, 32);
            bool bf = _agZone == 1 && _agIndex == AgOffBtn;
            ds.FillRoundedRectangle(b, 10, 10, bf ? Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96) : Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.12));
            PText(ds, "Steam 官方封面", f, 14, bf ? Gfx.Rgba(9, 9, 11) : CcText, (float)b.X, AgBandY, (float)bw, CanvasHorizontalAlignment.Center);
            _agHits.Add((b, 1, AgOffBtn, () => AgOpenOfficial(AgTabKinds[_agTab])));
        }

        using (ds.CreateLayer(1, new Rect(AgRightX - 8, AgContentTop - 8, AgRightW + 16, AgContentBottom - AgContentTop + 16)))
        {
            if (_agTab < AgTabOverview) AgPaintGrid(ds, d, AgTabKinds[_agTab]);
            else AgPaintOverview(ds, d);
        }
        AgDone(ds, _agZone == 2, () => AgFinish(d));
    }

    /// <summary>
    /// 左栏下面的预览：在 Steam 库里会是什么样——上面是游戏页的头图（背景铺满，徽标压在左边），
    /// 下面一排是网格里的封面图片和宽幅封面图片。跟着右栏的选择实时变。
    /// </summary>
    private void AgPaintPreview(CanvasDrawingSession ds, AgDraft d, float top)
    {
        string? Thumb(string kind) => d.Pick[kind] >= 0 && d.Pick[kind] < d.Cands[kind].Count ? d.Cands[kind][d.Pick[kind]].Thumb : null;
        PText(ds, "预览", TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.42), 40, top - 14, 200);
        var banner = new Rect(40, top, AgLeftW, 112);
        AgImage(ds, banner, Thumb("hero"), false, false, d.Pending.Contains("hero"));
        if (AgBitmapOf(Thumb("logo")) is { } logo)
            DrawContained(ds, logo, new Rect(banner.X + 16, banner.Y + 14, banner.Width * 0.52, banner.Height - 28));
        // 封面图片和宽幅封面图片一样高，两张正好排满左栏宽
        float h = (AgLeftW - 16) / (2f / 3 + 460f / 215);
        var cover = new Rect(40, top + 112 + 16, h * 2 / 3, h);
        AgImage(ds, cover, Thumb("p"), false, false, d.Pending.Contains("p"));
        var wide = new Rect(cover.X + cover.Width + 16, cover.Y, AgLeftW - cover.Width - 16, h);
        AgImage(ds, wide, Thumb("header"), false, false, d.Pending.Contains("header"));
    }

    /// <summary>页签（截图页同款）画在给定的中线上：名字 17，后面小号数字；当前白字 + 2 px 下划线，其余 45 %。</summary>
    private void AgTabsAt(CanvasDrawingSession ds, IReadOnlyList<(string Name, string? Count)> tabs, int current, float x0, float cy)
    {
        const float Pad = 12, Gap = 6;
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
            _agTabHits.Add((new Rect(x, cy - 22, w, 44), i));
            x += w + 8;
        }
    }

    private static (int Cols, float W, float H, bool Contain) AgGridGeo(string kind) => kind switch
    {
        "p" => (4, 188, 282, false),
        "hero" => (2, 396, 128, false),
        "logo" => (2, 396, 184, true),
        _ => (2, 396, 185, false),
    };

    /// <summary>网格里一行到下一行：图下面一行尺寸（「600 × 900」）再空一点。</summary>
    private const float AgGridGapY = 46;

    private Rect AgGridTile(string kind, int i)
    {
        var (cols, w, h, _) = AgGridGeo(kind);
        float gx = (AgRightW - cols * w) / (cols - 1);
        return new Rect(AgRightX + (i % cols) * (w + gx), AgContentTop + (i / cols) * (h + AgGridGapY) - _agScroll, w, h);
    }

    /// <summary>图的尺寸：网上的按 SteamGridDB / 商店给的，自己的（现在用的、本地选的）按图片本身。</summary>
    private string? AgSizeText(AgDraft d, (string? Url, string Thumb) c)
    {
        if (c.Url != null && d.Sizes.TryGetValue(c.Url, out var s)) return s;
        if (c.Url != null && !c.Url.StartsWith("file:")) return null;
        return AgBitmapOf(c.Thumb) is { } bmp ? $"{bmp.SizeInPixels.Width} × {bmp.SizeInPixels.Height}" : null;
    }

    /// <summary>一种图的候选网格：A 就换成这张；正在用的右上角白底勾；徽标多一格「不用」。</summary>
    private void AgPaintGrid(CanvasDrawingSession ds, AgDraft d, string kind)
    {
        var (cols, w, h, contain) = AgGridGeo(kind);
        var list = d.Cands[kind];
        int n = AgRightCount(), pick = d.Pick[kind];
        float rowH = h + AgGridGapY, view = AgContentBottom - AgContentTop;
        if (_agZone == 1 && _agIndex >= 0 && !_agManualScroll)
        {
            int frow = _agIndex / cols;
            if (frow * rowH < _agScroll) _agScroll = frow * rowH;
            if (frow * rowH + h > _agScroll + view) _agScroll = frow * rowH + h - view;
        }
        // 手指 / 滚轮滚过头就停在两头
        _agScroll = Math.Clamp(_agScroll, 0, Math.Max(0, ((n + cols - 1) / cols) * rowH - AgGridGapY + 22 - view));
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
            if (i < list.Count && AgSizeText(d, list[i]) is { } size)
                PText(ds, size, AgMono, 12, focused ? Gfx.WithAlpha(CcText, 0.8) : Gfx.WithAlpha(CcText, 0.45), (float)r.X, (float)(r.Y + r.Height + 15), (float)r.Width);
            int idx = i;
            double top = Math.Max(r.Y, AgContentTop), bottom = Math.Min(r.Y + r.Height, AgContentBottom);
            if (bottom > top) _agHits.Add((new Rect(r.X, top, r.Width, bottom - top), 1, idx, () => AgReplace(d, kind, idx)));
        }
    }

    /// <summary>
    /// 封面概览：一屏放下五种图，按真实比例排成对齐的一块——左边封面图片（2:3），右边上背景（3.1:1）、下宽幅封面图片和徽标并排；
    /// 封面图片的上下沿正好对齐背景的上沿和下面那排的下沿，右边都齐到 1240；图标在封面图片下面。
    /// 每张图上面一行：左边名字，右边「从本地选择」（图标的按钮在它右边）。
    /// </summary>
    private static Dictionary<string, Rect> AgOverviewSlots() => new()
    {
        ["p"] = new Rect(AgRightX, 182, 230, 345),
        ["hero"] = new Rect(AgRightX + 258, 182, 554, 179),
        ["header"] = new Rect(AgRightX + 258, 403, 265, 124),
        ["logo"] = new Rect(AgRightX + 547, 403, 265, 124),
        ["icon"] = new Rect(AgRightX, 569, 70, 70),
    };

    /// <summary>概览里能聚焦的：每种图本身（A 去它的页签；图标 = 本地选），和它的「从本地选择」。</summary>
    private List<(Rect R, string Kind, bool Local)> AgOverviewTargets(AgDraft d)
    {
        var list = new List<(Rect, string, bool)>();
        foreach (var (kind, r) in AgOverviewSlots())
        {
            list.Add((r, kind, false));
            list.Add((AgLocalButton(kind, r), kind, true));
        }
        return list;
    }

    private static Rect AgLocalButton(string kind, Rect img) => kind == "icon"
        ? new Rect(img.X + img.Width + 16, img.Y + (img.Height - 28) / 2, 100, 28)
        : new Rect(img.X + img.Width - 100, img.Y - 36, 100, 28);

    private void AgPaintOverview(CanvasDrawingSession ds, AgDraft d)
    {
        var targets = AgOverviewTargets(d);
        for (int i = 0; i < targets.Count; i++)
        {
            var (r, kind, local) = targets[i];
            bool focused = _agZone == 1 && i == _agIndex;
            int idx = i;
            if (local)
            {
                var f = TextKit.Ui(500);
                ds.FillRoundedRectangle(r, 10, 10, focused ? Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96) : Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.12));
                PText(ds, "从本地选择", f, 13, focused ? Gfx.Rgba(9, 9, 11) : CcText, (float)r.X, (float)(r.Y + r.Height / 2), (float)r.Width, CanvasHorizontalAlignment.Center);
                _agHits.Add((r, 1, idx, () => _ = AgPickLocal(d, kind)));
                continue;
            }
            string? thumb = d.Pick[kind] >= 0 && d.Pick[kind] < d.Cands[kind].Count ? d.Cands[kind][d.Pick[kind]].Thumb : null;
            bool labelFocus = focused || (i + 1 < targets.Count && _agZone == 1 && _agIndex == i + 1);
            PText(ds, AgKindTitle(kind), TextKit.Ui(500), 13, labelFocus ? CcText : Gfx.WithAlpha(CcText, 0.42), (float)r.X, (float)r.Y - 22, kind == "icon" ? 120 : (float)r.Width - 108);
            AgImage(ds, r, thumb, kind is "logo" or "icon", focused, d.Pending.Contains(kind));
            _agHits.Add((r, 1, idx, () => AgRightPress(d, idx)));
        }
    }

    /// <summary>「Steam 官方封面」弹窗：和匹配弹窗同一种面板（强调色压暗），里面是这一种图的官方版本和「用这张」。</summary>
    private void AgPaintOfficial(CanvasDrawingSession ds, AgDraft d)
    {
        string kind = _agOffKind!;
        ds.FillRectangle(0, 0, 1280, 800, Gfx.Rgba(0, 0, 0, 0.45));
        var (iw, ih) = kind switch { "p" => (240f, 360f), "hero" => (512f, 165f), "logo" => (420f, 160f), _ => (420f, 196f) };
        float pw = 560, ph = 52 + ih + 24 + 44 + 24;
        float px = (1280 - pw) / 2, py = (800 - ph) / 2;
        _agOffPanel = new Rect(px, py, pw, ph);
        ds.FillRoundedRectangle(_agOffPanel, 18, 18, Gfx.WithAlpha(Gfx.Mix(Theme.Bg, 0.78, PerfAccent), 0.96));
        PText(ds, $"Steam 官方 · {AgKindTitle(kind)}", TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.55), px + 24, py + 30, pw - 48);
        var img = new Rect(px + (pw - iw) / 2, py + 52, iw, ih);
        bool has = d.Official.TryGetValue(kind, out var o);
        AgImage(ds, img, has ? o.Thumb : null, kind == "logo", false);
        var f = TextKit.Ui(600);
        float bw = 140, bh = 44;
        _agOffUse = has ? new Rect(px + (pw - bw) / 2, img.Y + ih + 24, bw, bh) : Rect.Empty;
        if (has)
        {
            ds.FillRoundedRectangle(_agOffUse, 12, 12, Gfx.Rgba(0xfa, 0xfa, 0xfa, 0.96));
            PText(ds, "用这张", f, 17, Gfx.Rgba(9, 9, 11), (float)_agOffUse.X, (float)(_agOffUse.Y + bh / 2), bw, CanvasHorizontalAlignment.Center);
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
            _ = AgSearch(d);
        }
    }

    private bool _agPaintQueued;

    /// <summary>缩略图一张张到：攒一下再重画，不要每张都整页重画。</summary>
    private void AgPaintSoon()
    {
        if (_agPaintQueued || !(_agOpen || _agEmbed)) return;
        _agPaintQueued = true;
        After(60, () => { _agPaintQueued = false; AgPaint(); });
    }
}
