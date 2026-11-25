using System.Numerics;
using CocoonUI.Core;
using CocoonUI.Tokens;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace CocoonUI.Primitives;

/// <summary>Renders Cocoon surface materials: 6-layer 3D cards, recessed groove dividers, sheet groups, and drawers.</summary>
public static class CocoonCardPainter
{
    public static void DrawSoftShadow(
        CanvasDrawingSession ds,
        float raster,
        float x,
        float y,
        float w,
        float h,
        float r,
        float blur,
        float alpha)
    {
        using var cl = new CanvasCommandList(ds.Device);
        using (var s = cl.CreateDrawingSession())
        {
            s.Transform = ds.Transform;
            s.FillRoundedRectangle(x, y, w, h, r, r, Colors.Black);
        }
        using var bl = new GaussianBlurEffect
        {
            Source = cl,
            BlurAmount = blur * raster,
            BorderMode = EffectBorderMode.Soft,
        };
        using var op = new OpacityEffect { Source = bl, Opacity = alpha };
        var prev = ds.Transform;
        ds.Transform = Matrix3x2.Identity;
        ds.DrawImage(op);
        ds.Transform = prev;
    }

    /// <summary>6-layer tactile 3D container with dual shadows, #FDFDFE -> #F0F1F4 fill, inner shade, highlight, and beveled stroke.</summary>
    public static void DrawCard3D(
        CanvasDrawingSession ds,
        float raster,
        float x,
        float y,
        float w,
        float h,
        IReadOnlyList<float>? grooveYs = null,
        float radius = CocoonTokens.Radii.Card)
    {
        var dev = ds.Device;
        DrawSoftShadow(ds, raster, x + 4f, y + 12f, w - 8f, h, radius, 14f, 0.20f);
        DrawSoftShadow(ds, raster, x, y + 2f, w, h, radius, 3f, 0.10f);

        using var card = CanvasGeometry.CreateRoundedRectangle(dev, x, y, w, h, radius, radius);
        using (var fill = new CanvasLinearGradientBrush(dev, CocoonTokens.Colors.SurfaceCard3dTop, CocoonTokens.Colors.SurfaceCard3dBottom)
        {
            StartPoint = new Vector2(0f, y),
            EndPoint = new Vector2(0f, y + h),
        })
        {
            ds.FillGeometry(card, fill);
        }

        using (ds.CreateLayer(1f, card))
        {
            using var shade = new CanvasLinearGradientBrush(dev, Color.FromArgb(0, 0, 0, 0), CocoonTokens.Colors.SurfaceCard3dInnerShade)
            {
                StartPoint = new Vector2(0f, y + h - 22f),
                EndPoint = new Vector2(0f, y + h),
            };
            ds.FillRectangle(x, y + h - 22f, w, 22f, shade);

            if (grooveYs != null)
            {
                foreach (float sy in grooveYs)
                {
                    ds.FillRectangle(x, sy, w, 2.5f, CocoonTokens.Colors.SurfaceDividerGroove);
                    ds.FillRectangle(x, sy + 2.5f, w, 1.5f, CocoonTokens.Colors.NeutralPureWhite);
                }
            }
            ds.DrawLine(x + radius * 0.6f, y + 1.5f, x + w - radius * 0.6f, y + 1.5f, CocoonTokens.Colors.NeutralPureWhite, 2f);
        }

        using var edge = new CanvasLinearGradientBrush(dev, CocoonTokens.Colors.SurfaceCard3dEdgeTop, CocoonTokens.Colors.SurfaceCard3dEdgeBottom)
        {
            StartPoint = new Vector2(0f, y),
            EndPoint = new Vector2(0f, y + h),
        };
        ds.DrawRoundedRectangle(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, radius, radius, edge, 1.5f);
    }

    /// <summary>Modal sheet grouped card (#F7F8FA) with subtle shadow and 4 px dividers.</summary>
    public static void DrawGroupCard(
        CanvasDrawingSession ds,
        float raster,
        float x,
        float y,
        float w,
        float h,
        IReadOnlyList<float>? dividerYs = null,
        float sepH = CocoonTokens.Spacing.DividerGrooveTotal,
        float radius = CocoonTokens.Radii.Card)
    {
        var dev = ds.Device;
        DrawSoftShadow(ds, raster, x, y + 5f, w, h, radius, 6f, 0.16f);

        using var card = CanvasGeometry.CreateRoundedRectangle(dev, x, y, w, h, radius, radius);
        ds.FillGeometry(card, CocoonTokens.Colors.SurfaceGroupCard);

        using (ds.CreateLayer(1f, card))
        {
            using var shade = new CanvasLinearGradientBrush(dev, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(14, 0, 0, 0))
            {
                StartPoint = new Vector2(0f, y + h - 26f),
                EndPoint = new Vector2(0f, y + h),
            };
            ds.FillRectangle(x, y + h - 26f, w, 26f, shade);

            if (dividerYs != null)
            {
                foreach (float sy in dividerYs)
                    ds.FillRectangle(x, sy, w, sepH, CocoonTokens.Colors.SurfaceDividerLine);
            }
            ds.DrawLine(x + radius, y + 1f, x + w - radius, y + 1f, CocoonTokens.Colors.NeutralPureWhite, 1.5f);
        }

        using var edge = new CanvasLinearGradientBrush(dev, CocoonTokens.Colors.SurfaceGroupEdgeTop, CocoonTokens.Colors.SurfaceGroupEdgeBottom)
        {
            StartPoint = new Vector2(0f, y),
            EndPoint = new Vector2(0f, y + h),
        };
        ds.DrawRoundedRectangle(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, radius, radius, edge, 1.5f);
    }

    public static void DrawTileWell(
        CanvasDrawingSession ds,
        float x,
        float y,
        float w,
        float h,
        float radius = CocoonTokens.Radii.TileWell) =>
        ds.FillRoundedRectangle(x, y, w, h, radius, radius, CocoonTokens.Colors.SurfaceTileWell);

    public static CompositionLinearGradientBrush CreatePageBackgroundBrush(CocoonContext ctx)
    {
        var g = ctx.C.CreateLinearGradientBrush();
        g.StartPoint = new Vector2(0f, 0f);
        g.EndPoint = new Vector2(0f, 1f);
        g.ColorStops.Add(ctx.C.CreateColorGradientStop(0f, CocoonTokens.Colors.SurfacePageTop));
        g.ColorStops.Add(ctx.C.CreateColorGradientStop(1f, CocoonTokens.Colors.SurfacePageBottom));
        return g;
    }

    public static CompositionLinearGradientBrush CreateDrawerBrush(CocoonContext ctx, float drawerTop = 23f, float drawerHeight = 1139f)
    {
        var g = ctx.C.CreateLinearGradientBrush();
        g.StartPoint = new Vector2(0f, 0f);
        g.EndPoint = new Vector2(0f, 1f);
        (float Y, Color Col)[] stops =
        [
            (0f, CocoonTokens.Colors.SurfaceDrawerTop),
            (60f - drawerTop, CocoonTokens.Colors.SurfaceDrawerTop),
            (400f - drawerTop, CocoonTokens.Colors.SurfaceDrawerMid),
            (720f - drawerTop, CocoonTokens.Colors.SurfaceDrawerMid),
            (1120f - drawerTop, CocoonTokens.Colors.SurfaceDrawerLow),
            (drawerHeight - 10f, CocoonTokens.Colors.SurfaceDrawerLow),
            (drawerHeight, CocoonTokens.Colors.SurfaceDrawerBottomEdge),
        ];
        foreach (var (y, col) in stops)
            g.ColorStops.Add(ctx.C.CreateColorGradientStop(Math.Clamp(y / MathF.Max(1f, drawerHeight), 0f, 1f), col));
        return g;
    }
}
