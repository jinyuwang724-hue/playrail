using System.Numerics;
using CocoonUI.Core;
using CocoonUI.Primitives;
using CocoonUI.Tokens;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace CocoonUI.Charts;

/// <summary>Telemetry visualizers: 270° gauge rings, fixed-bucket activity bars, editable curve, and metric chips.</summary>
public static class CocoonChartPainter
{
    public static void DrawGaugeRing(
        CanvasDrawingSession ds,
        CocoonTypography typo,
        float cx,
        float cy,
        float r,
        float stroke,
        float fraction,
        string bigValue,
        string unit,
        string caption,
        bool dim = false)
    {
        const float startDeg = 135f, sweepDeg = 270f;
        static float Rad(float deg) => deg * MathF.PI / 180f;

        CanvasGeometry Arc(float deg)
        {
            using var pb = new CanvasPathBuilder(ds.Device);
            pb.BeginFigure(cx + r * MathF.Cos(Rad(startDeg)), cy + r * MathF.Sin(Rad(startDeg)));
            pb.AddArc(new Vector2(cx, cy), r, r, Rad(startDeg), Rad(deg));
            pb.EndFigure(CanvasFigureLoop.Open);
            return CanvasGeometry.CreatePath(pb);
        }

        using (var track = Arc(sweepDeg))
            ds.DrawGeometry(track, CocoonTokens.Colors.SurfaceTrack, stroke, CocoonContext.RoundStroke);

        fraction = Math.Clamp(fraction, 0f, 1f);
        if (fraction > 0.005f)
        {
            using var arc = Arc(sweepDeg * fraction);
            if (dim)
            {
                ds.DrawGeometry(arc, Color.FromArgb(255, 0xB4, 0xB6, 0xBE), stroke, CocoonContext.RoundStroke);
            }
            else
            {
                using var br = new CanvasLinearGradientBrush(ds.Device, CocoonTokens.Colors.BrandAccentStart, CocoonTokens.Colors.BrandAccentEnd)
                {
                    StartPoint = new Vector2(cx - r, cy + r),
                    EndPoint = new Vector2(cx + r, cy - r),
                };
                ds.DrawGeometry(arc, br, stroke, CocoonContext.RoundStroke);
            }
        }

        float bigSize = r * (bigValue.Length > 3 ? 0.42f : 0.56f);
        using (var bf = typo.CreateFormat(bigValue, bigSize, 600, CanvasHorizontalAlignment.Center, CanvasVerticalAlignment.Center))
            ds.DrawText(bigValue, new Rect(cx - r, cy - r * 0.62f, r * 2f, r * 0.9f), dim ? CocoonTokens.Colors.NeutralInkSecondary : CocoonTokens.Colors.NeutralInkPrimary, bf);

        if (unit.Length > 0)
        {
            using var uf = typo.CreateFormat(unit, r * 0.22f, 500, CanvasHorizontalAlignment.Center, CanvasVerticalAlignment.Center);
            ds.DrawText(unit, new Rect(cx - r, cy + r * 0.22f, r * 2f, r * 0.36f), CocoonTokens.Colors.NeutralInkSecondary, uf);
        }

        using var cf = typo.CreateFormat(caption, CocoonTokens.FontSizes.GaugeCaption, 600, CanvasHorizontalAlignment.Center, CanvasVerticalAlignment.Center);
        ds.DrawText(caption, new Rect(cx - r - 20f, cy + r * 0.72f, r * 2f + 40f, 48f), CocoonTokens.Colors.NeutralInkPrimary, cf);
    }

    /// <summary>Fixed-sequence 2-sample bucketing so historical bars slide cleanly without re-pairing jitter.</summary>
    public static double?[] ComputeActivityBuckets(IReadOnlyList<double> samples, int sampleSeq, int barCount = 30)
    {
        var result = new double?[barCount];
        int tail = (sampleSeq & 1) == 1 ? 1 : 2;
        for (int i = 0; i < barCount; i++)
        {
            int end = i == barCount - 1 ? samples.Count : samples.Count - tail - (barCount - 2 - i) * 2;
            int len = i == barCount - 1 ? tail : 2;
            int start = end - len;
            double sum = 0;
            int cnt = 0;
            for (int j = start; j < end; j++)
            {
                if (j >= 0 && j < samples.Count) { sum += samples[j]; cnt++; }
            }
            if (cnt > 0) result[i] = sum / cnt;
        }
        return result;
    }

    public static void DrawActivityBars(
        CanvasDrawingSession ds,
        float x,
        float y,
        float w,
        float h,
        IReadOnlyList<double> samples,
        int sampleSeq,
        double capMax,
        int barCount = 30)
    {
        const float gap = 6f;
        float bw = (w - gap * (barCount - 1)) / barCount;
        double max = capMax;
        for (int i = 0; i < samples.Count; i++)
            if (samples[i] > max) max = samples[i];
        max = Math.Max(max, 10.0);

        var buckets = ComputeActivityBuckets(samples, sampleSeq, barCount);
        for (int i = 0; i < barCount; i++)
        {
            float bx = x + i * (bw + gap);
            ds.FillRoundedRectangle(bx, y, bw, h, bw / 2f, bw / 2f, CocoonTokens.Colors.SurfaceBarTrack);
            if (buckets[i] is not double val) continue;

            float bh = Math.Max(bw, (float)(val / max) * h);
            using var br = new CanvasLinearGradientBrush(ds.Device, CocoonTokens.Colors.BrandAccentStart, CocoonTokens.Colors.BrandAccentEnd)
            {
                StartPoint = new Vector2(0f, y + h - bh),
                EndPoint = new Vector2(0f, y + h),
            };
            using (ds.CreateLayer(i == barCount - 1 ? 1f : 0.55f + 0.45f * i / barCount))
                ds.FillRoundedRectangle(bx, y + h - bh, bw, bh, bw / 2f, bw / 2f, br);
        }
    }

    public static void DrawCurveEditor(
        CanvasDrawingSession ds,
        CocoonTypography typo,
        float x,
        float y,
        float w,
        float h,
        IReadOnlyList<int> axis,
        IReadOnlyList<int> curve,
        bool editing,
        int selectedIndex,
        double? liveValue)
    {
        int n = Math.Min(axis.Count, curve.Count);
        if (n < 2) return;
        float minX = axis[0], spanX = Math.Max(1, axis[n - 1] - axis[0]);
        float X(double t) => x + (float)((Math.Clamp(t, minX, axis[n - 1]) - minX) / spanX * w);
        float Y(double d) => y + h - (float)(Math.Clamp(d, 0.0, 100.0) / 100.0 * h);

        using (var pb = new CanvasPathBuilder(ds.Device))
        {
            pb.BeginFigure(X(axis[0]), Y(curve[0]));
            for (int i = 1; i < n; i++) pb.AddLine(X(axis[i]), Y(curve[i]));
            pb.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(pb);
            using var br = new CanvasLinearGradientBrush(ds.Device, CocoonTokens.Colors.BrandAccentStart, CocoonTokens.Colors.BrandAccentEnd)
            {
                StartPoint = new Vector2(x, 0f),
                EndPoint = new Vector2(x + w, 0f),
            };
            ds.DrawGeometry(g, br, 10f, CocoonContext.RoundStroke);
        }

        for (int i = 0; i < n; i++)
        {
            float px = X(axis[i]), py = Y(curve[i]);
            if (editing && i == selectedIndex)
            {
                ds.FillCircle(px, py, 18f, CocoonTokens.Colors.BrandAccentEnd);
                ds.FillCircle(px, py, 8f, CocoonTokens.Colors.NeutralInkOnDark);
                string tag = $"{axis[i]} °C · {curve[i]} %";
                float tw = typo.MeasureWidth(ds, tag, 26f, 600) + 36f;
                float tx = Math.Clamp(px - tw / 2f, x - 16f, x + w + 16f - tw);
                float ty = Math.Max(y - 16f, py - 72f);
                ds.FillRoundedRectangle(tx, ty, tw, 46f, 23f, 23f, CocoonTokens.Colors.NeutralInkPrimary);
                using var f = typo.CreateFormat(tag, 26f, 600, CanvasHorizontalAlignment.Center, CanvasVerticalAlignment.Center);
                ds.DrawText(tag, new Rect(tx, ty, tw, 46f), CocoonTokens.Colors.NeutralInkOnDark, f);
            }
            else
            {
                ds.FillCircle(px, py, 12f, CocoonTokens.Colors.NeutralInkOnDark);
                ds.DrawCircle(px, py, 10f, CocoonTokens.Colors.BrandAccentEnd, 5f);
            }
        }

        if (liveValue is double tc && !editing)
            ds.FillCircle(X(tc), y + h + 2f, 9f, CocoonTokens.Colors.BrandAccentEnd);
    }

    public static void DrawMetricChip(
        CanvasDrawingSession ds,
        CocoonTypography typo,
        float x,
        float y,
        float w,
        float h,
        string label,
        string value)
    {
        ds.FillRoundedRectangle(x, y, w, h, h / 2f, h / 2f, CocoonTokens.Colors.SurfaceTileWell);
        using (var lf = typo.CreateFormat(label, CocoonTokens.FontSizes.ChipLabel, 500, vAlign: CanvasVerticalAlignment.Center))
            ds.DrawText(label, new Rect(x + 26f, y, w * 0.5f, h), CocoonTokens.Colors.NeutralInkSecondary, lf);

        float lw = typo.MeasureWidth(ds, label, CocoonTokens.FontSizes.ChipLabel, 500);
        using var vf = typo.CreateFormat(value, CocoonTokens.FontSizes.ChipValue, 600, CanvasHorizontalAlignment.Right, CanvasVerticalAlignment.Center, ellipsis: true);
        ds.DrawText(value, new Rect(x + 26f + lw + 16f, y, MathF.Max(10f, w - 52f - lw - 16f), h), CocoonTokens.Colors.NeutralInkPrimary, vf);
    }

    public static float DrawMetricChips(
        CanvasDrawingSession ds,
        CocoonTypography typo,
        float x,
        float y,
        float w,
        IReadOnlyList<(string Label, string Value, bool Wide)> chips,
        float h = 62f,
        float gap = CocoonTokens.Spacing.ChipGap)
    {
        float cy = y, col = (w - gap) / 2f;
        int k = 0;
        while (k < chips.Count)
        {
            if (!chips[k].Wide && k + 1 < chips.Count && !chips[k + 1].Wide)
            {
                DrawMetricChip(ds, typo, x, cy, col, h, chips[k].Label, chips[k].Value);
                DrawMetricChip(ds, typo, x + col + gap, cy, col, h, chips[k + 1].Label, chips[k + 1].Value);
                k += 2;
            }
            else
            {
                DrawMetricChip(ds, typo, x, cy, w, h, chips[k].Label, chips[k].Value);
                k++;
            }
            cy += h + gap;
        }
        return cy - y;
    }
}
