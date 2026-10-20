// 设置页自己的画法（不和性能页共用一套列表）：每一节是一张圆角卡片，节名小字写在卡片上面；
// 卡片里一行一项，行与行之间一根细线。开关画成拨动开关，两到四个选项画成分段按钮，数值是 ‹ 值 ›，
// 只能看的值靠右浅色，能按的行末尾一个 ›。行高和节头高度和其他页一样（PerfRowH / PerfSecH），滚动和指针命中照旧。
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using ClawLibrary.Engine;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static readonly string[] StIcons = { "", "", "", "", "", "", "", "", "" };
    private static readonly FontSpec StIconFont = new("Segoe Fluent Icons", 400);
    private const float StPad = 18;

    private static bool StIsToggle(PerfRow r) => r.Choices is { Length: 2 } c && c[0] == "关" && c[1] == "开";

    private void StPaintRows(CanvasDrawingSession ds, List<PerfRow> rows, int focus, List<SettingsPointerTarget> targets)
    {
        var ui = TextKit.Ui(400);
        float W = PerfRightW, y = 0;
        var card = Gfx.WithAlpha(CcText, 0.06f);
        var line = Gfx.WithAlpha(CcText, 0.07f);
        var muted = Gfx.WithAlpha(CcText, 0.5f);
        var accent = PerfAccent;

        // 先算每张卡片的范围：节头之后到下一个节头之前
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Section) { y += PerfSecH; continue; }
            int start = i;
            float top = y;
            while (i < rows.Count && !rows[i].Section) { y += PerfRowH; i++; }
            ds.FillRoundedRectangle(0, top + 2, W, y - top - 4, 12, 12, card);
            i--;
            _ = start;
        }

        y = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Section)
            {
                if (r.Title.Length > 0) PText(ds, r.Title, TextKit.Ui(500), 13, muted, 6, y + PerfSecH - 11, W);
                y += PerfSecH;
                continue;
            }
            bool f = i == focus, last = i + 1 >= rows.Count || rows[i + 1].Section, first = i == 0 || rows[i - 1].Section;
            float cy = y + PerfRowH / 2;
            float a = r.Disabled ? 0.38f : 1f;
            targets.Add(new(i, SettingsTargetKind.Row, 0, y, W, PerfRowH));
            if (f) ds.FillRoundedRectangle(4, y + (first ? 6 : 2), W - 8, PerfRowH - (first ? 8 : 4) - (last ? 4 : 0), 9, 9, Gfx.WithAlpha(CcText, 0.13f));
            else if (!last) ds.DrawLine(StPad, y + PerfRowH, W - StPad, y + PerfRowH, line, 1);
            if (r.Changed) ds.FillCircle(9, cy, 2.5f, accent);
            PText(ds, r.Title, ui, 16, Gfx.WithAlpha(CcText, (f ? 1f : 0.86f) * a), StPad, cy, W * 0.55f);

            float right = W - StPad;
            if (StIsToggle(r))
            {
                bool on = r.Choice == 1;
                const float sw = 42, sh = 24;
                float sx = right - sw;
                ds.FillRoundedRectangle(sx, cy - sh / 2, sw, sh, sh / 2, sh / 2, on ? Gfx.WithAlpha(accent, a) : Gfx.WithAlpha(CcText, 0.18f * a));
                ds.FillCircle(on ? sx + sw - sh / 2 : sx + sh / 2, cy, sh / 2 - 3, Gfx.WithAlpha(CcText, a));
                if (r.Value.Length > 0)
                {
                    float vw = PWidth(ds, r.Value, ui, 13);
                    PText(ds, r.Value, ui, 13, Gfx.WithAlpha(CcText, 0.45f * a), sx - 14 - vw, cy, vw + 4);
                }
            }
            else if (r.Choices is { Length: > 0 } choices)
            {
                // 分段按钮：选中的那段亮底
                var widths = choices.Select(c => PWidth(ds, c, ui, 14) + 22).ToArray();
                float total = widths.Sum(), h = 28, x = right - total;
                ds.FillRoundedRectangle(x - 2, cy - h / 2 - 2, total + 4, h + 4, 9, 9, Gfx.WithAlpha(CcText, 0.08f * a));
                for (int j = 0; j < choices.Length; j++)
                {
                    bool on = j == r.Choice;
                    if (on) ds.FillRoundedRectangle(x, cy - h / 2, widths[j], h, 7, 7, Gfx.WithAlpha(CcText, (f ? 0.26f : 0.18f) * a));
                    PText(ds, choices[j], ui, 14, Gfx.WithAlpha(CcText, (on ? 1f : 0.5f) * a), x, cy, widths[j], CanvasHorizontalAlignment.Center);
                    targets.Add(new(i, SettingsTargetKind.Choice, x, y, widths[j], PerfRowH, j));
                    x += widths[j];
                }
                if (r.Value.Length > 0)
                {
                    float vw = PWidth(ds, r.Value, ui, 13);
                    PText(ds, r.Value, ui, 13, Gfx.WithAlpha(CcText, 0.45f * a), right - total - 14 - vw, cy, vw + 4);
                }
            }
            else if (r.Numeric && !r.Disabled && r.Step != null)
            {
                // ‹ 值 ›：左右两半分别是减和加
                float vw = PWidth(ds, r.Value, ui, 15);
                var chev = Gfx.WithAlpha(CcText, f ? 0.9f : 0.35f);
                PText(ds, "›", ui, 18, chev, right - 10, cy - 1, 14);
                PText(ds, r.Value, ui, 15, Gfx.WithAlpha(CcText, f ? 1f : 0.75f), right - 22 - vw, cy, vw + 4);
                PText(ds, "‹", ui, 18, chev, right - 36 - vw, cy - 1, 14);
                float left = right - 40 - vw;
                targets.Add(new(i, SettingsTargetKind.Decrease, left, y, (right - left) / 2, PerfRowH));
                targets.Add(new(i, SettingsTargetKind.Increase, left + (right - left) / 2, y, (right - left) / 2 + StPad, PerfRowH));
            }
            else
            {
                bool action = r.Press != null && !r.Disabled;
                float end = action ? right - 16 : right;
                if (action) PText(ds, "›", ui, 18, Gfx.WithAlpha(CcText, f ? 0.9f : 0.35f), right - 8, cy - 1, 14);
                if (r.Value.Length > 0)
                {
                    string v = GpFit(ds, r.Value, ui, 14, W * 0.42f);
                    float vw = PWidth(ds, v, ui, 14);
                    PText(ds, v, ui, 14, Gfx.WithAlpha(CcText, (f ? 0.8f : 0.5f) * a), end - vw, cy, vw + 4);
                }
            }
            y += PerfRowH;
        }
    }

    /// <summary>左栏：图标 + 分类名。当前分类亮，焦点在左栏时整行一层浅底。</summary>
    private void PaintStNav(CanvasDrawingSession ds, float h)
    {
        for (int i = 0; i < StCats.Length; i++)
        {
            float top = i * StCatH, cy = top + StCatH / 2;
            bool cur = i == _stCat, focus = cur && _stZone == 0;
            if (focus) ds.FillRoundedRectangle(0, top + 4, PerfLeftW - 80, StCatH - 8, 10, 10, Gfx.WithAlpha(CcText, 0.13f));
            else if (cur) ds.FillRoundedRectangle(0, top + 4, PerfLeftW - 80, StCatH - 8, 10, 10, Gfx.WithAlpha(CcText, 0.06f));
            var c = Gfx.WithAlpha(CcText, cur ? 1f : 0.55f);
            PText(ds, StIcons[i], StIconFont, 18, cur ? PerfAccent : c, 16, cy, 30);
            PText(ds, StCats[i], TextKit.Ui((ushort)(cur ? 600 : 400)), 17, c, 50, cy, PerfLeftW - 140);
        }
        if (_stAgentDown) PText(ds, "连不上工具箱后台", TextKit.Ui(400), 13, Gfx.WithAlpha(CcText, 0.5), 0, h - 12, PerfLeftW);
    }
}
