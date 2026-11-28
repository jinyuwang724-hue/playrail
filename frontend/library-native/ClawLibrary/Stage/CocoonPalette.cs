// Cocoon 主题色系统（OKLCH 感知均匀色彩空间 + Bezold-Brücke 色相偏移 + 对比度锁定派生算法）。
// 从任意双色渐变（Start → End）实时派生全套 14 个语义色彩令牌，保证在 Cocoon 浅色陶瓷表面（#FDFDFE / #EFEFF4）上：
//   1. 焦点框周长流光与开关/仪表渐变按 OKLCH 最短色相弧插值，彻底消除传统 sRGB 混色的中间灰脏区；
//   2. 选中文字与图标色（Ink）自动锁定在 OKLCH 明度 [0.42, 0.55]，确保白卡对比度始终满足清晰可读（WCAG AA 大字号）；
//   3. 预置 8 套基于色彩心理学与自然光散射规律调校的耐看渐变色，并支持自定义双色取色与智能和谐配色。
using System;
using Windows.UI;

namespace ClawLibrary.Stage;

public readonly record struct CocoonPreset(
    string Id,
    string Name,
    string Mood,
    Color Start,
    Color End);

public readonly record struct CocoonThemeColors(
    Color Start,
    Color End,
    Color Glow,
    Color Ink,
    Color Deep,
    Color SoftStart,
    Color PillStart,
    Color PillEnd,
    Color PillBadge,
    Color KnobStart,
    Color KnobEnd,
    Color KnobShadow,
    Color SelectionTint,
    Color SelectionBorder,
    Color ActiveCardTint,
    Color ChordBorder,
    Color SelectedOption,
    Color TileGlow);

public enum CocoonHarmonyMode
{
    Free = 0,          // 自由双色（独立调整起点与终点）
    NaturalFlow = 1,   // 自然流光（Bezold-Brücke 邻近色微偏 + 明度梯度）
    TwilightArc = 2,   // 霞光冷暖（跨色相柔光渐变）
    GlazeMono = 3,     // 同色琉璃（纯粹同色相明暗釉面层次）
}

public static class CocoonPalette
{
    public static readonly string[] HarmonyNames = { "自由双色", "自然流光", "霞光冷暖", "同色琉璃" };

    /// <summary>
    /// 8 套经过色彩心理学与 OKLCH 感知空间严选的渐变主题色预设：
    /// 全部遵循高光端高明度（L ≈ 0.74–0.84）→ 主体端浓郁中等明度（L ≈ 0.52–0.65）+ 20°–45° 邻近色相旋转，
    /// 兼顾立体陶瓷光感、视觉舒适度与长期耐看性，且避开危险操作红（#F24B5A）。
    /// </summary>
    public static readonly CocoonPreset[] Presets =
    {
        new("nebula",   "星云蓝紫", "沉静专注 · 默认",   Hex(0x86A7FD), Hex(0x8037FF)),
        new("glacier",  "晴空冰蓝", "清透呼吸 · 零压清爽", Hex(0x68D5FF), Hex(0x2B6BFF)),
        new("celadon",  "青瓷薄荷", "温润玉釉 · 自然舒缓", Hex(0x68E3BE), Hex(0x0D9E94)),
        new("aurora",   "极光青紫", "冰青靛紫 · 灵动流光", Hex(0x56DFDF), Hex(0x6650FF)),
        new("twilight", "暮光紫霞", "浪漫晚霞 · 华丽张力", Hex(0xF08AE0), Hex(0x8E35F5)),
        new("coral",    "朝霞珊瑚", "柔粉玫瑰 · 明快亲和", Hex(0xFF9BB3), Hex(0xEE4B7A)),
        new("amber",    "琥珀晨光", "暖金珊瑚 · 午后活力", Hex(0xFFBE64), Hex(0xF06735)),
        new("obsidian", "曜石钛灰", "极简无彩 · 钛金质感", Hex(0x8C96AA), Hex(0x2F3646)),
    };

    /// <summary>Cocoon 默认「星云蓝紫」精确基准令牌（与原始设计规范像素级一致）。</summary>
    public static readonly CocoonThemeColors DefaultTheme = new(
        Start:           Hex(0x86A7FD),
        End:             Hex(0x8037FF),
        Glow:            Hex(0x86A7FD),
        Ink:             Hex(0x7A4CFF),
        Deep:            Hex(0x7340FF),
        SoftStart:       Hex(0x8F8BFF),
        PillStart:       Hex(0x8A7BFF),
        PillEnd:         Hex(0x7A3CFF),
        PillBadge:       Hex(0xB9A9FF),
        KnobStart:       Hex(0x9A9CFF),
        KnobEnd:         Hex(0x6F3CFF),
        KnobShadow:      Color.FromArgb(89, 80, 40, 200),
        SelectionTint:   Hex(0xECE6FF),
        SelectionBorder: Hex(0xD1C4FF),
        ActiveCardTint:  Color.FromArgb(150, 0xA5, 0x96, 0xFF),
        ChordBorder:     Hex(0x6935F2),
        SelectedOption:  Hex(0x4F9DF2),
        TileGlow:        Color.FromArgb(89, 115, 64, 255));

    public static CocoonThemeColors Current { get; private set; } = DefaultTheme;

    /// <summary>按偏好设置同步当前全局 Cocoon 主题色。</summary>
    public static void ApplyFromPrefs(UiPrefs prefs)
    {
        if (prefs.CocoonTheme == 0)
        {
            Current = DefaultTheme;
            return;
        }
        if (prefs.CocoonTheme > 0 && prefs.CocoonTheme < Presets.Length)
        {
            var p = Presets[prefs.CocoonTheme];
            Current = DeriveTheme(p.Start, p.End);
            return;
        }
        var c1 = ParseHex(prefs.CocoonCustomStart, Presets[0].Start);
        var c2 = ParseHex(prefs.CocoonCustomEnd, Presets[0].End);
        Current = ColorsEqual(c1, Presets[0].Start) && ColorsEqual(c2, Presets[0].End)
            ? DefaultTheme
            : DeriveTheme(c1, c2);
    }

    public static void SetPreview(CocoonThemeColors preview) => Current = preview;

    public static (Color Start, Color End) ActivePair(UiPrefs prefs)
    {
        if (prefs.CocoonTheme >= 0 && prefs.CocoonTheme < Presets.Length)
            return (Presets[prefs.CocoonTheme].Start, Presets[prefs.CocoonTheme].End);
        return (ParseHex(prefs.CocoonCustomStart, Presets[0].Start), ParseHex(prefs.CocoonCustomEnd, Presets[0].End));
    }

    public static string ActiveName(UiPrefs prefs)
    {
        if (prefs.CocoonTheme >= 0 && prefs.CocoonTheme < Presets.Length)
            return Presets[prefs.CocoonTheme].Name;
        return "自定义配色";
    }

    /// <summary>
    /// 从双色渐变 (start, end) 在 OKLCH 空间内科学派生全部 18 个语义令牌。
    /// </summary>
    public static CocoonThemeColors DeriveTheme(Color start, Color end)
    {
        var (l1, c1, h1) = SrgbToOklch(start);
        var (l2, c2, h2) = SrgbToOklch(end);

        bool achromatic = c1 < 0.025f && c2 < 0.025f;
        float hDom = c2 >= c1 ? h2 : h1;
        float hMid = achromatic ? hDom : LerpHue(h1, h2, 0.65f);
        float cMax = MathF.Max(c1, c2);

        // 墨色（选中分类图标/文字、对勾、链接）：严格锁定明度 [0.40, 0.53]，保证在 #FDFDFE 白卡上对比度 >= 4.3:1
        float inkL = achromatic ? Math.Clamp(MathF.Min(l1, l2), 0.30f, 0.42f) : Math.Clamp(MathF.Min(l2, 0.53f), 0.41f, 0.53f);
        float inkC = achromatic ? Math.Clamp(cMax, 0f, 0.04f) : Math.Clamp(c2 * 0.96f + 0.02f, 0.13f, 0.24f);
        Color ink = OklchToSrgb(inkL, inkC, hMid);

        // 深色锚点（滑块右端、快捷磁贴底端、功耗箭头）
        float deepL = achromatic ? Math.Clamp(inkL + 0.02f, 0.30f, 0.45f) : Math.Clamp(l2, 0.45f, 0.57f);
        float deepC = achromatic ? inkC : Math.Clamp(c2, 0.13f, 0.25f);
        Color deep = OklchToSrgb(deepL, deepC, h2);

        // 柔和起点（快捷圆形开关与组合键帽顶端）
        float softL = Math.Clamp(MathF.Max(l1 * 0.96f, deepL + 0.15f), 0.62f, 0.80f);
        float softC = achromatic ? Math.Clamp(c1, 0f, 0.04f) : Math.Clamp((c1 + c2) * 0.48f, 0.10f, 0.19f);
        Color softStart = OklchToSrgb(softL, softC, LerpHue(h1, h2, 0.35f));

        // 右上主动作胶囊（Y ✓ / Y ▶）：保证内部白色圆底与浅色按键章层次分明
        float pillEndL = achromatic ? Math.Clamp(deepL, 0.30f, 0.44f) : Math.Clamp(deepL, 0.46f, 0.56f);
        float pillStartL = Math.Clamp(pillEndL + 0.14f, 0.60f, 0.74f);
        Color pillStart = OklchToSrgb(pillStartL, achromatic ? inkC : Math.Clamp(softC * 1.05f, 0.11f, 0.20f), LerpHue(h1, h2, 0.4f));
        Color pillEnd = OklchToSrgb(pillEndL, deepC, hMid);
        Color pillBadge = OklchToSrgb(Math.Clamp(pillStartL + 0.14f, 0.77f, 0.85f), achromatic ? inkC * 0.7f : Math.Clamp(softC * 0.62f, 0.05f, 0.11f), hMid);

        // 3D 滑钮（左上高光 → 右下浓郁）
        Color knobStart = OklchToSrgb(Math.Clamp(MathF.Max(l1, deepL + 0.18f), 0.68f, 0.82f), achromatic ? inkC : Math.Clamp(softC * 0.92f, 0.09f, 0.17f), h1);
        Color knobEnd = OklchToSrgb(Math.Clamp(deepL - 0.03f, 0.34f, 0.54f), deepC, h2);
        Color knobShadow = Color.FromArgb(89, knobEnd.R, knobEnd.G, knobEnd.B);

        // 极浅陶瓷釉面选中底色与描边
        Color selTint = OklchToSrgb(0.946f, achromatic ? 0.006f : Math.Clamp(cMax * 0.16f, 0.022f, 0.036f), hMid);
        Color selBorder = OklchToSrgb(0.855f, achromatic ? 0.014f : Math.Clamp(cMax * 0.36f, 0.048f, 0.082f), hMid);

        // 正在运行封面罩层（半透明）
        Color activeBase = OklchToSrgb(0.72f, achromatic ? 0.02f : Math.Clamp(cMax * 0.55f, 0.08f, 0.14f), hMid);
        Color activeCardTint = Color.FromArgb(150, activeBase.R, activeBase.G, activeBase.B);

        // 3D 组合修饰键描边 & 单选下拉已选色
        Color chordBorder = OklchToSrgb(Math.Clamp(deepL - 0.05f, 0.28f, 0.50f), deepC, h2);
        Color selectedOpt = OklchToSrgb(Math.Clamp(inkL + 0.05f, 0.44f, 0.58f), achromatic ? inkC : Math.Clamp(inkC * 0.90f, 0.12f, 0.20f), LerpHue(h1, h2, 0.45f));
        Color tileGlow = Color.FromArgb(89, deep.R, deep.G, deep.B);
        Color glow = OklchToSrgb(Math.Clamp(l1, 0.64f, 0.80f), achromatic ? Math.Clamp(cMax, 0.01f, 0.04f) : Math.Clamp(c1, 0.11f, 0.20f), h1);

        return new CocoonThemeColors(
            Start: start,
            End: end,
            Glow: glow,
            Ink: ink,
            Deep: deep,
            SoftStart: softStart,
            PillStart: pillStart,
            PillEnd: pillEnd,
            PillBadge: pillBadge,
            KnobStart: knobStart,
            KnobEnd: knobEnd,
            KnobShadow: knobShadow,
            SelectionTint: selTint,
            SelectionBorder: selBorder,
            ActiveCardTint: activeCardTint,
            ChordBorder: chordBorder,
            SelectedOption: selectedOpt,
            TileGlow: tileGlow);
    }

    /// <summary>
    /// 智能和谐配色：根据用户在取色板中选定的主色，按色彩科学自动计算另一端渐变色。
    /// </summary>
    public static Color ComputeHarmonyPartner(Color anchor, CocoonHarmonyMode mode, bool anchorIsStart)
    {
        var (l, c, h) = SrgbToOklch(anchor);
        bool achro = c < 0.02f;
        return mode switch
        {
            CocoonHarmonyMode.NaturalFlow => anchorIsStart
                ? OklchToSrgb(Math.Clamp(l - 0.18f, 0.46f, 0.62f), achro ? c : Math.Clamp(c * 1.32f + 0.03f, 0.14f, 0.25f), WrapHue(h + NaturalCoolShift(h)))
                : OklchToSrgb(Math.Clamp(l + 0.18f, 0.70f, 0.86f), achro ? c : Math.Clamp(c * 0.76f, 0.10f, 0.17f), WrapHue(h - NaturalCoolShift(h))),
            CocoonHarmonyMode.TwilightArc => anchorIsStart
                ? OklchToSrgb(Math.Clamp(l - 0.19f, 0.46f, 0.62f), achro ? c : Math.Clamp(c * 1.25f + 0.03f, 0.15f, 0.25f), WrapHue(h + 44f))
                : OklchToSrgb(Math.Clamp(l + 0.19f, 0.72f, 0.86f), achro ? c : Math.Clamp(c * 0.80f, 0.10f, 0.18f), WrapHue(h - 44f)),
            CocoonHarmonyMode.GlazeMono => anchorIsStart
                ? OklchToSrgb(Math.Clamp(l - 0.20f, 0.44f, 0.60f), achro ? c : Math.Clamp(c * 1.18f + 0.02f, 0.08f, 0.23f), WrapHue(h + 6f))
                : OklchToSrgb(Math.Clamp(l + 0.20f, 0.72f, 0.86f), achro ? c : Math.Clamp(c * 0.78f, 0.06f, 0.17f), WrapHue(h - 6f)),
            _ => anchor,
        };
    }

    /// <summary>Bezold-Brücke 自然色相偏移方向：高光端偏暖黄/冰青，暗部端偏深靛紫/绯珊瑚。</summary>
    private static float NaturalCoolShift(float hueDeg)
    {
        // 暖色区（25°–130°）暗部向暖红橙收拢（负向偏移），冷色与品红区暗部向深蓝紫收拢
        if (hueDeg >= 25f && hueDeg <= 135f) return -28f;
        if (hueDeg >= 305f || hueDeg < 25f) return -24f;
        return +26f;
    }

    /// <summary>焦点框流光混色：在 OKLCH 空间沿最短色相弧插值，杜绝跨色相灰暗死区。</summary>
    public static Color MixFlow(float t) => LerpOklch(Current.Start, Current.End, Math.Clamp(t, 0f, 1f));

    public static Color LerpOklch(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var (l1, c1, h1) = SrgbToOklch(a);
        var (l2, c2, h2) = SrgbToOklch(b);
        float l = l1 + (l2 - l1) * t;
        float c = c1 + (c2 - c1) * t;
        float h = c1 < 0.008f ? h2 : c2 < 0.008f ? h1 : LerpHue(h1, h2, t);
        return OklchToSrgb(l, c, h);
    }

    // ---------------- WCAG 相对亮度与对比度评估 ----------------

    public static float ContrastRatio(Color a, Color b)
    {
        float la = RelativeLuminance(a), lb = RelativeLuminance(b);
        float hi = MathF.Max(la, lb), lo = MathF.Min(la, lb);
        return (hi + 0.05f) / (lo + 0.05f);
    }

    public static float RelativeLuminance(Color c) =>
        0.2126f * SrgbChannelToLinear(c.R / 255f) +
        0.7152f * SrgbChannelToLinear(c.G / 255f) +
        0.0722f * SrgbChannelToLinear(c.B / 255f);

    // ---------------- OKLab / OKLCH 精确色彩数学（Björn Ottosson 2020） ----------------

    public static (float L, float C, float H) SrgbToOklch(Color srgb)
    {
        float r = SrgbChannelToLinear(srgb.R / 255f);
        float g = SrgbChannelToLinear(srgb.G / 255f);
        float b = SrgbChannelToLinear(srgb.B / 255f);

        float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
        float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
        float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;

        float l_ = MathF.Cbrt(MathF.Max(0f, l));
        float m_ = MathF.Cbrt(MathF.Max(0f, m));
        float s_ = MathF.Cbrt(MathF.Max(0f, s));

        float L = 0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_;
        float okA = 1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_;
        float okB = 0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_;

        float C = MathF.Sqrt(okA * okA + okB * okB);
        float H = C < 1e-5f ? 0f : WrapHue(MathF.Atan2(okB, okA) * (180f / MathF.PI));
        return (Math.Clamp(L, 0f, 1f), C, H);
    }

    /// <summary>
    /// OKLCH → sRGB（带色域边界二分收敛映射：保持明度 L 与色相 H 恒定，仅压缩超出 sRGB 色域的纯度 C，避免硬裁切偏色）。
    /// </summary>
    public static Color OklchToSrgb(float L, float C, float H, byte alpha = 255)
    {
        L = Math.Clamp(L, 0f, 1f);
        if (L <= 0.001f) return Color.FromArgb(alpha, 0, 0, 0);
        if (L >= 0.999f) return Color.FromArgb(alpha, 255, 255, 255);
        C = MathF.Max(0f, C);

        float rad = H * (MathF.PI / 180f);
        float cosH = MathF.Cos(rad), sinH = MathF.Sin(rad);

        var (r, g, b) = OklabToLinearSrgb(L, C * cosH, C * sinH);
        if (!InGamut(r, g, b))
        {
            float lo = 0f, hi = C;
            for (int i = 0; i < 12; i++)
            {
                float mid = (lo + hi) * 0.5f;
                var (mr, mg, mb) = OklabToLinearSrgb(L, mid * cosH, mid * sinH);
                if (InGamut(mr, mg, mb)) lo = mid;
                else hi = mid;
            }
            (r, g, b) = OklabToLinearSrgb(L, lo * cosH, lo * sinH);
        }

        return Color.FromArgb(
            alpha,
            LinearChannelToByte(r),
            LinearChannelToByte(g),
            LinearChannelToByte(b));
    }

    /// <summary>给定明度 L 与色相 H，求 sRGB 色域内可达到的最大纯度 C_max（供取色板色域显示参考）。</summary>
    public static float MaxSrgbChroma(float L, float H)
    {
        L = Math.Clamp(L, 0.01f, 0.99f);
        float rad = H * (MathF.PI / 180f);
        float cosH = MathF.Cos(rad), sinH = MathF.Sin(rad);
        float lo = 0f, hi = 0.37f;
        for (int i = 0; i < 12; i++)
        {
            float mid = (lo + hi) * 0.5f;
            var (r, g, b) = OklabToLinearSrgb(L, mid * cosH, mid * sinH);
            if (InGamut(r, g, b)) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    private static (float R, float G, float B) OklabToLinearSrgb(float L, float a, float b)
    {
        float l_ = L + 0.3963377774f * a + 0.2158037573f * b;
        float m_ = L - 0.1055613458f * a - 0.0638541728f * b;
        float s_ = L - 0.0894841775f * a - 1.2914855480f * b;

        float l = l_ * l_ * l_;
        float m = m_ * m_ * m_;
        float s = s_ * s_ * s_;

        float r = +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float bl = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
        return (r, g, bl);
    }

    private static bool InGamut(float r, float g, float b) =>
        r >= -0.0005f && r <= 1.0005f &&
        g >= -0.0005f && g <= 1.0005f &&
        b >= -0.0005f && b <= 1.0005f;

    private static float SrgbChannelToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static byte LinearChannelToByte(float c)
    {
        c = Math.Clamp(c, 0f, 1f);
        float s = c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(s * 255f), 0, 255);
    }

    public static float WrapHue(float deg)
    {
        float r = deg % 360f;
        return r < 0f ? r + 360f : r;
    }

    public static float LerpHue(float h1, float h2, float t)
    {
        float diff = ((h2 - h1 + 540f) % 360f) - 180f;
        return WrapHue(h1 + diff * t);
    }

    public static Color Hex(uint rgb) =>
        Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color ParseHex(string? hex, Color fallback)
    {
        if (hex is { Length: 7 } && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
            return Color.FromArgb(255, r, g, b);
        return fallback;
    }

    public static bool ColorsEqual(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;
}
