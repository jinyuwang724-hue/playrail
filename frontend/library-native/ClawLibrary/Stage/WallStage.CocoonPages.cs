// 从左侧栏进的全屏页（性能 / 显示与声音 / 系统 / 设置）照 Cocoon 设置页重做（11-24 jinyu：用 Cocoon 的组件，不沿用旧页面的样式）。
// 结构照 jinyu 的 Cocoon 设置页截图和侧栏视频（只取尺寸和行为，不用 Cocoon 的代码或素材）：
//   左上「B ←」灰胶囊 + 页面标题；左列是分类（黑色实心图标 + 名称，选中的那项是白色圆角卡、名称紫色、下面一行灰色读数）；
//   右列是分组卡片（同二级页 RsPaintGroupAt），一行一项：开关（同二级页的拨动开关）、下拉（白色胶囊，A 展开选项卡片）、
//   滑块（「数值 − 轨道 +」，←→ 直接调）、带 ▸ 的进一步、只读值；焦点是首页同一套四角括号。
//   性能 / 显示与声音 / 系统的分类就是原来页面里的分组（功耗、CPU、风扇…）；设置页用它原来的分类。
// 数据、读写和各行的动作全部沿用原来的页面（PerfRow 列表），这里只换画法和手柄 / 触摸的走法。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // ---------------- 几何（首页的 1920×1200 设计单位） ----------------
    private const float CpTop = 150, CpBottom = 1186, CpLeftX = 44, CpLeftW = 562, CpListX = 636, CpListW = 1240;
    private const float CpRowH = 112, CpRowSubH = 140, CpRowSliderH = 160, CpSep = 4, CpGap = 36;
    private static readonly Color CpAccentInk = Color.FromArgb(255, 0x7A, 0x4C, 0xFF);

    private sealed class CpView
    {
        public required ContainerVisual Root, ListBox;
        public required SpriteVisual Bg, Head, Left, List, Press, Toast;
        /// <summary>左列每个分类的「选中样子」，各在各的位置，换分类时交叉淡入淡出。</summary>
        public required ContainerVisual Sel;
        public readonly List<SpriteVisual> Items = new();
        public readonly List<CompositionDrawingSurface> ItemS = new();
        public int SelOn = -1;
        public required CurSet Cur;
        public CompositionDrawingSurface? HeadS, LeftS, ListS, ToastS;
        public string SelKey = "";
        public int LastCat = -1;
        public string HeadKey = "", LeftKey = "", ListKey = "";
        public float Scroll, ListH;
        /// <summary>手指拖过列表：先不自动滚回焦点行。</summary>
        public bool Manual, OffsetInit;
        public int ToastGen;
        public List<(int I, float Y, float H, string? Sub)> Lay = new();
        public List<(string Name, string? Sub, int First)> Cats = new();
        public float Slot;
    }

    private readonly Dictionary<ContainerVisual, CpView> _cpViews = new();
    private CpView? _cpCompView;
    /// <summary>性能 / 显示与声音 / 系统：0 = 焦点在左列分类，1 = 在右列（设置页用它自己的 _stZone）。</summary>
    private int _cpZone;
    /// <summary>展开着的下拉：哪一行、焦点在第几个选项（-1 = 没有）。</summary>
    private int _cpDrop = -1, _cpDropSel;

    // 添加 / 编辑本地游戏页（_agOpen）是盖在设置页上的独立页面，输入归它自己
    private bool CpActive => _ccLight && _ccOpen && (_perfOpen || _dsOpen || _syOpen || _stOpen) && !GsOverlay && !_agOpen;
    private bool CpSectioned => _perfOpen || _dsOpen || _syOpen || _cpComp == "disp";   // 组合页两边用同一个行高，跨过去时左列不跳
    private int CpZone => _stOpen ? _stZone : _cpZone;

    private ContainerVisual? CpHost => _perfOpen ? _perf : _dsOpen ? _ds : _syOpen ? _sy : _stOpen ? _st : null;
    private string CpTitle => _cpComp == "disp" ? "显示与声音" : _perfOpen ? "性能" : _dsOpen ? "显示与声音" : _syOpen ? "系统" : "设置";
    /// <summary>组合页："disp" = 显示与声音和缩放设置。</summary>
    private string? _cpComp;

    /// <summary>页面刚打开：焦点从左列开始（Cocoon 进设置页时停在第一个分类）。</summary>
    private void CpReset() { _cpZone = 0; _cpComp = null; _cpDrop = -1; _cpTouchMode = false; foreach (var v in _cpViews.Values) { v.Scroll = 0; v.Manual = false; v.OffsetInit = false; v.HeadKey = v.LeftKey = v.ListKey = v.SelKey = ""; v.LastCat = -1; v.SelOn = -1; v.Sel.IsVisible = false; v.Cur.Shown = false; foreach (var c in v.Cur.All) { c.StopAnimation("Opacity"); c.Opacity = 0; } } }

    // ---------------- 行的分类 / 种类 ----------------

    private static bool CpIsToggle(PerfRow r) => r.Choices is { Length: 2 } c && c[0] == "关" && c[1] == "开";
    private static bool CpIsPick(PerfRow r) => r.Choices is { Length: > 0 } && !CpIsToggle(r) || r.Options is { Length: > 0 };
    private static string[]? CpOpts(PerfRow r) => r.Options ?? r.Choices;
    private static bool CpIsSlider(PerfRow r) => r.Numeric && r.Maximum > r.Minimum;
    private static bool CpFocusable(PerfRow r) => !r.Section && !r.Skip;

    /// <summary>左列分类：性能 / 显示 / 系统 = 原来的分组标题；设置 = 它自己的分类。</summary>
    private List<(string Name, string? Sub, int First)> CpCats(List<PerfRow> rows)
    {
        // First：≥ 0 = 这一页里分组标题所在的行；≤ -100 = 设置页的分类（-(编号 + 100)）
        var l = new List<(string, string?, int)>();
        if (_cpComp == "disp")
        {
            for (int i = 0; i < _dsRows.Count; i++)
                if (_dsRows[i].Section) l.Add((_dsRows[i].Title, null, i));
            l.Add((StCatsMain[3], null, -103));
            l.Add((StCatsMain[4], null, -104));
            return l;
        }
        if (_stOpen)
        {
            if (_gsItem != null) { var gc = StCats; for (int i = 0; i < gc.Length; i++) l.Add((gc[i], null, -(i + 100))); return l; }
            foreach (int id in CpSettingsIds) l.Add((StCatsMain[id], null, -(id + 100)));
            return l;
        }
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Section) l.Add((rows[i].Title, null, i));   // 11-25 jinyu：分类只显示名字，不带实时读数
        if (l.Count == 0) l.Add((CpTitle, null, -1));
        return l;
    }

    /// <summary>Magpie 和小黄鸭使用共享的设置页控件，在组合页显示。</summary>
    private static readonly int[] CpSettingsIds = { 0, 1, 2, 6, 7, 8, 9 };   // 账户与服务并进了游戏库（11-26）
    private static bool CpCatIsSt((string Name, string? Sub, int First) c) => c.First <= -100;
    private static int CpCatStId((string Name, string? Sub, int First) c) => -(c.First + 100);

    /// <summary>当前选中的分类（性能类页面：焦点行所在的分组）。</summary>
    private int CpCatIndex(List<PerfRow> rows, List<(string Name, string? Sub, int First)> cats)
    {
        if (_stOpen)
            return Math.Max(0, cats.FindIndex(c => _stBrowseMusic ? c.Name == DsSoundSection : CpCatIsSt(c) && CpCatStId(c) == _stCat));
        int f = SettingsFocus, k = 0;
        for (int c = 0; c < cats.Count; c++) if (cats[c].First >= 0 && cats[c].First <= f) k = c;
        return k;
    }

    /// <summary>换到第 to 个分类（左列）：设置页的分类、本页的分组，组合页（显示与声音）里两种之间切换时换底下的页面。</summary>
    private void CpGoCat(List<PerfRow> rows, List<(string Name, string? Sub, int First)> cats, int to)
    {
        var c = cats[to];
        if (CpCatIsSt(c))
        {
            int id = CpCatStId(c);
            if (!_stOpen) CpSwapToSettings(id);
            else StCategoryJump(id);
            _stZone = 0;
        }
        else if (_stOpen && _cpComp != null) CpSwapToDisplay(c.Name);
        else
        {
            int first = CpFirstIn(rows, c.First);
            if (first >= 0) SettingsFocus = first;
            _cpZone = 0;
            if (_dsOpen) { _stArmed = null; DsRefreshCategory(); }
        }
        if (CpHost is { } hh && _cpViews.TryGetValue(hh, out var vv)) { vv.Scroll = 0; vv.Manual = false; vv.OffsetInit = false; }
        CpRepaint();
    }

    /// <summary>组合页：显示 → 设置里的分类（同一个画面，底下换成设置页，不播开关动画）。</summary>
    private void CpSwapToSettings(int id)
    {
        CloseDisplay(instant: true);
        OpenSettingsPage();
        _stCat = id; _stZone = 0; _stArmed = null;
        BuildStRows();
        _stFocus = FirstStRow();
        if (_st != null) { _st.StopAnimation("Opacity"); _st.Opacity = 1; }
        _ = StRefresh();
        CpTouchMode(_cpTouchMode);
    }

    private void CpSwapToDisplay(string section)
    {
        CloseSettingsPage(instant: true);
        OpenDisplay(section);
        _cpZone = 0;
        if (_ds != null) { _ds.StopAnimation("Opacity"); _ds.Opacity = 1; }
    }

    /// <summary>右列要画的行：性能类页面只画当前分组；设置页全画（分组标题变成卡片上方的小字）。</summary>
    private List<int> CpVisible(List<PerfRow> rows, List<(string Name, string? Sub, int First)> cats, int cat)
    {
        var l = new List<int>();
        if (_stOpen) { for (int i = 0; i < rows.Count; i++) if (!rows[i].Skip) l.Add(i); return l; }
        int from = cats[cat].First < 0 ? 0 : cats[cat].First + 1;
        for (int i = from; i < rows.Count && !rows[i].Section; i++) if (!rows[i].Skip) l.Add(i);
        return l;
    }

    /// <summary>开关行后面跟的状态说明（「抑制自动唤醒」之类）放到标题下面一行。</summary>
    private static string? CpSub(PerfRow r) => CpIsToggle(r) && r.Value.Length > 0 ? r.Value : null;

    // ---------------- 画 ----------------

    private CpView CpEnsure(ContainerVisual host)
    {
        // 显示与声音（组合页）底下在显示页和设置页之间换：两边共用同一套画面（左列、选中卡、焦点框都不重来），
        // 换分类或打开音乐浏览器时沿用原来的选中卡和焦点画面。
        if (_cpComp == "disp" && _cpCompView is { } cv && (!_cpViews.TryGetValue(host, out var own) || own != cv))
        {
            if (own != null) host.Children.Remove(own.Root);
            (cv.Root.Parent as ContainerVisual)?.Children.Remove(cv.Root);
            host.Children.InsertAtTop(cv.Root);
            _cpViews[host] = cv;
            return cv;
        }
        if (_cpViews.TryGetValue(host, out var v)) { (v.Root.Parent as ContainerVisual)?.Children.Remove(v.Root); host.Children.InsertAtTop(v.Root); if (_cpComp == "disp") _cpCompView = v; return v; }
        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var g = G.C.CreateLinearGradientBrush();
        g.StartPoint = new Vector2(0, 0); g.EndPoint = new Vector2(0, 1);
        g.ColorStops.Add(G.C.CreateColorGradientStop(0, Color.FromArgb(255, 0xEF, 0xEF, 0xF4)));
        g.ColorStops.Add(G.C.CreateColorGradientStop(1, Color.FromArgb(255, 0xE2, 0xE3, 0xEA)));
        var bg = G.Sprite(g, relative: false);
        bg.Size = new Vector2(CsW, CsH);
        var box = G.Container(relative: false);
        box.Size = new Vector2(CsW, CsH);
        box.Clip = G.C.CreateInsetClip(CpListX - 40, CpTop - 14, 0, CsH - CpBottom);
        // 按下反馈：列表里那一行压一层淡灰（跟着列表滚，所以放在列表精灵里）
        var press = G.Sprite(G.Solid(Color.FromArgb(18, 30, 30, 60)), relative: false);
        press.Opacity = 0;
        v = new CpView { Root = root, ListBox = box, Bg = bg, Head = Sp(), Left = Sp(), Sel = G.Container(relative: false), List = Sp(), Press = press, Toast = Sp(), Cur = null! };
        box.Children.InsertAtTop(v.List);
        v.List.Children.InsertAtTop(press);
        foreach (var x in new Visual[] { bg, v.Head, v.Left, v.Sel, box }) root.Children.InsertAtTop(x);
        v.Cur = CurNew(root);
        v.Toast.Opacity = 0;
        root.Children.InsertAtTop(v.Toast);
        host.Children.InsertAtTop(root);
        _cpViews[host] = v;
        if (_cpComp == "disp") _cpCompView = v;
        return v;
    }

    /// <summary>四个页面的 Paint* 在浅色皮时改走这里。返回 true 表示画了（原来的画法跳过）。</summary>
    /// <remarks>
    /// 连按方向键时每一步都会进来：只有内容变了才重画列表 / 左列（按内容算的键比对），焦点移动只动焦点框和滚动，避免卡顿。
    /// </remarks>
    private bool CpPaint()
    {
        foreach (var cv in _cpViews.Values) cv.Root.IsVisible = false;
        if (_ccLight && CpHost is { } ch && _cpViews.TryGetValue(ch, out var shown)) shown.Root.IsVisible = true;
        if (!CpActive || CpHost is not { } host) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return CpPaintCore(host); }
        catch (Exception ex) { Log.Error("cp paint", ex); throw; }
        finally { double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; if (ms > 12) Log.Info($"cp paint {ms:0} ms"); }
    }

    private bool CpPaintCore(ContainerVisual host)
    {
        var v = CpEnsure(host);
        v.Root.IsVisible = true;
        v.Root.Scale = new Vector3(_ccK / 1.5f, _ccK / 1.5f, 1);
        var rows = SettingsRows;
        if (_cpDrop >= rows.Count || _cpDrop >= 0 && CpOpts(rows[_cpDrop]) == null) _cpDrop = -1;
        var cats = CpCats(rows);
        v.Cats = cats;
        int cat = Math.Clamp(CpCatIndex(rows, cats), 0, cats.Count - 1);
        CpPaintHead(v);
        // 左列：底图（所有分类 + 读数卡）只在内容变了时重画；选中的那张白卡单独一层，换分类时弹簧滑过去（连按跟手、不跳）
        string leftKey = $"{CpTitle}|{string.Join(",", cats.Select(c => c.Name))}|{CpPanelKey(cats[cat].Name)}";
        if (leftKey != v.LeftKey) { v.LeftKey = leftKey; CpPaintLeft(v, cats, cat); }
        CpPaintSel(v, cats, cat);
        // 换了分类：照 Cocoon，右列立刻换成新分类的内容、回到顶上（不延后、不做过渡）。
        // 以前连按时右列要停 140 ms 才重画：这段时间里焦点框和左列已经到了新分类，右列还是旧内容，看着就是错位 + 一卡
        v.LastCat = cat;
        var vis = CpVisible(rows, cats, cat);
        string listKey = CpListKey(rows, vis);
        if (listKey != v.ListKey)
        {
            v.Lay = CpLayout(rows, vis, out var groups);
            v.ListKey = listKey;
            CpPaintList(v, rows, groups);
        }
        else v.Lay = CpLayout(rows, vis, out _);
        CpScrollAndFocus(v, rows, cats, cat);
        return true;
    }

    /// <summary>列表内容的键：行的文字、值、选中项、数值、可用 / 改动标记，加上展开着的下拉。</summary>
    private string CpListKey(List<PerfRow> rows, List<int> vis)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(_cpDrop).Append('|').Append(CpTitle).Append('|');
        foreach (int i in vis)
        {
            var r = rows[i];
            sb.Append(r.Section ? '#' : '-').Append(r.Title).Append('\u0001').Append(r.Value).Append('\u0001').Append(r.Choice).Append('\u0001').Append(r.Number)
              .Append(r.Disabled ? 'd' : ' ').Append(r.Changed ? 'c' : ' ').Append(r.Indent).Append('\u0002');
        }
        return sb.ToString();
    }

    private CompositionDrawingSurface CpSurf(ref CompositionDrawingSurface? s, SpriteVisual v, float x, float y, float w, float h)
    {
        float R = CsRaster;
        int pw = (int)Math.Ceiling(w * R), ph = (int)Math.Ceiling(h * R);
        if (s == null || (int)s.Size.Width != pw || (int)s.Size.Height != ph) { s?.Dispose(); s = G.Surface(pw, ph); }
        ((CompositionSurfaceBrush)v.Brush).Surface = s;
        v.Size = new Vector2(w, h);
        v.Offset = new Vector3(x, y, 0);
        return s;
    }

    /// <summary>左上「B ←」胶囊 + 标题（同二级页的胶囊，挪到页面左上角）。</summary>
    private void CpPaintHead(CpView v)
    {
        string title = CpTitle;
        if (v.HeadKey == title) return;
        v.HeadKey = title;
        float x0 = 20, y0 = 20, w = 1000, h = 120;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.HeadS, v.Head, x0, y0, w, h));
        ds.Clear(Colors.Transparent);
        // RsPaintPills 画在 (134,104)：平移到 (40,36)
        ds.Transform = Matrix3x2.CreateTranslation(40 - 134 - x0, 36 - 104 - y0) * Matrix3x2.CreateScale(CsRaster);
        RsPaintPills(ds, noY: true);
        ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
        using var f = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 48, FontWeight = RsWt(title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        ds.DrawText(title, new Windows.Foundation.Rect(198, 36, 780, 74), RmInk, f);
    }

    /// <summary>左列：分类（选中 = 白卡、紫字、下面一行读数）；性能类页面在分类下面再放一张实时读数卡（原来左栏的图表）。</summary>
    private void CpPaintLeft(CpView v, List<(string Name, string? Sub, int First)> cats, int sel)
    {
        float x0 = 0, y0 = CpTop - 10, w = CpLeftX + CpLeftW + 30, h = CsH - y0;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.LeftS, v.Left, x0, y0, w, h));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(-x0, -y0) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        float slot = MathF.Min(CpSectioned ? 136 : 124, (CpBottom - CpTop) / Math.Max(1, cats.Count));
        v.Slot = slot;
        for (int i = 0; i < cats.Count; i++)
        {
            float y = CpTop + i * slot, cy = y + slot / 2;
            bool on = false;   // 选中的样子在单独一层（CpPaintSel）
            string? sub = null;
            float k = MathF.Min(1, slot / 124);
            CpIcon(ds, cats[i].Name, CpLeftX + 64, cy, on ? CpAccentInk : RmInk, 0.82f * k);
            string name = cats[i].Name;
            using (var f = new CanvasTextFormat { FontFamily = RmFont(name), FontSize = slot >= 100 ? 38 : 32, FontWeight = RsWt(name), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                ds.DrawText(name, new Windows.Foundation.Rect(CpLeftX + 126, sub != null ? y + 14 : y, CpLeftW - 150, sub != null ? slot * 0.5f : slot), on ? CpAccentInk : RmInk, f);
            if (sub != null)
            {
                using var sf = new CanvasTextFormat { FontFamily = RmFont(sub), FontSize = 27, FontWeight = RsWt(sub, 500), WordWrapping = CanvasWordWrapping.NoWrap };
                ds.DrawText(sub, CpLeftX + 126, y + slot * 0.5f + 6, RsSub, sf);
            }
        }
        // 性能 / 显示与声音 / 系统：分类下面一张白卡放实时读数（WallStage.CocoonPanels.cs）
        if (!(_perfOpen || _syOpen)) return;   // 11-25 jinyu：显示与声音不放读数卡
        float top = CpTop + cats.Count * slot + 24, bottom = CpBottom;
        if (bottom - top < 220) return;
        CpCard(ds, CpLeftX, top, CpLeftW, bottom - top, null);
        using var card = CanvasGeometry.CreateRoundedRectangle(ds.Device, CpLeftX, top, CpLeftW, bottom - top, 30, 30);
        using (ds.CreateLayer(1, card)) CpPanel(ds, CpLeftX, top, CpLeftW, bottom - top, cats[sel].Name);
    }

    /// <summary>
    /// 排右列：每行的 y、高、灰色说明；遇到分组标题（设置页）另起一张卡片。
    /// 展开着的下拉（Cocoon 的做法，同游戏编辑页）：那一行在原地变高，选项卡片整个装进行里，下面的行往下推。
    /// </summary>
    private List<(int I, float Y, float H, string? Sub)> CpLayout(List<PerfRow> rows, List<int> vis, out List<(float Y0, float Y1, List<float> Seps)> groups)
    {
        var lay = new List<(int, float, float, string?)>();
        groups = new();
        float y = 0;
        bool open = false;
        foreach (int i in vis)
        {
            var r = rows[i];
            if (r.Section)
            {
                // 11-25 jinyu：不要「开机」「打开游戏库时」这类小标题，分组之间只留一段空隙，靠卡片本身分开
                if (open) { y += CpGap; open = false; }
                continue;
            }
            string? sub = CpSub(r);
            float h = CpBaseH(r, sub);
            if (i == _cpDrop && CpOpts(r) is { Length: > 0 } o) h += o.Length * CpOptH + 34;
            if (!open) { groups.Add((y, y + h, new List<float>())); open = true; }
            else { var g = groups[^1]; g.Seps.Add(y - CpSep); groups[^1] = (g.Y0, y + h, g.Seps); }
            lay.Add((i, y, h, sub));
            y += h + CpSep;
        }
        return lay;
    }

    /// <summary>
    /// 选中的分类（照 Cocoon 设置页左栏）：每个分类自己一张「选中样子」（白卡 + 紫色图标和名字），固定在自己的位置上，
    /// 换分类时旧的淡出、新的淡入（Cocoon：settingsNavSelectedProgress，临界阻尼弹簧 stiffness 1500，约 150 ms），
    /// 不滑动 —— 连按时不会出现「新名字先画在旧位置再滑过去」的错位；移动感只交给焦点框（它自己弹簧过去）。
    /// </summary>
    private void CpPaintSel(CpView v, List<(string Name, string? Sub, int First)> cats, int sel)
    {
        if (sel < 0 || sel >= cats.Count || v.Slot <= 0) { v.Sel.IsVisible = false; return; }
        float slot = v.Slot, pad = 30, w = CpLeftW + pad * 2, h = slot + pad * 2, R = CsRaster;
        string key = $"{string.Join(",", cats.Select(c => c.Name))}|{slot}|{R}";
        if (key != v.SelKey)
        {
            v.SelKey = key;
            v.Sel.Children.RemoveAll();
            foreach (var s in v.ItemS) s.Dispose();
            v.ItemS.Clear(); v.Items.Clear();
            int pw = (int)Math.Ceiling(w * R), ph = (int)Math.Ceiling(h * R);
            for (int i = 0; i < cats.Count; i++)
            {
                var surf = G.Surface(pw, ph);
                var sp = G.Sprite(G.C.CreateSurfaceBrush(surf), relative: false);
                sp.Size = new Vector2(w, h);
                sp.Offset = new Vector3(CpLeftX - pad, CpTop + i * slot - pad, 0);
                sp.Opacity = 0;
                using (var ds = CanvasComposition.CreateDrawingSession(surf))
                {
                    ds.Clear(Colors.Transparent);
                    ds.Transform = Matrix3x2.CreateScale(R);
                    ds.Antialiasing = CanvasAntialiasing.Antialiased;
                    CpCard(ds, pad, pad + 4, CpLeftW, slot - 8, null, MathF.Min(30, (slot - 8) / 2));
                    float cy = pad + slot / 2, k = MathF.Min(1, slot / 124);
                    CpIcon(ds, cats[i].Name, pad + 64, cy, CpAccentInk, 0.82f * k);
                    string name = cats[i].Name;
                    using var f = new CanvasTextFormat { FontFamily = RmFont(name), FontSize = slot >= 100 ? 38 : 32, FontWeight = RsWt(name), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
                    ds.DrawText(name, new Windows.Foundation.Rect(pad + 126, pad, CpLeftW - 150, slot), CpAccentInk, f);
                }
                v.Sel.Children.InsertAtTop(sp);
                v.Items.Add(sp); v.ItemS.Add(surf);
            }
            v.SelOn = -1;
        }
        bool snap = !v.Sel.IsVisible || v.SelOn < 0 || Reduced;
        v.Sel.IsVisible = true;
        if (sel == v.SelOn) return;
        for (int i = 0; i < v.Items.Count; i++)
        {
            float to = i == sel ? 1 : 0;
            var it = v.Items[i];
            if (snap) { it.StopAnimation("Opacity"); it.Opacity = to; }
            else if (i == sel || i == v.SelOn || it.Opacity > 0.001f) it.StartAnimation("Opacity", CsS(to, 150, Bezier.EaseOut));
        }
        v.SelOn = sel;
    }

    private static float CpBaseH(PerfRow r, string? sub) => CpIsSlider(r) ? CpRowSliderH : sub != null ? CpRowSubH : CpRowH;

    private const float CpOptH = 88, CpPickW = 400;

    /// <summary>展开的下拉里第 k 个选项的方框（列表内坐标）：胶囊正下方。</summary>
    private static (float X, float Y, float W, float H) CpOptRect(float rowY, int k) =>
        (CpListW - 34 - CpPickW, rowY + CpRowH / 2 + 40 + 14 + k * CpOptH, CpPickW, CpOptH);

    private void CpPaintList(CpView v, List<PerfRow> rows, List<(float Y0, float Y1, List<float> Seps)> groups)
    {
        float H = v.Lay.Count == 0 ? 200 : v.Lay[^1].Y + v.Lay[^1].H;
        v.ListH = H;
        float pad = 16;
        // 只换画布和尺寸，不写 Offset：滚动的弹簧可能正在走，写一下就停了（位置由 CpScrollAndFocus / 触摸管）
        float R0 = CsRaster, lw = CpListW + pad * 2, lh = H + pad * 2 + 40;
        int pw = (int)Math.Ceiling(lw * R0), ph = (int)Math.Ceiling(lh * R0);
        if (v.ListS == null || (int)v.ListS.Size.Width != pw || (int)v.ListS.Size.Height != ph) { v.ListS?.Dispose(); v.ListS = G.Surface(pw, ph); ((CompositionSurfaceBrush)v.List.Brush).Surface = v.ListS; }
        v.List.Size = new Vector2(lw, lh);
        using var ds = CanvasComposition.CreateDrawingSession(v.ListS);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        foreach (var (y0, y1, seps) in groups) CpCard(ds, 0, y0, CpListW, y1 - y0, seps);
        if (v.Lay.Count == 0)
        {
            using var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 32, VerticalAlignment = CanvasVerticalAlignment.Center };
            ds.DrawText(_perfAgentDown ? "连不上工具箱后台" : "正在读取…", new Windows.Foundation.Rect(40, 0, 900, 120), RsSub, f);
        }
        foreach (var (i, y, h, sub) in v.Lay) CpPaintRow(ds, rows[i], i, y, h, sub);
    }

    private void CpPaintRow(CanvasDrawingSession ds, PerfRow r, int index, float y, float h, string? sub)
    {
        h = CpBaseH(r, sub);   // 展开的下拉：行变高了，但标题和胶囊还在原来的高度里
        float cy = y + h / 2, R = CpListW - 34, tx = 44 + r.Indent * 1.5f;
        using var layer = r.Disabled ? ds.CreateLayer(0.4f) : null;
        if (r.Changed) ds.FillCircle(24, cy, 6, CpAccentInk);
        float right = CpIsSlider(r) ? R : CpIsPick(r) ? R - 420 : CpIsToggle(r) ? R - 150 : R - 380;
        if (CpIsSlider(r))
        {
            // 两层（Cocoon「Highlight thickness」）：上面标题，下面「数值 − 轨道 +」；左边留给图标
            using var tf = new CanvasTextFormat { FontFamily = RmFont(r.Title), FontSize = 36, FontWeight = RsWt(r.Title), WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(r.Title, new Windows.Foundation.Rect(tx, y + 18, R - tx, 52), RmInk, tf);
        }
        else if (sub != null)
        {
            using (var tf = new CanvasTextFormat { FontFamily = RmFont(r.Title), FontSize = 36, FontWeight = RsWt(r.Title), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                ds.DrawText(r.Title, new Windows.Foundation.Rect(tx, y + 20, right - tx, 52), RmInk, tf);
            using (var sf = new CanvasTextFormat { FontFamily = RmFont(sub), FontSize = 26, FontWeight = RsWt(sub, 500), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
                ds.DrawText(sub, new Windows.Foundation.Rect(tx, y + 78, right - tx, 40), RsSub, sf);
        }
        else
        {
            using var tf = new CanvasTextFormat { FontFamily = RmFont(r.Title), FontSize = 36, FontWeight = RsWt(r.Title), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(r.Title, new Windows.Foundation.Rect(tx, y, Math.Max(200, right - tx), h), RmInk, tf);
        }
        if (CpIsToggle(r))
        {
            // 拨动开关（同二级页）
            bool on = r.Choice == 1;
            float x = R - 120, ty = cy - 38;
            if (on)
            {
                using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(x, ty), EndPoint = new Vector2(x + 120, ty + 76) };
                ds.FillRoundedRectangle(x, ty, 120, 76, 38, 38, br);
            }
            else
            {
                ds.FillRoundedRectangle(x, ty, 120, 76, 38, 38, Color.FromArgb(255, 0xE4, 0xE5, 0xE9));
                ds.DrawRoundedRectangle(x + 2, ty + 2, 116, 72, 36, 36, Color.FromArgb(255, 0x9A, 0x9C, 0xA3), 4);
            }
            RsSoftShadow(ds, (on ? x + 82 : x + 38) - 27, cy - 27 + 3, 54, 54, 27, 4, 0.22f);
            ds.FillCircle(on ? x + 82 : x + 38, cy, 27, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
        }
        else if (CpIsPick(r))
        {
            var opts = CpOpts(r)!;
            string val = r.Choice >= 0 && r.Choice < opts.Length ? opts[r.Choice] : r.Value;
            bool open = _cpDrop == index;
            RsPaintPickPill(ds, R - CpPickW, cy - 40, CpPickW, 80, val, open, 32);
            if (open)
            {
                // 展开：选项卡片装在这一行里、胶囊正下方（Cocoon 的下拉）
                var (ox, oy, ow, oh) = CpOptRect(y, 0);
                var tmp = new RsRow { Title = r.Title, Options = opts, Current = () => r.Choice };
                RsPaintOptionCard(ds, tmp, ox, oy, ow, oh, 32);
            }
        }
        else if (CpIsSlider(r))
        {
            // 下层：「数值 − 轨道 +」
            float frac = Math.Clamp((r.Number - r.Minimum) / (float)Math.Max(1, r.Maximum - r.Minimum), 0, 1);
            var (mx, x0, x1, px) = CpSliderX(tx, R);
            cy = y + h - 44;
            using (var vf = new CanvasTextFormat { FontFamily = RmFont(r.Value), FontSize = 30, FontWeight = RsWt(r.Value, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(r.Value, new Windows.Foundation.Rect(tx, cy - 24, mx - tx - 20, 48), RsSub, vf);
            ds.FillRoundedRectangle(mx - 16, cy - 4, 32, 8, 4, 4, RmInk);
            ds.FillRoundedRectangle(x0, cy - 5, x1 - x0, 10, 5, 5, Color.FromArgb(255, 0xDC, 0xDC, 0xE3));
            if (frac > 0)
            {
                using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, Color.FromArgb(255, 0x73, 0x40, 0xFF)) { StartPoint = new Vector2(x0, 0), EndPoint = new Vector2(x1, 0) };
                ds.FillRoundedRectangle(x0, cy - 5, (x1 - x0) * frac, 10, 5, 5, br);
            }
            float kx = x0 + (x1 - x0) * frac;
            RsSoftShadow(ds, kx - 24, cy - 24 + 3, 48, 48, 24, 5, 0.3f);
            using (var kb = new CanvasLinearGradientBrush(ds.Device, Color.FromArgb(255, 0x9A, 0x9C, 0xFF), Color.FromArgb(255, 0x6F, 0x3C, 0xFF)) { StartPoint = new Vector2(kx - 16, cy - 24), EndPoint = new Vector2(kx + 16, cy + 24) })
                ds.FillCircle(kx, cy, 24, kb);
            ds.FillRoundedRectangle(px - 16, cy - 4, 32, 8, 4, 4, RmInk);
            ds.FillRoundedRectangle(px - 4, cy - 16, 8, 32, 4, 4, RmInk);
        }
        else if (r.Numeric)
        {
            // 一档一档切的数值（排序位置、备份…）：Cocoon「Rows per Page」那种灰胶囊「−  值  +」，←→ 切
            var (px0, pw) = CpStepperX(ds, r.Value, R);
            ds.FillRoundedRectangle(px0, cy - 38, pw, 76, 38, 38, Color.FromArgb(255, 0xE6, 0xE7, 0xEC));
            ds.FillRoundedRectangle(px0 + 30, cy - 4, 28, 8, 4, 4, RmInk);
            ds.FillRoundedRectangle(px0 + pw - 58, cy - 4, 28, 8, 4, 4, RmInk);
            ds.FillRoundedRectangle(px0 + pw - 48, cy - 14, 8, 28, 4, 4, RmInk);
            using var vf = new CanvasTextFormat { FontFamily = RmFont(r.Value), FontSize = 30, FontWeight = RsWt(r.Value), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(r.Value, new Windows.Foundation.Rect(px0 + 70, cy - 38, pw - 140, 76), RmInk, vf);
        }
        else
        {
            bool nav = r.Sub;   // 11-26 jinyu：▸ 只给打开子页面的行；执行动作的行不加提示
            string v = r.Value.TrimEnd('›', ' ');   // 原来值里自带的 › 不要
            if (v.Length > 0)
            {
                using var vf = new CanvasTextFormat { FontFamily = RmFont(v), FontSize = 32, FontWeight = RsWt(v, 500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
                ds.DrawText(v, new Windows.Foundation.Rect(R - 620, y, 620 - (nav ? 44 : 0), h), RsSub, vf);
            }
            if (nav)
            {
                using var pb = new CanvasPathBuilder(ds.Device);
                pb.BeginFigure(R - 10, cy - 9); pb.AddLine(R, cy); pb.AddLine(R - 10, cy + 9); pb.EndFigure(CanvasFigureLoop.Closed);
                using var tri = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(tri, RmInk);
                ds.DrawGeometry(tri, RmInk, 3, RwRoundStroke);
            }
        }
    }

    /// <summary>滑块下层的横向位置（列表内坐标）：− 的中心、轨道两端、+ 的中心。</summary>
    private static (float Minus, float X0, float X1, float Plus) CpSliderX(float tx, float R) => (tx + 170, tx + 222, R - 64, R - 16);

    /// <summary>步进胶囊的左边和宽（右对齐到 R）。</summary>
    private static (float X, float W) CpStepperX(CanvasDrawingSession ds, string v, float R) { float w = Math.Max(240, CpTextW(ds, v, 30) + 160); return (R - w, w); }

    private static float CpTextW(CanvasDrawingSession ds, string s, float size)
    {
        using var f = new CanvasTextFormat { FontFamily = RmFont(s), FontSize = size, FontWeight = RsWt(s, 500), WordWrapping = CanvasWordWrapping.NoWrap };
        using var l = new CanvasTextLayout(ds, s, f, 2000, 200);
        return (float)l.LayoutBounds.Width;
    }

    private static void CpChev(CanvasDrawingSession ds, float x, float cy, bool left)
    {
        using var pb = new CanvasPathBuilder(ds.Device);
        float d = left ? 1 : -1;
        pb.BeginFigure(x + 8 * d, cy - 12); pb.AddLine(x, cy); pb.AddLine(x + 8 * d, cy + 12);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var g = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(g, RsSub, 4, RwRoundStroke);
    }

    /// <summary>
    /// 滚到焦点行露出来（上下各留 60；展开的下拉整行露出来），焦点框到焦点行 / 展开的选项 / 焦点分类。
    /// 手指拖过列表后（Manual）不再自动滚，直到下一次用手柄走焦点。
    /// </summary>
    private void CpScrollAndFocus(CpView v, List<PerfRow> rows, List<(string Name, string? Sub, int First)> cats, int cat)
    {
        int f = _cpDrop >= 0 ? _cpDrop : SettingsFocus;
        var hit = v.Lay.FirstOrDefault(e => e.I == f);
        bool inList = CpZone == 1 && hit.H > 0;
        float view = CpBottom - CpTop, max = Math.Max(0, v.ListH - view + 30), target = Math.Clamp(v.Scroll, 0, max);
        if (inList && !(v.Manual && _cpTouchMode))
        {
            float top = hit.Y - 60, bottom = hit.Y + hit.H + 60;
            if (_cpDrop >= 0) { var (_, oy, _, oh) = CpOptRect(hit.Y, _cpDropSel); top = Math.Min(top, oy - 60); bottom = oy + oh + 80; }
            if (bottom > target + view) target = bottom - view;
            if (top < target) target = top;
            target = Math.Clamp(target, 0, max);
        }
        // 直接写 Offset 会被还在走的滚动弹簧盖掉（上一个分类滚到一半时换分类，新内容就停在旧的滚动位置上）：用 RwJump
        if (!v.OffsetInit) { v.OffsetInit = true; v.Scroll = target; RwJump(v.List, new Vector3(CpListX - 16, CpTop - 16 - target, 0)); }
        else if (MathF.Abs(target - v.Scroll) > 0.5f)
        {
            v.Scroll = target;
            var to = new Vector3(CpListX - 16, CpTop - 16 - target, 0);
            if (Reduced) RwJump(v.List, to); else v.List.StartAnimation("Offset", RwSpring(to));
        }
        if (_cpTouchMode) return;   // 触摸操作时不显示焦点框
        bool first = !v.Cur.Shown;
        if (inList && _cpDrop >= 0)
        {
            var (ox, oy, ow, oh) = CpOptRect(hit.Y, _cpDropSel);
            CurToCocoon(v.Cur, CpListX + ox, CpTop + oy - target, ow, oh, 24, move: !first);
        }
        else if (inList) CurToCocoon(v.Cur, CpListX, CpTop + hit.Y - target, CpListW, hit.H, 30, move: !first);
        else if (CpZone == 0 && cats.Count > 0) CurToCocoon(v.Cur, CpLeftX, CpTop + cat * v.Slot + 4, CpLeftW, v.Slot - 8, MathF.Min(30, (v.Slot - 8) / 2), move: !first);
        else { if (v.Cur.Shown) CurShow(v.Cur, false, 60); return; }
        if (first) CurShow(v.Cur, true, 140, 60);
    }

    /// <summary>左列读数卡内容的键（数据没变就不重画）。</summary>
    private string CpPanelKey(string cat)
    {
        if (_perfOpen)
        {
            var v = _perfView;
            double? pkg = PerfDisplayPower();
            var last = _perfPower.Count > 0 ? _perfPower.Last().W : 0;
            return $"{cat}|{pkg:0}|{Num(v?["fan"]?["rpm1"]):0}|{Num(v?["live"]?["cpuTemp"]):0}|{_cpuFreq?.Mhz:0}|{_perfPowerSeq}|{last:0.0}|{_perfCurveEdit}|{_perfPoint}|{(_perfS == null ? "" : PerfPresetText())}|{string.Join(",", PerfCurve(Str(_perfS?["fan"]?["mode"]) ?? "balanced"))}";
        }
        if (_dsOpen) return _dsView?.ToJsonString() ?? "";
        if (_syOpen) { var b = SystemInfo.Battery(); return $"{b.Percent}|{b.OnAc}|{_syView?["battery"]?.ToJsonString()}"; }
        return "";
    }

    // ---------------- 提示（11-25 jinyu：不用旧的系统通知条；Cocoon 那种页面底部的白色小胶囊） ----------------

    /// <summary>页面开着时的提示：底部居中一颗白胶囊（左边一个圆点图标），上浮淡入，2.4 s 后淡出。</summary>
    private bool CpToast(string text)
    {
        if (!CpActive || CpHost is not { } host || !_cpViews.TryGetValue(host, out var v)) return false;
        float R = CsRaster, h = 84, pad = 30;
        float tw;
        using (var f0 = new CanvasTextFormat { FontFamily = RmFont(text), FontSize = 30, FontWeight = RsWt(text, 500), WordWrapping = CanvasWordWrapping.NoWrap })
        using (var l0 = new CanvasTextLayout(G.Device, text, f0, 1400, 100)) tw = (float)l0.LayoutBounds.Width;
        float w = Math.Min(1500, tw + 130), x = CsW / 2 - w / 2, y = CpBottom - h - 30;
        string kind = NoticeKind(text);
        using (var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.ToastS, v.Toast, x - pad, y - pad, w + pad * 2, h + pad * 2)))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - x, pad - y) * Matrix3x2.CreateScale(R);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            CpCard(ds, x, y, w, h, null, h / 2);
            var c1 = kind == "error" ? Color.FromArgb(255, 0xFF, 0x7A, 0x84) : kind == "warning" ? Color.FromArgb(255, 0xFF, 0xC1, 0x6B) : RwC1;
            var c2 = kind == "error" ? RmRed : kind == "warning" ? Color.FromArgb(255, 0xF0, 0x8A, 0x24) : RwC2;
            using (var br = new CanvasLinearGradientBrush(ds.Device, c1, c2) { StartPoint = new Vector2(x + 26, y + 22), EndPoint = new Vector2(x + 66, y + 62) })
                ds.FillCircle(x + 46, y + h / 2, 18, br);
            ds.FillCircle(x + 46, y + h / 2, 6, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
            using var f = new CanvasTextFormat { FontFamily = RmFont(text), FontSize = 30, FontWeight = RsWt(text, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            ds.DrawText(text, new Windows.Foundation.Rect(x + 84, y, w - 110, h), RmInk, f);
        }
        int gen = ++v.ToastGen;
        var home = v.Toast.Offset;
        if (Reduced) v.Toast.Opacity = 1;
        else
        {
            v.Toast.Offset = home + new Vector3(0, 24, 0);
            v.Toast.StartAnimation("Offset", CsV3(home, 220, new Bezier(0.2f, 0.8f, 0.2f, 1f)));
            v.Toast.StartAnimation("Opacity", CsS(1, 160, Bezier.EaseOut));
        }
        After(2400, () => { if (v.ToastGen == gen) v.Toast.StartAnimation("Opacity", CsS(0, 220, Bezier.EaseOut)); });
        return true;
    }

    // ---------------- 图标（黑色实心，Cocoon 设置页左列那样） ----------------

    private static void CpIcon(CanvasDrawingSession ds, string cat, float cx, float cy, Color ink, float k)
    {
        string? rs = cat switch
        {
            "功耗" => "power", "Magpie" => "magpie", "小黄鸭" => "duck", "关于" => "info",
            "游戏库" => "stack", "外观" or "外观与主题" => "artwork", "性能显示" => "sliders", "更新" => "scrape", _ => null,
        };
        if (rs != null) { RsGlyph(ds, rs, cx, cy, ink, k); return; }
        var t = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale(k, k, new Vector2(cx, cy)) * t;
        var dev = ds.Device;
        var paper = Color.FromArgb(255, 0xFA, 0xFA, 0xFA);
        switch (cat)
        {
            case "CPU": case "显卡":
                ds.FillRoundedRectangle(cx - 22, cy - 22, 44, 44, 8, 8, ink);
                for (int s = -1; s <= 1; s++)
                {
                    ds.FillRoundedRectangle(cx + s * 13 - 3, cy - 32, 6, 12, 3, 3, ink);
                    ds.FillRoundedRectangle(cx + s * 13 - 3, cy + 20, 6, 12, 3, 3, ink);
                    ds.FillRoundedRectangle(cx - 32, cy + s * 13 - 3, 12, 6, 3, 3, ink);
                    ds.FillRoundedRectangle(cx + 20, cy + s * 13 - 3, 12, 6, 3, 3, ink);
                }
                ds.FillRoundedRectangle(cx - 9, cy - 9, 18, 18, 4, 4, paper);
                break;
            case "风扇":
                for (int b = 0; b < 4; b++)
                {
                    var m = Matrix3x2.CreateRotation(b * MathF.PI / 2 + 0.3f, new Vector2(cx, cy));
                    using var blade = CanvasGeometry.CreateEllipse(dev, cx, cy - 16, 11, 17).Transform(m);
                    ds.FillGeometry(blade, ink);
                }
                ds.FillCircle(cx, cy, 8, paper);
                ds.FillCircle(cx, cy, 5, ink);
                break;
            case "显示":
                ds.FillRoundedRectangle(cx - 32, cy - 24, 64, 40, 6, 6, ink);
                ds.FillRoundedRectangle(cx - 4, cy + 14, 8, 12, 2, 2, ink);
                ds.FillRoundedRectangle(cx - 18, cy + 24, 36, 7, 3.5f, 3.5f, ink);
                break;
            case DsSoundSection:
            {
                using var pb = new CanvasPathBuilder(dev);
                pb.BeginFigure(cx - 28, cy - 10); pb.AddLine(cx - 16, cy - 10); pb.AddLine(cx + 2, cy - 26); pb.AddLine(cx + 2, cy + 26); pb.AddLine(cx - 16, cy + 10); pb.AddLine(cx - 28, cy + 10);
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(g, ink);
                using var arc = new CanvasPathBuilder(dev);
                arc.BeginFigure(cx + 14, cy - 14); arc.AddArc(new Vector2(cx + 14, cy + 14), 20, 20, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                arc.EndFigure(CanvasFigureLoop.Open);
                using var ag = CanvasGeometry.CreatePath(arc);
                ds.DrawGeometry(ag, ink, 6, RwRoundStroke);
                break;
            }
            case "电池与电源": case "电池":
                ds.FillRoundedRectangle(cx - 30, cy - 17, 54, 34, 7, 7, ink);
                ds.FillRoundedRectangle(cx + 26, cy - 7, 6, 14, 2, 2, ink);
                ds.FillRoundedRectangle(cx - 24, cy - 11, 30, 22, 3, 3, paper);
                ds.FillRoundedRectangle(cx - 21, cy - 8, 24, 16, 2, 2, ink);
                break;
            case "睡眠与唤醒":
            {
                using var moon = CanvasGeometry.CreateCircle(dev, cx, cy, 26);
                using var bite = CanvasGeometry.CreateCircle(dev, cx + 13, cy - 11, 21);
                using var g = moon.CombineWith(bite, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
                ds.FillGeometry(g, ink);
                break;
            }
            case "通用":
                ds.FillCircle(cx, cy, 22, ink);
                for (int b = 0; b < 8; b++)
                {
                    var m = Matrix3x2.CreateRotation(b * MathF.PI / 4, new Vector2(cx, cy));
                    using var tooth = CanvasGeometry.CreateRoundedRectangle(dev, cx - 6, cy - 31, 12, 14, 3, 3).Transform(m);
                    ds.FillGeometry(tooth, ink);
                }
                ds.FillCircle(cx, cy, 9, paper);
                break;
            case "账户与服务":
                ds.FillCircle(cx, cy - 12, 13, ink);
                ds.FillRoundedRectangle(cx - 24, cy + 6, 48, 24, 12, 12, ink);
                break;
            case "开机自启":
                ds.DrawCircle(cx, cy + 3, 22, ink, 7);
                ds.FillRoundedRectangle(cx - 9, cy - 30, 18, 22, 4, 4, paper);
                ds.FillRoundedRectangle(cx - 3.5f, cy - 28, 7, 26, 3.5f, 3.5f, ink);
                break;
            case "禁用第三方软件":
                ds.DrawCircle(cx, cy, 25, ink, 7);
                ds.DrawLine(cx - 17, cy + 17, cx + 17, cy - 17, ink, 7, RwRoundStroke);
                break;
            case "维护":
                ds.DrawLine(cx - 20, cy + 20, cx + 8, cy - 8, ink, 10, RwRoundStroke);
                ds.FillCircle(cx + 14, cy - 14, 15, ink);
                ds.FillCircle(cx + 22, cy - 22, 7, paper);
                break;
            default:
                ds.FillCircle(cx, cy, 10, ink);
                break;
        }
        ds.Transform = t;
    }

    // ---------------- 输入 ----------------

    private PerfRow? CpRow => SettingsFocus is int f && f >= 0 && f < SettingsRows.Count ? SettingsRows[f] : null;

    private void CpRepaint() => SettingsPaint();

    private void CpStep(PerfRow r, int d)
    {
        if (r.Disabled) { Cue(Sfx.Bump); return; }
        int next = Math.Clamp(r.Number + d * Math.Max(1, r.Increment), r.Minimum, r.Maximum);
        if (next == r.Number) { Cue(Sfx.Bump); return; }
        float f = (next - r.Minimum) / (float)(r.Maximum - r.Minimum);
        Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown, MathF.Pow(2, f - 0.5f));
        if (r.SetNumber != null) r.SetNumber(next); else r.Step?.Invoke(d);
    }

    private void CpChoose(PerfRow r, int i)
    {
        if (CpOpts(r) == null || i == r.Choice) return;
        if (r.Select != null) r.Select(i);
        else if (r.Step != null) r.Step(i - r.Choice);
    }

    /// <summary>下拉行的 ←→：直接换到上一个 / 下一个选项（不循环）。</summary>
    private void CpPickStep(PerfRow r, int d)
    {
        var opts = CpOpts(r)!;
        int to = Math.Clamp(r.Choice + d, 0, opts.Length - 1);
        if (to == r.Choice) { Cue(Sfx.Bump); return; }
        Cue(Sfx.Nav, pan: d < 0 ? 0.2f : 0.8f);
        CpChoose(r, to);
    }

    /// <summary>性能类页面：分类里的第一行 / 下一行（不跨分类）。</summary>
    private int CpFirstIn(List<PerfRow> rows, int first)
    {
        for (int i = first + 1; i < rows.Count && !rows[i].Section; i++) if (CpFocusable(rows[i])) return i;
        return -1;
    }

    /// <summary>
    /// 手柄方向键（11-25 jinyu：去掉旧页面「←→ 调选项」的走法）。四个页面统一：
    ///   左列：↑↓ 换分类，→ 进右列（焦点落在第一行）；
    ///   右列：↑↓ 在当前分类里走；←→ 只调滑块和「− 值 +」步进，别的行 ← 回左列；开关 / 下拉一律按 A；
    ///   下拉展开着：↑↓ 选项，←→ 不动。
    /// 设置页的特殊状态（打字中、曲线编辑）交回原来的页面。
    /// </summary>
    private bool CpMove(Direction dir)
    {
        if (_ak != null) return AkPad(dir == Direction.Up ? "Up" : dir == Direction.Down ? "Down" : "dir");
        if (!CpActive) return false;
        if (_stOpen && StEditing) return false;
        if (_perfOpen && _perfCurveEdit) return false;
        var rows = SettingsRows;
        int d = dir is Direction.Up or Direction.Left ? -1 : 1;
        bool horiz = dir is Direction.Left or Direction.Right;
        CpTouchMode(false);
        if (_cpDrop >= 0)
        {
            var opts = CpOpts(rows[_cpDrop])!;
            if (horiz) { Cue(Sfx.Bump); return true; }
            int to = _cpDropSel + d;
            if (to < 0 || to >= opts.Length) { Cue(Sfx.Bump); return true; }
            _cpDropSel = to;
            CuePan(0.5f);
            CpRepaint();
            return true;
        }
        var cats = CpCats(rows);
        int cat = CpCatIndex(rows, cats);
        if (CpZone == 0)
        {
            if (dir == Direction.Right) { CpEnterList(rows, cats, cat); return true; }
            if (dir == Direction.Left) { Cue(Sfx.Bump); return true; }
            int to = cat + d;
            while (to >= 0 && to < cats.Count && !CpCatIsSt(cats[to]) && CpFirstIn(_cpComp == "disp" ? _dsRows : rows, cats[to].First) < 0) to += d;
            if (to < 0 || to >= cats.Count) { Cue(Sfx.Bump); return true; }
            CuePan(0.5f);
            CpGoCat(rows, cats, to);
            return true;
        }
        var r = CpRow;
        if (!horiz)
        {
            int f = SettingsFocus;
            for (int i = f + d; i >= 0 && i < rows.Count; i += d)
            {
                if (rows[i].Section && !_stOpen) break;   // 性能类页面：不跨分类
                if (!CpFocusable(rows[i])) continue;
                SettingsFocus = i;
                if (_stOpen && _stArmed != null) { _stArmed = null; BuildStRows(); }
                CuePan(0.5f);
                CpRepaint();
                return true;
            }
            Cue(Sfx.Bump);
            return true;
        }
        if (r != null && !r.Disabled && CpIsSlider(r)) { CpStep(r, d); return true; }
        if (r != null && !r.Disabled && CpIsStepper(r)) { Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown); r.Step!(d); return true; }
        if (dir == Direction.Left) { CpLeaveList(); return true; }
        Cue(Sfx.Bump);
        return true;
    }

    /// <summary>「− 值 +」步进行：没有范围、也不是选项列表，只能一档一档走。</summary>
    private static bool CpIsStepper(PerfRow r) => r.Numeric && !CpIsSlider(r) && !CpIsPick(r) && r.Step != null;

    /// <summary>左列 → 右列：焦点落在这一分类的第一行，列表滚回顶上。</summary>
    private void CpEnterList(List<PerfRow> rows, List<(string Name, string? Sub, int First)> cats, int cat)
    {
        int first = _stOpen ? FirstStRow() : CpFirstIn(rows, cats[cat].First);
        if (first < 0 || first >= rows.Count || !CpFocusable(rows[first])) { Cue(Sfx.Bump); return; }
        Cue(Sfx.Nav);
        if (_stOpen) { _stFocus = first; _stZone = 1; } else { SettingsFocus = first; _cpZone = 1; }
        if (CpHost is { } h && _cpViews.TryGetValue(h, out var v)) { v.Manual = false; v.Scroll = 0; }
        CpRepaint();
    }

    private void CpLeaveList()
    {
        Cue(Sfx.Back);
        _cpDrop = -1;
        if (_stOpen) { _stZone = 0; _stArmed = null; } else _cpZone = 0;
        CpRepaint();
    }

    /// <summary>A：左列进右列；下拉展开 / 选定；滑块不响应；步进行执行它自己的动作；开关和 ▸ 交给原来的页面。</summary>
    private bool CpActivate()
    {
        if (_ak != null) return AkPad("A");
        if (!CpActive) return false;
        if (_stOpen && StEditing) return false;
        if (_perfOpen && _perfCurveEdit) return false;
        CpTouchMode(false);
        var rows = SettingsRows;
        if (_cpDrop >= 0)
        {
            var dr = rows[_cpDrop];
            int sel = _cpDropSel;
            _cpDrop = -1;
            Cue(Sfx.Positive);
            CpChoose(dr, sel);
            CpRepaint();
            return true;
        }
        var cats = CpCats(rows);
        if (CpZone == 0) { CpEnterList(rows, cats, CpCatIndex(rows, cats)); return true; }
        if (CpRow is not { } r) return true;
        if (r.Disabled) { Cue(Sfx.Bump); return true; }
        if (CpIsPick(r)) { _cpDrop = SettingsFocus; _cpDropSel = Math.Clamp(r.Choice, 0, CpOpts(r)!.Length - 1); Cue(Sfx.MenuIn); CpRepaint(); return true; }
        if (CpIsSlider(r)) { Cue(Sfx.Bump); return true; }
        if (CpIsStepper(r) || r.Numeric) { if (r.Press != null) { Cue(Sfx.Nav); r.Press(); } else Cue(Sfx.Bump); return true; }
        if (_stOpen) return false;   // 设置页的开关 / 动作 / 进下一级：照原来的 StActivate（含「再按一次确认」）
        RowPress(r);
        CpRepaint();
        return true;
    }

    /// <summary>B：先收下拉；右列退回左列；左列交给原来的页面（设置页的子列表一层层退、最后关页面）。</summary>
    private bool CpBack()
    {
        if (_ak != null) return AkPad("B");
        if (!CpActive) return false;
        if (_stOpen && StEditing) return false;
        if (_perfOpen && _perfCurveEdit) return false;
        if (_cpDrop >= 0) { _cpDrop = -1; Cue(Sfx.Back); CpRepaint(); return true; }
        if (_stOpen && (_stBrowse != null || _lgPage != LgPage.None)) return false;   // 文件夹浏览 / 本地游戏列表：原来的 B 一层层退
        if (CpZone == 1) { CpLeaveList(); return true; }
        return false;
    }

    // ---------------- 触摸（手机那样：点按直接生效、拖动滚动带惯性、拖滑块、按下有反馈、触摸时不显示焦点框） ----------------

    private (float X, float Y) CpLocal(double x, double y) { float s = _ccK / 1.5f; return ((float)(x / s), (float)(y / s)); }

    private sealed class CpTouchState
    {
        public uint Id;
        public float X0, Y0, Scroll0, LastY;
        public long LastT;
        public float Vel;          // 设计单位 / 秒
        public bool Drag, Slider;
        public int Row = -1;
    }
    private CpTouchState? _cpT;
    private bool _cpTouchMode;

    /// <summary>触摸操作时把焦点框藏起来（手机上没有焦点框）；手柄一动就回来。</summary>
    private void CpTouchMode(bool on)
    {
        if (_cpTouchMode == on) return;
        _cpTouchMode = on;
        if (CpHost is { } h && _cpViews.TryGetValue(h, out var v))
        {
            if (on) CurShow(v.Cur, false, 80);
            else v.Cur.Shown = false;   // 下次画焦点时按「第一次」处理：直接出现在当前行
        }
    }

    private bool CpPointerDown(PointerRoutedEventArgs e, Microsoft.UI.Input.PointerPoint pt)
    {
        if (!CpActive || CpHost is not { } host || !_cpViews.TryGetValue(host, out var v)) return false;
        if (_stOpen && StEditing) return false;
        // 屏幕左缘留给「边缘右划返回」
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch && pt.Position.X <= EdgeBackPx) return false;
        var (x, y) = CpLocal(pt.Position.X, pt.Position.Y);
        if (_ak != null) { e.Handled = true; return AkTap(x, y); }
        CpTouchMode(true);
        v.List.StopAnimation("Offset");
        v.List.Offset = new Vector3(CpListX - 16, CpTop - 16 - v.Scroll, 0);
        var t = new CpTouchState { Id = pt.PointerId, X0 = x, Y0 = y, Scroll0 = v.Scroll, LastY = y, LastT = NowMs };
        var rows = SettingsRows;
        if (x >= CpListX && x <= CpListX + CpListW && y >= CpTop && y <= CpBottom)
        {
            float ly = y - CpTop + v.Scroll;
            var hit = v.Lay.FirstOrDefault(l => ly >= l.Y && ly < l.Y + l.H);
            if (hit.H > 0)
            {
                t.Row = hit.I;
                var r = rows[hit.I];
                // 按在滑块轨道上：直接拖值（不滚列表）
                if (CpIsSlider(r) && !r.Disabled && r.SetNumber != null)
                {
                    var (_, sx0, sx1, _) = CpSliderX(44 + r.Indent * 1.5f, CpListW - 34);
                    float lx = x - CpListX;
                    if (ly > hit.Y + hit.H - 90 && lx >= sx0 - 30 && lx <= sx1 + 30) { t.Slider = true; CpSliderTo(r, lx, sx0, sx1); }
                }
                if (!t.Slider) CpPressFx(v, hit.Y, hit.H, true);
            }
        }
        _cpT = t;
        _rootEl.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private void CpSliderTo(PerfRow r, float lx, float x0, float x1)
    {
        float f = Math.Clamp((lx - x0) / (x1 - x0), 0, 1);
        int inc = Math.Max(1, r.Increment);
        int n = Math.Clamp(r.Minimum + (int)MathF.Round(f * (r.Maximum - r.Minimum) / inc) * inc, r.Minimum, r.Maximum);
        if (n == r.Number) return;
        Cue(n > r.Number ? Sfx.SliderUp : Sfx.SliderDown, MathF.Pow(2, f - 0.5f));
        r.SetNumber!(n);
        CpRepaint();
    }

    /// <summary>按下的那一行压一层淡灰（手机列表的按下反馈），抬手 / 开始拖动时淡掉。</summary>
    private void CpPressFx(CpView v, float rowY, float rowH, bool on)
    {
        if (on)
        {
            v.Press.Offset = new Vector3(16, 16 + rowY, 0);
            v.Press.Size = new Vector2(CpListW, rowH);
            v.Press.StopAnimation("Opacity");
            v.Press.Opacity = 1;
        }
        else v.Press.StartAnimation("Opacity", CsS(0, 160, Bezier.EaseOut));
    }

    private bool CpPointerMove(PointerRoutedEventArgs e)
    {
        if (_cpT is not { } t || e.Pointer.PointerId != t.Id) return false;
        e.Handled = true;
        if (CpHost is not { } host || !_cpViews.TryGetValue(host, out var v)) return true;
        var p = e.GetCurrentPoint(_rootEl).Position;
        var (x, y) = CpLocal(p.X, p.Y);
        var rows = SettingsRows;
        if (t.Slider && t.Row >= 0 && t.Row < rows.Count)
        {
            var r = rows[t.Row];
            var (_, sx0, sx1, _) = CpSliderX(44 + r.Indent * 1.5f, CpListW - 34);
            CpSliderTo(r, x - CpListX, sx0, sx1);
            return true;
        }
        if (!t.Drag && Math.Abs(y - t.Y0) < 18) return true;
        if (t.X0 < CpListX) return true;
        if (!t.Drag) { t.Drag = true; CpPressFx(v, 0, 0, false); }
        long now = NowMs;
        float dt = Math.Max(1, now - t.LastT) / 1000f;
        t.Vel = t.Vel * 0.4f + (y - t.LastY) / dt * 0.6f;
        t.LastY = y; t.LastT = now;
        float max = Math.Max(0, v.ListH - (CpBottom - CpTop) + 30);
        float s = t.Scroll0 - (y - t.Y0);
        // 拖过头：橡皮筋（超出部分只走三分之一）
        if (s < 0) s /= 3; else if (s > max) s = max + (s - max) / 3;
        v.Scroll = s;
        v.Manual = true;
        v.List.Offset = new Vector3(CpListX - 16, CpTop - 16 - s, 0);
        return true;
    }

    private bool CpPointerUp(PointerRoutedEventArgs e, bool canceled = false)
    {
        if (_cpT is not { } t || e.Pointer.PointerId != t.Id) return false;
        _cpT = null;
        _rootEl.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (CpHost is not { } host || !_cpViews.TryGetValue(host, out var v)) return true;
        CpPressFx(v, 0, 0, false);
        if (t.Slider || canceled) return true;
        if (t.Drag)
        {
            // 惯性：按松手时的速度再滑一段，弹簧停到范围内
            float max = Math.Max(0, v.ListH - (CpBottom - CpTop) + 30);
            float target = Math.Clamp(v.Scroll - t.Vel * 0.32f, 0, max);
            v.Scroll = target;
            var to = new Vector3(CpListX - 16, CpTop - 16 - target, 0);
            if (Reduced) RwJump(v.List, to);
            else
            {
                var a = G.C.CreateVector3KeyFrameAnimation();
                a.InsertKeyFrame(1, to, G.Ease(new Bezier(0.15f, 0.7f, 0.25f, 1f)));
                a.Duration = TimeSpan.FromMilliseconds(Math.Clamp(Math.Abs(t.Vel) * 0.5f, 220, 650));
                v.List.StartAnimation("Offset", a);
            }
            return true;
        }
        float x = t.X0, y = t.Y0;
        MarkInput();
        var rows = SettingsRows;
        WithCue(Sfx.None, () =>
        {
            if (x < 200 && y < 130) { Back(); return; }   // 左上「B ←」
            // 下拉展开着：点到选项 = 选定，点到别处 = 收起
            if (_cpDrop >= 0)
            {
                var dh = v.Lay.FirstOrDefault(l => l.I == _cpDrop);
                float ly0 = y - CpTop + v.Scroll;
                var (ox, oy, ow, _) = CpOptRect(dh.Y, 0);
                int k = (int)MathF.Floor((ly0 - oy) / CpOptH);
                if (dh.H > 0 && x - CpListX >= ox && x - CpListX <= ox + ow && k >= 0 && k < CpOpts(rows[_cpDrop])!.Length)
                { var dr = rows[_cpDrop]; _cpDrop = -1; Cue(Sfx.Positive); CpChoose(dr, k); CpRepaint(); }
                else { _cpDrop = -1; Cue(Sfx.Back); CpRepaint(); }
                return;
            }
            if (x >= CpLeftX && x <= CpLeftX + CpLeftW && y >= CpTop && v.Slot > 0)
            {
                int c = (int)((y - CpTop) / v.Slot);
                if (c < 0 || c >= v.Cats.Count) return;
                Cue(Sfx.Nav);
                CpGoCat(rows, v.Cats, c);
                return;
            }
            if (t.Row < 0 || t.Row >= rows.Count) return;
            var r = rows[t.Row];
            var hit = v.Lay.FirstOrDefault(l => l.I == t.Row);
            SettingsFocus = t.Row;
            if (_stOpen) _stZone = 1; else _cpZone = 1;
            if (r.Disabled) { Cue(Sfx.Bump); CpRepaint(); return; }
            float lx = x - CpListX;
            if (CpIsSlider(r))
            {
                var (smx, sx0, sx1, spx) = CpSliderX(44 + r.Indent * 1.5f, CpListW - 34);
                if (Math.Abs(lx - smx) < 40) CpStep(r, -1);
                else if (Math.Abs(lx - spx) < 40) CpStep(r, 1);
                CpRepaint();
                return;
            }
            if (CpIsStepper(r))
            {
                float R = CpListW - 34;
                Cue(Sfx.Nav);
                if (lx > R - 90) r.Step!(1); else if (lx > R - 400) r.Step!(-1);
                CpRepaint();
                return;
            }
            Cue(Sfx.Nav);
            v.Manual = true;   // 点了就别把列表滚走
            if (!CpActivate()) CcActivate();
            CpRepaint();
        });
        return true;
    }

    /// <summary>设置页：点左列分类（同 StCategoryClick 的效果，按序号）。</summary>
    private void StCategoryJump(int i)
    {
        if (i < 0 || i >= (_gsItem != null ? GsCats.Length : StCatsMain.Length)) return;
        _settingsEdit.End();
        _stCat = i; _stArmed = null; _stBrowse = null; _stBrowseExe = false; _stBrowseMusic = false; _stBrowsePick = null; _lgPage = LgPage.None;
        _stBrowseLossless = false;
        BuildStRows();
        _stFocus = FirstStRow();
        _stScroll = 0; _stScrollY = float.NaN;
        PaintSt();
        if (i is 3 or 4) _ = StRefresh();   // Magpie / 小黄鸭：进来就读它们的配置
    }
}
