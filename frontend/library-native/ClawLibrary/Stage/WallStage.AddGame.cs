// 设置 › 游戏库 › 本地游戏：添加游戏（选 exe → 确认页：名称 / 匹配 / 封面 / 加入 Steam / 分类）、本地游戏清单、
// 文件夹里的游戏一次收进来、补全封面、同步到 Steam、SteamGridDB 密钥。
// 封面从 SteamGridDB 找（用户自己的 API 密钥；没填密钥时用 Steam 商店里同名游戏的图），搜索、下载、写 Steam 都在工具箱后台做。
// 写进 Steam 要先关 Steam：添加只进游戏库，「同步到 Steam」一次写完所有待同步的游戏，Steam 只重启一次。
// 确认页左栏换成封面预览：竖封面，下面是背景图（焦点在「标志」上时换成标志）。
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum LgPage { None, List, Edit }

    private sealed class LgDraft
    {
        public string? Id;
        public string Name = "", Exe = "";
        public bool Steam = true;
        public string? Collection, NewCollection, Sgdb;
        public List<JsonObject> Matches = new();
        public int Match;
        /// <summary>搜索的结果词：无密钥 / 未找到 / 密钥无效 / 连不上。</summary>
        public string Status = "";
        public bool Searching, Saving;
        /// <summary>每种图的候选（Url 为 null 的是现在用着的那张），Pick 是选中的下标（标志可以是 -1 = 不要）。</summary>
        public readonly Dictionary<string, List<(string? Url, string Thumb)>> Cands = new();
        public readonly Dictionary<string, int> Pick = new();
        public string? Header;
        public int Version;
    }

    private static readonly (string Kind, string Title)[] LgKinds = { ("p", "竖封面"), ("hero", "背景"), ("logo", "标志") };

    private LgPage _lgPage;
    private bool _stBrowseExe, _lgPolling, _lgFolderTaken;
    private JsonObject? _lgState;
    private LgDraft? _lgDraft;
    private readonly Dictionary<string, CanvasBitmap?> _lgBitmaps = new();
    private TextBox? _stEditBox;
    private Action<string>? _stEditDone;

    private bool StEditing => _stEditBox?.Visibility == Visibility.Visible;

    // ---------------- 后台 ----------------

    private async Task LgRefresh()
    {
        try
        {
            _agent ??= new AgentClient();
            bool wasBusy = Bool(_lgState?["busy"]);
            if (await _agent.Call("local_games_state") is JsonObject o) _lgState = (JsonObject)o.DeepClone();
            if (wasBusy && !Bool(_lgState?["busy"]))
            {
                _stNeedsReload = true;
                if (Str(_lgState?["error"]) is { Length: > 0 } err) Toast(err);
            }
        }
        catch (Exception ex) { Log.Warn($"local games state: {ex.Message}"); }
        if (_stOpen && _stCat == 1 && _stBrowse == null) Repaint();
        if (Bool(_lgState?["busy"]) && !_lgPolling) _ = LgPoll();
    }

    /// <summary>后台在补封面 / 写 Steam 的时候，一秒读一次进度。</summary>
    private async Task LgPoll()
    {
        _lgPolling = true;
        try
        {
            while (_stOpen && Bool(_lgState?["busy"]))
            {
                await Task.Delay(1000);
                await LgRefresh();
            }
        }
        finally { _lgPolling = false; }
    }

    private async Task LgJob(string method, params JsonNode?[] args)
    {
        try
        {
            _agent ??= new AgentClient();
            await _agent.Call(method, args);
        }
        catch (Exception ex) { Toast(ex.Message.Contains("不支持的后台调用") ? "工具箱后台版本太旧" : ex.Message); }
        await LgRefresh();
    }

    private IEnumerable<JsonObject> LgGames() => (_lgState?["games"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    private static bool LgHasArt(JsonObject g, string kind) => g["art"] is JsonObject a && Str(a[kind]) is { Length: > 0 };

    // ---------------- 游戏库分类里的几行 ----------------

    private void BuildLgEntryRows()
    {
        var s = _lgState;
        var job = s?["job"] as JsonObject;
        string phase = Str(job?["phase"]) ?? "";
        bool busy = Bool(s?["busy"]);
        var games = LgGames().ToList();
        _stRows.Add(new PerfRow { Section = true, Title = "本地游戏" });
        _stRows.Add(StAction("添加游戏…", "lgadd", "", () => { _stBrowseExe = true; StBrowseTo(""); }));
        _stRows.Add(new PerfRow { Title = "本地游戏", Value = s == null ? "" : $"{games.Count} 款", Disabled = s != null && games.Count == 0, Press = () => LgOpenList() });
        int folder = _lgFolderTaken ? 0 : Library.FolderGames.Count;
        if (folder > 0)
            _stRows.Add(StAction("收进文件夹里的游戏", "lgfolder", $"{folder} 款", () =>
            {
                if (!StConfirm("lgfolder")) return;
                var items = new JsonArray(Library.FolderGames.Select(g => (JsonNode)new JsonObject { ["name"] = g.Title, ["exe"] = g.Exe }).ToArray());
                _lgFolderTaken = true;
                _ = LgJob("local_games_add_many", items);
            }));
        int missing = games.Count(g => !LgHasArt(g, "p"));
        string artValue = busy && phase == "补封面" ? $"{Int(job?["done"])}/{Int(job?["total"])}" : missing > 0 ? $"{missing} 款" : "完整";
        _stRows.Add(new PerfRow { Title = "补全封面", Value = artValue, Disabled = s == null || (missing == 0 && !busy), Press = () => { if (!busy) _ = LgJob("local_games_fill_art"); } });
        int pending = Int(s?["pending"]);
        string syncValue = busy && phase is "关闭 Steam" or "写入 Steam" or "启动 Steam" ? phase
            : _stArmed == "lgsync" ? "再按一次确认"
            : pending > 0 ? $"{pending} 款" : "已同步";
        _stRows.Add(new PerfRow
        {
            Title = "同步到 Steam", Value = syncValue, Disabled = s == null || (pending == 0 && !busy),
            Press = () => { if (!busy && StConfirm("lgsync")) _ = LgJob("local_games_sync"); },
        });
        _stRows.Add(new PerfRow
        {
            Title = "SteamGridDB 密钥", Value = s == null ? "" : Bool(s["key"]) ? "已填" : "未填",
            Press = () => StEditText("", key => _ = LgJob("set_steamgriddb_key", JsonValue.Create(key))),
        });
    }

    // ---------------- 页面切换 ----------------

    private void LgOpenList()
    {
        _lgPage = LgPage.List;
        _lgDraft = null;
        StPageReset();
    }

    private void LgClose()
    {
        _lgPage = LgPage.None;
        _lgDraft = null;
        StPageReset();
    }

    private void StPageReset()
    {
        _stZone = 1;
        _stArmed = null;
        BuildStRows();
        _stFocus = FirstStRow();
        _stScroll = 0; _stScrollY = float.NaN;
        PaintSt();
    }

    /// <summary>B：确认页回清单（新加的回游戏库分类），清单回游戏库分类。</summary>
    private bool LgBack()
    {
        if (_lgPage == LgPage.Edit) { if (_lgDraft?.Id != null) LgOpenList(); else LgClose(); return true; }
        if (_lgPage == LgPage.List) { LgClose(); return true; }
        return false;
    }

    private void BuildLgRows()
    {
        if (_lgPage == LgPage.Edit && _lgDraft != null) { BuildLgEdit(_lgDraft); return; }
        _stRows.Add(new PerfRow { Section = true, Title = "本地游戏" });
        foreach (var g in LgGames())
        {
            string state = !Bool(g["steam"]) ? "仅游戏库"
                : !Bool(g["inSteam"]) || g["artSynced"] is JsonValue { } sv && !Bool(sv) || g["collectionSynced"] is JsonValue { } cv && !Bool(cv) ? "待同步"
                : !Bool(g["collectionOk"]) ? "分类未保存"
                : !LgHasArt(g, "p") ? "缺封面" : "";
            var game = g;
            _stRows.Add(new PerfRow { Title = Str(g["name"]) ?? "", Value = state, Press = () => LgEditExisting(game) });
        }
    }

    /// <summary>浏览里选中了一个 exe：名字默认取它所在的文件夹（放在盘根下的取文件名），然后开始找封面。</summary>
    private void LgNewFromExe(string exe)
    {
        string dir = Path.GetDirectoryName(exe) ?? "";
        string name = Path.GetFileName(dir.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name) || dir.TrimEnd('\\').Length <= 2) name = Path.GetFileNameWithoutExtension(exe);
        var existing = LgGames().FirstOrDefault(g => string.Equals(Str(g["exe"]), exe, StringComparison.OrdinalIgnoreCase));
        _stBrowse = null;
        _stBrowseExe = false;
        if (existing != null) { LgEditExisting(existing); return; }
        _lgDraft = new LgDraft { Name = name, Exe = exe };
        _lgPage = LgPage.Edit;
        StPageReset();
        _ = LgSearch(_lgDraft);
    }

    private void LgEditExisting(JsonObject g)
    {
        var d = new LgDraft
        {
            Id = Str(g["id"]), Name = Str(g["name"]) ?? "", Exe = Str(g["exe"]) ?? "", Steam = Bool(g["steam"]),
            Collection = Str(g["collection"]), NewCollection = Str(g["newCollection"]), Sgdb = Str(g["sgdb"]),
        };
        foreach (var (kind, _) in LgKinds)
            if (g["art"] is JsonObject a && Str(a[kind]) is { Length: > 0 } path)
            {
                d.Cands[kind] = new() { (null, path) };
                d.Pick[kind] = 0;
            }
        _lgDraft = d;
        _lgPage = LgPage.Edit;
        StPageReset();
    }

    // ---------------- 确认页 ----------------

    private void BuildLgEdit(LgDraft d)
    {
        _stRows.Add(new PerfRow { Section = true, Title = d.Id == null ? "添加游戏" : d.Name });
        _stRows.Add(new PerfRow
        {
            Title = "名称", Value = d.Name,
            Press = () => StEditText(d.Name, t =>
            {
                t = t.Trim();
                if (t.Length == 0 || t == d.Name) return;
                d.Name = t;
                _ = LgSearch(d);
            }),
        });
        _stRows.Add(StInfo("程序", Path.GetFileName(d.Exe)));
        string matchValue = d.Searching ? "搜索中" : d.Matches.Count == 0 ? (d.Status.Length > 0 ? d.Status : d.Cands.Count > 0 ? "现有" : "")
            : LgMatchName(d.Matches[d.Match]);
        _stRows.Add(new PerfRow
        {
            Title = "匹配", Value = matchValue, Numeric = d.Matches.Count > 1, Disabled = d.Saving,
            Step = d.Matches.Count > 1 && !d.Searching ? dir =>
            {
                int ni = Math.Clamp(d.Match + dir, 0, d.Matches.Count - 1);
                if (ni == d.Match) return;
                d.Match = ni;
                _ = LgCandidates(d);
            } : null,
            Press = () => { if (!d.Searching) _ = LgSearch(d); },
        });
        foreach (var (kind, title) in LgKinds)
        {
            var list = d.Cands.TryGetValue(kind, out var l) ? l : new();
            int pick = d.Pick.TryGetValue(kind, out var p) ? p : list.Count > 0 ? 0 : -1;
            int low = kind == "logo" ? -1 : 0;
            string value = d.Searching && list.Count == 0 ? "" : pick < 0 || list.Count == 0 ? "无" : list.Count == 1 ? "1 张" : $"{pick + 1}/{list.Count}";
            _stRows.Add(new PerfRow
            {
                Title = title, Value = value, Numeric = list.Count + (low < 0 ? 1 : 0) > 1,
                Step = dir =>
                {
                    int ni = Math.Clamp(pick + dir, list.Count > 0 ? low : -1, list.Count - 1);
                    if (ni == pick) return;
                    d.Pick[kind] = ni;
                    Repaint();
                },
                Press = () =>
                {
                    if (list.Count + (low < 0 ? 1 : 0) <= 1) return;
                    int ni = pick + 1 > list.Count - 1 ? low : pick + 1;
                    d.Pick[kind] = ni;
                    Repaint();
                },
            });
        }
        _stRows.Add(StToggle("加入 Steam", d.Steam, on => { d.Steam = on; Repaint(); }));
        var cols = (_lgState?["collections"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var ids = new List<string?> { null };
        var names = new List<string> { "不加入" };
        foreach (var c in cols) { ids.Add(Str(c["id"])); names.Add(Str(c["name"]) ?? ""); }
        if (d.NewCollection != null) { ids.Add("new:" + d.NewCollection); names.Add(d.NewCollection); }
        ids.Add("new"); names.Add("新建…");
        int sel = d.NewCollection != null ? ids.IndexOf("new:" + d.NewCollection) : Math.Max(0, ids.IndexOf(d.Collection));
        var catRow = StPick("分类", names.ToArray(), sel, i =>
        {
            if (ids[i] == "new")
            {
                StEditText("", t =>
                {
                    t = t.Trim();
                    if (t.Length == 0) return;
                    var same = cols.FirstOrDefault(c => Str(c["name"]) == t);
                    if (same != null) { d.Collection = Str(same["id"]); d.NewCollection = null; }
                    else { d.NewCollection = t; d.Collection = null; }
                    Repaint();
                });
                return;
            }
            if (ids[i] is string id && id.StartsWith("new:")) { Repaint(); return; }
            d.Collection = ids[i];
            d.NewCollection = null;
            Repaint();
        });
        catRow.Disabled = !d.Steam;
        _stRows.Add(catRow);
        _stRows.Add(new PerfRow { Title = d.Id == null ? "添加" : "保存", Value = d.Saving ? "保存中" : "", Disabled = d.Saving || d.Name.Trim().Length == 0, Press = () => _ = LgSave(d) });
        if (d.Id != null)
            _stRows.Add(StAction("移除", "lgremove", "", () =>
            {
                if (!StConfirm("lgremove")) return;
                _ = LgRemove(d);
            }));
    }

    private static string LgMatchName(JsonObject m)
    {
        string name = Str(m["name"]) ?? "";
        int year = Int(m["year"]);
        return year > 0 ? $"{name} · {year}" : name;
    }

    private async Task LgSearch(LgDraft d)
    {
        int v = ++d.Version;
        d.Searching = true;
        d.Status = "";
        if (_stOpen) Repaint();
        try
        {
            _agent ??= new AgentClient();
            var r = await _agent.CallLong("local_games_search", 40, JsonValue.Create(d.Name)) as JsonObject;
            if (v != d.Version) return;
            d.Matches = (r?["matches"] as JsonArray)?.OfType<JsonObject>().Select(m => (JsonObject)m.DeepClone()).ToList() ?? new();
            d.Status = Str(r?["status"]) ?? "";
            d.Match = 0;
            if (d.Matches.Count > 0) await LgCandidates(d, v);
        }
        catch (Exception ex)
        {
            Log.Warn($"local games search: {ex.Message}");
            if (v == d.Version) d.Status = "连不上";
        }
        if (v != d.Version) return;
        d.Searching = false;
        if (_stOpen) Repaint();
    }

    /// <summary>当前匹配的图：现有的那张（改已有游戏时）排第一，后面是找到的。新加的游戏默认选每种的第一张。</summary>
    private async Task LgCandidates(LgDraft d, int? version = null)
    {
        int v = version ?? ++d.Version;
        d.Searching = true;
        if (_stOpen) Repaint();
        try
        {
            var match = d.Matches[d.Match];
            var r = await _agent!.CallLong("local_games_candidates", 90, match.DeepClone()) as JsonObject;
            if (v != d.Version) return;
            foreach (var (kind, _) in LgKinds)
            {
                var keep = d.Cands.TryGetValue(kind, out var old) ? old.Where(c => c.Url == null).ToList() : new();
                var found = (r?[kind] as JsonArray)?.OfType<JsonObject>()
                    .Select(c => (Url: Str(c["url"]), Thumb: Str(c["thumb"]) ?? "")).Where(c => c.Url != null && c.Thumb.Length > 0).ToList() ?? new();
                d.Cands[kind] = keep.Concat(found.Select(c => ((string?)c.Url, c.Thumb))).ToList();
                d.Pick[kind] = d.Cands[kind].Count > 0 ? 0 : -1;
            }
            d.Header = (r?["header"] as JsonArray)?.OfType<JsonObject>().Select(c => Str(c["url"])).FirstOrDefault();
            d.Sgdb = Str(match["source"]) == "sgdb" ? Str(match["id"]) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"local games candidates: {ex.Message}");
            if (v == d.Version) d.Status = "连不上";
        }
        if (version == null && v == d.Version) { d.Searching = false; if (_stOpen) Repaint(); }
    }

    private async Task LgSave(LgDraft d)
    {
        if (d.Saving) return;
        d.Saving = true;
        Repaint();
        var art = new JsonObject();
        foreach (var (kind, _) in LgKinds)
        {
            var list = d.Cands.TryGetValue(kind, out var l) ? l : new();
            int pick = d.Pick.TryGetValue(kind, out var p) ? p : -1;
            if (pick >= 0 && pick < list.Count && list[pick].Url is string url) art[kind] = url;
            else if (pick < 0 && list.Any(c => c.Url == null)) art[kind] = "";   // 不要现在这张
        }
        if (d.Header != null && (d.Id == null || art.ContainsKey("p"))) art["header"] = d.Header;
        var game = new JsonObject
        {
            ["id"] = d.Id, ["name"] = d.Name.Trim(), ["exe"] = d.Exe, ["steam"] = d.Steam,
            ["collection"] = d.Collection, ["newCollection"] = d.NewCollection, ["sgdb"] = d.Sgdb, ["art"] = art,
        };
        try
        {
            _agent ??= new AgentClient();
            await _agent.CallLong("local_games_save", 120, game);
            Toast(d.Id == null ? $"已添加：{d.Name}" : "已保存");
            _stNeedsReload = true;
            d.Saving = false;
            await LgRefresh();
            if (_stOpen && _lgDraft == d) LgOpenList();
        }
        catch (Exception ex)
        {
            d.Saving = false;
            Toast(ex.Message);
            if (_stOpen) Repaint();
        }
    }

    private async Task LgRemove(LgDraft d)
    {
        try
        {
            _agent ??= new AgentClient();
            await _agent.Call("local_games_remove", JsonValue.Create(d.Id));
            Toast("已移除");
            _stNeedsReload = true;
        }
        catch (Exception ex) { Toast(ex.Message); }
        await LgRefresh();
        if (_stOpen && _lgDraft == d) LgOpenList();
    }

    // ---------------- 选 exe（文件夹浏览的另一种用法） ----------------

    private void BuildStBrowseExes(string path)
    {
        string[] exes;
        try { exes = Directory.GetFiles(path, "*.exe").OrderBy(f => f, StringComparer.CurrentCulture).Take(200).ToArray(); }
        catch { return; }
        foreach (var exe in exes)
        {
            long mb = 0;
            try { mb = new FileInfo(exe).Length >> 20; } catch { }
            var target = exe;
            _stRows.Add(new PerfRow { Title = Path.GetFileName(exe), Value = $"{mb} MB", Press = () => LgNewFromExe(target) });
        }
    }

    // ---------------- 左栏：封面预览 ----------------

    private string? LgThumb(LgDraft d, string kind)
    {
        if (!d.Cands.TryGetValue(kind, out var list) || list.Count == 0) return null;
        int pick = d.Pick.TryGetValue(kind, out var p) ? p : 0;
        return pick >= 0 && pick < list.Count ? list[pick].Thumb : null;
    }

    private CanvasBitmap? LgBitmap(string? path)
    {
        if (path == null) return null;
        if (_lgBitmaps.TryGetValue(path, out var bmp)) return bmp;
        _lgBitmaps[path] = null;
        _ = LoadLgBitmap(path);
        return null;
    }

    private async Task LoadLgBitmap(string path)
    {
        try
        {
            var bmp = await CanvasBitmap.LoadAsync(G.Device, path);
            _dq.TryEnqueue(() =>
            {
                _lgBitmaps[path] = bmp;
                if (_lgBitmaps.Count > 80)
                    foreach (var k in _lgBitmaps.Keys.Where(k => k != path).Take(_lgBitmaps.Count - 60).ToList()) { _lgBitmaps[k]?.Dispose(); _lgBitmaps.Remove(k); }
                if (_stOpen && _lgPage == LgPage.Edit) PaintSt();
            });
        }
        catch (Exception ex) { Log.Warn($"preview {Path.GetFileName(path)}: {ex.Message}"); }
    }

    private void PaintLgPreview(CanvasDrawingSession ds, float h)
    {
        var d = _lgDraft;
        if (d == null) return;
        float x = StGutter, w = PerfLeftW - 40, top = PerfSecH;
        float ph = w * 1.5f;
        var frame = Gfx.WithAlpha(CcText, 0.10f);
        var portrait = new Rect(x, top, w, ph);
        if (LgBitmap(LgThumb(d, "p")) is { } p) DrawCovered(ds, p, portrait);
        else ds.DrawRectangle(portrait, frame, 1);
        bool logo = _stFocus >= 0 && _stFocus < _stRows.Count && _stRows[_stFocus].Title == "标志";
        float sy = top + ph + 16, sh = Math.Min(w / 3.1f, h - sy - 8);
        if (sh <= 20) return;
        var strip = new Rect(x, sy, w, sh);
        if (logo)
        {
            if (LgBitmap(LgThumb(d, "logo")) is { } l) DrawContained(ds, l, strip);
            else ds.DrawRectangle(strip, frame, 1);
        }
        else if (LgBitmap(LgThumb(d, "hero")) is { } hb) DrawCovered(ds, hb, strip);
        else ds.DrawRectangle(strip, frame, 1);
    }

    private static void DrawCovered(CanvasDrawingSession ds, CanvasBitmap bmp, Rect dst)
    {
        var b = bmp.Size;
        double s = Math.Max(dst.Width / b.Width, dst.Height / b.Height);
        double sw = dst.Width / s, sh = dst.Height / s;
        ds.DrawImage(bmp, dst, new Rect((b.Width - sw) / 2, (b.Height - sh) / 2, sw, sh));
    }

    private static void DrawContained(CanvasDrawingSession ds, CanvasBitmap bmp, Rect dst)
    {
        var b = bmp.Size;
        double s = Math.Min(dst.Width / b.Width, dst.Height / b.Height);
        double w = b.Width * s, h = b.Height * s;
        ds.DrawImage(bmp, new Rect(dst.X + (dst.Width - w) / 2, dst.Y + (dst.Height - h) / 2, w, h), new Rect(0, 0, b.Width, b.Height));
    }

    // ---------------- 打字（名称、新分类、密钥）：XAML TextBox 叠在焦点行的值上，为了中文输入法和触摸键盘 ----------------

    private void StEditText(string text, Action<string> done)
    {
        if (_stEditBox == null)
        {
            _stEditBox = new TextBox
            {
                MinHeight = 0, BorderThickness = new Thickness(0), RequestedTheme = ElementTheme.Dark,
                IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextAlignment = TextAlignment.Right,
                Visibility = Visibility.Collapsed,
            };
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var ink = new SolidColorBrush(Theme.Ink);
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
                _stEditBox.Resources[key] = clear;
            foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
                _stEditBox.Resources[key] = ink;
            _stEditBox.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; StEditEnd(true); }
                else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; StEditEnd(false); }
            };
            _stEditBox.LostFocus += (_, _) => { if (StEditing) StEditEnd(true); };
            _rootEl.Overlay.Children.Add(_stEditBox);
        }
        if (_stFocus < 0 || _stFocus >= _stRows.Count) return;
        float top = 0;
        for (int i = 0; i < _stFocus; i++) top += _stRows[i].Section ? PerfSecH : PerfRowH;
        var origin = _cc?.Offset ?? default;
        float k = _ccK;
        _stEditDone = done;
        _stEditBox.Text = text;
        _stEditBox.FontSize = 17 * k;
        _stEditBox.Width = (PerfRightW - 300) * k;
        _stEditBox.Height = PerfRowH * k;
        _stEditBox.Padding = new Thickness(0, (PerfRowH - 17 * 1.4) / 2 * k, 0, 0);
        Canvas.SetLeft(_stEditBox, origin.X + (PerfRightX + 300) * k);
        Canvas.SetTop(_stEditBox, origin.Y + (PerfTop + top - _stScroll) * k);
        _stEditBox.Visibility = Visibility.Visible;
        _stEditBox.Focus(FocusState.Programmatic);
        _stEditBox.SelectAll();
        ShowTouchKeyboard();
    }

    private void StEditEnd(bool commit)
    {
        if (_stEditBox == null || !StEditing) return;
        var done = _stEditDone;
        string text = _stEditBox.Text;
        _stEditDone = null;
        _stEditBox.Visibility = Visibility.Collapsed;
        _rootEl.Focus(FocusState.Programmatic);
        if (commit) done?.Invoke(text);
        if (_stOpen) Repaint();
    }

    /// <summary>手柄上没有键盘：打字时把触摸键盘叫出来（叫不出来就算了，接了键盘照样能打）。</summary>
    private void ShowTouchKeyboard()
    {
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            Windows.UI.ViewManagement.InputPaneInterop.GetForWindow(hwnd)?.TryShow();
        }
        catch (Exception ex) { Log.Warn($"touch keyboard: {ex.Message}"); }
    }
}
