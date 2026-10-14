// 中间的竖条（首页窄缝，120 px）：参照 folia 首页窄缝的排法（自己实现），并按需求加了系统信息与快捷入口。
// 从上到下：状态行（时间、电池）→ 导航区（搜索框〔打开时〕、竖排页签、分隔线、合集直达入口）→ 账户（头像 + 用户名）
// → 工具行（搜索、设置、文件管理器）。导航区是唯一可伸缩的一段：放不下全名时页签缩成一个字，再放不下就在里面滚，
// 保证焦点那一项露出来。设置打开时导航区换成设置列表。
// 竖条可以交互：手柄 / 键盘的焦点进到竖条里时，焦点那一项画白色描边高亮（墙上的焦点环同时熄掉）。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Windows.Foundation;
using Rect = Windows.Foundation.Rect;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public enum RailKind { Tab, Shortcut, Account, Search, Settings, Explorer, SearchField, Setting, Back }

public sealed class RailItem
{
    public RailKind Kind;
    public int Index;
    public Rect Bounds;
    /// <summary>同一行里左右可走的一组（合集入口的一行、工具行）；-1 表示单独一行。</summary>
    public int Row = -1;
}

public sealed class RailModel
{
    public bool Home = true;
    public List<string> Tabs = new();
    public int ActiveTab;
    public List<string> Shortcuts = new();
    public string FullTitle = "";
    public int Count;
    public double Hours;
    public DateTime Now = DateTime.Now;
    public BatteryState Battery;
    public string User = "";
    public CanvasBitmap? Avatar;
    public bool SearchOpen;
    public string Query = "";
    public int Matches = -1;
    public bool SettingsOpen;
    public List<(string Label, string Value)> Settings = new();
}

public sealed class Rail
{
    public const float HomeWidth = 120;
    private const float PadX = 10, PadTop = 16, PadBottom = 16;
    private const string IconFont = "Segoe Fluent Icons";
    private readonly Gfx _g;
    public float Dpi = 1;
    public readonly List<RailItem> Items = new();
    /// <summary>导航区滚动量（内容比导航区高时）。</summary>
    private float _scroll;

    public Rail(Gfx g) { _g = g; }

    // ---------------- 文字工具 ----------------

    private CanvasTextLayout H(string text, FontSpec f, float size, float ls = 0) =>
        TextKit.Layout(_g.Device, text, f, size, 100000, CanvasHorizontalAlignment.Left, ls);

    private CanvasTextLayout V(string text, FontSpec f, float size, float ls)
    {
        using var fmt = TextKit.Format(f, size, false, vertical: true);
        var l = new CanvasTextLayout(_g.Device, text, fmt, 100000, 100000);
        if (ls != 0) l.SetCharacterSpacing(0, text.Length, 0, ls, 0);
        return l;
    }

    private static void DrawCentered(CanvasDrawingSession ds, CanvasTextLayout l, float cx, float cy, Color c)
    {
        var ink = l.DrawBounds;
        var lb = l.LayoutBounds;
        // 横向按墨迹居中，纵向按排版框居中（竖排时纵向是字的排列方向）
        float x = cx - (float)(ink.X + ink.Width / 2);
        float y = cy - (float)(lb.Y + lb.Height / 2);
        ds.DrawTextLayout(l, x, y, c);
    }

    private static string Short(string label)
    {
        var t = label.Trim();
        var cjk = t.FirstOrDefault(ch => TextKit.Cjk().IsMatch(ch.ToString()));
        return cjk != default ? cjk.ToString() : t[..1].ToUpperInvariant();
    }

    // ---------------- 画 ----------------

    public ChromeArt Draw(RailModel m, float height, int focus)
    {
        var surface = _g.Surface((int)Math.Ceiling(HomeWidth * Dpi), (int)Math.Ceiling(height * Dpi));
        Items.Clear();
        using (var ds = CanvasComposition.CreateDrawingSession(surface))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(Dpi);
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            DrawHome(ds, m, height, focus);
        }
        return new ChromeArt(surface, new Vector2(HomeWidth, height));
    }

    private void Focus(CanvasDrawingSession ds, Rect r, float radius)
    {
        var rr = new Rect(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
        ds.FillRoundedRectangle(rr, radius + 2, radius + 2, Gfx.WithAlpha(Theme.Ink, 0.12));
        ds.DrawRoundedRectangle(new Rect(rr.X + 1, rr.Y + 1, rr.Width - 2, rr.Height - 2), radius + 1, radius + 1, Theme.Ink, 2);
    }

    private void DrawHome(CanvasDrawingSession ds, RailModel m, float H, int focus)
    {
        const float W = HomeWidth, inner = W - PadX * 2;
        float cx = W / 2;

        // ---- 自下而上先定位置：最底下时间 + 电池，上面工具行，再上面账户（头像在上、名字在下）----
        var time = this.H(m.Now.ToString("HH:mm"), TextKit.Ui(700), 16);
        var tb = time.LayoutBounds;
        float statusH = (float)tb.Height;
        float statusY = H - PadBottom - statusH;
        const float tool = 30, toolGap = 5;
        float toolsY = statusY - 12 - tool;
        const float av = 40;
        var name = this.H(m.User, TextKit.Ui(600), 11);
        float nameH = (float)name.LayoutBounds.Height;
        float accH = av + 6 + nameH;
        float accY = toolsY - 16 - accH;
        float navTop = PadTop, navBottom = accY - 18;

        // ---- 导航区（页签；先登记，焦点项的顺序才是从上到下）----
        using (ds.CreateLayer(1, new Rect(0, navTop - 4, W, Math.Max(0, navBottom - navTop + 8))))
        {
            if (m.SettingsOpen) DrawSettings(ds, m, navTop, navBottom, focus);
            else DrawNav(ds, m, navTop, navBottom, focus);
        }

        // ---- 账户：头像在上，名字在下 ----
        {
            var avRect = new Rect(cx - av / 2, accY, av, av);
            DrawAvatar(ds, m, avRect);
            float nameW = Math.Min((float)name.LayoutBounds.Width, inner);
            using (ds.CreateLayer(1, new Rect(cx - nameW / 2, accY + av + 6 - 2, nameW, nameH + 4)))
                ds.DrawTextLayout(name, cx - nameW / 2 - (float)name.LayoutBounds.X, accY + av + 6 - (float)name.LayoutBounds.Y, Gfx.WithAlpha(Theme.Ink, 0.85));
            Add(RailKind.Account, 0, new Rect(PadX, accY - 4, inner, accH + 8));
        }

        // ---- 工具行 ----
        var tools = new[] { (RailKind.Search, ""), (RailKind.Settings, ""), (RailKind.Explorer, "") };
        float tx = cx - (tools.Length * tool + (tools.Length - 1) * toolGap) / 2;
        for (int i = 0; i < tools.Length; i++)
        {
            var r = new Rect(tx + i * (tool + toolGap), toolsY, tool, tool);
            bool pressed = (tools[i].Item1 == RailKind.Search && m.SearchOpen) || (tools[i].Item1 == RailKind.Settings && m.SettingsOpen);
            ds.FillRoundedRectangle(r, 10, 10, Gfx.WithAlpha(Theme.Ink, pressed ? 0.16 : 0.06));
            var icon = this.H(tools[i].Item2, new FontSpec(IconFont, 400), 15);
            DrawCentered(ds, icon, (float)(r.X + tool / 2), (float)(r.Y + tool / 2), pressed ? Theme.Ink : Gfx.WithAlpha(Theme.Ink, 0.8));
            Add(tools[i].Item1, 0, r, row: 1000);
        }

        // ---- 最底下：时间 + 电池 ----
        float battW = 0;
        CanvasTextLayout? pct = null;
        if (m.Battery.HasBattery)
        {
            pct = this.H($"{m.Battery.Percent}", TextKit.Ui(600), 10);
            battW = 22 + 3 + (float)pct.LayoutBounds.Width;
        }
        float rowW = (float)tb.Width + (battW > 0 ? 8 + battW : 0);
        float x0 = cx - rowW / 2;
        ds.DrawTextLayout(time, x0 - (float)tb.X, statusY - (float)tb.Y, Gfx.WithAlpha(Theme.Ink, 0.9));
        if (pct != null)
        {
            float bx = x0 + (float)tb.Width + 8, by = statusY + statusH / 2 - 5;
            DrawBattery(ds, bx, by, m.Battery);
            ds.DrawTextLayout(pct, bx + 25 - (float)pct.LayoutBounds.X, statusY + statusH / 2 - (float)pct.LayoutBounds.Height / 2 - (float)pct.LayoutBounds.Y, Theme.SeamInkSoft);
        }

        // 焦点高亮画在最上面（导航区之外的几项）
        if (focus >= 0 && focus < Items.Count)
        {
            var it = Items[focus];
            if (it.Kind is RailKind.Account or RailKind.Search or RailKind.Settings or RailKind.Explorer)
                Focus(ds, it.Bounds, it.Kind == RailKind.Account ? 12 : 10);
        }
    }

    private void Add(RailKind kind, int index, Rect r, int row = -1) =>
        Items.Add(new RailItem { Kind = kind, Index = index, Bounds = r, Row = row });

    private void DrawBattery(CanvasDrawingSession ds, float x, float y, BatteryState b)
    {
        var body = new Rect(x, y, 20, 10);
        ds.DrawRoundedRectangle(new Rect(x + 0.5, y + 0.5, 19, 9), 2.5f, 2.5f, Theme.SeamInkSoft, 1);
        ds.FillRoundedRectangle(new Rect(x + 20.5, y + 3, 1.5, 4), 0.75f, 0.75f, Theme.SeamInkSoft);
        float fill = 16 * Math.Clamp(b.Percent, 0, 100) / 100f;
        var color = b.Percent <= 15 && !b.OnAc ? Gfx.Rgba(0xfd, 0x5c, 0x47) : b.Charging ? Gfx.Rgba(0x7d, 0xd8, 0x8a) : Theme.Ink;
        ds.FillRoundedRectangle(new Rect(x + 2, y + 2, Math.Max(1, fill), 6), 1.2f, 1.2f, color);
        if (b.Charging || b.OnAc)
        {
            var bolt = H("", new FontSpec(IconFont, 400), 9);
            DrawCentered(ds, bolt, x + 10, y + 5, Theme.Bg);
        }
        _ = body;
    }

    private void DrawAvatar(CanvasDrawingSession ds, RailModel m, Rect r)
    {
        using var geo = CanvasGeometry.CreateEllipse(ds, (float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2), (float)r.Width / 2, (float)r.Height / 2);
        if (m.Avatar != null)
        {
            using (ds.CreateLayer(1, geo))
                ds.DrawImage(m.Avatar, r, m.Avatar.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
        }
        else
        {
            ds.FillGeometry(geo, Gfx.WithAlpha(Theme.Ink, 0.14));
            var ch = H(m.User.Length > 0 ? m.User[..1] : "?", TextKit.Ui(700), 13);
            DrawCentered(ds, ch, (float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2), Theme.Ink);
        }
        ds.DrawGeometry(geo, Gfx.WithAlpha(Theme.Ink, 0.18), 1);
    }

    private void DrawNav(CanvasDrawingSession ds, RailModel m, float top, float bottom, int focus)
    {
        const float W = HomeWidth, inner = W - PadX * 2;
        float cx = W / 2;
        // 先量：全名页签 / 一个字的页签，各需多高
        var tabFont = TextKit.Ui(700);
        List<CanvasTextLayout> MakeTabs(bool shortLabels) => m.Tabs.Select(t => V(shortLabels ? Short(t) : t, tabFont, 14, shortLabels ? 0 : 0.18f * 14)).ToList();
        float TabsHeight(List<CanvasTextLayout> ls) => ls.Sum(l => (float)l.LayoutBounds.Height + 18 + 2) + 4 * Math.Max(0, ls.Count - 1);

        var scFont = TextKit.Ui(500);
        var scLayouts = m.Shortcuts.Select(s => V(s, scFont, 12, 0.08f * 12)).ToList();
        // 合集入口：竖排短列并排，一行放不下就折到下一行
        const float scColW = 22, scGap = 4;
        int perRow = Math.Max(1, (int)((inner + scGap) / (scColW + scGap)));
        var scRows = new List<List<int>>();
        for (int i = 0; i < scLayouts.Count; i++) { if (i % perRow == 0) scRows.Add(new List<int>()); scRows[^1].Add(i); }
        float RowH(List<int> row) => row.Max(i => (float)scLayouts[i].LayoutBounds.Height) + 12;
        float scH = scRows.Count == 0 ? 0 : scRows.Sum(RowH) + 6 * (scRows.Count - 1) + 16 + 1 + 16;   // 含分隔线上下

        float searchH = m.SearchOpen ? 32 + 6 + 14 + 12 : 0;
        float avail = bottom - top;
        var tabLs = MakeTabs(false);
        bool shortTabs = false;
        if (searchH + TabsHeight(tabLs) + scH > avail) { tabLs = MakeTabs(true); shortTabs = true; }
        float content = searchH + TabsHeight(tabLs) + scH;

        // 放不下就滚：保证焦点那一项在导航区里
        int itemsBefore = Items.Count;
        float y = top;
        var placed = new List<(RailItem item, Action<float> draw)>();

        if (m.SearchOpen)
        {
            var r = new Rect(PadX, y, inner, 32);
            Add(RailKind.SearchField, 0, r);
            int idx = Items.Count - 1;
            placed.Add((Items[idx], off =>
            {
                var rr = new Rect(r.X, r.Y - off, r.Width, r.Height);
                ds.FillRoundedRectangle(rr, 8, 8, Gfx.WithAlpha(Theme.Ink, 0.10));
                // 框里的字由叠在上面的输入框（XAML TextBox，支持中文输入法）来画
                var hint = H(m.Matches < 0 ? "B 关闭" : $"{m.Matches} 个结果 · B 关闭", TextKit.Ui(500), 10);
                DrawCentered(ds, hint, cx, (float)(rr.Y + rr.Height + 6 + 7), Theme.SeamInkSoft);
                if (focus == idx) Focus(ds, rr, 8);
            }));
            y += searchH;
        }

        for (int i = 0; i < tabLs.Count; i++)
        {
            var l = tabLs[i];
            float h = (float)l.LayoutBounds.Height + 18 + 2;
            float w = (float)l.LayoutBounds.Width + 8 + 2;
            var r = new Rect(cx - w / 2, y, w, h);
            Add(RailKind.Tab, i, r);
            int idx = Items.Count - 1;
            bool active = i == m.ActiveTab;
            placed.Add((Items[idx], off =>
            {
                var rr = new Rect(r.X, r.Y - off, r.Width, r.Height);
                if (active) ds.FillRoundedRectangle(rr, 10, 10, Gfx.WithAlpha(Theme.Ink, 0.14));
                DrawCentered(ds, l, cx, (float)(rr.Y + rr.Height / 2), active ? Theme.Ink : Theme.SeamInkSoft);
                if (focus == idx) Focus(ds, rr, 10);
            }));
            y += h + 4;
        }

        if (scRows.Count > 0)
        {
            // 合集入口贴在导航区底部（内容放得下时），与页签之间一道分隔线
            float scTop = content <= avail ? bottom - (scH - 16 - 1 - 16) : y + 16 + 1 + 16 - 4;
            float ruleY = scTop - 16 - 0.5f;
            placed.Add((new RailItem { Kind = RailKind.Back, Bounds = new Rect(0, ruleY, 0, 0) }, off =>
                ds.FillRectangle(cx - 12, ruleY - off, 24, 1, Gfx.WithAlpha(Theme.Ink, 0.16))));
            float ry = scTop;
            for (int rIdx = 0; rIdx < scRows.Count; rIdx++)
            {
                var row = scRows[rIdx];
                float rh = RowH(row);
                float rowW = row.Count * scColW + (row.Count - 1) * scGap;
                float rx = cx - rowW / 2;
                foreach (int i in row)
                {
                    var l = scLayouts[i];
                    float h = (float)l.LayoutBounds.Height + 12;
                    var r = new Rect(rx, ry, scColW, h);
                    Add(RailKind.Shortcut, i, r, row: rIdx);
                    int idx = Items.Count - 1;
                    placed.Add((Items[idx], off =>
                    {
                        var rr = new Rect(r.X, r.Y - off, r.Width, r.Height);
                        DrawCentered(ds, l, (float)(rr.X + rr.Width / 2), (float)(rr.Y + rr.Height / 2), Gfx.WithAlpha(Theme.Ink, 0.62));
                        if (focus == idx) Focus(ds, rr, 8);
                    }));
                    rx += scColW + scGap;
                }
                ry += rh + 6;
            }
        }

        // 滚动：内容高于导航区时，让焦点项完整露出来
        if (content > avail && focus >= itemsBefore && focus < Items.Count)
        {
            var fb = Items[focus].Bounds;
            if (fb.Y - _scroll < top + 6) _scroll = (float)fb.Y - top - 6;
            if (fb.Y + fb.Height - _scroll > bottom - 6) _scroll = (float)(fb.Y + fb.Height) - bottom + 6;
        }
        if (content <= avail) _scroll = 0;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, content - avail));
        for (int i = itemsBefore; i < Items.Count; i++)
        {
            var b = Items[i].Bounds;
            Items[i].Bounds = new Rect(b.X, b.Y - _scroll, b.Width, b.Height);
        }
        foreach (var (_, draw) in placed) draw(_scroll);
        if (content > avail)
        {
            // 底边淡出，提示下面还有
            var fade = new CanvasLinearGradientBrush(ds, Gfx.WithAlpha(Theme.Bg, 0), Gfx.WithAlpha(Theme.Bg, 0.9)) { StartPoint = new Vector2(0, bottom - 16), EndPoint = new Vector2(0, bottom + 4) };
            ds.FillRectangle(0, bottom - 16, W, 20, fade);
        }
        _ = shortTabs;
    }

    private void DrawSettings(CanvasDrawingSession ds, RailModel m, float top, float bottom, int focus)
    {
        const float W = HomeWidth, inner = W - PadX * 2;
        var title = H("设置", TextKit.Ui(800), 13, 0.3f * 13);
        DrawCentered(ds, title, W / 2, top + 9, Theme.Ink);
        float y = top + 28;
        for (int i = 0; i < m.Settings.Count; i++)
        {
            var (label, value) = m.Settings[i];
            var r = new Rect(PadX, y, inner, 44);
            Add(RailKind.Setting, i, r);
            int idx = Items.Count - 1;
            ds.FillRoundedRectangle(r, 10, 10, Gfx.WithAlpha(Theme.Ink, 0.06));
            var l1 = H(label, TextKit.Ui(500), 10);
            var l2 = H(value, TextKit.Ui(700), 12);
            using (ds.CreateLayer(1, r))
            {
                ds.DrawTextLayout(l1, (float)r.X + 8 - (float)l1.LayoutBounds.X, (float)r.Y + 6 - (float)l1.LayoutBounds.Y, Theme.SeamInkSoft);
                ds.DrawTextLayout(l2, (float)r.X + 8 - (float)l2.LayoutBounds.X, (float)r.Y + 22 - (float)l2.LayoutBounds.Y, Theme.Ink);
            }
            if (focus == idx) Focus(ds, r, 10);
            y += 44 + 6;
            if (y + 44 > bottom) break;
        }
    }

    // ---------------- 焦点走法 ----------------

    /// <summary>默认落点：当前页签（设置打开时第一行，搜索打开时搜索框）。</summary>
    public int DefaultFocus(RailModel m)
    {
        int i = Items.FindIndex(it => m.SettingsOpen ? it.Kind == RailKind.Setting : m.SearchOpen ? it.Kind == RailKind.SearchField : it.Kind == RailKind.Tab && it.Index == m.ActiveTab);
        return i >= 0 ? i : Items.FindIndex(it => it.Kind == RailKind.Search);
    }

    /// <summary>离屏幕纵坐标 y 最近的一项（从墙上横着走进来时用）。</summary>
    public int NearestTo(float y)
    {
        int best = -1; double d = double.MaxValue;
        for (int i = 0; i < Items.Count; i++)
        {
            var b = Items[i].Bounds;
            double dd = Math.Abs(b.Y + b.Height / 2 - y);
            if (dd < d) { d = dd; best = i; }
        }
        return best;
    }

    /// <summary>在竖条里走一步。返回新焦点；-1 表示从左边出去，-2 表示从右边出去。</summary>
    public int Step(int focus, Direction dir)
    {
        if (focus < 0 || focus >= Items.Count) return 0;
        var cur = Items[focus];
        if (dir is Direction.Left or Direction.Right)
        {
            if (cur.Row >= 0)
            {
                int j = dir == Direction.Left ? focus - 1 : focus + 1;
                if (j >= 0 && j < Items.Count && Items[j].Row == cur.Row) return j;
            }
            return dir == Direction.Left ? -1 : -2;
        }
        // 上下：按「行」走（同一行的几项算一行），落到新行里横向最近的一项
        var rows = new List<List<int>>();
        for (int i = 0; i < Items.Count; i++)
        {
            if (rows.Count > 0 && Items[i].Row >= 0 && Items[rows[^1][0]].Row == Items[i].Row) rows[^1].Add(i);
            else rows.Add(new List<int> { i });
        }
        int ri = rows.FindIndex(r => r.Contains(focus));
        int nr = ri + (dir == Direction.Down ? 1 : -1);
        if (nr < 0 || nr >= rows.Count) return focus;
        double cx = cur.Bounds.X + cur.Bounds.Width / 2;
        return rows[nr].OrderBy(i => Math.Abs(Items[i].Bounds.X + Items[i].Bounds.Width / 2 - cx)).First();
    }

    public int HitTest(float x, float y)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Bounds.Contains(new Point(x, y))) return i;
        return -1;
    }
}
