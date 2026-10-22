// 墙以外的东西：配色、缝里的内容（首页窄缝 / 集合信息条，照 src/stage/seam.ts 与 styles.css）、提示条、边缘标签、性能读数。
// 这些都在屏幕坐标（DIP）里，按系统缩放栅格化。
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public static class Theme
{
    public static readonly Color Bg = Gfx.Rgba(9, 9, 11);
    public static readonly Color Ink = Gfx.Rgba(244, 244, 245);
    public static readonly Color Accent = Gfx.Rgba(0xf4, 0xe0, 0x4d);
    public static readonly Color GlowA = Gfx.Rgba(0xfd, 0x5c, 0x47);
    public static readonly Color GlowB = Gfx.Rgba(0x6a, 0x48, 0xff);
    /// <summary>--poster-bg: color-mix(bg 88%, ink)</summary>
    public static readonly Color PosterBg = Gfx.Mix(Bg, 0.88, Ink);
    /// <summary>--seam-paper: color-mix(bg 90%, ink)</summary>
    public static readonly Color SeamPaper = Gfx.Mix(Bg, 0.90, Ink);
    public static readonly Color SeamInkSoft = Gfx.WithAlpha(Ink, 0.55);
    public static readonly Color TintA = Gfx.Mix(Bg, 0.78, GlowA);
    public static readonly Color TintB = Gfx.Mix(Bg, 0.84, GlowB);
}

/// <summary>墙面外观（folia 的海报灯光与海报叠色，设置在 ui.ini）：TintOpacity 是叠色强度（默认 0.5）。</summary>
public readonly record struct WallLook(bool LightsOn, bool Tint, bool TintCustom, Color TintColor, double TintOpacity);

public sealed record ChromeArt(CompositionDrawingSurface Surface, Vector2 Size);

public sealed class Chrome
{
    private readonly Gfx _g;
    public float Dpi = 1;

    public Chrome(Gfx g) { _g = g; }

    private ChromeArt Draw(float w, float h, Action<CanvasDrawingSession> paint)
    {
        var surface = _g.Surface((int)Math.Ceiling(w * Dpi), (int)Math.Ceiling(h * Dpi));
        using (var ds = CanvasComposition.CreateDrawingSession(surface))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(Dpi);
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            paint(ds);
        }
        return new ChromeArt(surface, new Vector2(w, h));
    }

    private CanvasTextLayout H(string text, FontSpec f, float size, float ls = 0) =>
        TextKit.Layout(_g.Device, text, f, size, 100000, CanvasHorizontalAlignment.Left, ls);

    private CanvasTextLayout V(string text, FontSpec f, float size, float ls, float? lineHeight)
    {
        using var fmt = TextKit.Format(f, size, false, vertical: true);
        if (lineHeight is float lh)
        {
            var (b, hh) = TextKit.NormalLine(_g.Device, f, size, text);
            fmt.LineSpacingMode = CanvasLineSpacingMode.Uniform;
            fmt.LineSpacing = lh;
            fmt.LineSpacingBaseline = (lh - hh) / 2 + b;
        }
        var l = new CanvasTextLayout(_g.Device, text, fmt, 100000, 100000);
        if (ls != 0) l.SetCharacterSpacing(0, text.Length, 0, ls, 0);
        return l;
    }

    private static (float x, float y, float w, float h) Box(CanvasTextLayout l)
    {
        var b = l.LayoutBounds;
        return ((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height);
    }

    /// <summary>竖排文字画在 (cx 居中, top) 处，返回 (宽, 高)。</summary>
    private static Vector2 DrawV(CanvasDrawingSession ds, CanvasTextLayout l, float cx, float top, Color c)
    {
        // 横向按字形墨迹居中（竖排时行盒的基线不对称），纵向按排版框
        var (bx, by, bw, bh) = Box(l);
        var ink = l.DrawBounds;
        float inkCx = (float)(ink.X + ink.Width / 2);
        ds.DrawTextLayout(l, cx - inkCx, top - by, c);
        return new Vector2(bw, bh);
    }

    // ---------------- 缝：首页窄缝 ----------------

    public ChromeArt SeamHome(string title, IReadOnlyList<string> tabs, int active, float height)
    {
        const float W = 120;
        return Draw(W, height, ds =>
        {
            float cx = W / 2, y = 26;
            // 竖排标题
            var t = V(title, TextKit.Ui(800), 40, 0.04f * 40, 1.25f * 40);
            y += DrawV(ds, t, cx, y, Theme.Ink).Y + 16;
            y += HintKey(ds, "LB", cx, y) + 16;
            // 竖排页签
            for (int i = 0; i < tabs.Count; i++)
            {
                if (i > 0) y += 8;
                var l = V(tabs[i], TextKit.Ui(700), 15, 0.2f * 15, null);
                var (_, _, bw, bh) = Box(l);
                float boxW = bw + 12 + 2, boxH = bh + 24 + 2;
                if (i == active) ds.FillRoundedRectangle(cx - boxW / 2, y, boxW, boxH, 12, 12, Gfx.WithAlpha(Theme.Ink, 0.14));
                DrawV(ds, l, cx, y + 13, i == active ? Theme.Ink : Theme.SeamInkSoft);
                y += boxH;
            }
            y += 16;
            HintKey(ds, "RB", cx, y);
        });
    }

    private float HintKey(CanvasDrawingSession ds, string key, float cx, float y)
    {
        var l = H(key, TextKit.Ui(800), 11, 0.08f * 11);
        var (bx, by, bw, bh) = Box(l);
        float w = bw + 12 + 2, h = bh + 4 + 2;
        ds.DrawRoundedRectangle(cx - w / 2 + 0.5f, y + 0.5f, w - 1, h - 1, 6, 6, Gfx.WithAlpha(Theme.Ink, 0.55 * 0.3), 1);
        ds.DrawTextLayout(l, cx - w / 2 + 7 - bx, y + 3 - by, Theme.SeamInkSoft);
        return h;
    }

    // ---------------- 缝：集合信息条 ----------------

    public ChromeArt SeamFull(string title, IReadOnlyList<string> crumbs, int count, double hours, float height, bool backFocused = false)
    {
        const float W = 300, PadX = 22, PadTop = 22, PadBottom = 20, Gap = 14;
        return Draw(W, height, ds =>
        {
            // 面包屑
            float y = PadTop;
            ds.FillCircle(PadX + 16, y + 16, 16, Gfx.Rgba(255, 255, 255, 0.08));
            if (backFocused)
            {
                // 焦点在竖条里：返回按钮画白色描边高亮
                ds.FillCircle(PadX + 16, y + 16, 18, Gfx.WithAlpha(Theme.Ink, 0.12));
                ds.DrawCircle(PadX + 16, y + 16, 17, Theme.Ink, 2);
            }
            var back = H("‹", TextKit.Ui(400), 18);
            var (bx, by, bw, bh) = Box(back);
            ds.DrawTextLayout(back, PadX + 16 - bw / 2 - bx, y + 16 - bh / 2 - by, Theme.Ink);
            var crumb = H(string.Join(" › ", crumbs), TextKit.Ui(400), 12, 0.08f * 12);
            var (cx0, cy0, _, ch) = Box(crumb);
            ds.DrawTextLayout(crumb, PadX + 32 + 8 - cx0, y + 16 - ch / 2 - cy0, Theme.SeamInkSoft);
            float wrapTop = y + 32 + Gap;

            // 自下而上：按键提示、统计、分隔线
            float keysH = 22;
            float keysY = height - PadBottom - keysH;
            float kx = PadX;
            foreach (var (k, label) in new[] { ("A", "打开"), ("B", "返回") })
            {
                ds.FillCircle(kx + 11, keysY + 11, 11, Gfx.WithAlpha(Theme.Ink, 0.55 * 0.22));
                var kl = H(k, TextKit.Ui(800), 11);
                var (kbx, kby, kbw, kbh) = Box(kl);
                ds.DrawTextLayout(kl, kx + 11 - kbw / 2 - kbx, keysY + 11 - kbh / 2 - kby, Theme.SeamInkSoft);
                var ll = H(label, TextKit.Ui(700), 12);
                var (lbx, lby, lbw, lbh) = Box(ll);
                ds.DrawTextLayout(ll, kx + 22 + 6 - lbx, keysY + 11 - lbh / 2 - lby, Theme.SeamInkSoft);
                kx += 22 + 6 + lbw + 14;
            }
            float metaH = 13 * 1.6f;
            float metaY = keysY - Gap - metaH;
            string hoursText = hours.ToString("0");
            string meta = $"{count} 款游戏 · 共 {hoursText} 小时";
            var ml = H(meta, TextKit.Ui(400), 13);
            ml.SetFontWeight(0, count.ToString().Length, new Windows.UI.Text.FontWeight { Weight = 700 });
            ml.SetColor(0, count.ToString().Length, Theme.Ink);
            int hi = meta.IndexOf(hoursText, count.ToString().Length, StringComparison.Ordinal);
            if (hi > 0) { ml.SetFontWeight(hi, hoursText.Length, new Windows.UI.Text.FontWeight { Weight = 700 }); ml.SetColor(hi, hoursText.Length, Theme.Ink); }
            var (mbx, mby, _, mbh) = Box(ml);
            ds.DrawTextLayout(ml, PadX - mbx, metaY + (metaH - mbh) / 2 - mby, Theme.SeamInkSoft);
            float ruleY = metaY - Gap - 1;
            ds.FillRectangle(PadX, ruleY, W - PadX * 2, 1, Gfx.WithAlpha(Theme.Ink, 0.12));

            // 竖排大标题（引号在左上，整组水平居中）
            float wrapBottom = ruleY - Gap;
            var q = H("“", TextKit.Ui(400), 28);
            var (qx, qy, qw, _) = Box(q);
            var vt = V(title, TextKit.Ui(800), 64, 0.04f * 64, 1.25f * 64);
            var (vx, vy, vw, vh) = Box(vt);
            const float colW = 1.25f * 64;   // 竖排一列的宽度 = 行高
            float groupW = qw + 6 + colW;
            float gx = PadX + (W - PadX * 2 - groupW) / 2;
            var vink = vt.DrawBounds;
            // 竖排西文字形在列里的位置与 Chrome 不同：对照截图实测偏右 15 px，在这里校正
            float vtX = gx + qw + 6 + colW / 2 - (float)(vink.X + vink.Width / 2) - 15;
            ds.DrawTextLayout(q, gx - qx, wrapTop - qy - 28 * 0.2f, Gfx.WithAlpha(Theme.Ink, 0.35));
            using (ds.CreateLayer(1, new Windows.Foundation.Rect(0, wrapTop, W, Math.Max(0, wrapBottom - wrapTop))))
                ds.DrawTextLayout(vt, vtX, wrapTop - vy, Theme.Ink);
        });
    }

    // ---------------- 缝：搜索面板 ----------------

    /// <summary>搜索面板里能聚焦 / 能点的几处（面板坐标，DIP）。Input 是输入框里放文字的那块（XAML TextBox 叠在上面）。</summary>
    public sealed record SearchPanelLayout(Windows.Foundation.Rect Field, Windows.Foundation.Rect Input, Windows.Foundation.Rect Clear, Windows.Foundation.Rect Submit);

    public const string SearchPlaceholder = "搜索游戏库";
    private const string SearchHint = "输入游戏名、原名或开发商，按回车搜索。";

    /// <summary>
    /// 首页窄缝临时展开成的搜索面板（照 folia 书库首页缝里的全局搜索框：BravaisSeamSearch + bravaisHome.css）。
    /// 自上而下：面包屑（游戏库 › 搜索）→ 带框的输入框（放大镜、输入、× 关闭）→ 提示 → 主按钮「搜索」。
    /// （folia 面包屑行里的 ‹ 与「关闭搜索」不要：× 和 B 已经能关。）
    /// 间距照 .bravais-seam-search（padding 22 22 0，gap 16）、.bravais-search-form（高 44、圆角 12、描边 24 %、底 5 %，
    /// 有焦点时描边换强调色）、.bravais-search-hint（12 px、行高 1.45、上边 -6）、.bravais-chrome-button.is-primary（高 44、
    /// 圆角 12、15 px 粗体、底 16 %）。focus：手柄焦点在哪一项（1 输入框、2 搜索按钮，-1 不在面板里）。
    /// </summary>
    public (ChromeArt Art, SearchPanelLayout Layout) SeamSearch(float height, int focus, bool typing)
    {
        const float W = 300, PadX = 22, PadTop = 22, Gap = 16;
        float inner = W - PadX * 2;
        var iconFont = new FontSpec("Segoe Fluent Icons", 400);
        // 面包屑行（min-height 32）
        // 输入框（44 高）
        float formY = PadTop + 32 + Gap;
        var field = new Windows.Foundation.Rect(PadX, formY, inner, 44);
        var clear = new Windows.Foundation.Rect(field.Right - 6 - 32, formY + 6, 32, 32);
        float inputX = PadX + 12 + 16 + 8;
        var input = new Windows.Foundation.Rect(inputX, formY + 1, clear.X - 4 - inputX, 42);
        // 提示（gap 16、margin-top -6）
        float hintY = (float)field.Bottom + Gap - 6;
        var hint = TextKit.Layout(_g.Device, SearchHint, TextKit.Ui(400), 12, inner, CanvasHorizontalAlignment.Left, 0, 12 * 1.45f, maxLines: 4);
        var (hbx, hby, _, hbh) = Box(hint);
        // 主按钮「搜索」
        var label = H("搜索", TextKit.Ui(700), 15);
        var (lbx, lby, lbw, lbh) = Box(label);
        float btnW = 16 + 18 + 8 + lbw + 16;
        var submit = new Windows.Foundation.Rect(PadX, hintY + hbh + Gap, btnW, 44);
        var layout = new SearchPanelLayout(field, input, clear, submit);

        var art = Draw(W, height, ds =>
        {
            var soft = Theme.SeamInkSoft;
            // 面包屑
            var crumb = H("游戏库 › 搜索", TextKit.Ui(400), 11, 0.08f * 11);
            var (cx0, cy0, _, ch) = Box(crumb);
            ds.DrawTextLayout(crumb, PadX - cx0, PadTop + 16 - ch / 2 - cy0, soft);

            // 输入框
            ds.FillRoundedRectangle(field, 12, 12, Gfx.WithAlpha(Theme.Ink, 0.05));
            var inset = new Windows.Foundation.Rect(field.X + 0.5, field.Y + 0.5, field.Width - 1, field.Height - 1);
            ds.DrawRoundedRectangle(inset, 11.5f, 11.5f, typing || focus == 1 ? Theme.Accent : Gfx.WithAlpha(Theme.Ink, 0.24), 1);
            var mag = H("", iconFont, 16);
            var (mx, my, mw, mh) = Box(mag);
            ds.DrawTextLayout(mag, PadX + 12 + 8 - (float)(mag.DrawBounds.X + mag.DrawBounds.Width / 2), (float)field.Y + 22 - mh / 2 - my, Gfx.WithAlpha(Theme.Ink, 0.7));
            var x = H("", iconFont, 14);
            var (xx, xy, xw, xh) = Box(x);
            ds.DrawTextLayout(x, (float)(clear.X + 16) - (float)(x.DrawBounds.X + x.DrawBounds.Width / 2), (float)(clear.Y + 16) - xh / 2 - xy, Gfx.WithAlpha(Theme.Ink, 0.8));

            // 提示
            ds.DrawTextLayout(hint, PadX - hbx, hintY - hby, soft);

            // 主按钮
            ds.FillRoundedRectangle(submit, 12, 12, Gfx.WithAlpha(Theme.Ink, focus == 2 ? 0.22 : 0.16));
            if (focus == 2) ds.DrawRoundedRectangle(submit, 12, 12, Gfx.WithAlpha(Theme.Ink, 0.26), 1);
            var si = H("", iconFont, 18);
            var (sx, sy, sw, sh) = Box(si);
            ds.DrawTextLayout(si, (float)submit.X + 16 + 9 - (float)(si.DrawBounds.X + si.DrawBounds.Width / 2), (float)submit.Y + 22 - sh / 2 - sy, Theme.Ink);
            ds.DrawTextLayout(label, (float)submit.X + 16 + 18 + 8 - lbx, (float)submit.Y + 22 - lbh / 2 - lby, Theme.Ink);

            // 手柄焦点：与竖条同一种白色描边（输入框、按钮各自的圆角）
            // 正在打字时输入框自己换了强调色描边（:focus-within），不再叠一圈手柄焦点
            var fr = focus switch { 1 when !typing => field, 2 => submit, _ => (Windows.Foundation.Rect?)null };
            if (fr is { } f)
            {
                var rr = new Windows.Foundation.Rect(f.X - 3, f.Y - 3, f.Width + 6, f.Height + 6);
                ds.DrawRoundedRectangle(new Windows.Foundation.Rect(rr.X + 1, rr.Y + 1, rr.Width - 2, rr.Height - 2), 14, 14, Theme.Ink, 2);
            }

            // 底部按键提示（手柄）
            float keysY = height - 20 - 22, kx = PadX;
            foreach (var (k, t) in new[] { ("A", "选择"), ("B", "关闭搜索") })
            {
                ds.FillCircle(kx + 11, keysY + 11, 11, Gfx.WithAlpha(Theme.Ink, 0.55 * 0.22));
                var kl = H(k, TextKit.Ui(800), 11);
                var (kbx, kby, kbw, kbh) = Box(kl);
                ds.DrawTextLayout(kl, kx + 11 - kbw / 2 - kbx, keysY + 11 - kbh / 2 - kby, soft);
                var ll = H(t, TextKit.Ui(700), 12);
                var (lx, ly, lw, lh) = Box(ll);
                ds.DrawTextLayout(ll, kx + 22 + 6 - lx, keysY + 11 - lh / 2 - ly, soft);
                kx += 22 + 6 + lw + 14;
            }
        });
        return (art, layout);
    }

    // ---------------- 边缘标签 / 提示条 / 读数 ----------------

    public ChromeArt EdgeTab(string text, bool left, float maxHeight)
    {
        var l = V(text, TextKit.Ui(800), 13, 0.12f * 13, null);
        var (bx, by, bw, bh) = Box(l);
        float w = bw + 16, h = Math.Min(maxHeight, bh + 28);
        return Draw(w, h, ds =>
        {
            using var geo = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateRoundedRectangle(ds, left ? -6 : 0, 0, w + 6, h, 6, 6);
            ds.FillGeometry(geo, Theme.SeamPaper);
            using (ds.CreateLayer(1, new Windows.Foundation.Rect(0, 14, w, Math.Max(0, h - 28))))
                ds.DrawTextLayout(l, 8 - bx, 14 - by, Theme.Ink);
        });
    }

    public ChromeArt Toast(string text)
    {
        var l = H(text, TextKit.Ui(700), 13);
        var (bx, by, bw, bh) = Box(l);
        float w = bw + 32, h = bh + 18;
        return Draw(w, h, ds =>
        {
            ds.FillRoundedRectangle(0, 0, w, h, 12, 12, Gfx.Rgba(0, 0, 0, 0.7));
            ds.DrawTextLayout(l, 16 - bx, 9 - by, Theme.Ink);
        });
    }

    public ChromeArt Hud(string text)
    {
        var l = H(text, new FontSpec("Segoe UI Variable Text", 400), 12);
        l.SetTypography(0, text.Length, TabularNums());
        var (bx, by, bw, bh) = Box(l);
        float w = bw + 16, h = bh + 8;
        return Draw(w, h, ds =>
        {
            ds.FillRoundedRectangle(0, 0, w, h, 6, 6, Gfx.Rgba(0, 0, 0, 0.6));
            ds.DrawTextLayout(l, 8 - bx, 4 - by, Gfx.Rgba(0xd4, 0xd4, 0xd8));
        });
    }

    private static CanvasTypography TabularNums()
    {
        var t = new CanvasTypography();
        t.AddFeature(CanvasTypographyFeatureName.TabularFigures, 1);
        return t;
    }
}
