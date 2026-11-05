// 电池图标和字形度量：状态栏里电池跟旁边的百分比一样高（照 Windows 任务栏：外框高 = 数字高度，垂直居中在数字中线上），
// 中英文混排时按实际字形的基线对齐（拉丁字母和中文用的是两套字体，行框顶端对齐会差出一截）。
using System.Numerics;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace ClawLibrary.Stage;

public static class TextInk
{
    private static readonly Dictionary<(string, float, ushort, string), (float Baseline, float Cap)> _cache = new();

    /// <summary>
    /// 这个格式排出来的字：基线离行框顶多远、数字 / 大写字母有多高。量的是 sample 的实际字形（"H" 和 "0" 底边就在基线上），
    /// familyText 决定用哪套字体（含中文 = 苹方，否则 SF Pro），和 TextKit.Layout 选字体的规则一致。
    /// </summary>
    public static (float Baseline, float Cap) Metrics(ICanvasResourceCreator dev, FontSpec f, float size, string familyText, string sample = "H")
    {
        var key = (f.Family, size, f.Weight, (TextKit.Cjk().IsMatch(familyText) ? "c" : "l") + sample);
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var fmt = TextKit.Shared(f, size, false, text: familyText);
        using var l = new CanvasTextLayout(dev, sample, fmt, 10000, 10000);
        var db = l.DrawBounds;
        var m = ((float)(db.Y + db.Height), (float)db.Height);
        _cache[key] = m;
        return m;
    }
}

public static class BatteryGlyph
{
    /// <summary>
    /// 画在 (x, capTop)，外框高 capH（= 旁边数字的高度），宽约 1.9 倍，右边一个正极小头；按电量填充，
    /// 低于 15 % 且没插电时填红色，插电时外框变绿、中间一道闪电。返回占的总宽。
    /// </summary>
    public static float Draw(CanvasDrawingSession ds, float x, float capTop, float capH, Color ink, BatteryState bat, Color charging, Color low)
    {
        bool plug = bat.Charging || bat.OnAc;
        var c = plug ? charging : ink;
        float h = capH, w = MathF.Round(capH * 1.9f), s = MathF.Max(1.2f, capH * 0.09f), r = capH * 0.26f;
        ds.DrawRoundedRectangle(x + s / 2, capTop + s / 2, w - s, h - s, r, r, c, s);
        float nubW = MathF.Max(1.5f, capH * 0.11f), nubH = h * 0.38f, gap = s * 0.5f;
        ds.FillRoundedRectangle(x + w + gap, capTop + (h - nubH) / 2, nubW, nubH, nubW / 2, nubW / 2, c);
        float pad = s * 2f, full = w - pad * 2, fw = MathF.Max(s, full * Math.Clamp(bat.Percent, 0, 100) / 100f);
        var fill = !plug && bat.Percent < 15 ? low : c;
        ds.FillRoundedRectangle(x + pad, capTop + pad, fw, h - pad * 2, r * 0.45f, r * 0.45f, fill);
        if (plug)
        {
            // 闪电：控制中心原来那支，按外框缩放
            Vector2 P(float px, float py) => new(x + (px - 1) / 14.2f * w, capTop + (py - 5) / 8.4f * h);
            using var pb = new CanvasPathBuilder(ds);
            pb.BeginFigure(P(9.3f, 4.4f));
            pb.AddLine(P(5.9f, 9.6f));
            pb.AddLine(P(8.2f, 9.6f));
            pb.AddLine(P(7.2f, 14f));
            pb.AddLine(P(10.9f, 8.6f));
            pb.AddLine(P(8.6f, 8.6f));
            pb.EndFigure(CanvasFigureLoop.Closed);
            using var g = CanvasGeometry.CreatePath(pb);
            ds.FillGeometry(g, Microsoft.UI.Colors.White);
            ds.DrawGeometry(g, Gfx.Rgba(0, 0, 0, 0.35), MathF.Max(0.6f, s * 0.5f));
        }
        return w + gap + nubW;
    }
}
