// 动效参数与缓动（数值与 src/engine/motion.ts 完全相同）。
// 合成线程上的动画用 CubicBezierEasingFunction / 关键帧；UI 线程这边用同一条曲线算「此刻相机在哪」。
namespace ClawLibrary.Engine;

public readonly record struct Bezier(float X1, float Y1, float X2, float Y2)
{
    /// <summary>与 CSS cubic-bezier 相同的定义，t → 进度。</summary>
    public double Ease(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double cx = 3 * X1, bx = 3 * (X2 - X1) - cx, ax = 1 - cx - bx;
        double cy = 3 * Y1, by = 3 * (Y2 - Y1) - cy, ay = 1 - cy - by;
        double Sx(double t) => ((ax * t + bx) * t + cx) * t;
        double Sy(double t) => ((ay * t + by) * t + cy) * t;
        double Dx(double t) => (3 * ax * t + 2 * bx) * t + cx;
        double tt = x;
        for (int i = 0; i < 8; i++)
        {
            double e = Sx(tt) - x;
            if (Math.Abs(e) < 1e-6) return Sy(tt);
            double d = Dx(tt);
            if (Math.Abs(d) < 1e-6) break;
            tt -= e / d;
        }
        double lo = 0, hi = 1;
        tt = x;
        while (hi - lo > 1e-6) { if (Sx(tt) < x) lo = tt; else hi = tt; tt = (lo + hi) / 2; }
        return Sy(tt);
    }

    public static readonly Bezier Ease_ = new(0.25f, 0.1f, 0.25f, 1f);
    public static readonly Bezier EaseIn = new(0.42f, 0f, 1f, 1f);
    public static readonly Bezier EaseOut = new(0f, 0f, 0.58f, 1f);
    public static readonly Bezier EaseInOut = new(0.42f, 0f, 0.58f, 1f);
}

public static class Motion
{
    /// <summary>相机补间（焦点露出、换层定位）。</summary>
    public const double CameraMs = 420;
    public static readonly Bezier CameraEase = new(0.22f, 1f, 0.36f, 1f);
    /// <summary>缝开口补间。</summary>
    public const double SeamMs = 360;
    public static readonly Bezier SeamEase = new(0.65f, 0f, 0.35f, 1f);

    /// <summary>翻牌：转到 90° 换内容，再转回来。</summary>
    public static class Flip
    {
        public const double OutMs = 150, InMs = 210, StaggerPerCell = 18, MaxDelay = 420, Perspective = 1400;
        public static readonly Bezier OutEase = new(.55f, 0f, .9f, .45f);
        public static readonly Bezier InEase = new(.2f, .7f, .25f, 1f);
    }

    /// <summary>整墙出场 → 入场（换页签）。</summary>
    public static class Wave
    {
        public const double Lift = 90, Scale = 0.88, OutMs = 280, InMs = 420, StaggerPerCell = 30, MaxDelay = 340;
        public static readonly Bezier OutEase = new(.4f, 0f, 1f, 1f);
        public static readonly Bezier InEase = new(.2f, .9f, .3f, 1.04f);
    }

    /// <summary>缝内容换层时的翻转（perspective 900px）。</summary>
    public static class SeamFlip
    {
        public const double OutMs = 160, InMs = 240, Perspective = 900;
        public static readonly Bezier OutEase = Bezier.EaseIn;
        public static readonly Bezier InEase = new(.2f, .7f, .25f, 1f);
    }

    /// <summary>聚焦卡按钮排：卡起步后多久出现、多长、从下方多少 px。</summary>
    public const double FocusActionsDelayMs = 140, FocusActionsMs = 240, FocusActionsRise = 12;
    /// <summary>降低动态效果时，翻牌与波次换成的淡出淡入总时长。</summary>
    public const double ReducedFadeMs = 180;
    /// <summary>右摇杆平移的指数平滑（与滚轮同一类曲线）。</summary>
    public const double PanTimeConstantMs = 80;

    // 卡片样式里的 CSS transition
    public const double PopMs = 300;          // .face transform 300ms ease-out
    public const double ShadowMs = 600;       // box-shadow 600ms ease
    public const double TintMs = 260;         // .tint opacity 260ms ease
    public const double HighlightMs = 200;    // ::after opacity 200ms ease
    public const double StackMs = 300;        // 叠页边 box-shadow 300ms ease（收放、叠色、熄灯）
    /// <summary>熄灯层（.lattice-poster-lights-out，260ms ease 和叠色同节奏）：82 % 黑；透明的缝只把内容压到 0.4。</summary>
    public const double LightsOutOpacity = 0.82, LightsOutSeamFade = 0.4;
    public const double ToastFadeMs = 200, ToastHoldMs = 2200;
    public const double EdgeTabInMs = 240;

    /// <summary>
    /// 让位弹簧（stiffness 300、damping 34、mass 1，阻尼比约 0.98，不过冲）。
    /// 线性系统：位移多大进度曲线都一样，所以只算一条归一化曲线，时长取「一整块宽度的位移」走到静止的时刻。
    /// </summary>
    private static readonly (int DurationMs, double[] Samples) Spring = BuildSpring();

    private static (int, double[]) BuildSpring()
    {
        const double k = 300, c = 34, distance = 12 * 136 - 8;
        const double dt = 1.0 / 1000;
        double x = 0, v = 0, t = 0;
        var samples = new List<double> { 0 };
        // 静止判据：剩余 ≤ 0.5 世界 px 且速度 ≤ 2 px/s
        for (; ; )
        {
            double a = -k * (x - distance) - c * v;
            v += a * dt; x += v * dt; t += dt;
            samples.Add(x / distance);
            if (Math.Abs(distance - x) <= 0.5 && Math.Abs(v) <= 2) break;
            if (t > 3) break;
        }
        samples[^1] = 1;
        return ((int)Math.Round(t * 1000), samples.ToArray());
    }

    public static int ReflowMs => Spring.DurationMs;

    /// <summary>让位进度：t ∈ [0,1] 是时间比例。</summary>
    public static double ReflowEase(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var s = Spring.Samples;
        double p = t * (s.Length - 1);
        int i = (int)Math.Floor(p);
        return s[i] + (s[i + 1] - s[i]) * (p - i);
    }
}
