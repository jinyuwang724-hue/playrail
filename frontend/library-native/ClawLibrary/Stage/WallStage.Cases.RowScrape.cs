// 「单行」游戏设置 › 编辑 › 刮削游戏（11-21 jinyu：照 Cocoon 的 Scrape game 做）。
// Cocoon 的单个游戏刮削就是对这一款强制跑一遍刮削器：选刮削方式（只补空缺 / 全部覆盖）、要不要资料、要不要图，
// 按来源优先级取第一个命中的，跑完报「成功 / 失败」。我们这边：
//   匹配：按游戏名搜（和封面管理同一个搜索，SteamGridDB + Steam 商店），←/→ 换候选，A 重新搜；Steam 游戏直接用它自己的 AppID；
//   资料：Steam 商店 appdetails（中文）——标题、开发商、发行商、发行日期、类型、简介，写进 game-meta.json（和编辑元数据同一份）；
//   图片：封面 / 背景 / 标志，Steam 官方图优先，没有就用 SteamGridDB 第一张，存法和封面管理一样（AgEmbedSave）；
//   只补空缺 = 已经有的资料和图不动；全部覆盖 = 都换成刮到的。
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClawLibrary.Data;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class ScState
    {
        public AgDraft? D;
        public bool Force, Meta = true, Art = true;
        public bool Busy, Searching;
        public string Status = "";
        /// <summary>用户改过的搜索词（null = 用游戏原名）。</summary>
        public string? Query;
        /// <summary>刮到的资料（预览，写入后也留着给用户看）。</summary>
        public Dictionary<string, string> Got = new();
    }

    private static readonly (string Kind, string Title)[] ScArtKinds = { ("p", "封面"), ("hero", "背景"), ("logo", "标志") };

    private RsPage RsScrapePage(LibraryItem g)
    {
        var s = new ScState();
        var page = new RsPage { Title = "刮削游戏", TitleIcon = "scrape", Build = () => ScRows(s, g) };
        _ = ScLoad(s, g);
        return page;
    }

    private List<RsRow> ScRows(ScState s, LibraryItem g)
    {
        var d = s.D;
        string match = d == null || s.Searching ? "正在找…"
            : d.Match >= 0 && d.Match < d.Matches.Count ? ScMatchName(d.Matches[d.Match])
            : g.Platform == "steam" ? $"{g.Title} · Steam" : "没找到";
        // 匹配：←/→ 换候选；A 改搜索词（中文名在 Steam 上常搜不到，换英文名或原名再搜）
        var matchRow = new RsRow
        {
            Kind = RsKind.Pick, Icon = "magpie", Title = "匹配", Value = match,
            Step = k => { if (s.D is { Matches.Count: > 1 } dd) { dd.Match = (Math.Max(0, dd.Match) + k + dd.Matches.Count) % dd.Matches.Count; ScSyncAppId(dd, g); } },
            Commit = q => { if (s.D is { } dd && q.Length > 0 && !s.Busy) { s.Query = q; _ = ScSearch(s, g, dd); } },
        };
        matchRow.Press = () =>
        {
            if (_rs is not { } rs || s.D == null || s.Busy) return;
            matchRow.Value = s.Query ?? (_rwOrigTitle.TryGetValue(g.Id, out var t) ? t : g.Title);
            RsBeginEdit(rs, matchRow);
        };
        var rows = new List<RsRow>
        {
            matchRow,
            new()
            {
                Kind = RsKind.Pick, Break = true, Title = "刮削方式", Value = s.Force ? "全部覆盖" : "只补空缺",
                Press = () => s.Force = !s.Force, Step = _ => s.Force = !s.Force,
            },
            new() { Kind = RsKind.Toggle, Title = "刮削资料", IsOn = () => s.Meta, Press = () => s.Meta = !s.Meta, Step = k => s.Meta = k > 0 },
            new() { Kind = RsKind.Toggle, Title = "刮削图片", IsOn = () => s.Art, Press = () => s.Art = !s.Art, Step = k => s.Art = k > 0 },
            new()
            {
                Kind = RsKind.Action, Break = true, Icon = "scrape", Title = s.Busy ? "正在刮削…" : "开始刮削", Value = s.Status,
                Press = () => { if (!s.Busy && s.D != null) _ = ScRun(s, g); },
            },
        };
        bool first = true;
        foreach (var (key, title, _) in RsMetaFields)
            if (s.Got.TryGetValue(key, out var v) && v.Length > 0)
            {
                rows.Add(new RsRow { Kind = RsKind.Info, Break = first, Title = title, Value = v });
                first = false;
            }
        return rows;
    }

    private static string ScMatchName(JsonObject m)
    {
        string name = Str(m["name"]) ?? "";
        string src = Str(m["steamAppId"]) != null ? "Steam" : Str(m["source"]) == "sgdb" ? "SteamGridDB" : "";
        return src.Length > 0 ? $"{name} · {src}" : name;
    }

    /// <summary>选中的候选带 Steam AppID 就用它；Steam 游戏本身的 AppID 兜底。</summary>
    private static void ScSyncAppId(AgDraft d, LibraryItem g)
    {
        var m = d.Match >= 0 && d.Match < d.Matches.Count ? d.Matches[d.Match] : null;
        d.SteamAppId = (m != null ? Str(m["steamAppId"]) : null) ?? (g.Platform == "steam" ? g.Id : null);
        d.Sgdb = m != null && Str(m["source"]) == "sgdb" ? Str(m["id"]) : d.Sgdb;
        d.Official.Clear();
    }

    /// <summary>草稿和封面管理一样：本地游戏用本地游戏那份，Steam 游戏读现在用着的图；读好就按名字搜一次。</summary>
    private async Task ScLoad(ScState s, LibraryItem g)
    {
        try
        {
            await LgRefresh();
            var local = LgGames().FirstOrDefault(x => Str(x["id"]) == g.Id);
            AgDraft d;
            if (local != null) d = AgDraftOf(local);
            else
            {
                d = new AgDraft { Id = g.Id, Name = g.Title, Exe = "", Query = g.Title, SteamArt = true, Steam = true };
                foreach (var (kind, _) in AgKinds) { d.Cands[kind] = new(); d.Pick[kind] = -1; }
                _agent ??= new AgentClient();
                if (await _agent.CallLong("game_art_get", 20, new JsonObject { ["id"] = g.Id, ["platform"] = g.Platform, ["title"] = g.Title }) is JsonObject r && r["art"] is JsonObject art)
                    foreach (var (kind, _) in AgKinds)
                        if (Str(art[kind]) is { Length: > 0 } path) { d.Cands[kind].Add((null, path)); d.Pick[kind] = 0; }
            }
            s.D = d;
            await ScSearch(s, g, d);
        }
        catch (Exception ex) { Log.Warn($"scrape load: {ex.Message}"); s.Status = "没读到这款游戏"; }
        RsRefresh();
    }

    private async Task ScSearch(ScState s, LibraryItem g, AgDraft d)
    {
        s.Searching = true;
        RsRefresh();
        try
        {
            d.Query = s.Query ?? (_rwOrigTitle.TryGetValue(g.Id, out var t) ? t : g.Title);
            await AgSearch(d);
            // Steam 游戏：AppID 一样的候选排第一
            int own = g.Platform == "steam" ? d.Matches.FindIndex(m => Str(m["steamAppId"]) == g.Id) : -1;
            d.Match = own >= 0 ? own : d.Matches.Count > 0 ? 0 : -1;
            ScSyncAppId(d, g);
        }
        catch (Exception ex) { Log.Warn($"scrape search: {ex.Message}"); }
        s.Searching = false;
        RsRefresh();
    }

    private async Task ScRun(ScState s, LibraryItem g)
    {
        var d = s.D!;
        s.Busy = true;
        s.Status = "";
        RsRefresh();
        int metaN = 0, artN = 0;
        var fails = new List<string>();
        if (s.Meta)
        {
            if (d.SteamAppId is { } appid)
            {
                var got = await ScFetchSteam(appid);
                if (got == null) fails.Add("资料");
                else
                {
                    s.Got = got;
                    var saved = RwMetaOf(g);
                    var next = new Dictionary<string, string>(saved);
                    foreach (var (key, v) in got)
                    {
                        if (v.Length == 0) continue;
                        bool has = saved.TryGetValue(key, out var old) && old.Length > 0
                            || key == "title"   // 标题一直有：只补空缺时不动
                            || key == "developer" && g.Subtitle.Length > 0 && g.Subtitle is not ("本地" or "非 Steam")
                            || key == "genre" && !string.IsNullOrEmpty(g.Genre);
                        if (s.Force || !has) { next[key] = v; metaN++; }
                    }
                    if (metaN > 0) RwMetaSave(g, next);
                }
            }
            else fails.Add("资料（没有 Steam 商店条目）");
        }
        if (s.Art)
        {
            try
            {
                var match = d.Match >= 0 && d.Match < d.Matches.Count ? d.Matches[d.Match] : null;
                var keepPick = d.Pick.ToDictionary(kv => kv.Key, kv => kv.Value);
                if (match != null) await AgLoadCandidates(d, match);
                foreach (var kv in keepPick) d.Pick[kv.Key] = kv.Value;
                if (d.SteamAppId != null) await AgLoadOfficial(d);
                foreach (var (kind, _) in ScArtKinds)
                {
                    bool has = keepPick.GetValueOrDefault(kind, -1) >= 0;
                    if (has && !s.Force) continue;
                    var list = d.Cands[kind];
                    (string? Url, string Thumb)? pick = d.Official.TryGetValue(kind, out var o) ? (o.Url, o.Thumb)
                        : list.FirstOrDefault(c => c.Url != null) is { Url: not null } c ? c : null;
                    if (pick is not { } p) continue;
                    int at = list.FindIndex(x => x.Url == p.Url);
                    if (at < 0) { list.Add(p); at = list.Count - 1; }
                    d.Pick[kind] = at;
                    AgEmbedSave(d, kind);
                    artN++;
                }
            }
            catch (Exception ex) { Log.Warn($"scrape art: {ex.Message}"); fails.Add("图片"); }
        }
        s.Busy = false;
        var parts = new List<string>();
        if (s.Meta && metaN > 0) parts.Add($"资料 {metaN} 项");
        if (s.Art && artN > 0) parts.Add($"图片 {artN} 张");
        s.Status = parts.Count > 0 ? "完成 · " + string.Join(" · ", parts)
            : fails.Count > 0 ? "失败：" + string.Join("、", fails)
            : "没有要补的";
        Cue(parts.Count > 0 ? Sfx.Positive : fails.Count > 0 ? Sfx.Error : Sfx.Bump);
        if (artN > 0) Toast($"「{g.Title}」的图在后台换上了");
        RsRefresh();
    }

    /// <summary>Steam 商店 appdetails（简体中文）：标题、开发商、发行商、发行日期、类型、简介。取不到返回 null。</summary>
    private static async Task<Dictionary<string, string>?> ScFetchSteam(string appid) => await ScFetchSteam(appid, "CN") ?? await ScFetchSteam(appid, "US");

    /// <summary>国区没上架的（锁区）商店接口回 success:false，换美区再取一次，语言仍是简体中文。</summary>
    private static async Task<Dictionary<string, string>?> ScFetchSteam(string appid, string cc)
    {
        try
        {
            string url = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appid)}&l=schinese&cc={cc}";
            using var doc = JsonDocument.Parse(await AgHttp.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty(appid, out var root) || !root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            var data = root.GetProperty("data");
            string S(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string Join(string name, string? field = null) => data.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? string.Join("、", arr.EnumerateArray().Select(e => field == null ? e.GetString() : e.TryGetProperty(field, out var f) ? f.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)).Take(3))
                : "";
            string date = data.TryGetProperty("release_date", out var rd) && rd.TryGetProperty("date", out var dd) ? dd.GetString() ?? "" : "";
            string desc = WebUtility.HtmlDecode(Regex.Replace(S("short_description"), "<[^>]+>", " ")).Trim();
            desc = Regex.Replace(desc, @"\s+", " ");
            return new Dictionary<string, string>
            {
                ["title"] = S("name").Trim(),
                ["developer"] = Join("developers"),
                ["publisher"] = Join("publishers"),
                ["released"] = date,
                ["genre"] = Join("genres", "description"),
                ["description"] = desc,
            };
        }
        catch (Exception ex) { Log.Warn($"scrape steam {appid}: {ex.Message}"); return null; }
    }
}
