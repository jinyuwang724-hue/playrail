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
    /// 照 Windows 11 任务栏：画在 (x, capTop)，外框高 capH（= 旁边数字的高度），宽约 1.9 倍，右边一个正极小头，外框和填充都是 ink；
    /// 按电量填充，低于 15 % 且没插电时填红色。充电中：外框正中一道闪电；接着电源但没在充电（充满了 / 被充电上限挡住）：一个插头。
    /// 闪电和插头四周挖掉一圈（外框和填充在那里断开），所以叠在填充上也看得清。不再用绿色。返回占的总宽。
    /// charging 参数保留只为调用处不变，已不用。
    /// </summary>
    public static float Draw(CanvasDrawingSession ds, float x, float capTop, float capH, Color ink, BatteryState bat, Color charging, Color low)
    {
        bool bolt = bat.Charging, plug = !bat.Charging && bat.OnAc;
        float h = capH, w = MathF.Round(capH * 1.9f), s = MathF.Max(1.2f, capH * 0.09f), r = capH * 0.26f;
        float nubW = MathF.Max(1.5f, capH * 0.11f), nubH = h * 0.38f, gap = s * 0.5f;
        float pad = s * 2f, full = w - pad * 2, fw = MathF.Max(s, full * Math.Clamp(bat.Percent, 0, 100) / 100f);
        var fillColor = !bolt && !plug && bat.Percent < 15 ? low : ink;
        using var frame = CanvasGeometry.CreateRoundedRectangle(ds, x + s / 2, capTop + s / 2, w - s, h - s, r, r).Stroke(s);
        using var fill = CanvasGeometry.CreateRoundedRectangle(ds, x + pad, capTop + pad, fw, h - pad * 2, r * 0.45f, r * 0.45f);
        using var nub = CanvasGeometry.CreateRoundedRectangle(ds, x + w + gap, capTop + (h - nubH) / 2, nubW, nubH, nubW / 2, nubW / 2);
        if (!bolt && !plug)
        {
            ds.FillGeometry(frame, ink);
            ds.FillGeometry(nub, ink);
            ds.FillGeometry(fill, fillColor);
            return w + gap + nubW;
        }
        float cx = x + w / 2, cy = capTop + h / 2;
        using var mark = bolt ? Bolt(ds, cx, cy, h) : Plug(ds, cx, cy, h, s);
        // 挖空：标记本身再往外扩一圈（约一笔粗）
        using var halo = mark.CombineWith(mark.Stroke(s * 2.2f), Matrix3x2.Identity, CanvasGeometryCombine.Union);
        using (var f2 = frame.CombineWith(halo, Matrix3x2.Identity, CanvasGeometryCombine.Exclude)) ds.FillGeometry(f2, ink);
        ds.FillGeometry(nub, ink);
        using (var f3 = fill.CombineWith(halo, Matrix3x2.Identity, CanvasGeometryCombine.Exclude)) ds.FillGeometry(f3, fillColor);
        ds.FillGeometry(mark, ink);
        return w + gap + nubW;
    }

    /// <summary>闪电：比外框略高（上下各探出一点），居中。</summary>
    private static CanvasGeometry Bolt(ICanvasResourceCreator dev, float cx, float cy, float h)
    {
        float bw = h * 0.62f, bh = h * 1.16f, x0 = cx - bw / 2, y0 = cy - bh / 2;
        Vector2 P(float u, float v) => new(x0 + u * bw, y0 + v * bh);
        using var pb = new CanvasPathBuilder(dev);
        pb.BeginFigure(P(0.64f, 0));
        pb.AddLine(P(0.06f, 0.57f));
        pb.AddLine(P(0.46f, 0.57f));
        pb.AddLine(P(0.34f, 1));
        pb.AddLine(P(0.94f, 0.41f));
        pb.AddLine(P(0.54f, 0.41f));
        pb.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(pb);
    }

    /// <summary>插头：插头身子朝左两根插脚，右边一截线。</summary>
    private static CanvasGeometry Plug(ICanvasResourceCreator dev, float cx, float cy, float h, float s)
    {
        float bw = h * 0.42f, bh = h * 0.62f, bx = cx - bw / 2 + h * 0.05f, by = cy - bh / 2;
        float pin = h * 0.26f, pinT = MathF.Max(1f, s * 0.95f), cord = h * 0.3f;
        using var head = CanvasGeometry.CreateRoundedRectangle(dev, bx, by, bw, bh, bh * 0.22f, bh * 0.22f);
        using var p1 = CanvasGeometry.CreateRoundedRectangle(dev, bx - pin, cy - bh * 0.3f - pinT / 2, pin + 1, pinT, pinT / 2, pinT / 2);
        using var p2 = CanvasGeometry.CreateRoundedRectangle(dev, bx - pin, cy + bh * 0.3f - pinT / 2, pin + 1, pinT, pinT / 2, pinT / 2);
        using var cd = CanvasGeometry.CreateRoundedRectangle(dev, bx + bw - 1, cy - pinT / 2, cord + 1, pinT, pinT / 2, pinT / 2);
        using var a = head.CombineWith(p1, Matrix3x2.Identity, CanvasGeometryCombine.Union);
        using var b = a.CombineWith(p2, Matrix3x2.Identity, CanvasGeometryCombine.Union);
        return b.CombineWith(cd, Matrix3x2.Identity, CanvasGeometryCombine.Union);
    }
}