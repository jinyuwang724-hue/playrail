// 方向导航（照 src/engine/nav.ts）：从当前卡往某个方向走到哪一张。候选必须整张在前进方向前方；
// 分数 = 前进距离 + （横向没有重叠时）2×错开距离 + 一个步距的惩罚，同分取横向偏移小的。
// 所以十字键会沿一行走，不会斜着抄近路。
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Engine;

public enum Direction { Up, Down, Left, Right }

public static class Nav
{
    private static (double a, double c, double asz, double csz) Axis(Rect r, Direction d) =>
        d is Direction.Left or Direction.Right ? (r.X, r.Y, r.Width, r.Height) : (r.Y, r.X, r.Height, r.Width);

    public static Slot? PickAdjacent(Rect from, List<(Slot value, Rect rect)> candidates, Direction dir)
    {
        int sign = dir is Direction.Right or Direction.Down ? 1 : -1;
        var (fa, fc, fas, fcs) = Axis(from, dir);
        Slot? best = null;
        double bestScore = double.PositiveInfinity, bestCross = double.PositiveInfinity;
        foreach (var (value, rect) in candidates)
        {
            var (ra, rc, ras, rcs) = Axis(rect, dir);
            double step = sign == 1 ? ra - (fa + fas) : fa - (ra + ras);
            if (step < 0) continue;
            double overlap = Math.Min(fc + fcs, rc + rcs) - Math.Max(fc, rc);
            double score = step + (overlap > 0 ? 0 : -overlap * 2 + Pitch);
            double cross = Math.Abs(rc + rcs / 2 - (fc + fcs / 2));
            if (score < bestScore || (score == bestScore && cross < bestCross))
            {
                best = value; bestScore = score; bestCross = cross;
            }
        }
        return best;
    }

    /// <summary>在周围块里找下一张（有内容的）卡；一圈找不到再向外扩，最多三圈。`drawn` 给出让位后的实际矩形。</summary>
    public static Slot? FindAdjacentSlot(Slot from, Direction dir, Func<Slot, bool> hasContent, Func<Slot, Rect> drawn)
    {
        var source = drawn(from);
        var candidates = new List<(Slot, Rect)>();
        for (int radius = 1; radius <= 3; radius++)
        {
            candidates.Clear();
            for (int r = from.Row - radius; r <= from.Row + radius; r++)
                for (int c = from.Column - radius; c <= from.Column + radius; c++)
                    foreach (var s in BlockSlots(c, r))
                    {
                        if (s.Key == from.Key || !hasContent(s)) continue;
                        candidates.Add((s, drawn(s)));
                    }
            var next = PickAdjacent(source, candidates, dir);
            if (next != null) return next;
        }
        return null;
    }

    public static Slot? NearestTo(IEnumerable<Slot> items, double x, double y)
    {
        Slot? best = null;
        double d = double.PositiveInfinity;
        foreach (var it in items)
        {
            double dx = it.Cx - x, dy = it.Cy - y;
            double v = dx * dx + dy * dy;
            if (v < d) { d = v; best = it; }
        }
        return best;
    }
}
