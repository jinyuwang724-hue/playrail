using System.Windows;
using System.Windows.Media;

namespace ClawSleepAnimation;

// The live overlay and exported preview share a deterministic vector renderer.
// The final suspend frame is black, and nothing renders during suspension.
internal sealed class TransitionScene : FrameworkElement
{
    public const int SuspendMilliseconds = 1150;
    public const int WakeMilliseconds = 950;
    public bool Waking { get; private set; }
    public double Progress { get; private set; }
    private bool loading;
    private double loadingSeconds, loadingExit;
    private static readonly Brush White = Solid(240, 243, 247);
    private static readonly Brush Silver = Solid(163, 173, 186);
    private static readonly Brush Soft = Solid(94, 111, 132);
    private static readonly RadialGradientBrush Halo = Gradient();
    private static readonly LinearGradientBrush Front = PageGradient(true);
    private static readonly LinearGradientBrush Back = PageGradient(false);

    internal void SetFrame(bool waking, double progress)
    {
        loading = false; Waking = waking; Progress = Math.Clamp(progress, 0, 1); InvalidateVisual();
    }
    internal void SetLoading(double seconds, double exit = 0)
    {
        loading = true; loadingSeconds = seconds; loadingExit = Math.Clamp(exit, 0, 1); InvalidateVisual();
    }
    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush;
    }
    private static RadialGradientBrush Gradient()
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new(Color.FromArgb(100, 142, 168, 201), 0));
        brush.GradientStops.Add(new(Color.FromArgb(38, 91, 112, 142), .42));
        brush.GradientStops.Add(new(Color.FromArgb(0, 40, 50, 70), 1));
        brush.Freeze(); return brush;
    }
    private static LinearGradientBrush PageGradient(bool front)
    {
        var brush = new LinearGradientBrush(front ? Color.FromRgb(248, 249, 251) : Color.FromRgb(184, 195, 209),
            front ? Color.FromRgb(189, 201, 216) : Color.FromRgb(121, 137, 158), new Point(0, 0), new Point(1, 1));
        brush.Freeze(); return brush;
    }
    private static double Unit(double v) => Math.Clamp(v, 0, 1);
    private static double Range(double a, double b, double v) => Unit((v - a) / (b - a));
    private static double Smooth(double a, double b, double v)
    {
        double x = Range(a, b, v); return x * x * (3 - 2 * x);
    }
    private static double Out(double a, double b, double v) => 1 - Math.Pow(1 - Range(a, b, v), 3);
    private static Pen Stroke(Brush brush, double width) => new(brush, width)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    protected override void OnRender(DrawingContext dc)
    {
        double t = Progress, w = ActualWidth, h = ActualHeight;
        if (loading) { DrawLoading(dc, w, h); return; }
        if (w <= 0 || h <= 0 || (Waking && t >= 1)) return;
        double scale = Math.Max(.1, Math.Min(w / 1280, h / 800));
        // Reveal the original screen evenly, without an expanding spatial mask.
        dc.PushOpacity(Waking ? 1 - Smooth(.52, 1, t) : Smooth(0, .22, t));
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h)); dc.Pop();
        if (!Waking && t >= 1) return;
        dc.PushTransform(new TranslateTransform(w / 2, h / 2 - 8 * scale));
        dc.PushTransform(new ScaleTransform(scale, scale));
        double life = Waking ? Smooth(0, .16, t) * (1 - Smooth(.65, .94, t))
            : Smooth(0, .18, t) * (1 - Smooth(.76, .98, t));
        double unfold = Waking ? Out(.10, .64, t) : 1 - Smooth(.33, .84, t);
        double motion = Waking ? Out(0, .85, t) : Smooth(.08, .9, t);
        double haloSize = Waking ? 110 + 140 * motion : 245 - 130 * motion;
        dc.PushOpacity(life * .75);
        dc.DrawEllipse(Halo, null, new Point(0, 0), haloSize, haloSize * .82);
        dc.Pop();
        DrawOrbit(dc, t, life, motion);
        DrawSparks(dc, t, life, motion);
        double markScale = Waking ? .72 + .28 * Out(.02, .55, t) : (.9 + .1 * Out(0, .28, t)) * (1 - .16 * Smooth(.55, .92, t));
        dc.PushOpacity(life);
        dc.PushTransform(new ScaleTransform(markScale, markScale));
        for (int i = 2; i >= 1; i--)
        {
            double echo = Waking ? Math.Max(0, unfold - .11 * i) : Math.Min(1, unfold + .11 * i);
            dc.PushOpacity(.045 * (3 - i) * Math.Sin(Math.PI * Unit(t)));
            DrawPages(dc, echo, true); dc.Pop();
        }
        DrawPages(dc, unfold, false); dc.Pop(); dc.Pop();
        double filament = Waking ? (1 - Smooth(.12, .36, t)) * Smooth(0, .08, t)
            : Smooth(.64, .83, t) * (1 - Smooth(.88, 1, t));
        dc.PushOpacity(filament * .8);
        dc.DrawEllipse(Halo, null, new Point(0, 0), 26, 94);
        dc.DrawLine(Stroke(White, 1.5), new Point(0, -48), new Point(0, 48));
        dc.Pop(); dc.Pop(); dc.Pop();
    }
    private void DrawLoading(DrawingContext dc, double w, double h)
    {
        if (w <= 0 || h <= 0 || loadingExit >= 1) return;
        double scale = Math.Max(.1, Math.Min(w / 1280, h / 800));
        double breath = .5 - .5 * Math.Cos(loadingSeconds * Math.PI * 2 / 2.2);
        dc.PushOpacity(1 - Smooth(0, 1, loadingExit));
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
        dc.PushTransform(new TranslateTransform(w / 2, h / 2 - 8 * scale));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushOpacity(Smooth(0, .24, loadingSeconds));
        dc.PushOpacity(.4 + .22 * breath);
        dc.DrawEllipse(Halo, null, new Point(0, 0), 210 + 16 * breath, 175 + 12 * breath);
        dc.Pop();
        double size = .98 + .025 * breath;
        dc.PushTransform(new ScaleTransform(size, size));
        dc.PushOpacity(.72 + .28 * breath);
        DrawPages(dc, Out(0, .65, loadingSeconds), false);
        dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
    }
    private static void DrawPages(DrawingContext dc, double open, bool ghost)
    {
        // Perspective folding around the central spine preserves the selected mark.
        double x = 4 + 65 * open, drop = 13 + 25 * open;
        var right = new StreamGeometry();
        using (var g = right.Open())
        {
            g.BeginFigure(new Point(7, -42), true, true);
            g.LineTo(new Point(x - 3, -68 + 14 * (1 - open)), true, false);
            g.QuadraticBezierTo(new Point(x + 7, -71), new Point(x + 7, -58), true, false);
            g.LineTo(new Point(x + 7, 37), true, false);
            g.QuadraticBezierTo(new Point(x + 7, 46), new Point(x - 1, 49), true, false);
            g.LineTo(new Point(7, 73 - drop), true, false);
        }
        dc.DrawGeometry(ghost ? Silver : Back, null, right);
        double lx = -x - 7;
        var left = new StreamGeometry();
        using (var g = left.Open())
        {
            g.BeginFigure(new Point(-7, -41 + 12 * (1 - open)), true, true);
            g.LineTo(new Point(lx + 10, -72 + 13 * (1 - open)), true, false);
            g.QuadraticBezierTo(new Point(lx, -76), new Point(lx, -62), true, false);
            g.LineTo(new Point(lx, 35), true, false);
            g.QuadraticBezierTo(new Point(lx, 42), new Point(lx + 9, 47), true, false);
            g.LineTo(new Point(-15, 75), true, false);
            g.QuadraticBezierTo(new Point(-5, 80), new Point(-5, 65), true, false);
            g.LineTo(new Point(-5, -27), true, false);
            g.QuadraticBezierTo(new Point(-5, -35), new Point(-7, -41 + 12 * (1 - open)), true, false);
        }
        dc.DrawGeometry(ghost ? Silver : Front, null, left);
        if (!ghost)
        {
            dc.PushOpacity(.30 * open);
            dc.DrawLine(Stroke(White, .7), new Point(lx + 7, -61), new Point(-16, -31));
            dc.DrawLine(Stroke(White, .6), new Point(16, -43), new Point(x - 2, -64)); dc.Pop();
        }
    }
    private void DrawOrbit(DrawingContext dc, double t, double life, double motion)
    {
        dc.PushOpacity(life * .30 * Math.Sin(Math.PI * Unit(t)));
        double radius = Waking ? 92 + 77 * motion : 168 - 74 * motion;
        double angle = Waking ? -96 + 80 * motion : -12 - 80 * motion;
        for (int i = 0; i < 2; i++)
        {
            double start = (angle + i * 180) * Math.PI / 180, end = start + 1.15;
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(new Point(Math.Cos(start) * radius, Math.Sin(start) * radius), false, false);
                g.ArcTo(new Point(Math.Cos(end) * radius, Math.Sin(end) * radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, Stroke(Silver, .8), arc);
            dc.DrawEllipse(White, null, new Point(Math.Cos(end) * radius, Math.Sin(end) * radius), 1.5, 1.5);
        }
        dc.Pop();
    }
    private void DrawSparks(DrawingContext dc, double t, double life, double motion)
    {
        double pulse = Math.Sin(Math.PI * Range(.04, .94, t));
        for (int i = 0; i < 18; i++)
        {
            double angle = (i * 137.508 + 11) * Math.PI / 180;
            double seed = ((i * 43) % 17) / 17.0;
            double distance = Waking ? 78 + motion * (110 + seed * 75) : 230 - motion * (125 + seed * 38);
            double x = Math.Cos(angle) * distance, y = Math.Sin(angle) * distance * .78;
            double length = 2 + 5 * seed;
            dc.PushOpacity(life * pulse * (.16 + .28 * seed));
            dc.DrawLine(Stroke(i % 3 == 0 ? White : Soft, seed > .6 ? 1.2 : .7), new Point(x, y), new Point(x + Math.Cos(angle) * length, y + Math.Sin(angle) * length)); dc.Pop();
        }
    }
}
