// 控制中心 → 截图（左下功能坞的「截图」）：自己的一页，不套 3:7 两栏。
// 底子和别的页一样：控制中心那层封面取色的动态背景、左上 24 px 粗标题、右上状态；标题右边一排游戏（LB / RB 换），当前项白字加下划线。
// 三种排版（设置 › 外观 › 截图页样式；页面里按 X 也能换），画法在 WallStage.Shots.Paint.cs：
// - 日历：左边一列大号日期，右边五列拼版，每天最新的一张占 2×2；连拍叠成一摞。
// - 放映：中间一张大图，左右露出上一张 / 下一张；下面一行读数，最底下按天分组的小图条。
// - 冲印：控制中心拍立得的相纸，放大到和放映差不多，左右两张斜着露一半。
// A 看大图（← → 换张、↑ ↓ 选操作、A 执行、X 藏起文字），Y 收藏，B 返回。删除进回收站，要连按两下 A。
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

    /// <summary>标题右边的一项：一款游戏 / 其他 / 收藏 / 全部。Key 用来换数据后找回同一项。</summary>
    private sealed record ShTab(string Key, string Name, List<Capture> Shots);

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

    private void OpenShots()
    {
        if (_cc == null) return;
        EnsureShVisuals();
        _shOpen = true;
        _shViewer = false;
        _shArmed = null;
        _shStyle = (ShStyle)Math.Clamp(_prefs.ShotsStyle, 0, 2);
        int v = ++_shVersion;
        _shFavs = Captures.LoadFavorites();
        _shTabKey = null;   // 每次打开都先落在当前游戏
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
        foreach (var s in new Visual[] { _shBody, _shTitle, _shTabsSprite }) _sh.Children.InsertAtTop(s);
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
        if (_shTabKey == null)
        {
            // 默认落在当前游戏（控制中心背景取色的那款）；它没有截图就落在最近有截图的那一项
            var cur = SelectedGame();
            int i = cur == null ? -1 : _shTabs.FindIndex(t => t.Key == cur.Id);
            _shTab = Math.Max(0, i);
        }
        else
        {
            int i = _shTabs.FindIndex(t => t.Key == _shTabKey);
            _shTab = i >= 0 ? i : Math.Clamp(_shTab, 0, Math.Max(0, _shTabs.Count - 1));
        }
        if (_shLoaded) _shTabKey = ShCurrentTab?.Key;   // 还没读完时只有一个空的「全部」，别把它记下来
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

    private List<ShTab> ShBuildTabs()
    {
        var tabs = new List<ShTab>();
        foreach (var g in _shAll.Where(c => c.GameId != null).GroupBy(c => c.GameId!).OrderByDescending(g => g.Max(c => c.Time)))
            tabs.Add(new ShTab(g.Key, g.First().Game, g.ToList()));
        var other = _shAll.Where(c => c.GameId == null).ToList();
        if (other.Count > 0) tabs.Add(new ShTab("other", "其他", other));
        var favs = _shAll.Where(c => _shFavs.Contains(c.Path)).ToList();
        if (favs.Count > 0) tabs.Add(new ShTab("fav", "收藏", favs));
        if (tabs.Count > 1 || tabs.Count == 0) tabs.Add(new ShTab("all", "全部", _shAll));
        return tabs;
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
        if (_shViewer) { ShRunAction(); return; }
        if (_shUnits.Count == 0) return;
        ShOpenViewer(ShFlat.IndexOf(_shUnits[_shFocus].Top));
    }

    private void ShBack()
    {
        if (_shViewer) { ShCloseViewer(); return; }
        CloseShots();
    }

    /// <summary>Y：收藏 / 取消收藏（日历里的连拍收藏最上面那张）。</summary>
    private void ShY()
    {
        var c = _shViewer ? ShViewerShot() : _shUnits.Count > 0 ? _shUnits[_shFocus].Top : null;
        if (c != null) ShToggleFav(c);
    }

    /// <summary>X：大图里藏起 / 露出文字；页面里换排版（存进 ui.ini）。</summary>
    private void ShX()
    {
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

    /// <summary>LB / RB：换游戏。大图里不换。</summary>
    private void ShSwitchTab(int delta)
    {
        if (_shViewer || _shTabs.Count < 2) return;
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
        if (_shTabKey == "fav" || oldCount != _shTabs.Count)
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
