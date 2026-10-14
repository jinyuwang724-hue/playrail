// 哪个 slot 显示哪一项（照 src/engine/content.ts）。
// - 无限墙：内容按一个横向周期（若干整块）重复铺满整个平面；偏移量让「起点」那一格显示第 0 项。
// - 有限墙（过滤后）：每个 slot 打分，第 0、1、2… 名按分数排，好的挨着起点、向外螺旋。
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Engine;

public readonly record struct Period(int Across, int Down);

public abstract record WallLayout(int Count);
public sealed record InfiniteLayout(int Count, Period Period, int Offset) : WallLayout(Count);
public sealed record FiniteLayout(int Count, Dictionary<SlotKey, int> Ranks) : WallLayout(Count);

public static class Content
{
    private static int Mod(int v, int n) => ((v % n) + n) % n;

    /// <summary>周期的横纵比：低于 1 时竖向重复得比横向快，看上去像条纹。</summary>
    private const double FieldAspect = 2.2;

    public static Period PeriodFor(int itemCount)
    {
        int blocks = Math.Max(1, (int)Math.Ceiling(itemCount / (double)SlotsPerBlock));
        int across = blocks <= 1 ? 1 : Math.Max(1, (int)Math.Round(Math.Sqrt(FieldAspect * blocks * BlockRows / BlockCols), MidpointRounding.AwayFromZero));
        return new Period(across, (int)Math.Ceiling(blocks / (double)across));
    }

    /// <summary>一个 slot 在周期序列里的位置（加偏移之前）。</summary>
    private static int WrapIndex(SlotKey slot, int itemCount, Period period)
    {
        int block = Mod(slot.Row, period.Down) * period.Across + Mod(slot.Column, period.Across);
        return (block * SlotsPerBlock + slot.Index) % itemCount;
    }

    public static InfiniteLayout Infinite(int count, Slot? start)
    {
        var period = PeriodFor(count);
        int offset = start != null && count > 0 ? Mod(-WrapIndex(start.Key, count, period), count) : 0;
        return new InfiniteLayout(count, period, offset);
    }

    /// <summary>第几项（没有内容时为 -1）。</summary>
    public static int ItemAt(WallLayout layout, SlotKey slot)
    {
        if (layout.Count <= 0) return -1;
        switch (layout)
        {
            case InfiniteLayout inf: return Mod(WrapIndex(slot, inf.Count, inf.Period) + inf.Offset, inf.Count);
            case FiniteLayout fin: return fin.Ranks.TryGetValue(slot, out int rank) && rank < fin.Count ? rank : -1;
            default: return -1;
        }
    }

    // ---------- 有限墙的排名 ----------

    /// <summary>竖向距离打九折（墙是横的）；大卡按 √面积 往前拉，头几名落在大海报上。</summary>
    public const double RankVertical = 0.9;
    public const double RankArea = 0.35;

    public static double RankScore(Slot s, double ox, double oy) =>
        Math.Abs(s.Cx - ox) + Math.Abs(s.Cy - oy) * RankVertical - Math.Sqrt(s.Cols * s.Rows) * Pitch * RankArea;

    /// <summary>候选 slot 排序：起点（若给）排第 0，其余按分数，同分按世界位置，结果稳定。</summary>
    public static List<Slot> RankSlots(IReadOnlyList<Slot> candidates, double ox, double oy, SlotKey? startKey = null)
    {
        var order = candidates
            .Select(s => (s, v: RankScore(s, ox, oy)))
            .OrderBy(e => e.v).ThenBy(e => e.s.Row).ThenBy(e => e.s.Column).ThenBy(e => e.s.Index)
            .Select(e => e.s).ToList();
        if (startKey is { } k)
        {
            int i = order.FindIndex(s => s.Key == k);
            if (i > 0) { var s = order[i]; order.RemoveAt(i); order.Insert(0, s); }
        }
        return order;
    }

    public static FiniteLayout Finite(IReadOnlyList<Slot> candidates, int count, double ox, double oy, SlotKey? startKey = null)
    {
        var order = RankSlots(candidates, ox, oy, startKey).Take(count).ToList();
        var ranks = new Dictionary<SlotKey, int>();
        for (int i = 0; i < order.Count; i++) ranks[order[i].Key] = i;
        return new FiniteLayout(count, ranks);
    }
}
