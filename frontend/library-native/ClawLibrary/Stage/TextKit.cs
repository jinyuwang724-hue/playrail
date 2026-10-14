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

    /// <summary>窗口创建期间在后台把 DirectWrite 的字体集合与中文回退预热（第一次排版要 100+ ms）。</summary>
    public static void Warm()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var dev = CanvasDevice.GetSharedDevice();
            foreach (var f in new[] { TitleCjk, TitleLatin, Kicker, Ui(800), Ui(600), UiB(700) })
            {
                using var l = Layout(dev, "游戏库 Galgame 非 Steam 2077", f, 20, 1000, CanvasHorizontalAlignment.Left, 1);
                _ = l.LayoutBounds;
                FaceMetrics(f);
            }
            using var fmt = Format(Ui(800), 40, false, vertical: true);
            using var v = new CanvasTextLayout(dev, "游戏库全部最近", fmt, 1000, 1000);
            _ = v.DrawBounds;
            // 再把 Direct2D（文字栅格化、阴影特效）也走一遍
            using var rt = new CanvasRenderTarget(dev, 128, 128, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
                using var cl = new CanvasCommandList(ds);
                using (var cds = cl.CreateDrawingSession()) cds.DrawTextLayout(v, 0, 0, Ink());
                using var sh = new Microsoft.Graphics.Canvas.Effects.ShadowEffect { Source = cl, BlurAmount = 6 };
                ds.DrawImage(sh);
                ds.DrawImage(cl);
            }
            Log.Info($"text warm-up done in {sw.ElapsedMilliseconds}ms ({ClawLibrary.App.SinceProcessStartMs():F0}ms since process start)");
        }
        catch (Exception ex) { Log.Warn($"text warm-up: {ex.Message}"); }
    }

    /// <summary>默认（line-height: normal）时的 (基线, 行高)。</summary>
    public static (float Baseline, float Height) NormalLine(ICanvasResourceCreator dev, FontSpec f, float size, string sample)
    {
        using var fmt = Format(f, size, false);
        using var l = new CanvasTextLayout(dev, string.IsNullOrEmpty(sample) ? "国Ag" : sample, fmt, 100000, 100000);
        // 不用 LineMetrics：NativeAOT 下 Win2D 这个数组属性的封送会抛异常。行高取单行排版框，基线取主字体的 ascent。
        float h = (float)l.LayoutBounds.Height;
        var (asc, desc, gap) = FaceMetrics(f);
        float content = (asc + desc + gap) * size;
        return (asc * size + Math.Max(0, h - content) / 2, h);
    }

    /// <summary>
    /// 用到的几款字体的 (ascent, descent, lineGap)，单位 em（字体文件里的 hhea/OS2 值，本机实测）。
    /// 不在运行时查系统字体集：GetSystemFontSet 第一次要几百毫秒，会拖慢启动。
    /// </summary>
    private static (float Ascent, float Descent, float LineGap) FaceMetrics(FontSpec f) => f.Family switch
    {
        "Noto Serif SC" => (1.151f, 0.286f, 0f),
        "Noto Sans SC" => (1.160f, 0.288f, 0f),
        "Georgia" => (0.917f, 0.219f, 0f),
        "Segoe UI Variable Text" => (1.079f, 0.251f, 0f),
        _ => (1.058f, 0.262f, 0f),   // Microsoft YaHei
    };

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
