// 「单行」游戏设置 › 编辑 › 刮削游戏（11-21 jinyu：照 Cocoon 的「Scrape selected games」做，来源也对齐）。
// 页面和 Cocoon 截图同一套排布（每行标题 + 灰色说明，右上 Y ▶ 开始）：
//   一组：匹配（单个游戏，代替 Cocoon 的「Selected games」）、刮削方式（只补空缺 / 补选中项 / 全部覆盖）、资料语言、覆盖手动修改；
//   二组：刮削资料、资料类型、资料来源（每个来源一行：↓↑ 调优先级，开关启用；排前面的先用）；
//   三组：刮削图片、图片类型、图片来源（同上）；
//   最后：开始刮削（结果写在行尾），刮到的资料列在下面。
// 来源：Steam 商店（中文资料）、LaunchBox Games DB（免账号，首次下载整库建索引）、IGDB（用户自己的 Twitch 密钥）、SteamGridDB（图）。
// Cocoon 的 IGDB / ScreenScraper 走的是它自己的中转服务器和密钥，我们不用；ScreenScraper 对 PC 游戏覆盖很差，不接。
// 设置存在缓存目录 scrape.json，所有游戏共用；手动改过的资料（编辑元数据里存的 _manual）默认不被覆盖。
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClawLibrary.Data;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const string RwManualKey = "_manual";

    private sealed class ScPrefs
    {
        /// <summary>0 只补空缺（基本资料缺什么补什么），1 补选中项（只看选中的类型），2 全部覆盖。</summary>
        public int Mode;
        public bool En, Replace, Meta = true, Art = true;
        public HashSet<string> MetaTypes = new(RsMetaFields.Select(f => f.Key));
        public HashSet<string> ArtTypes = new(ScArtKinds.Select(k => k.Kind));
        public List<(string Id, bool On)> MetaSrc = new() { ("steam", true), ("launchbox", true), ("igdb", true) };
        public List<(string Id, bool On)> ArtSrc = new() { ("steam", true), ("sgdb", true), ("launchbox", true), ("igdb", true) };
        public string IgdbId = "", IgdbSecret = "";
        public bool IgdbReady => IgdbId.Length > 0 && IgdbSecret.Length > 0;
    }

    private sealed class ScState
    {
        public AgDraft? D;
        public bool Busy, Searching;
        public string Status = "";
        public string? Query;
        public Dictionary<string, string> Got = new();
        /// <summary>进度页：第几步（共 ScSteps 步）、每种图刮到的地址（预览用）、哪几种写进去了、结束了没有。</summary>
        public int Step;
        public Dictionary<string, string> ArtUrl = new();
        public HashSet<string> Wrote = new();
        public bool Done;
    }

    private static readonly (string Kind, string Title)[] ScArtKinds = { ("p", "封面"), ("hero", "背景"), ("logo", "标志"), ("header", "横封面") };
    private static readonly string[] ScModes = { "只补空缺", "补选中项", "全部覆盖" };
    private static string ScSrcName(string id) => id switch { "steam" => "Steam 商店", "launchbox" => "LaunchBox", "igdb" => "IGDB", "sgdb" => "SteamGridDB", _ => id };
    private static string ScSrcIcon(string id) => id switch { "steam" => "steam", "launchbox" => "cube", "igdb" => "igdb", "sgdb" => "stack", _ => "" };

    private static string ScPrefsPath => Path.Combine(App.CacheDir, "scrape.json");
    private ScPrefs? _scPrefs;

    private ScPrefs ScLoadPrefs()
    {
        if (_scPrefs != null) return _scPrefs;
        var p = new ScPrefs();
        try
        {
            if (File.Exists(ScPrefsPath) && JsonNode.Parse(File.ReadAllText(ScPrefsPath)) is JsonObject o)
            {
                p.Mode = Math.Clamp((int?)o["mode"] ?? 0, 0, 2);
                p.En = (bool?)o["en"] ?? false;
                p.Replace = (bool?)o["replace"] ?? false;
                p.Meta = (bool?)o["meta"] ?? true;
                p.Art = (bool?)o["art"] ?? true;
                if (o["metaTypes"] is JsonArray mt) p.MetaTypes = mt.Select(x => (string?)x ?? "").ToHashSet();
                if (o["artTypes"] is JsonArray at) p.ArtTypes = at.Select(x => (string?)x ?? "").ToHashSet();
                List<(string, bool)> Src(JsonNode? n, List<(string Id, bool On)> def)
                {
                    if (n is not JsonArray a) return def;
                    var l = a.OfType<JsonObject>().Select(x => ((string?)x["id"] ?? "", (bool?)x["on"] ?? true)).Where(x => def.Any(d => d.Id == x.Item1)).ToList();
                    foreach (var d in def) if (!l.Any(x => x.Item1 == d.Id)) l.Add(d);   // 新来源补在最后
                    return l;
                }
                p.MetaSrc = Src(o["metaSrc"], p.MetaSrc);
                p.ArtSrc = Src(o["artSrc"], p.ArtSrc);
                p.IgdbId = (string?)o["igdbId"] ?? "";
                p.IgdbSecret = (string?)o["igdbSecret"] ?? "";
            }
        }
        catch (Exception ex) { Log.Warn($"scrape prefs: {ex.Message}"); }
        return _scPrefs = p;
    }

    private void ScSavePrefs()
    {
        if (_scPrefs is not { } p) return;
        JsonArray Src(List<(string Id, bool On)> l) => new(l.Select(x => (JsonNode?)new JsonObject { ["id"] = x.Id, ["on"] = x.On }).ToArray());
        var o = new JsonObject
        {
            ["mode"] = p.Mode, ["en"] = p.En, ["replace"] = p.Replace, ["meta"] = p.Meta, ["art"] = p.Art,
            ["metaTypes"] = new JsonArray(p.MetaTypes.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["artTypes"] = new JsonArray(p.ArtTypes.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["metaSrc"] = Src(p.MetaSrc), ["artSrc"] = Src(p.ArtSrc), ["igdbId"] = p.IgdbId, ["igdbSecret"] = p.IgdbSecret,
        };
        try { File.WriteAllText(ScPrefsPath, o.ToJsonString()); }
        catch (Exception ex) { Log.Warn($"scrape prefs save: {ex.Message}"); }
    }

    // ---------------- 页面 ----------------

    private RsPage RsScrapePage(LibraryItem g)
    {
        var s = new ScState();
        var page = new RsPage { Title = "刮削游戏", TitleIcon = "scrape", Build = () => ScRows(s, g) };
        // Y ▶：进下一步——刮削进度页（Cocoon：Y 就是开始，页面底下不另放「开始」）
        page.YAction = () => { if (!s.Busy && s.D != null) { Cue(Sfx.Activate); RsPush(ScProgressPage(s, g)); _ = ScRun(s, g); } else Cue(Sfx.Bump); };
        _ = ScLoad(s, g);
        return page;
    }

    private List<RsRow> ScRows(ScState s, LibraryItem g)
    {
        var p = ScLoadPrefs();
        var d = s.D;
        string match = d == null || s.Searching ? "正在找…"
            : d.Match >= 0 && d.Match < d.Matches.Count ? ScMatchName(d.Matches[d.Match])
            : g.Platform == "steam" ? $"{g.Title} · Steam" : "没找到";
        var matchRow = new RsRow
        {
            Kind = RsKind.Pick, Icon = "pad", Title = "匹配", Sub = "←/→ 换候选，A 改搜索词再搜", Value = match,
            Step = k => { if (s.D is { Matches.Count: > 1 } dd) { dd.Match = (Math.Max(0, dd.Match) + k + dd.Matches.Count) % dd.Matches.Count; ScSyncAppId(dd, g); } },
            Commit = q => { if (s.D is { } dd && q.Length > 0 && !s.Busy) { s.Query = q; _ = ScSearch(s, g, dd); } },
        };
        matchRow.Press = () =>
        {
            if (_rs is not { } rs || s.D == null || s.Busy) return;
            matchRow.Value = s.Query ?? (_rwOrigTitle.TryGetValue(g.Id, out var t) ? t : g.Title);
            RsBeginEdit(rs, matchRow);
        };
        void Save() => ScSavePrefs();
        var rows = new List<RsRow>
        {
            matchRow,
            new()
            {
                Kind = RsKind.Pick, Icon = "filter", Title = "刮削方式", Sub = "只补缺的基本资料、只补选中的类型，或全部重新刮削", Value = ScModes[p.Mode],
                Options = ScModes, Current = () => p.Mode, Choose = i => { p.Mode = i; Save(); },
            },
            new()
            {
                Kind = RsKind.Pick, Icon = "globe", Title = "资料语言", Sub = "Steam 商店资料用哪种语言（其他来源只有英文）", Value = p.En ? "English" : "简体中文",
                Options = new[] { "简体中文", "English" }, Current = () => p.En ? 1 : 0, Choose = i => { p.En = i == 1; Save(); },
            },
            new()
            {
                Kind = RsKind.Toggle, Icon = "sliders", Title = "覆盖手动修改", Sub = "全部覆盖时，连你在编辑元数据里改过的也换掉",
                IsOn = () => p.Replace, Press = () => { p.Replace = !p.Replace; Save(); }, Step = k => { p.Replace = k > 0; Save(); },
            },
            new()
            {
                Kind = RsKind.Toggle, Break = true, Icon = "abc", Title = "刮削资料", Sub = "简介、开发商、发行商、发行日期、类型",
                IsOn = () => p.Meta, Press = () => { p.Meta = !p.Meta; Save(); }, Step = k => { p.Meta = k > 0; Save(); },
            },
        };
        // 刮削资料关着：资料类型和资料来源收起来（Cocoon：开关管着的子项跟着展开 / 收起）
        if (p.Meta) rows.Add(new RsRow
            {
                Kind = RsKind.Pick, Icon = "filter", Title = "资料类型", Sub = "刮削时包括哪些资料",
                Value = p.MetaTypes.Count >= RsMetaFields.Length ? "全部资料" : $"{p.MetaTypes.Count} 项",
                Options = RsMetaFields.Select(f => f.Title).ToArray(), Multi = true, Checked = i => p.MetaTypes.Contains(RsMetaFields[i].Key),
                ToggleOpt = i => { if (!p.MetaTypes.Remove(RsMetaFields[i].Key)) p.MetaTypes.Add(RsMetaFields[i].Key); Save(); },
            });
        if (p.Meta) ScSourceRows(rows, p.MetaSrc, p);
        rows.Add(new RsRow
        {
            Kind = RsKind.Toggle, Break = true, Icon = "artwork", Title = "刮削图片", Sub = "封面、横封面、背景、标志",
            IsOn = () => p.Art, Press = () => { p.Art = !p.Art; Save(); }, Step = k => { p.Art = k > 0; Save(); },
        });
        if (p.Art) rows.Add(new RsRow
        {
            Kind = RsKind.Pick, Icon = "filter", Title = "图片类型", Sub = "刮削时包括哪些图",
            Value = p.ArtTypes.Count >= ScArtKinds.Length ? "全部图片" : $"{p.ArtTypes.Count} 种",
            Options = ScArtKinds.Select(k => k.Title).ToArray(), Multi = true, Checked = i => p.ArtTypes.Contains(ScArtKinds[i].Kind),
            ToggleOpt = i => { if (!p.ArtTypes.Remove(ScArtKinds[i].Kind)) p.ArtTypes.Add(ScArtKinds[i].Kind); Save(); },
        });
        if (p.Art) ScSourceRows(rows, p.ArtSrc, p);
        return rows;
    }

    /// <summary>来源一行一个：↓↑ 调优先级（←/→），A 开关；IGDB 没填密钥时 A 去填密钥。</summary>
    private void ScSourceRows(List<RsRow> rows, List<(string Id, bool On)> src, ScPrefs p)
    {
        for (int i = 0; i < src.Count; i++)
        {
            int at = i;
            var (id, on) = src[i];
            bool needKey = id == "igdb" && !p.IgdbReady;
            rows.Add(new RsRow
            {
                Kind = RsKind.Order, Icon = ScSrcIcon(id), Title = ScSrcName(id),
                Sub = needKey ? "未设置密钥（在 设置 › 刮削来源密钥 里填）" : $"优先级 {i + 1} / {src.Count}",
                IsOn = () => src[at].On && !(src[at].Id == "igdb" && !p.IgdbReady),
                CanUp = i > 0, CanDown = i < src.Count - 1,
                Press = () =>
                {
                    if (src[at].Id == "igdb" && !p.IgdbReady) { Cue(Sfx.Bump); Toast("先在 设置 › 刮削来源密钥 里填 IGDB 的 Client ID 和 Secret"); return; }
                    src[at] = (src[at].Id, !src[at].On);
                    ScSavePrefs();
                },
                Step = k =>
                {
                    int to = at + (k > 0 ? -1 : 1);   // →（↑）往前挪，←（↓）往后挪
                    if (to < 0 || to >= src.Count) { Cue(Sfx.Bump); return; }
                    (src[at], src[to]) = (src[to], src[at]);
                    ScSavePrefs();
                    if (_rs is { } rs) { var pg = rs.Stack[^1]; pg.Sel += to - at; }   // 焦点跟着这一行走
                },
            });
        }
    }



    /// <summary>
    /// 设置 › 刮削来源密钥（11-21 jinyu：各刮削源的 key 放到设置里，设置页之后会重做）。
    /// 这一版设置页还没有文字输入：在浏览器里复制好，按 A 从剪贴板粘贴；再按一次清除。
    /// </summary>
    /// <summary>IGDB 刮削密钥：一行，按下弹「填密钥」小窗（同 Steam API Key，11-26 jinyu）；放在设置 › 游戏库的本地游戏卡里。</summary>
    private void BuildStScrapeKeys()
    {
        var p = ScLoadPrefs();
        _stRows.Add(new PerfRow
        {
            Sub = true, Title = "IGDB 刮削密钥", Value = p.IgdbReady ? "已配置" : "未配置",
            Press = () => { if (!AkShowIgdb()) Toast("在 Cocoon 主题的设置页里配置"); },
        });
    }

    private static string ScMatchName(JsonObject m)
    {
        string name = Str(m["name"]) ?? "";
        string src = Str(m["steamAppId"]) != null ? "Steam" : Str(m["source"]) == "sgdb" ? "SteamGridDB" : "";
        return src.Length > 0 ? $"{name} · {src}" : name;
    }

    private static void ScSyncAppId(AgDraft d, LibraryItem g)
    {
        var m = d.Match >= 0 && d.Match < d.Matches.Count ? d.Matches[d.Match] : null;
        d.SteamAppId = (m != null ? Str(m["steamAppId"]) : null) ?? (g.Platform == "steam" ? g.Id : null);
        d.Sgdb = m != null && Str(m["source"]) == "sgdb" ? Str(m["id"]) : d.Sgdb;
        d.Official.Clear();
    }

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
            int own = g.Platform == "steam" ? d.Matches.FindIndex(m => Str(m["steamAppId"]) == g.Id) : -1;
            d.Match = own >= 0 ? own : d.Matches.Count > 0 ? 0 : -1;
            ScSyncAppId(d, g);
        }
        catch (Exception ex) { Log.Warn($"scrape search: {ex.Message}"); }
        s.Searching = false;
        RsRefresh();
    }

    // ---------------- 刮削 ----------------

    private async Task ScRun(ScState s, LibraryItem g)
    {
        var p = ScLoadPrefs();
        var d = s.D!;
        s.Busy = true; s.Done = false; s.Step = 0;
        s.Got = new(); s.ArtUrl = new(); s.Wrote = new();
        void Say(string msg) { s.Status = msg; _dq.TryEnqueue(RsPaintSoon); }
        void Next(string msg) { s.Step++; Say(msg); }
        Say("正在准备");
        var fails = new List<string>();
        string? appid = d.SteamAppId;
        var match = d.Match >= 0 && d.Match < d.Matches.Count ? d.Matches[d.Match] : null;
        var names = new List<string>();
        void Name(string? n) { if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n)) names.Add(n); }
        Name(s.Query);
        Name(match != null ? Str(match["name"]) : null);
        Name(_rwOrigTitle.TryGetValue(g.Id, out var orig) ? orig : g.Title);

        bool metaOn(string id) => p.Meta && p.MetaSrc.Any(x => x.Id == id && x.On);
        bool artOn(string id) => p.Art && p.ArtSrc.Any(x => x.Id == id && x.On);

        // Steam 商店：资料（按语言）+ 英文名（LaunchBox / IGDB 按英文名找）
        Dictionary<string, string>? steam = null;
        if (appid != null)
        {
            Next("正在读 Steam 商店");
            if (metaOn("steam")) { steam = await ScFetchSteam(appid, p.En ? "english" : "schinese"); if (steam == null) fails.Add("Steam 商店"); }
            var en = p.En && steam != null ? steam : await ScFetchSteam(appid, "english");
            Name(en?.GetValueOrDefault("title"));
        }
        // LaunchBox
        LbGame? lb = null;
        if (metaOn("launchbox") || artOn("launchbox"))
        {
            Next(LaunchBoxDb.Indexed ? "正在查 LaunchBox" : "第一次用 LaunchBox：下载资料库（约 110 MB）");
            if (await LaunchBoxDb.EnsureAsync(Say)) lb = LaunchBoxDb.Find(names, appid);
            else fails.Add("LaunchBox");
        }
        // IGDB
        IgdbGame? ig = null;
        if ((metaOn("igdb") || artOn("igdb")) && p.IgdbReady)
        {
            Next("正在查 IGDB");
            try { ig = await Igdb.FindAsync(p.IgdbId, p.IgdbSecret, names, appid); }
            catch (Exception ex) { Log.Warn($"igdb: {ex.Message}"); fails.Add(ex.Message); }
        }

        // ---- 资料：每一项按来源顺序取第一个有值的 ----
        int metaN = 0;
        s.Step = 3;
        if (p.Meta)
        {
            Next("正在整理资料");
            Dictionary<string, string>? Of(string id) => id switch
            {
                "steam" => steam,
                "launchbox" => lb == null ? null : new() { ["title"] = lb.Name, ["developer"] = lb.Developer ?? "", ["publisher"] = lb.Publisher ?? "", ["released"] = lb.Date ?? "", ["genre"] = lb.Genres ?? "", ["description"] = lb.Overview ?? "" },
                "igdb" => ig == null ? null : new() { ["title"] = ig.Name, ["developer"] = ig.Developer ?? "", ["publisher"] = ig.Publisher ?? "", ["released"] = ig.Date ?? "", ["genre"] = ig.Genres ?? "", ["description"] = ig.Summary ?? "" },
                _ => null,
            };
            var got = new Dictionary<string, string>();
            foreach (var (key, _, _) in RsMetaFields)
                foreach (var (id, on) in p.MetaSrc)
                    if (on && Of(id) is { } m && m.GetValueOrDefault(key) is { Length: > 0 } v) { got[key] = v; break; }
            s.Got = got;
            _dq.TryEnqueue(RsPaintSoon);
            var saved = RwMetaOf(g);
            var manual = (saved.TryGetValue(RwManualKey, out var mk) ? mk.Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>()).ToHashSet();
            var next = new Dictionary<string, string>(saved);
            foreach (var (key, v) in got)
            {
                if (p.Mode != 0 && !p.MetaTypes.Contains(key)) continue;   // 只补空缺看基本资料（全部），另两种只看选中的类型
                bool has = saved.TryGetValue(key, out var old) && old.Length > 0
                    || key == "title"
                    || key == "developer" && g.Subtitle.Length > 0 && g.Subtitle is not ("本地" or "非 Steam")
                    || key == "genre" && !string.IsNullOrEmpty(g.Genre);
                bool write = p.Mode == 2 ? !(manual.Contains(key) && !p.Replace) : !has;
                if (!write) continue;
                next[key] = v;
                manual.Remove(key);
                metaN++;
            }
            if (manual.Count > 0) next[RwManualKey] = string.Join(",", manual); else next.Remove(RwManualKey);
            if (metaN > 0) RwMetaSave(g, next);
        }

        // ---- 图片：每一种按来源顺序取第一张 ----
        int artN = 0;
        if (p.Art)
        {
            try
            {
                s.Step = 4;
                var keepPick = d.Pick.ToDictionary(kv => kv.Key, kv => kv.Value);
                var kinds = ScArtKinds.Where(k => p.Mode == 0 || p.ArtTypes.Contains(k.Kind)).Select(k => k.Kind)
                    .Where(k => p.Mode == 2 || keepPick.GetValueOrDefault(k, -1) < 0).ToList();
                {
                    if (artOn("steam") && appid != null) { Next("正在取 Steam 官方图"); await AgLoadOfficial(d); }
                    if (artOn("sgdb") && match != null) { Say("正在取 SteamGridDB 的图"); await AgLoadCandidates(d, match); foreach (var kv in keepPick) d.Pick[kv.Key] = kv.Value; }
                    // 每种图都选出一张给进度页预览；要写的那几种再存进去
                    foreach (var (kind, _) in ScArtKinds)
                    {
                        string? url = null;
                        foreach (var (id, on) in p.ArtSrc)
                        {
                            if (!on) continue;
                            url = id switch
                            {
                                "steam" => d.Official.TryGetValue(kind, out var o) ? o.Url : null,
                                "sgdb" => d.Cands.GetValueOrDefault(kind)?.FirstOrDefault(c => c.Url != null).Url,
                                "launchbox" => lb?.Images.GetValueOrDefault(kind),
                                "igdb" => ig?.Images.GetValueOrDefault(kind),
                                _ => null,
                            };
                            if (url != null) break;
                        }
                        if (url == null) continue;
                        s.ArtUrl[kind] = url;
                        _dq.TryEnqueue(RsPaintSoon);
                        if (!kinds.Contains(kind)) continue;
                        if (!d.Cands.TryGetValue(kind, out var list)) d.Cands[kind] = list = new();
                        int at = list.FindIndex(x => x.Url == url);
                        if (at < 0) { list.Add((url, url)); at = list.Count - 1; }
                        d.Pick[kind] = at;
                        AgEmbedSave(d, kind);
                        s.Wrote.Add(kind);
                        artN++;
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"scrape art: {ex.Message}"); fails.Add("图片"); }
        }

        s.Busy = false;
        var parts = new List<string>();
        if (metaN > 0) parts.Add($"资料 {metaN} 项");
        if (artN > 0) parts.Add($"图片 {artN} 张");
        s.Status = parts.Count > 0 ? "完成 · " + string.Join(" · ", parts) + (fails.Count > 0 ? $"（{string.Join("、", fails.Distinct())} 没取到）" : "")
            : fails.Count > 0 ? "失败：" + string.Join("、", fails.Distinct())
            : lb == null && ig == null && steam == null ? "哪个来源都没找到这款游戏"
            : "没有要补的";
        Cue(parts.Count > 0 ? Sfx.Positive : fails.Count > 0 ? Sfx.Error : Sfx.Bump);
        // 进度页停一下给人看结果，再自己退回设置页
        s.Done = true; s.Step = ScSteps;
        RsPaintSoon();
        var done = s;
        After(1600, () =>
        {
            if (_rs is { } rs && rs.Stack.Count > 0 && rs.Stack[^1].Title == "正在刮削" && done.Done) { Cue(Sfx.Back); RsPop(); }
            Toast(done.Status + (artN > 0 ? " · 图片在后台换上" : ""));
        });
        RsRefresh();
    }

    /// <summary>Steam 商店 appdetails：国区没上架的（锁区）换美区再取一次。</summary>
    private static async Task<Dictionary<string, string>?> ScFetchSteam(string appid, string lang) =>
        await ScFetchSteam(appid, lang, "CN") ?? await ScFetchSteam(appid, lang, "US");

    private static async Task<Dictionary<string, string>?> ScFetchSteam(string appid, string lang, string cc)
    {
        try
        {
            string url = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appid)}&l={lang}&cc={cc}";
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
                ["title"] = S("name").Trim(), ["developer"] = Join("developers"), ["publisher"] = Join("publishers"),
                ["released"] = date, ["genre"] = Join("genres", "description"), ["description"] = desc,
            };
        }
        catch (Exception ex) { Log.Warn($"scrape steam {appid} {cc}: {ex.Message}"); return null; }
    }
}
