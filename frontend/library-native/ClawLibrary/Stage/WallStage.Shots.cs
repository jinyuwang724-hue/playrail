// 控制中心 → 截图（左下功能坞的「截图」）：自己的一页，不套 3:7 两栏。
// 底子和别的页一样：控制中心那层封面取色的动态背景、左上 24 px 粗标题、右上状态；标题右边三个页签「全部 / Steam / 本地」（LB / RB 换），
// 当前项白字加下划线。Steam = Steam 库里的游戏和加进 Steam 的非 Steam 游戏；本地 = 其余（文件夹里的游戏、桌面、浏览器……）。
// 游戏只是筛选：View 键弹出这个页签下有截图的游戏（和「收藏」），选一款就只看它；游戏库展开卡上的「X 截图」直接带着筛选进来。
// 三种排版（设置 › 外观 › 截图页样式；页面里按 X 也能换），画法在 WallStage.Shots.Paint.cs：
// - 日历：左边一列大号日期，右边五列拼版，每天最新的一张占 2×2；连拍叠成一摞。
// - 放映：中间一张大图，左右露出上一张 / 下一张；下面一行读数，最底下按天分组的小图条。
// - 冲印：控制中心拍立得的相纸，放大到和放映差不多，左右两张斜着露一半。
// A 看大图（← → 换张、↑ ↓ 选操作、A 执行、X 藏起文字），Y 收藏，View 筛选，B 返回。删除进回收站，要连按两下 A。
// 截图从哪来、怎么归到游戏：见 Data/Captures.cs。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum ShStyle { Calendar, Projector, Prints }
    private static readonly string[] ShStyleNames = { "日历", "放映", "冲印" };

    /// <summary>标题右边的一项：全部 / Steam / 本地（Shots 已经按筛选过滤）。Key 用来换数据后找回同一项。</summary>
    private sealed record ShTab(string Key, string Name, List<Capture> Shots);

    /// <summary>筛选里的一项：一款游戏（Key = Capture.GameId）、「其他」（other）、「收藏」（fav），或第一行的「所有游戏」（null）。</summary>
    private sealed record ShFilterItem(string? Key, string Name, int Count);

    /// <summary>当前筛选（null = 不筛）；筛选面板开着没有、焦点、内容。</summary>
    private string? _shFilter;
    private bool _shFilterOpen;
    private int _shFilterFocus;
    private List<ShFilterItem> _shFilterItems = new();
    private SpriteVisual? _shFilterSprite;

    /// <summary>页面上的一格：一张，或一摞连拍（Shots[0] 是最新的那张）。坐标是设计 px。</summary>
    private sealed class ShUnit
    {
        public required List<Capture> Shots;
        public Capture Top => Shots[0];
        public float X, Y, W, H;
        public bool Hero;
        public ContainerVisual? El;
        public SpriteVisual? Img, Over, Shadow;
        public SpriteVisual[]? Backs;
        public LoadedImageSurface? Ref;
        public string OverDrawn = "";
    }

    private ContainerVisual? _sh;
    private SpriteVisual _shTitle = null!, _shTabsSprite = null!;
    private ContainerVisual _shBody = null!;
    private bool _shOpen, _shLoading, _shLoaded;
    private int _shVersion;
    private ShStyle _shStyle;
    private List<Capture> _shAll = new();
    private List<ShTab> _shTabs = new();
    private int _shTab;
    private string? _shTabKey;
    private List<ShUnit> _shUnits = new();
    private int _shFocus;
    private HashSet<string> _shFavs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int W, int H)?> _shSizes = new();
    /// <summary>标题右边每一项的点击范围（设计 px，相对舞台）。</summary>
    private readonly List<(float X, float W)> _shTabHits = new();

    // 大图
    private bool _shViewer, _shViewerChrome = true;
    private int _shVIndex, _shVAction;
    private string? _shArmed;
    private long _shArmedAt;

    private ShTab? ShCurrentTab => _shTab >= 0 && _shTab < _shTabs.Count ? _shTabs[_shTab] : null;
    /// <summary>当前这一项里的全部截图（大图里 ← → 一张一张走）。</summary>
    private List<Capture> ShFlat => ShCurrentTab?.Shots ?? new List<Capture>();

    // ---------------- 开 / 关 ----------------

    /// <summary>打开截图页。filter：游戏库展开卡上「X 截图」带进来的游戏（Capture.GameId），落在「全部」并只看它。</summary>
    private void OpenShots(string? filter = null)
    {
        if (_cc == null) return;
        EnsureShVisuals();
        _shOpen = true;
        _shViewer = false;
        _shArmed = null;
        _shStyle = (ShStyle)Math.Clamp(_prefs.ShotsStyle, 0, 2);
        int v = ++_shVersion;
        _shFavs = Captures.LoadFavorites();
        _shFilter = filter;
        _shFilterOpen = false;
        if (_shFilterSprite != null) _shFilterSprite.IsVisible = false;
        _shTabKey = "all";   // 每次打开都落在「全部」
        _shLoaded = false;  // 每次打开重新扫一遍（可能刚截了新图）
        ShRebuild();
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        _sh!.IsVisible = true;
        _sh.Opacity = 0;
        _sh.StartAnimation("Opacity", ToScalar(1, 240, EaseOutCss, 80));
        _ = ShLoad(v);
        Log.Info($"shots page open ({ShStyleNames[(int)_shStyle]})");
    }

    private void CloseShots(bool instant = false)
    {
        if (!_shOpen || _sh == null) return;
        if (!instant) Cue(Sfx.ModalHide);
        _shOpen = false;
        _shViewer = false;
        int v = ++_shVersion;
        if (instant || Reduced)
        {
            _sh.StopAnimation("Opacity"); _sh.IsVisible = false; _sh.Opacity = 1;
            foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); vis.Opacity = 1; }
            ShReleaseAll();
            return;
        }
        _sh.StartAnimation("Opacity", ToScalar(0, 140, EaseOutCss));
        foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(1, 220, EaseOutCss, 60));
        After(160, () =>
        {
            if (v != _shVersion || _shOpen || _sh == null) return;
            _sh.IsVisible = false; _sh.StopAnimation("Opacity"); _sh.Opacity = 1;
            ShReleaseAll();
        });
        Log.Info("shots page close");
    }

    private void EnsureShVisuals()
    {
        if (_sh != null) return;
        _sh = G.Container();
        _sh.IsVisible = false;
        _shTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _shTabsSprite = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _shBody = G.Container();
        _shFilterSprite = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _shFilterSprite.IsVisible = false;
        foreach (var s in new Visual[] { _shBody, _shTitle, _shTabsSprite, _shFilterSprite }) _sh.Children.InsertAtTop(s);
        _cc!.Children.InsertAtTop(_sh);
    }

    /// <summary>后台扫一遍文件夹（Steam 截图多的时候要一会儿），回到界面线程再排版。</summary>
    private async Task ShLoad(int v)
    {
        // 不挡重复的读：快速关了又开时，旧的那次读完会因为版本号不对被丢掉，新的照常读
        _shLoading = true;
        try
        {
            var lib = _lib;
            var list = await Task.Run(() => Captures.Scan(lib));
            if (v != _shVersion || !_shOpen) return;
            string? keep = ShFocusedPath();
            _shAll = list;
            SetShotCounts(list);
            _shLoaded = true;
            ShRebuild(keep);
            Log.Info($"shots: {list.Count} images, {_shTabs.Count} tabs");

            // Steam 云端截图：请 agent 经 Steam 前端拿一遍最新的列表、下几秒新图；多出来的就重扫一遍
            int cloud = list.Count(c => c.CloudId != null);
            JsonNode? refreshed;
            if (_agent == null) return;
            try { refreshed = await _agent.Call("refresh_steam_cloud_screenshots"); }
            catch (Exception ex) { Log.Warn($"shots steam cloud: {ex.Message}"); return; }
            int local = (int?)refreshed?["local"] ?? cloud;
            Log.Info($"shots: steam cloud {local}/{(int?)refreshed?["total"] ?? -1} local{((bool?)refreshed?["offline"] == true ? " (steam offline)" : "")}");
            if (local == cloud || v != _shVersion || !_shOpen) return;
            var again = await Task.Run(() => Captures.Scan(lib));
            if (v != _shVersion || !_shOpen) return;
            keep = ShFocusedPath();
            _shAll = again;
            SetShotCounts(again);
            ShRebuild(keep);
        }
        catch (Exception ex) { Log.Warn($"shots load: {ex.Message}"); _shLoaded = true; if (_shOpen) ShRebuild(); }
        finally { _shLoading = false; }
    }

    private async Task HideCloudShot(string id)
    {
        try { if (_agent != null) await _agent.Call("hide_steam_cloud_screenshot", id); }
        catch (Exception ex) { Log.Warn($"shots hide cloud {id}: {ex.Message}"); }
    }

    // ---------------- 数据 → 分组 ----------------

    /// <summary>重分组、重排版。keepPath：换完以后焦点还落在这张上（找不到就落在第一格）。</summary>
    private void ShRebuild(string? keepPath = null)
    {
        _shTabs = ShBuildTabs();
        _shTab = Math.Max(0, _shTabs.FindIndex(t => t.Key == _shTabKey));
        _shTabKey = ShCurrentTab?.Key;
        ShBuildUnits();
        _shFocus = 0;
        if (keepPath != null)
        {
            int f = _shUnits.FindIndex(u => u.Shots.Any(s => s.Path.Equals(keepPath, StringComparison.OrdinalIgnoreCase)));
            if (f >= 0) _shFocus = f;
        }
        ShBuildBody();
        ShPaintAll();
    }

    /// <summary>
    /// 算 Steam 的：Steam 截图（本机和云端）、归到 Steam 游戏或 Steam 里的非 Steam 快捷方式（游戏库里 Platform 是 steam / other）的；
    /// 其余（手动加的游戏文件夹、桌面、浏览器、对不上的）算本地。
    /// </summary>
    private bool ShIsSteam(Capture c)
    {
        if (c.Source.StartsWith("Steam", StringComparison.Ordinal)) return true;
        if (c.GameId == null) return false;
        if (c.GameId.StartsWith("steam:", StringComparison.Ordinal)) return true;
        return _lib.ById.TryGetValue(c.GameId, out var g) && (g.Platform is "steam" or "other" || g.Platform == "local" && g.ExePath == null);   // 本地游戏同步进 Steam 后也是快捷方式
    }

    private bool ShPasses(Capture c) => _shFilter switch
    {
        null => true,
        "fav" => _shFavs.Contains(c.Path),
        "other" => c.GameId == null,
        var key => c.GameId == key,
    };

    /// <summary>三个页签固定在那（没有截图也在，张数写 0）；每个页签里已经按当前筛选过滤。</summary>
    private List<ShTab> ShBuildTabs()
    {
        var all = _shAll.Where(ShPasses).ToList();
        return new List<ShTab>
        {
            new("all", "全部", all),
            new("steam", "Steam", all.Where(ShIsSteam).ToList()),
            new("local", "本地", all.Where(c => !ShIsSteam(c)).ToList()),
        };
    }

    /// <summary>当前页签（不看筛选）里有截图的游戏，最近截过图的在前；最上面是「所有游戏」，有收藏时第二行是「收藏」。</summary>
    private List<ShFilterItem> ShBuildFilterItems()
    {
        string tab = ShCurrentTab?.Key ?? "all";
        var pool = _shAll.Where(c => tab == "all" || (tab == "steam") == ShIsSteam(c)).ToList();
        var items = new List<ShFilterItem> { new(null, "所有游戏", pool.Count) };
        int favs = pool.Count(c => _shFavs.Contains(c.Path));
        if (favs > 0) items.Add(new("fav", "收藏", favs));
        foreach (var g in pool.Where(c => c.GameId != null).GroupBy(c => c.GameId!).OrderByDescending(g => g.Max(c => c.Time)))
            items.Add(new(g.Key, g.First().Game, g.Count()));
        int other = pool.Count(c => c.GameId == null);
        if (other > 0) items.Add(new("other", "其他", other));
        return items;
    }

    /// <summary>筛选的显示名（页签后面那行小字用）。</summary>
    private string? ShFilterName() => _shFilter switch
    {
        null => null,
        "fav" => "收藏",
        "other" => "其他",
        var key => _shAll.FirstOrDefault(c => c.GameId == key)?.Game ?? (_lib.ById.TryGetValue(key, out var g) ? g.Title : key),
    };

    // ---------------- 截图跟着游戏走：游戏库展开卡上的「X 截图」 ----------------

    /// <summary>每款游戏（游戏库 Id）有几张截图：启动后后台扫一遍，截图页每次读完再更新。</summary>
    private Dictionary<string, int> _shotCounts = new();

    internal int ShotCount(string id) => _shotCounts.TryGetValue(id, out var n) ? n : 0;

    private void SetShotCounts(List<Capture> list) =>
        _shotCounts = list.Where(c => c.GameId != null).GroupBy(c => c.GameId!).ToDictionary(g => g.Key, g => g.Count());

    private async Task CountShotsAsync()
    {
        try
        {
            var lib = _lib;
            var list = await Task.Run(() => Captures.Scan(lib));
            SetShotCounts(list);
            Log.Info($"shot counts: {list.Count} images, {_shotCounts.Count} games");
        }
        catch (Exception ex) { Log.Warn($"shot counts: {ex.Message}"); }
    }

    /// <summary>墙上展开卡按 X：这款有截图就开控制中心、等出场形变走完，进截图页并筛好它。</summary>
    private bool TryOpenShotsForExpanded()
    {
        if (_ccOpen || _expandedKey is not { } key || Engine.Geometry.SlotByKey(key) is not { } slot) return false;
        if (ItemFor(slot) is not { } id || Lookup(id) is not { Kind: ItemKind.Game } item || ShotCount(item.Id) == 0) return false;
        OpenControlCenter();
        void Enter(int tries)
        {
            if (!_ccOpen) return;
            if (MorphBusy && tries > 0) { After(50, () => Enter(tries - 1)); return; }
            OpenShots(item.Id);
        }
        Enter(60);
        Log.Info($"shots for game {item.Id}");
        return true;
    }

    // ---------------- 筛选面板 ----------------

    /// <summary>View：打开 / 关上筛选。大图里不开。</summary>
    private void ShToggleFilter()
    {
        if (_shViewer) return;
        if (_shFilterOpen) { ShCloseFilter(); return; }
        _shFilterItems = ShBuildFilterItems();
        _shFilterFocus = Math.Max(0, _shFilterItems.FindIndex(i => i.Key == _shFilter));
        _shFilterOpen = true;
        ShPaintFilter();
    }

    private void ShCloseFilter()
    {
        _shFilterOpen = false;
        if (_shFilterSprite != null) _shFilterSprite.IsVisible = false;
    }

    private void ShFilterMove(Direction dir)
    {
        if (dir is not (Direction.Up or Direction.Down) || _shFilterItems.Count == 0) return;
        int next = Math.Clamp(_shFilterFocus + (dir == Direction.Up ? -1 : 1), 0, _shFilterItems.Count - 1);
        if (next == _shFilterFocus) return;
        _shFilterFocus = next;
        ShPaintFilter();
    }

    private void ShFilterPick()
    {
        if (_shFilterFocus < 0 || _shFilterFocus >= _shFilterItems.Count) return;
        ShSetFilter(_shFilterItems[_shFilterFocus].Key);
        ShCloseFilter();
    }

    private void ShSetFilter(string? key)
    {
        _shFilter = key;
        ShRebuild();
        Log.Info($"shots filter: {key ?? "-"}");
    }

    /// <summary>日历把连拍叠成一格；放映和冲印一张一格。</summary>
    private void ShBuildUnits()
    {
        _shUnits = new List<ShUnit>();
        var shots = ShFlat;
        for (int i = 0; i < shots.Count; i++)
        {
            var unit = new ShUnit { Shots = new List<Capture> { shots[i] } };
            if (_shStyle == ShStyle.Calendar && shots[i].Burst >= 0)
                while (i + 1 < shots.Count && shots[i + 1].Burst == shots[i].Burst) unit.Shots.Add(shots[++i]);
            _shUnits.Add(unit);
        }
    }

    private string? ShFocusedPath() => _shFocus >= 0 && _shFocus < _shUnits.Count ? _shUnits[_shFocus].Top.Path : null;

    // ---------------- 输入 ----------------

    private void ShMove(Direction dir)
    {
        if (_shFilterOpen) { ShFilterMove(dir); return; }
        if (_shViewer) { ShViewerMove(dir); return; }
        if (_shUnits.Count == 0) return;
        int next = _shFocus;
        if (dir is Direction.Left or Direction.Right) next = Math.Clamp(_shFocus + (dir == Direction.Left ? -1 : 1), 0, _shUnits.Count - 1);
        else if (_shStyle == ShStyle.Calendar) next = ShCalNeighbor(dir == Direction.Up);
        if (next == _shFocus) return;
        ShSetFocus(next);
    }

    /// <summary>日历里 ↑ ↓：上 / 下面一排里横向离得最近的一格（2×2 的大格也算一排）。</summary>
    private int ShCalNeighbor(bool up)
    {
        var cur = _shUnits[_shFocus];
        int best = _shFocus;
        float bestGap = float.MaxValue, bestDx = float.MaxValue;
        float cx = cur.X + cur.W / 2;
        for (int i = 0; i < _shUnits.Count; i++)
        {
            var u = _shUnits[i];
            float gap = up ? cur.Y - (u.Y + u.H) : u.Y - (cur.Y + cur.H);
            if (gap < -1) continue;
            float dx = Math.Abs(u.X + u.W / 2 - cx);
            if (gap < bestGap - 1 || (Math.Abs(gap - bestGap) <= 1 && dx < bestDx)) { best = i; bestGap = gap; bestDx = dx; }
        }
        return best;
    }

    private void ShSetFocus(int i)
    {
        int old = _shFocus;
        _shFocus = i;
        ShFocusChanged(old);
    }

    private void ShActivate()
    {
        if (_shFilterOpen) { ShFilterPick(); return; }
        if (_shViewer) { ShRunAction(); return; }
        if (_shUnits.Count == 0) return;
        ShOpenViewer(ShFlat.IndexOf(_shUnits[_shFocus].Top));
    }

    private void ShBack()
    {
        if (_shFilterOpen) { ShCloseFilter(); return; }
        if (_shViewer) { ShCloseViewer(); return; }
        CloseShots();
    }

    /// <summary>Y：收藏 / 取消收藏（日历里的连拍收藏最上面那张）。</summary>
    private void ShY()
    {
        if (_shFilterOpen) return;
        var c = _shViewer ? ShViewerShot() : _shUnits.Count > 0 ? _shUnits[_shFocus].Top : null;
        if (c != null) ShToggleFav(c);
    }

    /// <summary>X：大图里藏起 / 露出文字；页面里换排版（存进 ui.ini）。</summary>
    private void ShX()
    {
        if (_shFilterOpen) return;
        if (_shViewer) { _shViewerChrome = !_shViewerChrome; ShPaintViewer(); return; }
        ShSetStyle((ShStyle)(((int)_shStyle + 1) % 3));
        Toast($"截图排版：{ShStyleNames[(int)_shStyle]}");
    }

    private void ShSetStyle(ShStyle style)
    {
        if (_prefs.ShotsStyle != (int)style) SaveUi(_prefs with { ShotsStyle = (int)style });
        if (!_shOpen) return;
        string? keep = ShFocusedPath();
        _shStyle = style;
        ShRebuild(keep);
    }

    /// <summary>LB / RB：换页签（全部 / Steam / 本地），筛选跟着走。大图里不换。</summary>
    private void ShSwitchTab(int delta)
    {
        if (_shViewer || _shFilterOpen || _shTabs.Count < 2) return;
        _shTab = (_shTab + delta + _shTabs.Count) % _shTabs.Count;
        _shTabKey = ShCurrentTab?.Key;
        ShBuildUnits();
        _shFocus = 0;
        ShBuildBody();
        ShPaintAll();
    }

    private void ShToggleFav(Capture c)
    {
        bool on = !_shFavs.Contains(c.Path);
        if (on) _shFavs.Add(c.Path); else _shFavs.Remove(c.Path);
        Captures.SaveFavorites(_shFavs);
        Toast(on ? "已收藏" : "已取消收藏");
        // 「收藏」这一项可能刚出现或刚空掉：在那一项里取消收藏就留在原地（重排后焦点落到相邻的一张）
        string? keep = _shViewer ? c.Path : ShFocusedPath();
        int oldCount = _shTabs.Count;
        _shTabs = ShBuildTabs();
        int ti = _shTabs.FindIndex(t => t.Key == _shTabKey);
        if (ti < 0) { _shTabKey = "all"; ti = _shTabs.FindIndex(t => t.Key == "all"); }
        _shTab = Math.Max(0, ti);
        if (_shFilter == "fav" || oldCount != _shTabs.Count)
        {
            ShBuildUnits();
            int f = keep == null ? -1 : _shUnits.FindIndex(u => u.Shots.Any(s => s.Path == keep));
            _shFocus = f >= 0 ? f : Math.Clamp(_shFocus, 0, Math.Max(0, _shUnits.Count - 1));
            if (_shViewer)
            {
                _shVIndex = Math.Clamp(keep == null ? 0 : Math.Max(0, ShFlat.FindIndex(s => s.Path == keep)), 0, Math.Max(0, ShFlat.Count - 1));
                if (ShFlat.Count == 0) ShCloseViewer();
            }
            ShBuildBody();
        }
        ShPaintAll();
        if (_shViewer) ShPaintViewer();
    }

    // ---------------- 大图 ----------------

    private Capture? ShViewerShot() => _shVIndex >= 0 && _shVIndex < ShFlat.Count ? ShFlat[_shVIndex] : null;

    private void ShOpenViewer(int index)
    {
        if (index < 0 || index >= ShFlat.Count) return;
        _shViewer = true;
        _shViewerChrome = true;
        _shVIndex = index;
        _shVAction = 0;
        _shArmed = null;
        ShShowViewer(true);
        ShPaintViewer();
    }

    private void ShCloseViewer()
    {
        if (!_shViewer) return;
        _shViewer = false;
        _shArmed = null;
        // 回到页面时焦点落在刚才看的那张
        if (ShViewerShot() is { } c)
        {
            int f = _shUnits.FindIndex(u => u.Shots.Contains(c));
            if (f >= 0 && f != _shFocus) ShSetFocus(f);
        }
        ShShowViewer(false);
    }

    private void ShViewerMove(Direction dir)
    {
        if (dir is Direction.Up or Direction.Down)
        {
            if (!_shViewerChrome) { _shViewerChrome = true; ShPaintViewer(); return; }
            _shVAction = Math.Clamp(_shVAction + (dir == Direction.Up ? -1 : 1), 0, ShActions.Length - 1);
            _shArmed = null;
            ShPaintViewer();
            return;
        }
        int next = Math.Clamp(_shVIndex + (dir == Direction.Left ? -1 : 1), 0, ShFlat.Count - 1);
        if (next == _shVIndex) return;
        _shVIndex = next;
        _shArmed = null;
        ShPaintViewer();
    }

    private static readonly string[] ShActions = { "收藏", "复制到剪贴板", "在文件夹中显示", "删除" };

    private void ShRunAction()
    {
        if (ShViewerShot() is not { } c) return;
        if (!_shViewerChrome) { _shViewerChrome = true; ShPaintViewer(); return; }
        switch (_shVAction)
        {
            case 0: ShToggleFav(c); break;
            case 1: _ = ShCopy(c); break;
            case 2:
                SystemInfo.Open("explorer.exe", $"/select,\"{c.Path}\"");
                Toast("正在打开文件夹");
                break;
            case 3:
                if (_shArmed != c.Path || NowMs - _shArmedAt > 3000)
                {
                    _shArmed = c.Path; _shArmedAt = NowMs;
                    ShPaintViewer();
                    int v = _shVersion;
                    After(3100, () => { if (v == _shVersion && _shArmed == c.Path && NowMs - _shArmedAt >= 3000) { _shArmed = null; if (_shViewer) ShPaintViewer(); } });
                    return;
                }
                _shArmed = null;
                ShDelete(c);
                break;
        }
    }

    private async Task ShCopy(Capture c)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(c.Path);
            var dp = new DataPackage();
            dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            dp.SetStorageItems(new IStorageItem[] { file });
            Clipboard.SetContent(dp);
            Toast("已复制到剪贴板");
        }
        catch (Exception ex) { Log.Warn($"shots copy: {ex.Message}"); Toast($"没有复制成：{ex.Message}"); }
    }

    private void ShDelete(Capture c)
    {
        if (!Captures.Delete(c)) { Toast("没有删掉：文件可能正被占用"); return; }
        Log.Info($"shots: recycled {c.Path}");
        if (c.CloudId is string cloudId)
        {
            // 只删这里的副本，并让 agent 以后不再下这一张；Steam 上的上传不动
            _ = HideCloudShot(cloudId);
            Toast("已从这里移除（Steam 上的上传还在）");
        }
        else Toast("已移到回收站");
        _shAll.Remove(c);
        SetShotCounts(_shAll);
        _shFavs.Remove(c.Path);
        Captures.SaveFavorites(_shFavs);
        int at = _shVIndex;
        _shTabs = ShBuildTabs();
        int ti = _shTabs.FindIndex(t => t.Key == _shTabKey);
        _shTab = Math.Max(0, ti >= 0 ? ti : _shTabs.FindIndex(t => t.Key == "all"));
        _shTabKey = ShCurrentTab?.Key;
        ShBuildUnits();
        if (ShFlat.Count == 0) { ShCloseViewer(); _shFocus = 0; ShBuildBody(); ShPaintAll(); return; }
        _shVIndex = Math.Clamp(at, 0, ShFlat.Count - 1);
        int f = _shUnits.FindIndex(u => u.Shots.Contains(ShFlat[_shVIndex]));
        _shFocus = Math.Max(0, f);
        ShBuildBody();
        ShPaintAll();
        ShPaintViewer();
    }

    // ---------------- 点击 ----------------

    private void ShClick(double sx, double sy)
    {
        float k = _ccK;
        float x = (float)sx / k, y = (float)sy / k;
        if (_shViewer) { ShViewerClick(x, y); return; }
        if (_shFilterOpen)
        {
            int row = ShFilterHit(x, y);
            if (row >= 0) { _shFilterFocus = row; ShFilterPick(); }
            else if (row == -1) ShCloseFilter();
            return;
        }
        if (y >= 26 && y <= 70)
            for (int i = 0; i < _shTabHits.Count; i++)
                if (x >= _shTabHits[i].X && x <= _shTabHits[i].X + _shTabHits[i].W)
                {
                    if (i != _shTab) ShSwitchTab(i - _shTab);
                    return;
                }
        int hit = ShHitUnit(x, y);
        if (hit < 0) return;
        if (hit == _shFocus) ShActivate(); else ShSetFocus(hit);
    }

    /// <summary>截图自测用（--state=shots0|1|2|v）：打开截图页，排版定在这一种（不存 ui.ini）。</summary>
    internal void ShotsTestState(int style)
    {
        if (!_ccOpen) return;
        if (_shOpen) CloseShots(instant: true);
        OpenShots();
        _shStyle = (ShStyle)style;
        ShRebuild(ShFocusedPath());
    }

    // ---------------- 小工具 ----------------

    private static readonly string[] ShWeek = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    private static string ShDay(DateTime t) =>
        (t.Year == DateTime.Now.Year ? "" : $"{t.Year} 年 ") + $"{t.Month} 月 {t.Day} 日 · {ShWeek[(int)t.DayOfWeek]}";

    private static string ShBytes(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : $"{Math.Max(1, b >> 10)} KB";

    private (int W, int H)? ShSize(Capture c)
    {
        if (!_shSizes.TryGetValue(c.Path, out var s)) _shSizes[c.Path] = s = Captures.ImageSize(c.Path);
        return s;
    }

    private string ShSizeText(Capture c) => ShSize(c) is { } s ? $"{s.W} × {s.H} · {ShBytes(c.Bytes)}" : ShBytes(c.Bytes);

    private Vector3KeyFrameAnimation ToV3(Vector3 to, double ms, Bezier ease, double delay = 0)
    {
        var a = G.C.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(1, to, G.Ease(ease));
        a.Duration = Gfx.Ms(ms);
        if (delay > 0) a.DelayTime = Gfx.Ms(delay);
        return a;
    }
}
