using CocoonUI.Tokens;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI.Text;

namespace CocoonUI.Core;

/// <summary>Configurable font families for Latin/numeric and CJK runs.</summary>
public sealed record CocoonFontConfig(
    string LatinRegular = "Segoe UI Variable Display",
    string LatinBold = "Segoe UI Variable Display",
    string LatinExtraBold = "Segoe UI Variable Display",
    string Cjk = "Microsoft YaHei UI")
{
    public static CocoonFontConfig Packaged(string baseAppxPath = "ms-appx:///Assets/Fonts") => new(
        LatinRegular: $"{baseAppxPath}/Poppins-SemiBold.ttf#Poppins",
        LatinBold: $"{baseAppxPath}/Poppins-Bold.ttf#Poppins",
        LatinExtraBold: $"{baseAppxPath}/Poppins-ExtraBold.ttf#Poppins",
        Cjk: $"{baseAppxPath}/PingFangSC-Medium.otf#PingFang SC");
}

/// <summary>Resolves fonts and enforces CJK Medium (500) weight rule to prevent synthetic bold blur.</summary>
public sealed class CocoonTypography
{
    public CocoonFontConfig Config { get; }

    public CocoonTypography(CocoonFontConfig? config = null) => Config = config ?? new CocoonFontConfig();

    public static bool HasCjk(string text)
    {
        foreach (char ch in text)
            if (ch >= 0x2E80) return true;
        return false;
    }

    public string FontFor(string text) => HasCjk(text) ? Config.Cjk : Config.LatinRegular;

    public string BoldFontFor(string text) => HasCjk(text) ? Config.Cjk : Config.LatinBold;

    public FontWeight WeightFor(string text, ushort latin = 600) => CocoonTokens.TextWeight(text, latin);

    public CanvasTextFormat CreateFormat(
        string text,
        float fontSize,
        ushort latinWeight = 600,
        CanvasHorizontalAlignment hAlign = CanvasHorizontalAlignment.Left,
        CanvasVerticalAlignment vAlign = CanvasVerticalAlignment.Top,
        bool ellipsis = false) => new()
    {
        FontFamily = FontFor(text),
        FontSize = fontSize,
        FontWeight = WeightFor(text, latinWeight),
        HorizontalAlignment = hAlign,
        VerticalAlignment = vAlign,
        WordWrapping = CanvasWordWrapping.NoWrap,
        TrimmingGranularity = ellipsis ? CanvasTextTrimmingGranularity.Character : CanvasTextTrimmingGranularity.None,
        TrimmingSign = ellipsis ? CanvasTrimmingSign.Ellipsis : CanvasTrimmingSign.None,
    };

    public float MeasureWidth(ICanvasResourceCreator creator, string text, float fontSize, ushort latinWeight = 500)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        using var fmt = CreateFormat(text, fontSize, latinWeight);
        using var layout = new CanvasTextLayout(creator, text, fmt, 4000f, 400f);
        return (float)layout.LayoutBounds.Width;
    }
}
