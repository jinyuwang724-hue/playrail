// 视口 ↔ 世界的换算，缝（信息条 / 书脊）的几何，以及「最小移动把一张卡露出来」（照 src/engine/view.ts）。
// 相机的真源是「视图中心」：缝基准线（屏幕上 lineX、半高处）下面的那个世界点。
// 缝只开在块边界上（没有卡跨过两块之间的缝隙），墙按缝分成左右两半，各让出半个开口 + 半个 gap。
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Engine;

/// <summary>视口（DIP，等于网页版的 CSS px）。</summary>
public readonly record struct View(double Width, double Height, double Scale, double LineX);
public record struct Center(double X, double Y);

public static class ViewMath
{
    /// <summary>开口离视口边缘保留的屏幕 px。</summary>
    public const double SeamEdge = 14;
    /// <summary>选块边界时「离基准线的距离」相对「相机要让的距离」的权重。</summary>
    private const double SeamLineWeight = 0.15;

    public const double SeamHome = 120, SeamFull = 300, SeamSpine = 64;

    /// <summary>块 `column` 左边那道缝隙的中线（世界 x）。</summary>
    public static double BoundaryX(int column) => column * BlockW - Gap / 2;
    public static int BoundaryColumn(double x) => (int)Math.Floor((x + Gap / 2) / BlockW + 0.5); // 与 JS Math.round 相同

    /// <summary>两半各自的屏幕偏移：半个开口 + 半个 gap（开口很窄时 gap 也跟着收）。</summary>
    public static (double Left, double Right) SplitOffsets(double width, double scale)
    {
        double pad = Gap / 2 * scale * Math.Min(1, width / 16);
        return (-width / 2 - pad, width / 2 + pad);
    }

    /// <summary>选缝开在哪：相机附近的三条块边界，各自让相机挪最少的距离把整个开口放进屏幕，分数最低的胜出。</summary>
    public static (double AnchorX, double CenterX) PlanSeam(Center center, double openWidth, View view, double? prefer = null)
    {
        double need = openWidth / 2 + SeamEdge;
        int b = BoundaryColumn(center.X);
        double[] candidates = prefer is { } p ? new[] { p } : new[] { BoundaryX(b - 1), BoundaryX(b), BoundaryX(b + 1) };
        (double, double) best = (candidates[0], center.X);
        double bestScore = double.PositiveInfinity;
        foreach (double x in candidates)
        {
            double screenX = view.LineX + (x - center.X) * view.Scale;
            double fitX = Math.Max(need, Math.Min(view.Width - need, screenX));
            double nextCenterX = x - (fitX - view.LineX) / view.Scale;
            double score = Math.Abs(nextCenterX - center.X) * view.Scale + Math.Abs(fitX - view.LineX) * SeamLineWeight;
            if (score < bestScore) { bestScore = score; best = (x, nextCenterX); }
        }
        return best;
    }

    /// <summary>缝跟着世界走，靠近视口边时开口收窄，出屏就合上（collapsed = 本该开着却被挤没了，显示边缘标签）。</summary>
    public static (double ScreenX, double Width, bool Collapsed, bool LeftSide) SeamGeometry(double anchorX, Center center, double openWidth, View view)
    {
        double screenX = view.LineX + (anchorX - center.X) * view.Scale;
        double room = Math.Min(screenX, view.Width - screenX) - SeamEdge;
        double width = Math.Max(0, Math.Min(openWidth, room * 2));
        return (screenX, width, openWidth > 1 && width < 2, screenX < view.LineX);
    }

    /// <summary>此刻看得见的世界范围（缝张开时左右各放宽一个开口）。</summary>
    public static Bounds VisibleWorld(Center center, View view, double seamWidth = 0, double overscan = 0) => new(
        center.X - (view.LineX + seamWidth) / view.Scale - overscan,
        center.X + (view.Width - view.LineX + seamWidth) / view.Scale + overscan,
        center.Y - view.Height / 2 / view.Scale - overscan,
        center.Y + view.Height / 2 / view.Scale + overscan);

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    /// <summary>把一个世界矩形完整露出来的最小相机移动（屏幕留 pad）；缝能同时留在屏上时优先留着，两者冲突时矩形优先。</summary>
    public static Center RevealCenter(Rect rect, Center center, View view, double? anchorX, double seamWidth, double pad = 24, double? bottomInset = null)
    {
        double bi = bottomInset ?? pad;
        double s = view.Scale;
        bool hasSeam = anchorX != null && seamWidth > 1;
        var offsets = hasSeam ? SplitOffsets(seamWidth, s) : (0, 0);
        double offset = anchorX != null && rect.X + rect.Width / 2 < anchorX ? offsets.Item1 : offsets.Item2;
        double fitLow = rect.X + rect.Width - (view.Width - pad - view.LineX - offset) / s;
        double fitHigh = rect.X - (pad - view.LineX - offset) / s;
        double x;
        double lo = Math.Min(fitLow, fitHigh), hi = Math.Max(fitLow, fitHigh);
        if (hasSeam)
        {
            double need = seamWidth / 2 + SeamEdge;
            double seamLow = anchorX!.Value - (view.Width - need - view.LineX) / s;
            double seamHigh = anchorX.Value + (view.LineX - need) / s;
            double a = Math.Max(fitLow, seamLow), b = Math.Min(fitHigh, seamHigh);
            x = a <= b ? Clamp(center.X, a, b) : Clamp(center.X, lo, hi);
        }
        else x = Clamp(center.X, lo, hi);
        double yLow = rect.Y + rect.Height - (view.Height / 2 - bi) / s;
        double yHigh = rect.Y - (pad - view.Height / 2) / s;
        double y = yLow <= yHigh ? Clamp(center.Y, yLow, yHigh) : rect.Y + rect.Height / 2;
        return new Center(x, y);
    }
}
