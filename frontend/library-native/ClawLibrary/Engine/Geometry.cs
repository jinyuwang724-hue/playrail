// 墙面几何：格、块、slot。世界坐标的单位是「世界 px」，相机再整体缩放到屏幕（照 src/engine/geometry.ts）。
// 一个块 = 12×8 格、正好 12 张卡；块的样式（4 套模板 × 4 种镜像）只由块坐标决定，不存状态。
namespace ClawLibrary.Engine;

public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Cx => X + Width / 2;
    public double Cy => Y + Height / 2;
    public Bounds Bounds => new(X, X + Width, Y, Y + Height);
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

public readonly record struct Bounds(double Left, double Right, double Top, double Bottom)
{
    public bool Overlaps(Bounds b) => Left < b.Right && Right > b.Left && Top < b.Bottom && Bottom > b.Top;
    public Bounds Union(Bounds b) => new(Math.Min(Left, b.Left), Math.Max(Right, b.Right), Math.Min(Top, b.Top), Math.Max(Bottom, b.Bottom));
}

/// <summary>`列,行,序号`：跨层、跨过滤都不变的身份，内容是「翻」到它上面的。</summary>
public readonly record struct SlotKey(int Column, int Row, int Index)
{
    public override string ToString() => $"{Column},{Row},{Index}";
}

public sealed class Slot
{
    public SlotKey Key { get; }
    public int Column => Key.Column;
    public int Row => Key.Row;
    public int Index => Key.Index;
    public int Cols { get; }
    public int Rows { get; }
    public Rect Rect { get; }
    public double X => Rect.X;
    public double Y => Rect.Y;
    public double Width => Rect.Width;
    public double Height => Rect.Height;
    public double Cx => Rect.Cx;
    public double Cy => Rect.Cy;

    public Slot(SlotKey key, int cols, int rows, Rect rect) { Key = key; Cols = cols; Rows = rows; Rect = rect; }
}

public static class Geometry
{
    public const double Cell = 128;
    public const double Gap = 8;
    public const double Pitch = Cell + Gap;
    public const int BlockCols = 12;
    public const int BlockRows = 8;
    public const int SlotsPerBlock = 12;
    public const double BlockW = BlockCols * Pitch;
    public const double BlockH = BlockRows * Pitch;

    private static (int x, int y, int c, int r) Mirror(int mirror, int x, int y, int c, int r) => mirror switch
    {
        1 => (BlockCols - x - c, y, c, r),
        2 => (x, BlockRows - y - r, c, r),
        3 => (BlockCols - x - c, BlockRows - y - r, c, r),
        _ => (x, y, c, r),
    };

    /// <summary>块坐标的整数哈希（只用来挑镜像，让整面墙看不出周期）。与 TS 版逐位相同。</summary>
    private static uint HashBlock(int column, int row)
    {
        int h = unchecked(column * 0x27d4eb2d + row * 0x165667b1);
        h = unchecked((h ^ (int)((uint)h >> 16)) * 0x7feb352d);
        h = unchecked((h ^ (int)((uint)h >> 15)) * (int)0x846ca68b);
        return (uint)(h ^ (int)((uint)h >> 16));
    }

    private static int Mod(int v, int n) => ((v % n) + n) % n;

    /// <summary>模板按 (列 + 2×行) 轮换，相邻块永远不同；镜像来自哈希。</summary>
    public static (int Template, int Mirror) BlockOrientation(int column, int row) =>
        (Mod(column + row * 2, 4), (int)(HashBlock(column, row) % 4));

    private static Slot ToSlot(int column, int row, int index, int gx, int gy, int cols, int rows)
    {
        double x = column * BlockW + gx * Pitch;
        double y = row * BlockH + gy * Pitch;
        double width = cols * Pitch - Gap;
        double height = rows * Pitch - Gap;
        return new Slot(new SlotKey(column, row, index), cols, rows, new Rect(x, y, width, height));
    }

    private static readonly Dictionary<(int, int), Slot[]> BlockCache = new();

    public static Slot[] BlockSlots(int column, int row)
    {
        if (BlockCache.TryGetValue((column, row), out var slots)) return slots;
        var (template, mirror) = BlockOrientation(column, row);
        slots = new Slot[SlotsPerBlock];
        for (int i = 0; i < SlotsPerBlock; i++)
        {
            var (x, y, c, r) = Mirror(mirror, BlockData.Templates[template, i, 0], BlockData.Templates[template, i, 1], BlockData.Templates[template, i, 2], BlockData.Templates[template, i, 3]);
            slots[i] = ToSlot(column, row, i, x, y, c, r);
        }
        if (BlockCache.Count > 4096) BlockCache.Clear();
        BlockCache[(column, row)] = slots;
        return slots;
    }

    public static Slot? SlotByKey(SlotKey key) =>
        key.Index < 0 || key.Index >= SlotsPerBlock ? null : BlockSlots(key.Column, key.Row)[key.Index];

    /// <summary>`expanded` 展开成 6×6 时本块 12 张卡的位置（键不变，只有矩形变）。块外一张不动。</summary>
    public static Slot[] FocusedBlockSlots(int column, int row, int expanded)
    {
        var (template, mirror) = BlockOrientation(column, row);
        var slots = new Slot[SlotsPerBlock];
        for (int i = 0; i < SlotsPerBlock; i++)
        {
            var (x, y, c, r) = Mirror(mirror, BlockData.Reflows[template, expanded, i, 0], BlockData.Reflows[template, expanded, i, 1], BlockData.Reflows[template, expanded, i, 2], BlockData.Reflows[template, expanded, i, 3]);
            slots[i] = ToSlot(column, row, i, x, y, c, r);
        }
        return slots;
    }

    /// <summary>与世界矩形相交的所有 slot，按块、按序号。</summary>
    public static List<Slot> SlotsIn(Bounds b, int limit = 400, List<Slot>? into = null)
    {
        var output = into ?? new List<Slot>();
        output.Clear();
        int c0 = (int)Math.Floor(b.Left / BlockW), c1 = (int)Math.Floor(b.Right / BlockW);
        int r0 = (int)Math.Floor(b.Top / BlockH), r1 = (int)Math.Floor(b.Bottom / BlockH);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                foreach (var s in BlockSlots(c, r))
                {
                    if (!s.Rect.Bounds.Overlaps(b)) continue;
                    output.Add(s);
                    if (output.Count >= limit) return output;
                }
        return output;
    }

    /// <summary>相机缩放：按视口宽（CSS px / DIP）分三档。</summary>
    public static double ScaleFor(double viewportWidth) => viewportWidth < 640 ? 0.52 : viewportWidth < 1100 ? 0.64 : 0.76;
}
