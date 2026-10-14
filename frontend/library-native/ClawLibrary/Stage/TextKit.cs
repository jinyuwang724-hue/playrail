// Win2D 文字排版的小工具：按 CSS 的写法（字号、字重、字距 em、行高、单行省略、两行截断、balance 换行）出 CanvasTextLayout。
// 字体取网页样式表在本机实际落到的那一款：Noto Serif SC / Georgia / Segoe UI Variable Text / Noto Sans SC，中文缺字走系统回退（zh-CN）。
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace ClawLibrary.Stage;

public readonly record struct FontSpec(string Family, ushort Weight, bool Italic = false);

public static partial class TextKit
{
    public static readonly FontSpec TitleCjk = new("Noto Serif SC", 600);
    public static readonly FontSpec TitleLatin = new("Georgia", 700);          // Noto Serif Display / Noto Serif 本机没有 → Georgia
    public static readonly FontSpec Kicker = new("Georgia", 700, true);        // Cormorant Garamond 没有 → Georgia；600 没有 → 粗体
    public static FontSpec Ui(ushort weight) => new("Segoe UI Variable Text", weight);
    public static FontSpec UiB(ushort weight) => new("Noto Sans SC", weight);

    [GeneratedRegex("[぀-ヿ㐀-鿿＀-￯]")]
    public static partial Regex Cjk();

    public static CanvasTextFormat Format(FontSpec f, float size, bool wrap, CanvasHorizontalAlignment align = CanvasHorizontalAlignment.Left, bool ellipsis = false, bool vertical = false)
    {
        var fmt = new CanvasTextFormat
        {
            FontFamily = f.Family,
            FontSize = Math.Max(1, size),
            FontWeight = new Windows.UI.Text.FontWeight { Weight = f.Weight },
            FontStyle = f.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            WordWrapping = wrap ? CanvasWordWrapping.Wrap : CanvasWordWrapping.NoWrap,
            HorizontalAlignment = align,
            LocaleName = "zh-CN",
            Options = CanvasDrawTextOptions.EnableColorFont,
        };
        if (ellipsis)
        {
            fmt.TrimmingGranularity = CanvasTextTrimmingGranularity.Character;
            fmt.TrimmingSign = CanvasTrimmingSign.Ellipsis;
        }
        if (vertical) fmt.Direction = CanvasTextDirection.TopToBottomThenRightToLeft;
        return fmt;
    }

    /// <summary>默认（line-height: normal）时的 (基线, 行高)。</summary>
    public static (float Baseline, float Height) NormalLine(ICanvasResourceCreator dev, FontSpec f, float size, string sample)
    {
        using var fmt = Format(f, size, false);
        using var l = new CanvasTextLayout(dev, string.IsNullOrEmpty(sample) ? "国Ag" : sample, fmt, 100000, 100000);
        var m = l.LineMetrics;
        return m.Length > 0 ? (m[0].Baseline, m[0].Height) : (size * 0.9f, size * 1.3f);
    }

    /// <summary>
    /// 排一段文字。letterSpacing 是 px（CSS 的 em × 字号）；lineHeight 为 null 时 = normal。
    /// maxLines：超过时最后一行加省略号（-webkit-line-clamp）；balance：两行时把宽度收到最窄仍是两行（text-wrap: balance）。
    /// </summary>
    public static CanvasTextLayout Layout(ICanvasResourceCreator dev, string text, FontSpec f, float size, float width,
        CanvasHorizontalAlignment align, float letterSpacing = 0, float? lineHeight = null, int maxLines = 1, bool balance = false)
    {
        bool wrap = maxLines > 1;
        float baseline = 0;
        if (lineHeight is float lh)
        {
            var (b, h) = NormalLine(dev, f, size, text);
            baseline = (lh - h) / 2 + b;
        }
        CanvasTextLayout Make(float w, float hgt)
        {
            var fmt = Format(f, size, wrap, align, ellipsis: true);
            if (lineHeight is float lh2)
            {
                fmt.LineSpacingMode = CanvasLineSpacingMode.Uniform;
                fmt.LineSpacing = lh2;
                fmt.LineSpacingBaseline = baseline;
            }
            var l = new CanvasTextLayout(dev, text, fmt, Math.Max(1, w), hgt);
            if (letterSpacing != 0) l.SetCharacterSpacing(0, text.Length, 0, letterSpacing, 0);
            fmt.Dispose();
            return l;
        }
        float maxH = lineHeight is float lh3 ? lh3 * maxLines + 0.5f : 100000;
        var layout = Make(width, wrap ? 100000 : maxH);
        if (!wrap) return layout;
        int lines = layout.LineCount;
        if (lines > maxLines)
        {
            layout.Dispose();
            // 截断：只留 maxLines 行，最后一行省略号
            var clamped = Make(width, maxH);
            return clamped;
        }
        if (balance && lines >= 2)
        {
            float lo = width / lines * 0.8f, hi = width;
            for (int i = 0; i < 10 && hi - lo > 1; i++)
            {
                float mid = (lo + hi) / 2;
                using var probe = Make(mid, 100000);
                if (probe.LineCount > lines) lo = mid; else hi = mid;
            }
            if (hi < width - 0.5f)
            {
                layout.Dispose();
                // 用 balance 后的宽度换行，但仍在原宽度里对齐
                var narrow = Make(hi, 100000);
                return narrow;
            }
        }
        return layout;
    }

    public static Color Ink(double alpha = 1) => Gfx.Rgba(244, 244, 245, alpha);
}
