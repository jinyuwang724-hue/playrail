// 从左侧栏进的全屏页（性能 / 显示与声音 / 系统 / 设置）照 Cocoon 设置页重做（11-24 jinyu：用 Cocoon 的组件，不沿用旧页面的样式）。
// 结构照 jinyu 的 Cocoon 设置页截图和侧栏视频（只取尺寸和行为，不用 Cocoon 的代码或素材）：
//   左上「B ←」灰胶囊 + 页面标题；左列是分类（黑色实心图标 + 名称，选中的那项是白色圆角卡、名称紫色、下面一行灰色读数）；
//   右列是分组卡片（同二级页 RsPaintGroupAt），一行一项：开关（同二级页的拨动开关）、下拉（白色胶囊，A 展开选项卡片）、
//   滑块（「数值 − 轨道 +」，←→ 直接调）、带 ▸ 的进一步、只读值；焦点是首页同一套四角括号。
//   性能 / 显示与声音 / 系统的分类就是原来页面里的分组（功耗、CPU、风扇…）；设置页用它原来的分类。
// 数据、读写和各行的动作全部沿用原来的页面（PerfRow 列表），这里只换画法和手柄 / 触摸的走法。
using System.Numerics;
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
    private const float CpTop = 150, CpBottom = 1186, CpLeftX = 24, CpLeftW = 584, CpListX = 636, CpListW = 1240;
    private const float CpRowH = 112, CpRowSubH = 140, CpRowSliderH = 160, CpSep = 4, CpGap = 36;
    private static readonly Color CpAccentInk = Color.FromArgb(255, 0x7A, 0x4C, 0xFF);

    private sealed class CpView
    {
        public required ContainerVisual Root, ListBox;
        public required SpriteVisual Bg, Head, Left, List, Drop;
        public required CurSet Cur;
        public CompositionDrawingSurface? HeadS, LeftS, ListS, DropS;
        public string HeadKey = "";
        public float Scroll, ListH;
        public List<(int I, float Y, float H, string? Sub)> Lay = new();
        public List<(string Name, string? Sub, int First)> Cats = new();
        public float Slot;
    }

    private readonly Dictionary<ContainerVisual, CpView> _cpViews = new();
    /// <summary>性能 / 显示与声音 / 系统：0 = 焦点在左列分类，1 = 在右列（设置页用它自己的 _stZone）。</summary>
    private int _cpZone;
    /// <summary>展开着的下拉：哪一行、焦点在第几个选项（-1 = 没有）。</summary>
    private int _cpDrop = -1, _cpDropSel;
    private (uint Id, double X0, double Y0, float Scroll0, bool Drag)? _cpTouch;

    // 添加 / 编辑本地游戏页（_agOpen）是盖在设置页上的独立页面，输入归它自己
    private bool CpActive => _ccLight && _ccOpen && (_perfOpen || _dsOpen || _syOpen || _stOpen) && !GsOverlay && !_agOpen;
    private bool CpSectioned => _perfOpen || _dsOpen || _syOpen;
    private int CpZone => _stOpen ? _stZone : _cpZone;

    private ContainerVisual? CpHost => _perfOpen ? _perf : _dsOpen ? _ds : _syOpen ? _sy : _stOpen ? _st : null;
    private string CpTitle => _perfOpen ? "性能" : _dsOpen ? "显示与声音" : _syOpen ? "系统" : "设置";

    /// <summary>页面刚打开：焦点从左列开始（Cocoon 进设置页时停在第一个分类）。</summary>
    private void CpReset() { _cpZone = 0; _cpDrop = -1; foreach (var v in _cpViews.Values) { v.Scroll = 0; v.HeadKey = ""; v.Cur.Shown = false; foreach (var c in v.Cur.All) { c.StopAnimation("Opacity"); c.Opacity = 0; } } }

    // ---------------- 行的分类 / 种类 ----------------

    private static bool CpIsToggle(PerfRow r) => r.Choices is { Length: 2 } c && c[0] == "关" && c[1] == "开";
    private static bool CpIsPick(PerfRow r) => r.Choices is { Length: > 0 } && !CpIsToggle(r);
    private static bool CpIsSlider(PerfRow r) => r.Numeric && r.Maximum > r.Minimum;
    private static bool CpFocusable(PerfRow r) => !r.Section && !r.Skip;

    /// <summary>左列分类：性能 / 显示 / 系统 = 原来的分组标题；设置 = 它自己的分类。</summary>
    private List<(string Name, string? Sub, int First)> CpCats(List<PerfRow> rows)
    {
        var l = new List<(string, string?, int)>();
        if (_stOpen)
        {
            var cats = StCats;
            for (int i = 0; i < cats.Length; i++) l.Add((cats[i], null, -1));
            return l;
        }
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Section) l.Add((rows[i].Title, null, i));   // 11-25 jinyu：分类只显示名字，不带实时读数
        if (l.Count == 0) l.Add((CpTitle, null, -1));
        return l;
    }

    /// <summary>当前选中的分类（性能类页面：焦点行所在的分组）。</summary>
    private int CpCatIndex(List<PerfRow> rows, List<(string Name, string? Sub, int First)> cats)
    {
        if (_stOpen) return _stCat;
        int f = SettingsFocus, k = 0;
        for (int c = 0; c < cats.Count; c++) if (cats[c].First >= 0 && cats[c].First <= f) k = c;
        return k;
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
        if (_cpViews.TryGetValue(host, out var v)) { host.Children.Remove(v.Root); host.Children.InsertAtTop(v.Root); return v; }
        SpriteVisual Sp() => G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var g = G.C.CreateLinearGradientBrush();
        g.StartPoint = new Vector2(0, 0); g.EndPoint = new Vector2(0, 1);
        g.ColorStops.Add(G.C.CreateColorGradientStop(0, Color.FromArgb(255, 0xF5, 0xF5, 0xF8)));
        g.ColorStops.Add(G.C.CreateColorGradientStop(1, Color.FromArgb(255, 0xE6, 0xE7, 0xED)));
        var bg = G.Sprite(g, relative: false);
        bg.Size = new Vector2(CsW, CsH);
        var box = G.Container(relative: false);
        box.Size = new Vector2(CsW, CsH);
        box.Clip = G.C.CreateInsetClip(CpListX - 40, CpTop - 14, 0, CsH - CpBottom);
        v = new CpView { Root = root, ListBox = box, Bg = bg, Head = Sp(), Left = Sp(), List = Sp(), Drop = Sp(), Cur = null! };
        box.Children.InsertAtTop(v.List);
        foreach (var x in new Visual[] { bg, v.Head, v.Left, box }) root.Children.InsertAtTop(x);
        v.Cur = CurNew(root);
        root.Children.InsertAtTop(v.Drop);
        host.Children.InsertAtTop(root);
        _cpViews[host] = v;
        return v;
    }

    /// <summary>四个页面的 Paint* 在浅色皮时改走这里。返回 true 表示画了（原来的画法跳过）。</summary>
    private bool CpPaint()
    {
        foreach (var (h, cv) in _cpViews) cv.Root.IsVisible = _ccLight && h == CpHost;
        if (!CpActive || CpHost is not { } host) return false;
        var v = CpEnsure(host);
        v.Root.IsVisible = true;
        v.Root.Scale = new Vector3(_ccK / 1.5f, _ccK / 1.5f, 1);
        var rows = SettingsRows;
        var cats = CpCats(rows);
        v.Cats = cats;
        int cat = Math.Clamp(CpCatIndex(rows, cats), 0, cats.Count - 1);
        CpPaintHead(v);
        CpPaintLeft(v, cats, cat);
        var vis = CpVisible(rows, cats, cat);
        CpPaintList(v, rows, vis);
        CpScrollAndFocus(v, rows, cats, cat);
        CpPaintDrop(v, rows);
        return true;
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
            bool on = i == sel;
            string? sub = on && slot >= 110 ? cats[i].Sub : null;
            if (on)
            {
                RsSoftShadow(ds, CpLeftX, y + 4 + 4, CpLeftW, slot - 8, 30, 8, 0.14f);
                ds.FillRoundedRectangle(CpLeftX, y + 4, CpLeftW, slot - 8, 30, 30, Color.FromArgb(255, 0xFB, 0xFB, 0xFC));
                ds.DrawRoundedRectangle(CpLeftX + 0.5f, y + 4.5f, CpLeftW - 1, slot - 9, 30, 30, Color.FromArgb(255, 0xEC, 0xED, 0xF0), 1);
            }
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
        if (!CpSectioned) return;
        float top = CpTop + cats.Count * slot + 24, bottom = CpBottom;
        if (bottom - top < 220) return;
        using var card = CanvasGeometry.CreateRoundedRectangle(ds.Device, CpLeftX, top, CpLeftW, bottom - top, 30, 30);
        RsSoftShadow(ds, CpLeftX, top + 4, CpLeftW, bottom - top, 30, 8, 0.12f);
        ds.FillGeometry(card, Color.FromArgb(255, 0xFB, 0xFB, 0xFC));
        using (ds.CreateLayer(1, card)) CpPanel(ds, CpLeftX, top, CpLeftW, bottom - top, cats[sel].Name);
        ds.DrawRoundedRectangle(CpLeftX + 0.5f, top + 0.5f, CpLeftW - 1, bottom - top - 1, 30, 30, Color.FromArgb(255, 0xEC, 0xED, 0xF0), 1);
    }

    /// <summary>排右列：每行的 y、高、灰色说明；遇到分组标题（设置页）留出标题高度、另起一张卡片。</summary>
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
            float h = CpIsSlider(r) ? CpRowSliderH : sub != null ? CpRowSubH : CpRowH;
            if (!open) { groups.Add((y, y + h, new List<float>())); open = true; }
            else { var g = groups[^1]; g.Seps.Add(y - CpSep); groups[^1] = (g.Y0, y + h, g.Seps); }
            lay.Add((i, y, h, sub));
            y += h + CpSep;
        }
        return lay;
    }

    private void CpPaintList(CpView v, List<PerfRow> rows, List<int> vis)
    {
        v.Lay = CpLayout(rows, vis, out var groups);
        float H = v.Lay.Count == 0 ? 200 : v.Lay[^1].Y + v.Lay[^1].H;
        v.ListH = H;
        float pad = 16;
        using var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.ListS, v.List, CpListX - pad, CpTop - pad - v.Scroll, CpListW + pad * 2, H + pad * 2));
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation(pad, pad) * Matrix3x2.CreateScale(CsRaster);
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        foreach (var (y0, y1, seps) in groups) RsPaintGroupAt(ds, 0, CpListW, y0, y1, seps);
        if (v.Lay.Count == 0)
        {
            using var f = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 32, VerticalAlignment = CanvasVerticalAlignment.Center };
            ds.DrawText(_perfAgentDown ? "连不上工具箱后台" : "正在读取…", new Windows.Foundation.Rect(40, 0, 900, 120), RsSub, f);
        }
        foreach (var (i, y, h, sub) in v.Lay) CpPaintRow(ds, rows[i], i, y, h, sub);
    }

    private void CpPaintRow(CanvasDrawingSession ds, PerfRow r, int index, float y, float h, string? sub)
    {
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
            string val = r.Choice >= 0 && r.Choice < r.Choices!.Length ? r.Choices[r.Choice] : r.Value;
            RsPaintPickPill(ds, R - 400, cy - 40, 400, 80, val, _cpDrop == index, 32);
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
            // 没有范围的数值（刷新率、分辨率…）：‹ 值 ›，←→ 一档一档切
            using var vf = new CanvasTextFormat { FontFamily = RmFont(r.Value), FontSize = 32, FontWeight = RsWt(r.Value, 500), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
            ds.DrawText(r.Value, new Windows.Foundation.Rect(R - 640, y, 600, h), RmInk, vf);
            float vw = CpTextW(ds, r.Value, 32);
            CpChev(ds, R - 40 - vw - 26, cy, left: true);
            CpChev(ds, R - 6, cy, left: false);
        }
        else
        {
            bool nav = r.Press != null || r.Secondary != null;
            string v = r.Value.TrimEnd('›', ' ');   // 原来值里自带的 › 不要（行尾已经有 ▸）
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

    /// <summary>滚到焦点行露出来（上下各留 60），焦点框到焦点行 / 焦点分类。</summary>
    private void CpScrollAndFocus(CpView v, List<PerfRow> rows, List<(string Name, string? Sub, int First)> cats, int cat)
    {
        int f = SettingsFocus;
        var hit = v.Lay.FirstOrDefault(e => e.I == f);
        bool inList = CpZone == 1 && hit.H > 0;
        float view = CpBottom - CpTop, target = v.Scroll;
        if (inList)
        {
            if (hit.Y - 60 < target) target = hit.Y - 60;
            if (hit.Y + hit.H + 60 > target + view) target = hit.Y + hit.H + 60 - view;
        }
        target = Math.Clamp(target, 0, Math.Max(0, v.ListH - view + 30));
        if (target != v.Scroll)
        {
            v.Scroll = target;
            var to = new Vector3(CpListX - 16, CpTop - 16 - target, 0);
            if (Reduced) RwJump(v.List, to); else v.List.StartAnimation("Offset", RwSpring(to));
        }
        else v.List.Offset = new Vector3(CpListX - 16, CpTop - 16 - v.Scroll, 0);
        if (_cpDrop >= 0) return;   // 下拉开着：焦点框在选项上（CpPaintDrop）
        bool first = !v.Cur.Shown;
        if (inList) CurTo(v.Cur, CpListX + 6, CpTop + hit.Y - v.Scroll + 4, CpListW - 12, hit.H - 8, 8, 40, move: !first);
        else if (CpZone == 0 && cats.Count > 0) CurTo(v.Cur, CpLeftX, CpTop + cat * v.Slot + 4, CpLeftW, v.Slot - 8, 8, 36, move: !first);
        else { if (v.Cur.Shown) CurShow(v.Cur, false, 60); return; }
        if (first) CurShow(v.Cur, true, 160, 120);
    }

    /// <summary>展开的下拉：选项卡片贴在胶囊下面（放不下就放上面），焦点框在选项上。</summary>
    private void CpPaintDrop(CpView v, List<PerfRow> rows)
    {
        if (_cpDrop < 0 || _cpDrop >= rows.Count || rows[_cpDrop].Choices is not { Length: > 0 } opts)
        {
            v.Drop.IsVisible = false;
            return;
        }
        var hit = v.Lay.FirstOrDefault(e => e.I == _cpDrop);
        float optH = 88, w = 400, h = opts.Length * optH;
        float x = CpListX + CpListW - 34 - w, rowTop = CpTop + hit.Y - v.Scroll;
        float y = rowTop + hit.H / 2 + 48;
        if (y + h > CpBottom) y = Math.Max(CpTop - 20, rowTop + hit.H / 2 - 48 - h);
        float pad = 24;
        using (var ds = CanvasComposition.CreateDrawingSession(CpSurf(ref v.DropS, v.Drop, x - pad, y - pad, w + pad * 2, h + pad * 2)))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateTranslation(pad - x, pad - y) * Matrix3x2.CreateScale(CsRaster);
            var r = rows[_cpDrop];
            var tmp = new RsRow { Title = r.Title, Options = opts, Current = () => r.Choice };
            RsPaintOptionCard(ds, tmp, x, y, w, optH, 32);
        }
        v.Drop.IsVisible = true;
        CurTo(v.Cur, x, y + _cpDropSel * optH, w, optH, 6, 30, move: true);
        if (!v.Cur.Shown) CurShow(v.Cur, true, 90);
    }

    // ---------------- 图标（黑色实心，Cocoon 设置页左列那样） ----------------

    private static void CpIcon(CanvasDrawingSession ds, string cat, float cx, float cy, Color ink, float k)
    {
        string? rs = cat switch
        {
            "功耗" => "power", "Magpie" => "magpie", "小黄鸭" => "duck", "Steam 插件" => "steam", "关于" => "info",
            "游戏库" => "stack", "外观" => "artwork", "性能显示" => "sliders", "更新" => "scrape", _ => null,
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
            case "声音": case "声音与反馈":
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
        if (r.Choices == null || i == r.Choice) return;
        if (r.Select != null) r.Select(i);
        else if (r.Step != null) r.Step(i - r.Choice);
    }

    /// <summary>性能类页面：分类里的第一行 / 下一行（不跨分类）。</summary>
    private int CpFirstIn(List<PerfRow> rows, int first)
    {
        for (int i = first + 1; i < rows.Count && !rows[i].Section; i++) if (CpFocusable(rows[i])) return i;
        return -1;
    }

    /// <summary>手柄方向键。处理了返回 true；没处理的交给原来的页面（设置页的左列、曲线编辑…）。</summary>
    private bool CpMove(Direction dir)
    {
        if (!CpActive) return false;
        var rows = SettingsRows;
        int d = dir is Direction.Up or Direction.Left ? -1 : 1;
        if (_cpDrop >= 0)
        {
            var opts = rows[_cpDrop].Choices!;
            if (dir is Direction.Left or Direction.Right) { Cue(Sfx.Bump); return true; }
            int to = _cpDropSel + d;
            if (to < 0 || to >= opts.Length) { Cue(Sfx.Bump); return true; }
            _cpDropSel = to;
            CuePan(0.5f);
            CpRepaint();
            return true;
        }
        if (_perfOpen && _perfCurveEdit) return false;
        var r = CpRow;
        bool horiz = dir is Direction.Left or Direction.Right;
        if (_stOpen)
        {
            if (_stZone == 1 && horiz && r is { } sr && CpIsSlider(sr)) { CpStep(sr, d); return true; }
            if (_stZone == 1 && horiz && r is { Numeric: true, Disabled: false, Step: not null } nr) { Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown); nr.Step(d); return true; }
            return false;   // 其余照设置页原来的走法（左列分类、右列上下、左右切值 / 回左列）
        }
        var cats = CpCats(rows);
        int cat = CpCatIndex(rows, cats);
        if (_cpZone == 0)
        {
            if (dir == Direction.Right) { _cpZone = 1; Cue(Sfx.Nav); CpRepaint(); return true; }
            if (dir == Direction.Left) { Cue(Sfx.Bump); return true; }
            int to = cat + d;
            while (to >= 0 && to < cats.Count && CpFirstIn(rows, cats[to].First) < 0) to += d;
            if (to < 0 || to >= cats.Count) { Cue(Sfx.Bump); return true; }
            SettingsFocus = CpFirstIn(rows, cats[to].First);
            CuePan(0.5f);
            CpRepaint();
            return true;
        }
        if (!horiz)
        {
            int f = SettingsFocus;
            for (int i = f + d; i >= 0 && i < rows.Count && !rows[i].Section; i += d)
                if (CpFocusable(rows[i])) { SettingsFocus = i; CuePan(0.5f); CpRepaint(); return true; }
            Cue(Sfx.Bump);
            return true;
        }
        if (r == null) return true;
        if (CpIsSlider(r)) { CpStep(r, d); return true; }
        if (r.Numeric && r.Step != null && !r.Disabled) { Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown); r.Step(d); return true; }
        if (r.Step != null && !r.Disabled) { RowStep(r, d); return true; }
        if (dir == Direction.Left) { _cpZone = 0; Cue(Sfx.Back); CpRepaint(); return true; }
        Cue(Sfx.Bump);
        return true;
    }

    /// <summary>A：下拉展开 / 选定；左列进右列；滑块不响应；其余（开关、进下一步）交给原来的页面。</summary>
    private bool CpActivate()
    {
        if (!CpActive) return false;
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
        if (_perfOpen && _perfCurveEdit) return false;
        if (CpSectioned && _cpZone == 0) { _cpZone = 1; Cue(Sfx.Nav); CpRepaint(); return true; }
        if (CpZone != 1 || CpRow is not { } r) return false;
        if (r.Disabled) { Cue(Sfx.Bump); return true; }
        if (CpIsPick(r)) { _cpDrop = SettingsFocus; _cpDropSel = Math.Max(0, r.Choice); Cue(Sfx.MenuIn); CpRepaint(); return true; }
        if (CpIsSlider(r)) { Cue(Sfx.Bump); return true; }
        return false;
    }

    /// <summary>B：先收下拉；性能类页面右列退回左列；其余交给原来的页面（设置页同样右列 → 左列 → 退出）。</summary>
    private bool CpBack()
    {
        if (!CpActive) return false;
        if (_cpDrop >= 0) { _cpDrop = -1; Cue(Sfx.Back); CpRepaint(); return true; }
        if (_perfOpen && _perfCurveEdit) return false;
        if (CpSectioned && _cpZone == 1) { _cpZone = 0; Cue(Sfx.Back); CpRepaint(); return true; }
        return false;
    }

    // ---------------- 触摸 ----------------

    private (float X, float Y) CpLocal(double x, double y) { float s = _ccK / 1.5f; return ((float)(x / s), (float)(y / s)); }

    private bool CpPointerDown(PointerRoutedEventArgs e, Microsoft.UI.Input.PointerPoint pt)
    {
        if (!CpActive || CpHost is not { } host || !_cpViews.TryGetValue(host, out var v)) return false;
        _cpTouch = (pt.PointerId, pt.Position.X, pt.Position.Y, v.Scroll, false);
        _rootEl.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private bool CpPointerMove(PointerRoutedEventArgs e)
    {
        if (_cpTouch is not { } t || e.Pointer.PointerId != t.Id) return false;
        e.Handled = true;
        if (CpHost is not { } host || !_cpViews.TryGetValue(host, out var v)) return true;
        var pt = e.GetCurrentPoint(_rootEl).Position;
        var (x0, y0) = CpLocal(t.X0, t.Y0);
        var (x, y) = CpLocal(pt.X, pt.Y);
        if (!t.Drag && Math.Abs(y - y0) < 24) return true;
        if (x0 < CpListX) return true;
        _cpTouch = t with { Drag = true };
        float view = CpBottom - CpTop;
        v.Scroll = Math.Clamp(t.Scroll0 - (y - y0), 0, Math.Max(0, v.ListH - view + 30));
        v.List.StopAnimation("Offset");
        v.List.Offset = new Vector3(CpListX - 16, CpTop - 16 - v.Scroll, 0);
        if (v.Cur.Shown) CurShow(v.Cur, false, 60);
        return true;
    }

    private bool CpPointerUp(PointerRoutedEventArgs e, bool canceled = false)
    {
        if (_cpTouch is not { } t || e.Pointer.PointerId != t.Id) return false;
        _cpTouch = null;
        _rootEl.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (canceled || t.Drag || CpHost is not { } host || !_cpViews.TryGetValue(host, out var v)) return true;
        var (x, y) = CpLocal(t.X0, t.Y0);
        MarkInput();
        var rows = SettingsRows;
        WithCue(Sfx.None, () =>
        {
            // 下拉开着：点选项 = 选定，点别处 = 收起
            if (_cpDrop >= 0)
            {
                if (v.Drop.IsVisible && x >= v.Drop.Offset.X + 24 && x <= v.Drop.Offset.X + v.Drop.Size.X - 24 && y >= v.Drop.Offset.Y + 24 && y <= v.Drop.Offset.Y + v.Drop.Size.Y - 24)
                { _cpDropSel = (int)((y - v.Drop.Offset.Y - 24) / 88); CpActivate(); }
                else CpBack();
                return;
            }
            if (x < 200 && y < 130) { Back(); return; }   // 左上「B ←」
            if (x >= CpLeftX && x <= CpLeftX + CpLeftW && y >= CpTop && v.Slot > 0)
            {
                int c = (int)((y - CpTop) / v.Slot);
                if (c < 0 || c >= v.Cats.Count) return;
                Cue(Sfx.Nav);
                if (_stOpen) { _stZone = 0; StCategoryJump(c); return; }
                int first = CpFirstIn(rows, v.Cats[c].First);
                if (first >= 0) { SettingsFocus = first; _cpZone = 0; CpRepaint(); }
                return;
            }
            if (x < CpListX || x > CpListX + CpListW || y < CpTop) return;
            float ly = y - CpTop + v.Scroll;
            var hit = v.Lay.FirstOrDefault(e => ly >= e.Y && ly < e.Y + e.H);
            if (hit.H <= 0) return;
            var r = rows[hit.I];
            SettingsFocus = hit.I;
            if (_stOpen) _stZone = 1; else _cpZone = 1;
            float R = CpListX + CpListW - 34;
            if (CpIsSlider(r) && !r.Disabled)
            {
                var (smx, sx0, sx1, _) = CpSliderX(44 + r.Indent * 1.5f, CpListW - 34);
                float x0 = CpListX + sx0, x1 = CpListX + sx1;
                if (x >= CpListX + smx - 40 && x < x0 - 10) { CpStep(r, -1); return; }
                if (x > x1 + 10) { CpStep(r, 1); return; }
                if (x >= x0 - 10 && x <= x1 + 10 && r.SetNumber != null)
                {
                    float f = Math.Clamp((x - x0) / (x1 - x0), 0, 1);
                    int n = r.Minimum + (int)MathF.Round(f * (r.Maximum - r.Minimum) / Math.Max(1, r.Increment)) * Math.Max(1, r.Increment);
                    Cue(Sfx.SliderUp);
                    r.SetNumber(Math.Clamp(n, r.Minimum, r.Maximum));
                }
                CpRepaint();
                return;
            }
            Cue(Sfx.Nav);
            if (!CpActivate()) CcActivate();
            else CpRepaint();
        });
        return true;
    }

    /// <summary>设置页：点左列分类（同 StCategoryClick 的效果，按序号）。</summary>
    private void StCategoryJump(int i)
    {
        if (i < 0 || i >= StCats.Length) return;
        _settingsEdit.End();
        _stCat = i; _stArmed = null; _stBrowse = null; _stBrowseExe = false; _stBrowseMusic = false; _stBrowsePick = null; _lgPage = LgPage.None;
        _stBrowseLossless = false;
        BuildStRows();
        _stFocus = FirstStRow();
        _stScroll = 0; _stScrollY = float.NaN;
        PaintSt();
    }
}
