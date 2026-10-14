// 换内容时的动画计划（纯计算，动画由舞台做；照 src/engine/plans.ts）。
// - 翻牌：只翻内容变了的 slot，从起点向外按距离错开；视口外的、超过上限的直接换。
// - 整墙波次：先按「离视口左上角越远越先走」抬起淡出，全部走完后按「越近越先到」落回。
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Engine;

public readonly record struct Change(Slot Slot, string? From, string? To);

public abstract record FlipOrigin
{
    public sealed record Point(double X, double Y) : FlipOrigin;
    public sealed record Line(double X) : FlipOrigin;
}

public readonly record struct FlipStep(SlotKey Key, double Delay, int Direction);
public sealed record FlipPlan(List<FlipStep> Steps, List<SlotKey> Instant, double DurationMs);
public readonly record struct WaveStep(SlotKey Key, double OutDelay, double InDelay);
public sealed record WavePlan(List<WaveStep> Steps, List<SlotKey> Instant, double DurationMs);

public static class Plans
{
    public const int MaxAnimated = 400;

    public static FlipPlan PlanFlip(IReadOnlyList<Change> changes, FlipOrigin origin, Bounds visible)
    {
        var near = new List<(Slot slot, double d)>();
        var instant = new List<SlotKey>();
        double ox = origin switch { FlipOrigin.Point p => p.X, FlipOrigin.Line l => l.X, _ => 0 };
        foreach (var ch in changes)
        {
            if (ch.From == ch.To) continue;
            if (!ch.Slot.Rect.Bounds.Overlaps(visible)) { instant.Add(ch.Slot.Key); continue; }
            double d = origin is FlipOrigin.Point pt
                ? Math.Sqrt((ch.Slot.Cx - pt.X) * (ch.Slot.Cx - pt.X) + (ch.Slot.Cy - pt.Y) * (ch.Slot.Cy - pt.Y))
                : Math.Abs(ch.Slot.Cx - ox);
            near.Add((ch.Slot, d));
        }
        near.Sort((a, b) => a.d.CompareTo(b.d));
        double last = -1;
        var steps = new List<FlipStep>();
        for (int i = 0; i < near.Count; i++)
        {
            var (slot, d) = near[i];
            if (i >= MaxAnimated) { instant.Add(slot.Key); continue; }
            double delay = Math.Min(Motion.Flip.MaxDelay, d / Pitch * Motion.Flip.StaggerPerCell);
            last = Math.Max(last, delay);
            steps.Add(new FlipStep(slot.Key, delay, slot.Cx < ox ? -1 : 1));
        }
        return new FlipPlan(steps, instant, steps.Count > 0 ? last + Motion.Flip.OutMs + Motion.Flip.InMs : 0);
    }

    public static double WaveStagger(double x, double y, double cornerLeft, double cornerTop)
    {
        double cells = (Math.Max(0, x - cornerLeft) + Math.Max(0, y - cornerTop)) / Pitch;
        return Math.Min(Motion.Wave.MaxDelay, cells * Motion.Wave.StaggerPerCell);
    }

    public static WavePlan PlanWave(IReadOnlyList<Change> changes, Bounds viewport, Bounds visible)
    {
        double exitTotal = Motion.Wave.MaxDelay + Motion.Wave.OutMs;
        var list = new List<(Slot slot, double s)>();
        var instant = new List<SlotKey>();
        foreach (var ch in changes)
        {
            if (ch.From == ch.To) continue;
            if (!ch.Slot.Rect.Bounds.Overlaps(visible)) { instant.Add(ch.Slot.Key); continue; }
            list.Add((ch.Slot, WaveStagger(ch.Slot.X, ch.Slot.Y, viewport.Left, viewport.Top)));
        }
        // 稳定排序（与 JS Array.sort 一致）
        list = list.Select((e, i) => (e, i)).OrderBy(t => t.e.s).ThenBy(t => t.i).Select(t => t.e).ToList();
        foreach (var e in list.Skip(MaxAnimated)) instant.Add(e.slot.Key);
        var steps = list.Take(MaxAnimated).Select(e => new WaveStep(
            e.slot.Key,
            Math.Round(Motion.Wave.MaxDelay - e.s, MidpointRounding.AwayFromZero),
            Math.Round(exitTotal + e.s, MidpointRounding.AwayFromZero))).ToList();
        double duration = steps.Aggregate(0.0, (m, st) => Math.Max(m, st.InDelay + Motion.Wave.InMs));
        return new WavePlan(steps, instant, duration);
    }
}
