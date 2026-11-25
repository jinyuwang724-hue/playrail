using System.Numerics;
using CocoonUI.Core;
using CocoonUI.Tokens;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace CocoonUI.Primitives;

/// <summary>Stateless Win2D painters for Cocoon atomic controls (capsules, toggles, dropdowns, sliders, steppers, toasts).</summary>
public static class CocoonControlPainter
{
    public const float CapsuleW = 134f, CapsuleH = 74f;

    public static void DrawBackCapsule(CanvasDrawingSession ds, CocoonTypography typo, float x, float y)
    {
        var dev = ds.Device;
        ds.FillRoundedRectangle(x, y, CapsuleW, CapsuleH, CapsuleH / 2f, CapsuleH / 2f, CocoonTokens.Colors.SurfaceBackPill);
        ds.FillCircle(x + 36f, y + 37f, 21f, CocoonTokens.Colors.NeutralInkPlaceholder);
        using (var f = new CanvasTextFormat
        {
            FontFamily = typo.Config.LatinBold,
            FontSize = CocoonTokens.FontSizes.Keycap,
            FontWeight = new Windows.UI.Text.FontWeight(700),
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        })
        {
            ds.DrawText("B", new Rect(x + 15f, y + 16f, 42f, 42f), CocoonTokens.Colors.SurfaceDrawerTop, f);
        }
        using var pb = new CanvasPathBuilder(dev);
        pb.BeginFigure(x + 114f, y + 37f); pb.AddLine(x + 78f, y + 37f); pb.EndFigure(CanvasFigureLoop.Open);
        pb.BeginFigure(x + 94f, y + 21f); pb.AddLine(x + 78f, y + 37f); pb.AddLine(x + 94f, y + 53f); pb.EndFigure(CanvasFigureLoop.Open);
        using var arrow = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(arrow, CocoonTokens.Colors.NeutralInkPrimary, 7f, CocoonContext.RoundStroke);
    }

    public static void DrawActionCapsule(CanvasDrawingSession ds, CocoonTypography typo, float raster, float x, float y, bool playIcon = false)
    {
        var dev = ds.Device;
        CocoonCardPainter.DrawSoftShadow(ds, raster, x + 4f, y + 8f, CapsuleW - 8f, CapsuleH - 4f, (CapsuleH - 4f) / 2f, 9f, 0.45f);
        using (var br = new CanvasLinearGradientBrush(dev, CocoonTokens.Colors.BrandPillActionStart, CocoonTokens.Colors.BrandPillActionEnd)
        {
            StartPoint = new Vector2(x, y),
            EndPoint = new Vector2(x + CapsuleW, y + CapsuleH),
        })
        {
            ds.FillRoundedRectangle(x, y, CapsuleW, CapsuleH, CapsuleH / 2f, CapsuleH / 2f, br);
        }
        ds.FillCircle(x + 37f, y + 37f, 21f, CocoonTokens.Colors.BrandPillBadgeLight);
        using (var f = new CanvasTextFormat
        {
            FontFamily = typo.Config.LatinBold,
            FontSize = CocoonTokens.FontSizes.Keycap,
            FontWeight = new Windows.UI.Text.FontWeight(700),
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        })
        {
            ds.DrawText("Y", new Rect(x + 16f, y + 16f, 42f, 42f), CocoonTokens.Colors.BrandAccentInk, f);
        }

        if (playIcon)
        {
            using var pb = new CanvasPathBuilder(dev);
            pb.BeginFigure(x + 88f, y + 20f);
            pb.AddLine(x + 114f, y + 37f);
            pb.AddLine(x + 88f, y + 54f);
            pb.EndFigure(CanvasFigureLoop.Closed);
            using var tri = CanvasGeometry.CreatePath(pb);
            ds.FillGeometry(tri, CocoonTokens.Colors.NeutralInkOnDark);
            ds.DrawGeometry(tri, CocoonTokens.Colors.NeutralInkOnDark, 4f, CocoonContext.RoundStroke);
            return;
        }

        ds.FillCircle(x + 98f, y + 37f, 21f, CocoonTokens.Colors.NeutralInkOnDark);
        using var tickPb = new CanvasPathBuilder(dev);
        tickPb.BeginFigure(x + 88f, y + 37f);
        tickPb.AddLine(x + 95f, y + 44f);
        tickPb.AddLine(x + 108f, y + 30f);
        tickPb.EndFigure(CanvasFigureLoop.Open);
        using var tick = CanvasGeometry.CreatePath(tickPb);
        ds.DrawGeometry(tick, CocoonTokens.Colors.BrandAccentInk, 5f, CocoonContext.RoundStroke);
    }

    public static void DrawToggleSwitch(CanvasDrawingSession ds, float raster, float x, float cy, bool isOn)
    {
        const float w = 120f, h = 76f;
        float ty = cy - h / 2f;
        if (isOn)
        {
            using var br = new CanvasLinearGradientBrush(ds.Device, CocoonTokens.Colors.BrandAccentStart, CocoonTokens.Colors.BrandAccentEnd)
            {
                StartPoint = new Vector2(x, ty),
                EndPoint = new Vector2(x + w, ty + h),
            };
            ds.FillRoundedRectangle(x, ty, w, h, h / 2f, h / 2f, br);
        }
        else
        {
            ds.FillRoundedRectangle(x, ty, w, h, h / 2f, h / 2f, CocoonTokens.Colors.SurfaceToggleOff);
            ds.DrawRoundedRectangle(x + 2f, ty + 2f, w - 4f, h - 4f, h / 2f - 2f, h / 2f - 2f, CocoonTokens.Colors.NeutralInkPlaceholder, 4f);
        }
        float kx = isOn ? x + 82f : x + 38f;
        CocoonCardPainter.DrawSoftShadow(ds, raster, kx - 27f, cy - 27f + 3f, 54f, 54f, 27f, 4f, 0.22f);
        ds.FillCircle(kx, cy, 27f, CocoonTokens.Colors.NeutralInkOnDark);
    }

    public static void DrawPickPill(
        CanvasDrawingSession ds,
        CocoonTypography typo,
        float raster,
        float x,
        float y,
        float w,
        float h,
        string value,
        bool isOpen,
        float fontSize = CocoonTokens.FontSizes.RowValue,
        bool drawText = true)
    {
        float cy = y + h / 2f;
        float k = h / 84f;
        CocoonCardPainter.DrawSoftShadow(ds, raster, x, y + 3f * k, w, h, h / 2f, 6f * k, 0.14f);
        ds.FillRoundedRectangle(x, y, w, h, h / 2f, h / 2f, CocoonTokens.Colors.SurfacePillWhite);
        ds.DrawRoundedRectangle(x + 0.5f, y + 0.5f, w - 1f, h - 1f, h / 2f, h / 2f, CocoonTokens.Colors.SurfacePillBorder, 1f);

        if (drawText)
        {
            using var vf = typo.CreateFormat(value, fontSize, 600, CanvasHorizontalAlignment.Left, CanvasVerticalAlignment.Center, ellipsis: true);
            ds.DrawText(value, new Rect(x + 30f * k, y, MathF.Max(10f, w - 90f * k), h), CocoonTokens.Colors.NeutralInkPrimary, vf);
        }

        float tx = x + w - 43f * k, a = 7f * k, b = 4.5f * k;
        using var pb = new CanvasPathBuilder(ds.Device);
        if (isOpen) { pb.BeginFigure(tx - a, cy + b); pb.AddLine(tx + a, cy + b); pb.AddLine(tx, cy - b); }
        else { pb.BeginFigure(tx - a, cy - b); pb.AddLine(tx + a, cy - b); pb.AddLine(tx, cy + b); }
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var tri = CanvasGeometry.CreatePath(pb);
        var chevColor = isOpen ? CocoonTokens.Colors.NeutralInkPrimary : CocoonTokens.Colors.NeutralInkSecondary;
        ds.FillGeometry(tri, chevColor);
        ds.DrawGeometry(tri, chevColor, 3f * k, CocoonContext.RoundStroke);
    }

    public static void DrawOptionCard(
        CanvasDrawingSession ds,
        CocoonTypography typo,
        float raster,
        float x0,
        float y0,
        float w,
        float optH,
        IReadOnlyList<string> options,
        int selectedIndex,
        bool multiSelect = false,
        Func<int, bool>? isChecked = null,
        int focusedOption = -1,
        float fontSize = CocoonTokens.FontSizes.DropdownOption)
    {
        float h = options.Count * optH;
        float cr = MathF.Min(CocoonTokens.Radii.Card, optH * 0.4f);
        CocoonCardPainter.DrawSoftShadow(ds, raster, x0, y0 + 5f, w, h, cr, 8f, 0.18f);

        using var card = CanvasGeometry.CreateRoundedRectangle(ds.Device, x0, y0, w, h, cr, cr);
        ds.FillGeometry(card, CocoonTokens.Colors.SurfaceOptionCard);

        using (ds.CreateLayer(1f, card))
        {
            for (int k = 0; k < options.Count; k++)
            {
                float oy = y0 + k * optH;
                if (multiSelect && k == focusedOption)
                    ds.FillRectangle(x0, oy, w, optH, CocoonTokens.Colors.BrandSelectionTint);
                if (k > 0)
                    ds.FillRectangle(x0 + 28f, oy - 1f, w - 56f, 2f, CocoonTokens.Colors.SurfaceDividerSubtle);

                bool sel = !multiSelect && k == selectedIndex;
                using var tf = typo.CreateFormat(options[k], fontSize, 600, CanvasHorizontalAlignment.Left, CanvasVerticalAlignment.Center, ellipsis: true);
                ds.DrawText(
                    options[k],
                    new Rect(x0 + 30f * fontSize / 36f, oy, MathF.Max(20f, w - (multiSelect ? 110f : 60f)), optH),
                    sel ? CocoonTokens.Colors.SemanticSelectedOption : CocoonTokens.Colors.NeutralInkPrimary,
                    tf);

                if (!multiSelect) continue;
                float cx = x0 + w - 52f, ccy = oy + optH / 2f;
                if (isChecked?.Invoke(k) == true)
                {
                    CocoonCardPainter.DrawSoftShadow(ds, raster, cx - 21f, ccy - 21f + 2f, 42f, 42f, 21f, 3f, 0.2f);
                    using var br = new CanvasLinearGradientBrush(ds.Device, CocoonTokens.Colors.BrandAccentStart, CocoonTokens.Colors.BrandAccentEnd)
                    {
                        StartPoint = new Vector2(cx - 21f, ccy - 21f),
                        EndPoint = new Vector2(cx + 21f, ccy + 21f),
                    };
                    ds.FillCircle(cx, ccy, 21f, br);
                    ds.DrawCircle(cx, ccy, 20f, CocoonTokens.Colors.NeutralInkOnDark, 2.5f);
                    using var pb = new CanvasPathBuilder(ds.Device);
                    pb.BeginFigure(cx - 9f, ccy);
                    pb.AddLine(cx - 2.5f, ccy + 7f);
                    pb.AddLine(cx + 10f, ccy - 7f);
                    pb.EndFigure(CanvasFigureLoop.Open);
                    using var tick = CanvasGeometry.CreatePath(pb);
                    ds.DrawGeometry(tick, CocoonTokens.Colors.NeutralInkOnDark, 4.5f, CocoonContext.RoundStroke);
                }
                else
                {
                    ds.DrawCircle(cx, ccy, 19f, Color.FromArgb(255, 0xB4, 0xB6, 0xBC), 3.5f);
                }
            }
        }

        ds.DrawRoundedRectangle(x0 + 0.5f, y0 + 0.5f, w - 1f, h - 1f, cr, cr, CocoonTokens.Colors.SurfaceDividerSubtle, 1f);
    }

    public static (float MinusX, float TrackX0, float TrackX1, float PlusX) SliderMetrics(float tx, float rightEdge) =>
        (tx + 170f, tx + 222f, rightEdge - 64f, rightEdge - 16f);

    public static void DrawSliderRow(
        CanvasDrawingSession ds,
        CocoonTypography typo,
        float raster,
        float tx,
        float rightEdge,
        float y,
        float h,
        string title,
        string valueText,
        float fraction)
    {
        using (var tf = typo.CreateFormat(title, CocoonTokens.FontSizes.RowTitle, 600))
            ds.DrawText(title, new Rect(tx, y + 18f, rightEdge - tx, 52f), CocoonTokens.Colors.NeutralInkPrimary, tf);

        fraction = Math.Clamp(fraction, 0f, 1f);
        var (mx, x0, x1, px) = SliderMetrics(tx, rightEdge);
        float cy = y + h - 44f;

        using (var vf = typo.CreateFormat(valueText, CocoonTokens.FontSizes.SliderValue, 500, vAlign: CanvasVerticalAlignment.Center))
            ds.DrawText(valueText, new Rect(tx, cy - 24f, MathF.Max(20f, mx - tx - 20f), 48f), CocoonTokens.Colors.NeutralInkSecondary, vf);

        ds.FillRoundedRectangle(mx - 16f, cy - 4f, 32f, 8f, 4f, 4f, CocoonTokens.Colors.NeutralInkPrimary);
        ds.FillRoundedRectangle(x0, cy - 5f, x1 - x0, 10f, 5f, 5f, CocoonTokens.Colors.SurfaceSliderTrack);
        if (fraction > 0f)
        {
            using var br = new CanvasLinearGradientBrush(ds.Device, CocoonTokens.Colors.BrandAccentStart, CocoonTokens.Colors.BrandAccentDeep)
            {
                StartPoint = new Vector2(x0, 0f),
                EndPoint = new Vector2(x1, 0f),
            };
            ds.FillRoundedRectangle(x0, cy - 5f, (x1 - x0) * fraction, 10f, 5f, 5f, br);
        }

        float kx = x0 + (x1 - x0) * fraction;
        CocoonCardPainter.DrawSoftShadow(ds, raster, kx - 24f, cy - 24f + 3f, 48f, 48f, 24f, 5f, 0.30f);
        using (var kb = new CanvasLinearGradientBrush(ds.Device, CocoonTokens.Colors.BrandKnobStart, CocoonTokens.Colors.BrandKnobEnd)
        {
            StartPoint = new Vector2(kx - 16f, cy - 24f),
            EndPoint = new Vector2(kx + 16f, cy + 24f),
        })
        {
            ds.FillCircle(kx, cy, 24f, kb);
        }

        ds.FillRoundedRectangle(px - 16f, cy - 4f, 32f, 8f, 4f, 4f, CocoonTokens.Colors.NeutralInkPrimary);
        ds.FillRoundedRectangle(px - 4f, cy - 16f, 8f, 32f, 4f, 4f, CocoonTokens.Colors.NeutralInkPrimary);
    }

    public static (float X, float Width) StepperBounds(CanvasDrawingSession ds, CocoonTypography typo, string valueText, float rightEdge)
    {
        float w = Math.Max(240f, typo.MeasureWidth(ds, valueText, CocoonTokens.FontSizes.SliderValue, 500) + 160f);
        return (rightEdge - w, w);
    }

    public static void DrawStepperPill(CanvasDrawingSession ds, CocoonTypography typo, float rightEdge, float cy, string valueText)
    {
        var (x, w) = StepperBounds(ds, typo, valueText, rightEdge);
        ds.FillRoundedRectangle(x, cy - 38f, w, 76f, 38f, 38f, CocoonTokens.Colors.SurfaceStepperPill);
        ds.FillRoundedRectangle(x + 30f, cy - 4f, 28f, 8f, 4f, 4f, CocoonTokens.Colors.NeutralInkPrimary);
        ds.FillRoundedRectangle(x + w - 58f, cy - 4f, 28f, 8f, 4f, 4f, CocoonTokens.Colors.NeutralInkPrimary);
        ds.FillRoundedRectangle(x + w - 48f, cy - 14f, 8f, 28f, 4f, 4f, CocoonTokens.Colors.NeutralInkPrimary);
        using var vf = typo.CreateFormat(valueText, CocoonTokens.FontSizes.SliderValue, 600, CanvasHorizontalAlignment.Center, CanvasVerticalAlignment.Center);
        ds.DrawText(valueText, new Rect(x + 70f, cy - 38f, w - 140f, 76f), CocoonTokens.Colors.NeutralInkPrimary, vf);
    }

    public static void DrawOrderPillExpanded(CanvasDrawingSession ds, float x, float y, bool canDown, bool canUp)
    {
        const float w = 258f, h = 84f;
        var dev = ds.Device;
        ds.FillRoundedRectangle(x, y, w, h, h / 2f, h / 2f, CocoonTokens.Colors.SurfaceToggleOff);
        float iw = (w - 18f) / 2f, ih = h - 14f;
        for (int s = 0; s < 2; s++)
        {
            bool on = s == 0 ? canDown : canUp;
            float bx = x + 6f + s * (iw + 6f), by = y + 7f;
            ds.FillRoundedRectangle(bx, by, iw, ih, ih / 2f, ih / 2f, Color.FromArgb(255, 0xCC, 0xCD, 0xD2));
            var ink = on ? CocoonTokens.Colors.NeutralInkPrimary : CocoonTokens.Colors.NeutralInkDisabled;
            float dx = bx + 34f, dy = by + ih / 2f;
            ds.FillRoundedRectangle(dx - 16f, dy - 6f, 32f, 12f, 3f, 3f, CocoonTokens.Colors.NeutralInkDisabled);
            ds.FillRoundedRectangle(dx - 6f, dy - 16f, 12f, 32f, 3f, 3f, CocoonTokens.Colors.NeutralInkDisabled);
            float side = s == 0 ? -1f : 1f;
            ds.FillRoundedRectangle(side < 0 ? dx - 16f : dx + 4f, dy - 6f, 12f, 12f, 3f, 3f, ink);
            using (var tp = new CanvasPathBuilder(dev))
            {
                float tx = dx + side * 10f;
                tp.BeginFigure(tx + side * 3f, dy); tp.AddLine(tx - side * 2f, dy - 4f); tp.AddLine(tx - side * 2f, dy + 4f);
                tp.EndFigure(CanvasFigureLoop.Closed);
                using var tri = CanvasGeometry.CreatePath(tp);
                ds.FillGeometry(tri, Color.FromArgb(255, 0xF2, 0xF2, 0xF4));
            }
            float ax = bx + iw - 34f, d = s == 0 ? 1f : -1f;
            using var pb = new CanvasPathBuilder(dev);
            pb.BeginFigure(ax - 13f, dy - 6f * d); pb.AddLine(ax, dy + 7f * d); pb.AddLine(ax + 13f, dy - 6f * d);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var g = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(g, ink, 7f, CocoonContext.RoundStroke);
        }
    }

    public static void DrawToastPill(
        CanvasDrawingSession ds,
        CocoonTypography typo,
        float raster,
        float x,
        float y,
        float w,
        float h,
        string text,
        Color? dotColor = null)
    {
        CocoonCardPainter.DrawSoftShadow(ds, raster, x, y + 6f, w, h, h / 2f, 12f, 0.25f);
        ds.FillRoundedRectangle(x, y, w, h, h / 2f, h / 2f, CocoonTokens.Colors.SurfaceOptionCard);
        ds.DrawRoundedRectangle(x + 0.5f, y + 0.5f, w - 1f, h - 1f, h / 2f, h / 2f, CocoonTokens.Colors.SurfaceDividerSubtle, 1f);
        ds.FillCircle(x + 40f, y + h / 2f, 11f, dotColor ?? CocoonTokens.Colors.BrandAccentInk);
        using var tf = typo.CreateFormat(text, CocoonTokens.FontSizes.Toast, 500, CanvasHorizontalAlignment.Left, CanvasVerticalAlignment.Center, ellipsis: true);
        ds.DrawText(text, new Rect(x + 70f, y, MathF.Max(20f, w - 104f), h), CocoonTokens.Colors.NeutralInkPrimary, tf);
    }
}
