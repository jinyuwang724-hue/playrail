// Cocoon 样式页面左下角的实时读数卡（11-25 jinyu：照 Cocoon / 3DS 时期那种圆润、厚实的界面重画，去掉原来左栏的细线图表和小字行）。
// 组成：粗圆环仪表（浅灰轨道 + 紫色渐变弧、圆头）中间放大号数字；浅灰圆角小方块里的柱状历史 / 风扇曲线；
// 底部一排胶囊（灰字标签 + 墨色读数）。全部是这一页自己的数据（原来页面读回来的同一份）。
using System.Numerics;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static readonly Color CpTile = Color.FromArgb(255, 0xF0, 0xF0, 0xF5), CpTrack = Color.FromArgb(255, 0xE4, 0xE4, 0xEC);

    /// <summary>读数卡里画什么：按页面和当前分类（性能页：选中风扇时下面换成风扇曲线）。</summary>
    private void CpPanel(CanvasDrawingSession ds, float x, float y, float w, float h, string cat)
    {
        float pad = 30;
        x += pad; y += pad; w -= pad * 2; h -= pad * 2;
        if (_perfOpen) CpPanelPerf(ds, x, y, w, h, cat);
        else if (_dsOpen) CpPanelDisplay(ds, x, y, w, h);
        else if (_syOpen) CpPanelSystem(ds, x, y, w, h);
    }

    // ---------------- 组件 ----------------

    /// <summary>
    /// 卡片（11-25 jinyu：照 Cocoon 设置页的容器做出立体感）：底下两层投影（贴身的一圈淡影 + 往下压的一层重影，底边最深）；
    /// 面是从上 #FDFDFE 到下 #F0F1F4 的竖向渐变（不是纯白）；上边一道白色高光、描边上浅下深；底边往里一层淡淡的压暗；
    /// 组内分隔线是浅灰线下面再垫一道白线（压出来的凹槽）。
    /// </summary>
    private void CpCard(CanvasDrawingSession ds, float x, float y, float w, float h, List<float>? seps, float r = 30)
    {
        var dev = ds.Device;
        RsSoftShadow(ds, x + 4, y + 12, w - 8, h, r, 14, 0.20f);
        RsSoftShadow(ds, x, y + 2, w, h, r, 3, 0.10f);
        using var card = CanvasGeometry.CreateRoundedRectangle(dev, x, y, w, h, r, r);
        using (var fill = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xFD, 0xFD, 0xFE), Color.FromArgb(255, 0xF0, 0xF1, 0xF4)) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + h) })
            ds.FillGeometry(card, fill);
        using (ds.CreateLayer(1, card))
        {
            using var shade = new CanvasLinearGradientBrush(dev, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(16, 20, 20, 40)) { StartPoint = new Vector2(0, y + h - 22), EndPoint = new Vector2(0, y + h) };
            ds.FillRectangle(x, y + h - 22, w, 22, shade);
            if (seps != null)
                foreach (var sy in seps)
                {
                    ds.FillRectangle(x, sy, w, 2.5f, Color.FromArgb(255, 0xE1, 0xE2, 0xE7));
                    ds.FillRectangle(x, sy + 2.5f, w, 1.5f, Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
                }
            ds.DrawLine(x + r * 0.6f, y + 1.5f, x + w - r * 0.6f, y + 1.5f, Color.FromArgb(255, 0xFF, 0xFF, 0xFF), 2);
        }
        using var edge = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xF4, 0xF4, 0xF7), Color.FromArgb(255, 0xC4, 0xC5, 0xCC)) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + h) };
        ds.DrawRoundedRectangle(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, r, r, edge, 1.5f);
    }

    /// <summary>圆环仪表：开口朝下 270° 的轨道，紫色渐变弧到 frac，圆头；中间大号数字 + 单位，下面一行名称。</summary>
    private void CpRing(CanvasDrawingSession ds, float cx, float cy, float r, float stroke, float frac, string big, string unit, string caption, bool dim = false)
    {
        const float start = 135, sweep = 270;
        static float Rad(float deg) => deg * MathF.PI / 180;
        CanvasGeometry Arc(float deg)
        {
            using var pb = new CanvasPathBuilder(ds.Device);
            pb.BeginFigure(cx + r * MathF.Cos(Rad(start)), cy + r * MathF.Sin(Rad(start)));
            pb.AddArc(new Vector2(cx, cy), r, r, Rad(start), Rad(deg));
            pb.EndFigure(CanvasFigureLoop.Open);
            return CanvasGeometry.CreatePath(pb);
        }
        using (var track = Arc(sweep)) ds.DrawGeometry(track, CpTrack, stroke, RwRoundStroke);
        frac = Math.Clamp(frac, 0, 1);
        if (frac > 0.005f)
        {
            using var arc = Arc(sweep * frac);
            if (dim) ds.DrawGeometry(arc, Color.FromArgb(255, 0xB4, 0xB6, 0xBE), stroke, RwRoundStroke);
            else
            {
                using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(cx - r, cy + r), EndPoint = new Vector2(cx + r, cy - r) };
                ds.DrawGeometry(arc, br, stroke, RwRoundStroke);
            }
        }
        float bigSize = r * (big.Length > 3 ? 0.42f : 0.56f);
        using (var bf = new CanvasTextFormat { FontFamily = RmFont(big), FontSize = bigSize, FontWeight = RsWt(big), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(big, new Windows.Foundation.Rect(cx - r, cy - r * 0.62f, r * 2, r * 0.9f), dim ? RsSub : RmInk, bf);
        if (unit.Length > 0)
            using (var uf = new CanvasTextFormat { FontFamily = RmFont(unit), FontSize = r * 0.22f, FontWeight = RsWt(unit, 500), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(unit, new Windows.Foundation.Rect(cx - r, cy + r * 0.22f, r * 2, r * 0.36f), RsSub, uf);
        using var cf = new CanvasTextFormat { FontFamily = RmFont(caption), FontSize = 30, FontWeight = RsWt(caption), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap };
        ds.DrawText(caption, new Windows.Foundation.Rect(cx - r - 20, cy + r * 0.72f, r * 2 + 40, 48), RmInk, cf);
    }

    /// <summary>胶囊：浅灰圆角底，左边灰字标签，右边墨色读数（放不下时截断）。</summary>
    private static void CpChip(CanvasDrawingSession ds, float x, float y, float w, float h, string label, string value)
    {
        ds.FillRoundedRectangle(x, y, w, h, h / 2, h / 2, CpTile);
        using (var lf = new CanvasTextFormat { FontFamily = RmFont(label), FontSize = 25, FontWeight = RsWt(label, 500), VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap })
            ds.DrawText(label, new Windows.Foundation.Rect(x + 26, y, w * 0.5f, h), RsSub, lf);
        using var vf = new CanvasTextFormat { FontFamily = RmFont(value), FontSize = 27, FontWeight = RsWt(value), HorizontalAlignment = CanvasHorizontalAlignment.Right, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
        float lw = CpTextW(ds, label, 25);
        ds.DrawText(value, new Windows.Foundation.Rect(x + 26 + lw + 16, y, w - 52 - lw - 16, h), RmInk, vf);
    }

    /// <summary>浅灰圆角方块（柱状图、曲线的底）。</summary>
    private static void CpTileBox(CanvasDrawingSession ds, float x, float y, float w, float h) => ds.FillRoundedRectangle(x, y, w, h, 26, 26, CpTile);

    /// <summary>一排胶囊（两个一行），返回用掉的高度。</summary>
    private static float CpChips(CanvasDrawingSession ds, float x, float y, float w, IReadOnlyList<(string L, string V, bool Wide)> chips, float h = 62, float gap = 14)
    {
        float cy = y, col = (w - gap) / 2;
        int k = 0;
        while (k < chips.Count)
        {
            if (chips[k].Wide) { CpChip(ds, x, cy, w, h, chips[k].L, chips[k].V); k++; }
            else if (k + 1 < chips.Count && !chips[k + 1].Wide) { CpChip(ds, x, cy, col, h, chips[k].L, chips[k].V); CpChip(ds, x + col + gap, cy, col, h, chips[k + 1].L, chips[k + 1].V); k += 2; }
            else { CpChip(ds, x, cy, w, h, chips[k].L, chips[k].V); k++; }
            cy += h + gap;
        }
        return cy - y;
    }

    // ---------------- 性能 ----------------

    private void CpPanelPerf(CanvasDrawingSession ds, float x, float y, float w, float h, string cat)
    {
        var v = _perfView;
        var s = _perfS;
        double? pkg = Num(v?["actual"]?["package"]);
        int capW = Math.Max(Int(v?["target"]?["pl1"], 0), 1);
        double? rpm = Num(v?["fan"]?["rpm1"]);
        string fmode = Str(s?["fan"]?["mode"]) ?? "balanced";
        int fanCap = Int(v?["fan"]?["caps"]?[fmode], 76);
        double rpmMax = FanRpm(Math.Max(fanCap, 1));
        double? temp = Num(v?["live"]?["cpuTemp"]);

        // 两个圆环：功耗（占当前上限的比例）、风扇转速（占这一档上限）
        float r = MathF.Min(98, w / 4 - 16), ringCy = y + r + 6;
        CpRing(ds, x + w * 0.25f, ringCy, r, 24, pkg is double p ? (float)(p / Math.Max(capW, 10)) : 0, pkg is double p2 ? $"{p2:0}" : "—", "W", "功耗");
        CpRing(ds, x + w * 0.75f, ringCy, r, 24, rpm is double rr ? (float)(rr / Math.Max(rpmMax, 1)) : 0, rpm is double r2 ? $"{r2:0}" : "—", "RPM", "风扇");

        float chipsH = 62 * 2 + 14;
        float tileY = ringCy + r * 0.72f + 64, tileH = y + h - chipsH - 20 - tileY;
        if (tileH >= 120)
        {
            CpTileBox(ds, x, tileY, w, tileH);
            if (cat == "风扇") CpFanCurve(ds, x + 26, tileY + 26, w - 52, tileH - 52, fmode, temp);
            else CpPowerBars(ds, x + 26, tileY + 26, w - 52, tileH - 52, capW);
        }
        string cpu = _cpuFreq?.Mhz is double mhz ? $"{mhz / 1000:0.00} GHz" : "—";
        string preset = s == null ? "—" : PerfPresetText();
        CpChips(ds, x, y + h - chipsH, w, new[] { ("温度", temp is double t ? $"{t:0} °C" : "—", false), ("CPU", cpu, false), ("预设", preset, true) });
    }

    /// <summary>最近 60 秒的功耗：一根根圆头竖条（3DS 活动记录那种），最新的一根最亮。</summary>
    private void CpPowerBars(CanvasDrawingSession ds, float x, float y, float w, float h, int capW)
    {
        var hist = _perfPower.ToArray();
        const int n = 30;
        float gap = 6, bw = (w - gap * (n - 1)) / n;
        double max = Math.Max(Math.Max(capW, hist.Length > 0 ? hist.Max() : 0), 10);
        for (int i = 0; i < n; i++)
        {
            // 每两个采样合一根（最近 60 个采样 → 30 根）
            int a = hist.Length - (n - i) * 2;
            double val = 0;
            int cnt = 0;
            for (int j = a; j < a + 2; j++) if (j >= 0 && j < hist.Length) { val += hist[j]; cnt++; }
            float bx = x + i * (bw + gap);
            ds.FillRoundedRectangle(bx, y, bw, h, bw / 2, bw / 2, Color.FromArgb(255, 0xE6, 0xE6, 0xEE));
            if (cnt == 0) continue;
            float bh = Math.Max(bw, (float)(val / cnt / max) * h);
            using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(0, y + h - bh), EndPoint = new Vector2(0, y + h) };
            using (ds.CreateLayer(i == n - 1 ? 1f : 0.55f + 0.45f * i / n))
                ds.FillRoundedRectangle(bx, y + h - bh, bw, bh, bw / 2, bw / 2, br);
        }
    }

    /// <summary>风扇曲线：粗圆头折线 + 白心紫边的点；曲线编辑时选中的点放大、上面一个读数胶囊；当前温度一个紫点。</summary>
    private void CpFanCurve(CanvasDrawingSession ds, float x, float y, float w, float h, string mode, double? temp)
    {
        var v = _perfView;
        int[] axis = (v?["fan"]?["axis"] as JsonArray)?.Select(nd => Int(nd)).ToArray() is { Length: 6 } ax ? ax : new[] { 50, 60, 70, 75, 80, 90 };
        int[] curve = PerfCurve(mode);
        float X(double t) => x + (float)((Math.Clamp(t, axis[0], axis[^1]) - axis[0]) / (axis[^1] - axis[0]) * w);
        float Y(double d) => y + h - (float)(Math.Clamp(d, 0, 100) / 100 * h);
        using (var pb = new CanvasPathBuilder(ds.Device))
        {
            pb.BeginFigure(X(axis[0]), Y(curve[0]));
            for (int i = 1; i < 6; i++) pb.AddLine(X(axis[i]), Y(curve[i]));
            pb.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(pb);
            using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(x, 0), EndPoint = new Vector2(x + w, 0) };
            using var join = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
            ds.DrawGeometry(g, br, 10, join);
        }
        for (int i = 0; i < 6; i++)
        {
            float px = X(axis[i]), py = Y(curve[i]);
            bool sel = _perfCurveEdit && i == _perfPoint;
            if (sel)
            {
                ds.FillCircle(px, py, 18, RwC2);
                ds.FillCircle(px, py, 8, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                string tag = $"{axis[i]} °C · {curve[i]} %";
                float tw = CpTextW(ds, tag, 26) + 36;
                float tx = Math.Clamp(px - tw / 2, x - 16, x + w + 16 - tw), ty = Math.Max(y - 16, py - 72);
                ds.FillRoundedRectangle(tx, ty, tw, 46, 23, 23, RmInk);
                using var f = new CanvasTextFormat { FontFamily = RmFont(tag), FontSize = 26, FontWeight = RsWt(tag), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
                ds.DrawText(tag, new Windows.Foundation.Rect(tx, ty, tw, 46), Color.FromArgb(255, 0xFA, 0xFA, 0xFA), f);
            }
            else
            {
                ds.FillCircle(px, py, 12, Color.FromArgb(255, 0xFA, 0xFA, 0xFA));
                ds.DrawCircle(px, py, 10, RwC2, 5);
            }
        }
        if (temp is double tc && !_perfCurveEdit)
        {
            // 当前温度：曲线底下一颗紫色小圆点
            ds.FillCircle(X(tc), y + h + 2, 9, RwC2);
        }
    }

    // ---------------- 显示与声音 ----------------

    private void CpPanelDisplay(CanvasDrawingSession ds, float x, float y, float w, float h)
    {
        var v = _dsView;
        int bri = Int(v?["brightness"], -1), vol = Int(v?["volume"], -1), hz = Int(v?["refresh"]);
        bool muted = Bool(v?["muted"]);
        float r = MathF.Min(98, w / 4 - 16), ringCy = y + r + 6;
        CpRing(ds, x + w * 0.25f, ringCy, r, 24, bri < 0 ? 0 : bri / 100f, bri < 0 ? "—" : $"{bri}", "%", "亮度");
        CpRing(ds, x + w * 0.75f, ringCy, r, 24, vol < 0 ? 0 : vol / 100f, vol < 0 ? "—" : muted ? "静音" : $"{vol}", muted ? "" : "%", "音量", dim: muted);
        var res = v?["resolution"] as JsonArray;
        string resText = res?.Count == 2 ? $"{Int(res[0])} × {Int(res[1])}" : "—";
        string output = "—";
        if (v?["outputs"] is JsonArray outs && Str(v["output"]) is string oid)
            output = outs.FirstOrDefault(o => Str(o?["id"]) == oid) is JsonNode on ? Str(on["name"]) ?? "—" : "—";
        var chips = new List<(string, string, bool)> { ("分辨率", resText, true), ("刷新率", hz > 0 ? $"{hz} Hz" : "—", false) };
        chips.Add(("缩放", v?["scale"] is JsonValue ? $"{Int(v["scale"])} %" : "—", false));
        chips.Add(("输出", output, true));
        CpChips(ds, x, ringCy + r * 0.72f + 72, w, chips);
    }

    // ---------------- 系统 ----------------

    private void CpPanelSystem(CanvasDrawingSession ds, float x, float y, float w, float h)
    {
        var v = _syView;
        var bat = SystemInfo.Battery();
        var b = v?["battery"] as JsonObject;
        float r = MathF.Min(118, w / 3), ringCy = y + r + 6;
        string state = !bat.HasBattery ? "电池" : bat.OnAc ? (bat.Percent >= 99 ? "已充满" : "充电中") : "使用电池";
        CpRing(ds, x + w / 2, ringCy, r, 28, bat.HasBattery ? bat.Percent / 100f : 0, bat.HasBattery ? $"{bat.Percent}" : "—", "%", state);
        var chips = new List<(string, string, bool)>();
        int minutes = Int(b?[bat.OnAc ? "chargeMinutes" : "minutes"]);
        if (minutes > 0) chips.Add((bat.OnAc ? "充满" : "剩余", minutes >= 60 ? $"{minutes / 60} 小时 {minutes % 60:00} 分" : $"{minutes} 分钟", true));
        int design = Int(b?["design"]), full = Int(b?["full"]);
        if (design > 0 && full > 0) chips.Add(("健康度", $"{Math.Round(100.0 * full / design)} %", false));
        if (Int(b?["cycles"]) > 0) chips.Add(("循环", $"{Int(b?["cycles"])} 次", false));
        CpChips(ds, x, ringCy + r * 0.72f + 72, w, chips);
    }
}
