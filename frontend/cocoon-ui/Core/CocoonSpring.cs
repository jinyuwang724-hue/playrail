using System.Numerics;
using CocoonUI.Tokens;
using Microsoft.UI.Composition;

namespace CocoonUI.Core;

/// <summary>Critically damped spring animations, instant jump override, and directional rubber-band stretch.</summary>
public static class CocoonSpring
{
    public static SpringVector3NaturalMotionAnimation CreateVector3(
        CocoonContext ctx,
        Vector3 to,
        float stiffness = CocoonTokens.Motion.SpringFocusStiffness,
        float dampingRatio = CocoonTokens.Motion.SpringFocusDampingRatio,
        double delayMs = 0)
    {
        var a = ctx.C.CreateSpringVector3Animation();
        a.DampingRatio = dampingRatio;
        a.Period = CocoonTokens.Motion.CompositionSpringPeriod(stiffness);
        a.FinalValue = to;
        if (delayMs > 0) a.DelayTime = CocoonContext.Ms(delayMs);
        return a;
    }

    public static SpringScalarNaturalMotionAnimation CreateScalar(
        CocoonContext ctx,
        float to,
        float stiffness = CocoonTokens.Motion.SpringFocusStiffness,
        float dampingRatio = CocoonTokens.Motion.SpringFocusDampingRatio,
        double delayMs = 0)
    {
        var a = ctx.C.CreateSpringScalarAnimation();
        a.DampingRatio = dampingRatio;
        a.Period = CocoonTokens.Motion.CompositionSpringPeriod(stiffness);
        a.FinalValue = to;
        if (delayMs > 0) a.DelayTime = CocoonContext.Ms(delayMs);
        return a;
    }

    /// <summary>Stops active offset spring and forces a 1 ms keyframe so stale spring ticks never snap back.</summary>
    public static void Jump(CocoonContext ctx, Visual visual, Vector3 to)
    {
        visual.StopAnimation("Offset");
        visual.Offset = to;
        visual.StartAnimation("Offset", ctx.Vector3Anim(to, CocoonTokens.Motion.DurationInstantMs, CocoonBezier.EaseOut));
    }

    /// <summary>230 ms directional rubber-band stretch along primary travel axis.</summary>
    public static void Stretch(CocoonContext ctx, Visual visual, float dx, float dy, float width, float height)
    {
        if (ctx.ReducedMotion) return;
        bool horiz = MathF.Abs(dx) > MathF.Abs(dy);
        float d = horiz ? MathF.Abs(dx) : MathF.Abs(dy);
        float size = horiz ? width : height;
        if (d < 1f || size <= 0f) return;

        float maxExtra = CocoonTokens.Motion.RubberbandMaxScale - 1f;
        float k = 1f + MathF.Min(maxExtra, d / size * CocoonTokens.Motion.RubberbandDistanceRatio);
        var peak = horiz ? new Vector3(k, 1f, 1f) : new Vector3(1f, k, 1f);

        var a = ctx.C.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0f, Vector3.One, ctx.Linear);
        a.InsertKeyFrame(CocoonTokens.Motion.RubberbandPeakProgress, peak, ctx.Ease(CocoonBezier.StretchPeak));
        a.InsertKeyFrame(1f, Vector3.One, ctx.Ease(CocoonBezier.SlideOut));
        a.Duration = CocoonContext.Ms(CocoonTokens.Motion.DurationRubberbandMs);
        visual.StartAnimation("Scale", a);
    }
}
