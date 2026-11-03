// 卡片上的文字：标题排版（D「电影字幕」默认 / B「极简黑体」）、角标、聚焦卡的按钮排、无图时的大字。
// 规则逐条照 src/styles.css（.copy / .kicker / strong / .tail / small / 容器查询 / 展开态），字号单位 cqi = 卡宽 / 100。
// 每张图按「世界 px × 栅格倍率」画进 CompositionDrawingSurface，同一款游戏、同一种卡尺寸共用一张。
using System.Numerics;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

/// <summary>一张画好的文字图：Rect 是它在卡面（宽 W 高 H，世界 px）里的位置，含阴影留边。</summary>
public sealed class TextArt
{
    public required CompositionDrawingSurface Surface;
    public required Engine.Rect Rect;
    public int Refs;
    public long LastUse;
}

public sealed class CardText
{
    private readonly Gfx _g;
    private readonly Dictionary<string, TextArt> _cache = new();
    private long _tick;
    private const float Margin = 20;   // 阴影留边（世界 px）

    /// <summary>Folia 的字号按视口 vw 计算，其他样式按卡宽计算。</summary>
    public float ViewportWidth { get; set; } = 1920;

    public CardText(Gfx g) { _g = g; }

    public static float Clamp(float lo, float v, float hi) => Math.Max(lo, Math.Min(hi, v));

    public TextArt Acquire(string key, Func<TextArt> make)
    {
        if (!_cache.TryGetValue(key, out var art))
        {
            art = make();
            _cache[key] = art;
        }
        art.Refs++;
        art.LastUse = ++_tick;
        return art;
    }

    public void Release(TextArt? art)
    {
        if (art == null) return;
        art.Refs--;
        if (_cache.Count > 600) Trim();
    }

    private void Trim()
    {
        foreach (var (k, a) in _cache.Where(e => e.Value.Refs <= 0).OrderBy(e => e.Value.LastUse).Take(_cache.Count - 400).ToList())
        {
            _cache.Remove(k);
            a.Surface.Dispose();
        }
    }

    /// <summary>栅格倍率或设备变了：全部作废（调用方重画）。</summary>
    public void Clear()
    {
        foreach (var a in _cache.Values) if (a.Refs <= 0) a.Surface.Dispose();
        _cache.Clear();
    }

    private CompositionDrawingSurface Draw(Engine.Rect rect, Action<CanvasDrawingSession> paint)
    {
        float rs = _g.RasterScale;
        var surface = _g.Surface((int)Math.Ceiling(rect.Width * rs), (int)Math.Ceiling(rect.Height * rs));
        using var ds = CanvasComposition.CreateDrawingSession(surface);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateTranslation((float)-rect.X, (float)-rect.Y) * Matrix3x2.CreateScale(rs);
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        paint(ds);
        return surface;
    }

    // ---------------- 标题 ----------------

    public static string CopyKey(LibraryItem item, double w, double h, bool expanded, TitleStyle style) =>
        $"copy|{item.Id}|{Math.Round(w)}x{Math.Round(h)}|{(expanded ? 1 : 0)}|{style}|{(expanded ? item.LastPlayedDays?.ToString() ?? "-" : "")}|title{item.Title.Length}:{item.Title}";

    /// <summary>标题块（不含展开态的按钮排）。</summary>
    public TextArt Copy(LibraryItem item, double w, double h, bool expanded, TitleStyle style) =>
        Acquire(CopyKey(item, w, h, expanded, style) + (style == TitleStyle.Folia ? $"|vw{Math.Round(ViewportWidth)}" : ""),
            () => BuildCopy(item, (float)w, (float)h, expanded, style));

    private TextArt BuildCopy(LibraryItem item, float w, float h, bool expanded, TitleStyle style)
    {
        if (style is TitleStyle.Folia or TitleStyle.Quiet or TitleStyle.Editorial)
            return BuildSimpleCopy(item, w, h, expanded, style);
        var dev = _g.Device;
        bool b = style == TitleStyle.B;
        float cqi = w / 100;
        bool narrow = w <= 300, shortH = h <= 300;
        string main = item.Main ?? item.Title;
        bool latin = !TextKit.Cjk().IsMatch(main);
        float left = expanded ? 32 : 6 * cqi;
        float bottom = expanded ? 30 : b ? 5.5f * cqi : 7 * cqi;
        float copyW = w - left * 2;
        float gap = (b ? 0.35f : 0.45f) * 16;
        var align = b ? CanvasHorizontalAlignment.Left : CanvasHorizontalAlignment.Center;

        // 每一行元素：高度、绘制
        var blocks = new List<(float height, float marginTop, Action<CanvasDrawingSession, float> draw)>();

        // 小字原名（kicker）：两侧细线
        if (!b && item.Kicker is { Length: > 0 } kicker && (expanded || !narrow))
        {
            float size = expanded ? 22 : Clamp(11, 3.8f * cqi, 20);
            float ls = 0.08f * size;
            var lay = TextKit.Layout(dev, kicker, TextKit.Kicker, size, 100000, CanvasHorizontalAlignment.Left, ls);
            float tw = (float)lay.LayoutBounds.Width;
            float lh = (float)lay.LayoutBounds.Height;
            float g2 = 0.8f * size;
            float basis = (expanded ? 4 : 3) * size, minLine = size;
            float line = Clamp(minLine, (copyW - tw - 2 * g2) / 2, basis);
            float total = tw + 2 * (line + g2);
            blocks.Add((lh, 0, (ds, y) =>
            {
                float x0 = left + copyW / 2 - total / 2;
                // overflow: hidden：超宽时两端裁掉（仍居中）
                using var clip = ds.CreateLayer(1, new Windows.Foundation.Rect(left, y - 2, copyW, lh + 4));
                ds.FillRectangle(x0, y + lh / 2 - 0.5f, line, 1, Gfx.Rgba(255, 255, 255, 0.55 * 0.9));
                ds.DrawTextLayout(lay, x0 + line + g2, y, TextKit.Ink(0.9));
                ds.FillRectangle(x0 + line + g2 + tw + g2, y + lh / 2 - 0.5f, line, 1, Gfx.Rgba(255, 255, 255, 0.55 * 0.9));
            }));
        }

        // 主标题
        {
            FontSpec font; float size, ls, lh; float maxW = copyW;
            if (b)
            {
                font = TextKit.UiB(700); size = Clamp(15, 8.6f * cqi, 56); ls = 0; lh = 1.2f * size;
                if (expanded) maxW = copyW * 0.8f;
            }
            else if (latin)
            {
                font = TextKit.TitleLatin; size = expanded ? Clamp(32, 6.4f * cqi, 64) : Clamp(14, 7 * cqi, 46); ls = 0.08f * size; lh = 1.12f * size;
            }
            else
            {
                font = TextKit.TitleCjk;
                size = expanded ? Clamp(32, 6.4f * cqi, 64) : narrow ? 8.4f * cqi : Clamp(14, 6.6f * cqi, 44);
                ls = (narrow && !expanded ? 0.18f : 0.26f) * size; lh = 1.35f * size;
            }
            float pad = 0.06f * size;
            float indent = b ? 0 : ls;   // text-indent = 字距，抵消行尾字距
            var lines = BalanceLines(main, font, size, ls, maxW, indent, latin);
            if (lines != null)
            {
                // 一行或两行（text-wrap: balance：在仍是这么多行的前提下取最窄宽度，再按贪心断行；首行算上缩进）
                var lays = lines.Select(t => TextKit.Layout(dev, t.text, font, size, 100000, CanvasHorizontalAlignment.Left, ls, lh)).ToList();
                float height = lines.Count * lh + 2 * pad;
                blocks.Add((height, 0, (ds, y) =>
                {
                    for (int i = 0; i < lays.Count; i++)
                    {
                        float boxW = lines[i].width + (i == 0 ? indent : 0);
                        float x = b ? left : left + (copyW - boxW) / 2;
                        if (i == 0) x += indent;
                        ds.DrawTextLayout(lays[i], x, y + pad + i * lh, TextKit.Ink());
                    }
                }));
            }
            else
            {
                // 两行放不下：截断成两行，末尾省略号（-webkit-line-clamp: 2）
                var lay = TextKit.Layout(dev, main, font, size, maxW, align, ls, lh, maxLines: 2);
                float layW = (float)lay.RequestedSize.Width;
                blocks.Add((2 * lh + 2 * pad, 0, (ds, y) =>
                {
                    float x = b ? left : left + (copyW - layW) / 2 + indent / 2;
                    ds.DrawTextLayout(lay, x, y + pad, TextKit.Ink());
                }));
            }
        }

        // 副标题（tail）
        if (item.Tail is { Length: > 0 } tail)
        {
            FontSpec font = b ? TextKit.UiB(500) : TextKit.TitleCjk;
            float size = b ? Clamp(11, 3.8f * cqi, 22) : expanded ? 24 : narrow ? 11 : Clamp(11, 3.6f * cqi, 22);
            float ls = b ? 0 : 0.14f * size;
            var lay = TextKit.Layout(dev, tail, font, size, copyW, align, ls);
            float lh = LineHeight(lay, font, size, tail);
            blocks.Add((lh, 0, (ds, y) => ds.DrawTextLayout(lay, left, y, TextKit.Ink(0.85))));
        }

        // 开发商 · 年份（小卡、矮卡不显示；展开态换成 focus-meta）
        if (!expanded && !narrow && !shortH && item.Subtitle.Length > 0)
        {
            var font = TextKit.Ui(b ? (ushort)500 : (ushort)600);
            float size = Clamp(10, 2.8f * cqi, 14);
            float ls = b ? 0 : 0.06f * size;
            var lay = TextKit.Layout(dev, item.Subtitle, font, size, copyW, align, ls);
            float lh = LineHeight(lay, font, size, item.Subtitle);
            blocks.Add((lh, 0, (ds, y) => ds.DrawTextLayout(lay, left, y, TextKit.Ink(0.62))));
        }

        if (expanded)
        {
            string played = item.LastPlayedDays is int d ? (d == 0 ? "今天玩过" : $"{d} 天前玩过") : "";
            var font = TextKit.Ui(650);
            const float size = 17;
            string text = played.Length > 0 ? $"{item.Subtitle} · {played}" : item.Subtitle;
            var lay = TextKit.Layout(dev, text, font, size, copyW, align);
            if (played.Length > 0)
            {
                int dot = item.Subtitle.Length + 1;
                lay.SetColor(dot, 1, TextKit.Ink(0.85 * 0.5));
            }
            float lh = LineHeight(lay, font, size, text);
            blocks.Add((lh, 7, (ds, y) => ds.DrawTextLayout(lay, left, y, TextKit.Ink(0.85))));
        }

        // 竖向堆叠：从下往上（底边对齐 bottom；展开态底下还有按钮排）
        float contentH = 0;
        for (int i = 0; i < blocks.Count; i++) contentH += blocks[i].height + blocks[i].marginTop + (i > 0 ? gap : 0);
        float bottomEdge = h - bottom;
        if (expanded) bottomEdge -= ActionsHeight + 26 + gap;
        float top = bottomEdge - contentH;
        var rect = new Engine.Rect(left - Margin, top - Margin, copyW + Margin * 2, contentH + Margin * 2);
        var surface = Draw(rect, ds =>
        {
            using var cl = new CanvasCommandList(ds);
            using (var cds = cl.CreateDrawingSession())
            {
                cds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                float y = top;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (i > 0) y += gap;
                    y += blocks[i].marginTop;
                    blocks[i].draw(cds, y);
                    y += blocks[i].height;
                }
            }
            // text-shadow: 0 1px 12px rgb(0 0 0 / 45%)
            using var shadow = new ShadowEffect { Source = cl, BlurAmount = 6, ShadowColor = Gfx.Rgba(0, 0, 0, 0.45), Optimization = EffectOptimization.Speed };
            ds.DrawImage(shadow, 0, 1);
            ds.DrawImage(cl);
        });
        return new TextArt { Surface = surface, Rect = rect };
    }

    /// <summary>
    /// 简约样式使用完整游戏名。Folia 按 Bravais / wall.css 的留边、字距、行高、三行标题与元信息参数重画，
    /// 不引入原项目组件代码；另两款只保留标题和一行元信息，让封面保留更多空间。
    /// </summary>
    private TextArt BuildSimpleCopy(LibraryItem item, float w, float h, bool expanded, TitleStyle style)
    {
        bool folia = style == TitleStyle.Folia, editorial = style == TitleStyle.Editorial;
        bool leftAligned = TitleStyles.IsLeftAligned(style);
        float cqi = w / 100;
        float left = expanded ? 32 : folia ? 17 : Clamp(18, 6 * cqi, 30);
        float bottom = expanded ? (folia ? 28 : 30) : folia ? 17 : Clamp(18, 6 * cqi, 30);
        float copyW = Math.Max(1, w - left * 2);
        float size = folia ? expanded ? Clamp(48, ViewportWidth * .06f, 82) : Clamp(22, ViewportWidth * .02f, 36)
            : expanded ? Clamp(30, 5.5f * cqi, 52) : editorial ? Clamp(17, 6.2f * cqi, 34) : Clamp(16, 5.8f * cqi, 32);
        float spacing = folia ? -.045f * size : editorial ? .045f * size : .01f * size;
        float lineHeight = size * (folia ? expanded ? .95f : .96f : editorial ? 1.24f : 1.18f);
        float pad = size * (folia ? .14f : .08f);
        float gap = folia ? 5 : 8;
        float metaSize = expanded ? 17 : folia ? 13 : 12;
        var align = leftAligned ? CanvasHorizontalAlignment.Left : CanvasHorizontalAlignment.Center;
        string main = item.Title;
        var titleFont = folia ? TextKit.FoliaTitle : editorial
            ? TextKit.Cjk().IsMatch(main) ? new FontSpec(TextKit.SerifCjk, 500) : new FontSpec(TextKit.SerifDisplay, 400)
            : TextKit.UiB(500);
        string played = expanded && item.LastPlayedDays is int days ? days == 0 ? "今天玩过" : $"{days} 天前玩过" : "";
        string meta = played.Length == 0 ? item.Subtitle : item.Subtitle.Length == 0 ? played : $"{item.Subtitle} · {played}";
        using var metadata = TextKit.Layout(_g.Device, meta, TextKit.Ui(650), metaSize, copyW, align);
        float metaH = meta.Length > 0 ? (float)metadata.LayoutBounds.Height : 0;
        float bottomEdge = h - bottom - (expanded ? ActionsHeight + 26 + gap : 0);
        float maxTitleW = folia && expanded ? copyW * .76f : copyW;
        // 矮横卡保留顶部角标空间；不让第三行标题被卡面裁成半行。
        float availableH = Math.Max(lineHeight, bottomEdge - 42 - (metaH > 0 ? metaH + gap : 0));
        int maxLines = Math.Clamp((int)Math.Floor(availableH / lineHeight), 1, folia && !expanded ? 3 : 2);
        using var title = TextKit.Layout(_g.Device, main, titleFont, size, maxTitleW, align, spacing, lineHeight, maxLines, balance: true);
        float titleH = Math.Min(maxLines, Math.Max(1, title.LineCount)) * lineHeight;
        float contentH = titleH + (metaH > 0 ? metaH + gap + (expanded ? 7 : 0) : 0);
        float top = bottomEdge - contentH;
        var rect = new Engine.Rect(left - Margin, top - pad - Margin, copyW + Margin * 2, contentH + pad * 2 + Margin * 2);
        var surface = Draw(rect, ds =>
        {
            using var cl = new CanvasCommandList(ds);
            using (var cds = cl.CreateDrawingSession())
            {
                cds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                float titleX = leftAligned ? left : left + (copyW - (float)title.RequestedSize.Width) / 2;
                cds.DrawTextLayout(title, titleX, top, TextKit.Ink());
                if (metaH > 0) cds.DrawTextLayout(metadata, left, top + titleH + gap + (expanded ? 7 : 0), TextKit.Ink(folia ? .78 : .64));
            }
            using var shadow = new ShadowEffect { Source = cl, BlurAmount = folia ? 3 : 5, ShadowColor = Gfx.Rgba(0, 0, 0, .50), Optimization = EffectOptimization.Speed };
            ds.DrawImage(shadow, 0, 1);
            ds.DrawImage(cl);
        });
        return new TextArt { Surface = surface, Rect = rect };
    }

    /// <summary>单行宽度（含每个字后面的字距，与 Chrome 一样）。</summary>
    private float Advance(string text, FontSpec font, float size, float ls)
    {
        if (text.Length == 0) return 0;
        using var l = TextKit.Layout(_g.Device, text, font, size, 100000, CanvasHorizontalAlignment.Left, ls);
        return (float)l.LayoutBounds.Width;
    }

    /// <summary>
    /// 标题断行：一行放得下就一行；否则在所有断点里找「两行都放得下、最长那行最短」的宽度 x*，
    /// 再按 x* 贪心断（首行尽量多放）——等同 Chrome 的 text-wrap: balance。两行也放不下返回 null。
    /// </summary>
    private List<(string text, float width)>? BalanceLines(string s, FontSpec font, float size, float ls, float maxW, float indent, bool latin)
    {
        // 整行排一次，用光标位置得到每个前缀的宽度（不再每个断点排两次）
        using var full = TextKit.Layout(_g.Device, s, font, size, 100000, CanvasHorizontalAlignment.Left, ls);
        float whole = (float)full.LayoutBounds.Width;
        if (indent + whole <= maxW + 0.5f) return new() { (s, whole) };
        float x0 = full.GetCaretPosition(0, false).X;
        float At(int k) => full.GetCaretPosition(k, false).X - x0;
        var cands = new List<(int k, float w1, float w2)>();
        for (int k = 1; k < s.Length; k++)
        {
            char a = s[k - 1], c = s[k];
            if (char.IsLowSurrogate(c)) continue;
            bool cjk = TextKit.Cjk().IsMatch(a.ToString()) || TextKit.Cjk().IsMatch(c.ToString());
            bool ok = a == ' ' || a == '-' || (cjk && !"，。、：；！？）」』》·~～".Contains(c) && !"（「『《".Contains(a));
            if (!ok) continue;
            string l1 = s[..k].TrimEnd(), l2 = s[k..].TrimStart();
            if (l1.Length == 0 || l2.Length == 0) continue;
            // 断在空格处时两边都不含空格
            int e1 = k; while (e1 > 0 && s[e1 - 1] == ' ') e1--;
            int s2 = k; while (s2 < s.Length && s[s2] == ' ') s2++;
            float w1 = indent + At(e1), w2 = whole - At(s2);
            if (w1 <= maxW + 0.5f && w2 <= maxW + 0.5f) cands.Add((k, w1, w2));
        }
        if (cands.Count == 0) return null;
        float target = cands.Min(e => Math.Max(e.w1, e.w2));
        var pick = cands.Where(e => e.w1 <= target + 0.01f).MaxBy(e => e.k);
        return new() { (s[..pick.k].TrimEnd(), pick.w1 - indent), (s[pick.k..].TrimStart(), pick.w2) };
    }

    private float LineHeight(CanvasTextLayout lay, FontSpec font, float size, string text)
    {
        return (float)lay.LayoutBounds.Height / Math.Max(1, lay.LineCount);
    }

    // ---------------- 聚焦卡按钮排 ----------------

    public const float ActionsHeight = 44;

    public TextArt Actions(double w, double h, TitleStyle style, bool install = false, int shots = 0) =>
        Acquire($"actions|{Math.Round(w)}x{Math.Round(h)}|{style}|{install}|{shots}", () => BuildActions((float)w, (float)h, style, install, shots));

    private TextArt BuildActions(float w, float h, TitleStyle style, bool install, int shots)
    {
        var dev = _g.Device;
        float left = 32, copyW = w - 64;
        var buttons = new List<(string, string, bool)> { ("A", install ? "安装游戏" : "开始游戏", true) };
        if (!install) buttons.Add(("Y", "游戏设置", false));
        buttons.Add(("B", install ? "返回" : "收起", false));
        var font = TextKit.Ui(700);
        var keyFont = TextKit.Ui(800);
        var labels = buttons.Select(bt => TextKit.Layout(dev, bt.Item2, font, 15, 100000, CanvasHorizontalAlignment.Left)).ToArray();
        var keys = buttons.Select(bt => TextKit.Layout(dev, bt.Item1, keyFont, 11, 22, CanvasHorizontalAlignment.Center)).ToArray();
        var widths = labels.Select(l => 16 + 22 + 8 + (float)l.LayoutBounds.Width + 16).ToArray();
        float total = widths.Sum() + 8 * (buttons.Count - 1);
        float x0 = TitleStyles.IsLeftAligned(style) ? left : left + (copyW - total) / 2;
        float y0 = h - (style == TitleStyle.Folia ? 28 : 30) - ActionsHeight;
        var rect = new Engine.Rect(left - Margin, y0 - Margin, copyW + Margin * 2, ActionsHeight + Margin * 2);
        var surface = Draw(rect, ds =>
        {
            using var cl = new CanvasCommandList(ds);
            using (var cds = cl.CreateDrawingSession())
            {
                cds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                float x = x0;
                for (int i = 0; i < buttons.Count; i++)
                {
                    bool primary = buttons[i].Item3;
                    double op = primary ? 1 : 0.65;
                    if (primary) cds.FillRoundedRectangle(x, y0, widths[i], ActionsHeight, 12, 12, TextKit.Ink(0.16));
                    float kx = x + 16, ky = y0 + (ActionsHeight - 22) / 2;
                    cds.FillEllipse(kx + 11, ky + 11, 11, 11, TextKit.Ink(0.22 * op));
                    // 单个键名字母按实际墨迹居中，避免字体的上升部、下降部和左右留白造成视觉偏移。
                    var kb = keys[i].DrawBounds;
                    cds.DrawTextLayout(keys[i], kx + 11 - (float)(kb.X + kb.Width / 2),
                        ky + 11 - (float)(kb.Y + kb.Height / 2), TextKit.Ink(op));
                    var lb = labels[i].DrawBounds;
                    cds.DrawTextLayout(labels[i], kx + 22 + 8 - (float)lb.X,
                        y0 + ActionsHeight / 2 - (float)(lb.Y + lb.Height / 2), TextKit.Ink(op));
                    x += widths[i] + 8;
                }
            }
            // filter: drop-shadow(0 1px 3px rgb(0 0 0 / 55%))
            using var shadow = new ShadowEffect { Source = cl, BlurAmount = 1.5f, ShadowColor = Gfx.Rgba(0, 0, 0, 0.55) };
            ds.DrawImage(shadow, 0, 1);
            ds.DrawImage(cl);
        });
        return new TextArt { Surface = surface, Rect = rect };
    }

    // ---------------- 角标 ----------------

    public TextArt Badge(string text, bool collection) =>
        Acquire($"badge|{text}|{collection}", () =>
        {
            var font = TextKit.Ui(800);
            const float size = 11;
            var lay = TextKit.Layout(_g.Device, text, font, size, 100000, CanvasHorizontalAlignment.Left, 0.12f * size);
            float tw = (float)lay.LayoutBounds.Width, th = (float)lay.LayoutBounds.Height;
            var rect = new Engine.Rect(14, 14, tw + 16, th + 10);
            var surface = Draw(rect, ds =>
            {
                ds.FillRectangle(14, 14, (float)rect.Width, (float)rect.Height, collection ? Gfx.Rgba(0xf4, 0xe0, 0x4d) : Gfx.Rgba(0, 0, 0, 0.62));
                ds.DrawTextLayout(lay, 14 + 8, 14 + 5, collection ? Gfx.Rgba(0x2b, 0x26, 0x00) : TextKit.Ink());
            });
            return new TextArt { Surface = surface, Rect = rect };
        });

    // ---------------- 无图时的大字 ----------------

    public TextArt Glyph(string title, double slotW, double slotH, double w, double h, bool expanded) =>
        Acquire($"glyph|{title}|{Math.Round(slotW)}x{Math.Round(slotH)}|{Math.Round(w)}x{Math.Round(h)}|{expanded}", () =>
        {
            string ch = char.IsSurrogate(title, 0) ? title[..2] : title[..1];
            float size = (float)Math.Round(Math.Min(slotW, slotH) * (expanded ? 0.8 : 0.92));
            var lay = TextKit.Layout(_g.Device, ch, new FontSpec("Noto Serif SC", 900), size, 100000, CanvasHorizontalAlignment.Left, 0, size);
            float gw = (float)lay.LayoutBounds.Width;
            // right: -.06em; bottom: -.2em; line-height: 1
            float x = (float)w + 0.06f * size - gw, y = (float)h + 0.2f * size - size;
            var rect = new Engine.Rect(0, 0, w, h);
            var surface = Draw(rect, ds => ds.DrawTextLayout(lay, x, y, TextKit.Ink(0.14)));
            return new TextArt { Surface = surface, Rect = rect };
        });
}
