// Win2D 文字排版的小工具：按 CSS 的写法（字号、字重、字距 em、行高、单行省略、两行截断、balance 换行）出 CanvasTextLayout。
// 字体：界面 SF Pro + 苹方，海报标题 Noto Serif Display / Cormorant Garamond + 思源宋体。
// SF Pro、苹方、Noto Serif Display、Cormorant Garamond 随包（Assets\Fonts）；思源宋体是 Windows 11 自带的 Noto Serif SC。
// 拉丁字体里的中文段落由 Mix 显式换成配对的中文字体，不走系统回退（回退会落到微软雅黑）。
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace ClawLibrary.Stage;

/// <summary>Tabular：等宽数字（OpenType tnum），读数、时间用，数字跳动时不左右晃。</summary>
public readonly record struct FontSpec(string Family, ushort Weight, bool Italic = false, bool Tabular = false);

public static partial class TextKit
{
    public const string SerifDisplay = "ms-appx:///Assets/Fonts/NotoSerifDisplay-VF.ttf#Noto Serif Display";
    public const string Cormorant = "ms-appx:///Assets/Fonts/CormorantGaramond-Italic-VF.ttf#Cormorant Garamond";
    public const string SansCjk = "Noto Sans SC", SerifCjk = "Noto Serif SC";

    public static readonly FontSpec TitleCjk = new(SerifCjk, 600);
    public static readonly FontSpec TitleLatin = new(SerifDisplay, 700);
    public static readonly FontSpec Kicker = new(Cormorant, 600, true);
    public static readonly FontSpec FoliaTitle = new(SansKey, 700);
    public static FontSpec Ui(ushort weight) => new(SansKey, weight);
    public static FontSpec UiB(ushort weight) => new(SansKey, weight);
    /// <summary>读数：界面字体的等宽数字（代替 Cascadia Mono）。</summary>
    public static FontSpec Num(ushort weight) => new(SansKey, weight, Tabular: true);
    /// <summary>路径、文件名这类真要等宽的地方。</summary>
    public static FontSpec Mono(ushort weight) => new("Cascadia Mono", weight);

    // 界面字体：SF Pro（Text 用在 20 以下，Display 用在 20 及以上）+ 苹方；都是一个文件一个字重，随包放在 Assets\Fonts。
    // 用占位名 "@sans" / "@cjk"，排版时按字重、字号换成具体的字体文件。
    public const string SansKey = "@sans", CjkKey = "@cjk";
    private const string A = "ms-appx:///Assets/Fonts/";

    private static string ResolveSans(ushort w, float size) => A + (size >= 20 ? "SFProDisplay-" : "SFProText-")
        + (w <= 450 ? "Regular" : w <= 550 ? "Medium" : w <= 650 ? "Semibold" : "Bold")
        + (size >= 20 ? ".otf#SF Pro Display" : ".otf#SF Pro Text");

    private static string ResolveCjk(ushort w) =>
        A + "PingFangSC-" + (w <= 350 ? "Light" : w <= 450 ? "Regular" : w <= 550 ? "Medium" : "Semibold") + ".otf#PingFang SC";

    /// <summary>
    /// 首次启动引导把随包字体装进了系统（%WINDIR%\Fonts）：按字体名用系统字体，不再按文件路径加载。
    /// 按路径加载的字体每排一段要 2.7 ms，系统字体 0.05 ms。启动时看一次，装好后下次启动生效。
    /// </summary>
    public static readonly bool SystemFonts = Installed("SFProText-Regular.otf") && Installed("PingFangSC-Regular.otf");

    private static bool Installed(string file)
    {
        try { return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file)); }
        catch { return false; }
    }

    private static string Resolve(string family, ushort w, float size)
    {
        string f = family == SansKey ? ResolveSans(w, size) : family == CjkKey ? ResolveCjk(w) : family;
        int hash = f.IndexOf('#');
        return SystemFonts && hash > 0 && f.StartsWith(A, StringComparison.Ordinal) ? f[(hash + 1)..] : f;
    }

    /// <summary>XAML 输入框用的字体串（XAML 认逗号分隔的回退列表）。</summary>
    public static readonly string XamlUi = ResolveSans(400, 15) + ", " + ResolveCjk(400);

    [GeneratedRegex("[⺀-⿿　-〿぀-ヿ㄀-ㄯ㈀-㏿㐀-䶿一-鿿豈-﫿︰-﹏＀-￯]+")]
    private static partial Regex CjkRuns();

    /// <summary>拉丁字体配哪款中文字体；null = 不换（本身就是中文字体、图标字体、OOBE 的 Motiva）。</summary>
    private static string? CjkFor(string family) => family switch
    {
        SansKey or "Cascadia Mono" or "Segoe UI Variable Text" or "Segoe UI Variable Display" => CjkKey,
        SerifDisplay or Cormorant or "Georgia" => SerifCjk,
        _ => null,
    };

    /// <summary>
    /// 实际用的字体。界面字体遇到含中文的文字整段用苹方（苹方自带西文和数字），不再逐段换字体：
    /// 随包字体按文件加载，版面里每换一次字体要 2.7 ms（系统字体 0.05 ms），设置页一屏两百段文字会卡半秒。
    /// </summary>
    private static string FamilyFor(FontSpec f, float size, string? text) =>
        !SystemFonts && f.Family == SansKey && text != null && CjkRuns().IsMatch(text) ? ResolveCjk(f.Weight) : Resolve(f.Family, f.Weight, size);

    /// <summary>把排好的文字里的中文段落换成配对的思源字体（斜体的拉丁字配正体中文），并按需打开等宽数字。</summary>
    public static void Mix(CanvasTextLayout l, string text, FontSpec f)
    {
        // 系统里没装字体时，界面字体的中文已在 FamilyFor 里整段换成苹方（逐段换太慢）；装了就逐段换，西文留 SF Pro
        if (CjkFor(f.Family) is string cjk && (cjk != CjkKey || SystemFonts))
            foreach (Match m in CjkRuns().Matches(text))
            {
                l.SetFontFamily(m.Index, m.Length, Resolve(cjk, f.Weight, 0));
                if (f.Italic) l.SetFontStyle(m.Index, m.Length, Windows.UI.Text.FontStyle.Normal);
            }
        if (f.Tabular && text.Length > 0)
        {
            var t = new CanvasTypography();
            t.AddFeature(CanvasTypographyFeatureName.TabularFigures, 1);
            l.SetTypography(0, text.Length, t);
        }
    }
    /// <summary>首次启动引导用的 Motiva Sans（随包 Assets\Fonts，可变字体）；中文字形没有，走系统回退（微软雅黑）。</summary>
    public const string Motiva = "ms-appx:///Assets/Fonts/MotivaVFCyrillic.ttf#Motiva Sans Cyrillic";
    public static FontSpec Deck(ushort weight) => new(Motiva, weight);

    [GeneratedRegex("[぀-ヿ㐀-鿿＀-￯]")]
    public static partial Regex Cjk();

    // 排版格式按参数缓存、反复用：Win2D 第一次用一个格式时才去加载随包字体（2.7 ms），之后同一个格式只要 0.2 ms。
    // 每个线程一份（预热在后台线程跑），满了整个清掉。缓存里的格式不能 Dispose，也不能再改。
    [ThreadStatic] private static Dictionary<(string, float, ushort, bool, bool, CanvasHorizontalAlignment, bool, bool, float, float), CanvasTextFormat>? _formats;

    /// <summary>缓存的排版格式（见上）；text 用来决定中文是否整段用苹方。调用处不要 Dispose。</summary>
    public static CanvasTextFormat Shared(FontSpec f, float size, bool wrap, CanvasHorizontalAlignment align = CanvasHorizontalAlignment.Left,
        bool ellipsis = false, bool vertical = false, string? text = null, float? lineHeight = null, float baseline = 0)
    {
        string family = FamilyFor(f, size, text);
        var key = (family, size, f.Weight, f.Italic, wrap, align, ellipsis, vertical, lineHeight ?? -1, lineHeight != null ? baseline : 0);
        _formats ??= new();
        if (_formats.TryGetValue(key, out var cached)) return cached;
        if (_formats.Count > 600) { foreach (var old in _formats.Values) old.Dispose(); _formats.Clear(); }
        var fmt = Format(f, size, wrap, align, ellipsis, vertical, text);
        if (lineHeight is float lh)
        {
            fmt.LineSpacingMode = CanvasLineSpacingMode.Uniform;
            fmt.LineSpacing = lh;
            fmt.LineSpacingBaseline = baseline;
        }
        _formats[key] = fmt;
        return fmt;
    }

    public static CanvasTextFormat Format(FontSpec f, float size, bool wrap, CanvasHorizontalAlignment align = CanvasHorizontalAlignment.Left, bool ellipsis = false, bool vertical = false, string? text = null)
    {
        var fmt = new CanvasTextFormat
        {
            FontFamily = FamilyFor(f, size, text),
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
        var fmt = Shared(f, size, false, text: sample);
        using var l = new CanvasTextLayout(dev, string.IsNullOrEmpty(sample) ? "国Ag" : sample, fmt, 100000, 100000);
        Mix(l, string.IsNullOrEmpty(sample) ? "\u56FDAg" : sample, f);
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
        SansKey => (0.952f, 0.241f, 0f),   // SF Pro
        "Noto Serif SC" => (1.151f, 0.286f, 0f),
        "Noto Sans SC" => (1.160f, 0.288f, 0f),
        "Georgia" => (0.917f, 0.219f, 0f),
        SerifDisplay => (1.069f, 0.293f, 0f),
        Cormorant => (0.924f, 0.287f, 0f),
        "Segoe UI Variable Text" => (1.079f, 0.251f, 0f),
        Motiva => (0.99f, 0.24f, 0f),
        _ => (1.058f, 0.262f, 0f),   // Microsoft YaHei
    };

    /// <summary>
    /// 排一段文字。letterSpacing 是 px（CSS 的 em × 字号）；lineHeight 为 null 时 = normal。
    /// maxLines：超过时最后一行加省略号（-webkit-line-clamp）；balance：两行时把宽度收到最窄仍是两行（text-wrap: balance）。
    /// </summary>
    public static CanvasTextLayout Layout(ICanvasResourceCreator dev, string text, FontSpec f, float size, float width,
        CanvasHorizontalAlignment align, float letterSpacing = 0, float? lineHeight = null, int maxLines = 1, bool balance = false)
    {
        text = I18n.T(text);
        bool wrap = maxLines > 1;
        float baseline = 0;
        if (lineHeight is float lh)
        {
            var (b, h) = NormalLine(dev, f, size, text);
            baseline = (lh - h) / 2 + b;
        }
        CanvasTextLayout Make(float w, float hgt)
        {
            var fmt = Shared(f, size, wrap, align, ellipsis: true, text: text, lineHeight: lineHeight, baseline: baseline);
            var l = new CanvasTextLayout(dev, text, fmt, Math.Max(1, w), hgt);
            Mix(l, text, f);
            if (letterSpacing != 0) l.SetCharacterSpacing(0, text.Length, 0, letterSpacing, 0);
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
