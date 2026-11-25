using CocoonUI.Tokens;

namespace CocoonUI.Core;

public enum BracketShapeKind
{
    Circle,
    Capsule,
    RoundedRect,
}

public readonly record struct BracketMetrics(
    BracketShapeKind Kind,
    float Outset,
    float BoxW,
    float BoxH,
    float Radius,
    float Gap,
    float LegX,
    float LegY,
    float StrokeWidth);

/// <summary>Pure mathematical geometry and perimeter flow sampling for Cocoon's concentric bracket cursor.</summary>
public static class CocoonBracketMath
{
    public const float CompactOutset = 5.5f;

    public static (float Outset, float Radius) ConcentricCompact(float targetW, float targetH, float targetRadius)
    {
        float tr = Math.Clamp(targetRadius, 0f, MathF.Min(targetW, targetH) / 2f);
        return (CompactOutset, tr + CompactOutset);
    }

    public static (float Outset, float Radius) ConcentricDynamic(float targetW, float targetH, float targetRadius)
        => CocoonTokens.Focus.Concentric(targetW, targetH, targetRadius);

    public static BracketMetrics ComputeMetrics(float boxW, float boxH, float r, float dp = CocoonTokens.Dp)
    {
        float mn = MathF.Min(boxW, boxH);
        float sw = 8f * dp * CocoonTokens.Focus.ThicknessScale;
        if (MathF.Abs(boxW - boxH) <= 2f && r >= mn * 0.45f)
        {
            float rc = mn / 2f;
            float gapRad = Math.Clamp(26f / MathF.Max(1f, rc), 0.30f, 0.62f);
            return new BracketMetrics(BracketShapeKind.Circle, CompactOutset, boxW, boxH, rc, gapRad, 0f, 0f, sw);
        }

        if (boxW > boxH + 2f && (r >= boxH * 0.44f || boxH - 2f * r < 24f))
        {
            float rp = boxH / 2f;
            float gapC = Math.Clamp(20f, 18f, boxH * 0.30f);
            float legH = MathF.Min(Math.Clamp(0.06f * boxH, 4f, 10f), MathF.Max(0f, (boxW - boxH - 24f) / 2f));
            return new BracketMetrics(BracketShapeKind.Capsule, CompactOutset, boxW, boxH, rp, gapC, legH, 0f, sw);
        }

        float innerMn = MathF.Max(1f, mn - CompactOutset * 2f);
        float sf = Math.Clamp(mn / (innerMn * 2.35f), CocoonTokens.Focus.ScaleFactorMin, CocoonTokens.Focus.ScaleFactorMax);
        float gapMin = MathF.Max(26f, MathF.Min(0.28f * mn, 38f));
        float cr = MathF.Min(r, MathF.Max(0f, (mn - gapMin) / 2f));
        float legBase = 14f * dp * sf;
        float legX = MathF.Max(0f, MathF.Min(legBase, (boxW - 2f * cr - gapMin) / 2f));
        float legY = MathF.Max(0f, MathF.Min(legBase, (boxH - 2f * cr - gapMin) / 2f));
        return new BracketMetrics(BracketShapeKind.RoundedRect, CompactOutset, boxW, boxH, cr, gapMin, legX, legY, sw);
    }

    public static (byte R, byte G, byte B) SamplePerimeterColor(float s, float phase)
    {
        float t = 0.5f + 0.5f * MathF.Cos(2f * MathF.PI * (s - phase));
        var c1 = CocoonTokens.Colors.BrandAccentStart;
        var c2 = CocoonTokens.Colors.BrandAccentEnd;
        return (
            (byte)Math.Round(c1.R + (c2.R - c1.R) * t),
            (byte)Math.Round(c1.G + (c2.G - c1.G) * t),
            (byte)Math.Round(c1.B + (c2.B - c1.B) * t)
        );
    }
}
