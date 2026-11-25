using CocoonUI.Charts;
using CocoonUI.Core;
using CocoonUI.Models;
using CocoonUI.Tokens;

int passed = 0;
void Check(bool cond, string name)
{
    if (!cond) throw new InvalidOperationException($"FAIL: {name}");
    passed++;
    Console.WriteLine($"  PASS: {name}");
}

void CheckNear(double a, double b, double eps, string name)
{
    if (Math.Abs(a - b) > eps) throw new InvalidOperationException($"FAIL: {name} (expected {b}, got {a})");
    passed++;
    Console.WriteLine($"  PASS: {name}");
}

Console.WriteLine("Running CocoonUI unit tests...");

// 1. Token integrity & CJK weight rule
Check(CocoonTokens.TextWeight("性能").Weight == 500, "CJK text enforces Medium (500) weight");
Check(CocoonTokens.TextWeight("性能 20 W").Weight == 500, "Mixed CJK+Latin text stays at Medium (500)");
Check(CocoonTokens.TextWeight("60 FPS").Weight == 600, "Pure Latin/numeric text defaults to SemiBold (600)");
Check(CocoonTokens.Colors.BrandAccentInk.R == 0x7A && CocoonTokens.Colors.BrandAccentInk.G == 0x4C && CocoonTokens.Colors.BrandAccentInk.B == 0xFF, "AccentInk matches #7A4CFF");
Check(CocoonTokens.Colors.ScrimModal.A == 194, "Modal scrim alpha matches 194 (0.76)");

// 2. Bracket math classification & concentricity
var (compactOutset, compactR) = CocoonBracketMath.ConcentricCompact(400f, 80f, 40f);
CheckNear(compactOutset, 5.5, 0.001, "Compact outset is 5.5 px");
CheckNear(compactR, 45.5, 0.001, "Concentric radius = targetRadius + outset");

var circleM = CocoonBracketMath.ComputeMetrics(120f + compactOutset * 2f, 120f + compactOutset * 2f, 60f + compactOutset);
Check(circleM.Kind == BracketShapeKind.Circle, "Square 120x120 r=60 classifies as Circle");
Check(circleM.Gap >= 0.30f && circleM.Gap <= 0.62f, "Circle gap angle stays within [0.30, 0.62] rad");

var capsuleM = CocoonBracketMath.ComputeMetrics(400f + compactOutset * 2f, 80f + compactOutset * 2f, 40f + compactOutset);
Check(capsuleM.Kind == BracketShapeKind.Capsule, "Wide 400x80 r=40 classifies as Capsule");
Check(capsuleM.Gap >= 18f, "Capsule maintains >= 18 px center gap");

var rectM = CocoonBracketMath.ComputeMetrics(562f + compactOutset * 2f, 124f + compactOutset * 2f, 30f + compactOutset);
Check(rectM.Kind == BracketShapeKind.RoundedRect, "562x124 r=30 classifies as RoundedRect");
Check(rectM.LegX > 0f && rectM.LegY > 0f, "RoundedRect has positive X and Y bracket legs");

var (dynOutset, dynR) = CocoonBracketMath.ConcentricDynamic(280f, 420f, 48.7f);
CheckNear(dynOutset, 0.12 * 280.0 * 0.6, 0.01, "Dynamic outset scales at 0.12 * min(w,h) * 0.6");
CheckNear(dynR, 48.7 + dynOutset / 2.0, 0.01, "Dynamic concentric radius = targetR + outset / 2");

var (clampedOutset, _) = CocoonBracketMath.ConcentricDynamic(400f, 500f, 40f);
CheckNear(clampedOutset, 24.0 * 1.75 * 0.6, 0.01, "Dynamic outset clamps at 24dp * 1.75 * 0.6 for >=350px");

var (r0, g0, b0) = CocoonBracketMath.SamplePerimeterColor(0f, 0f);
Check(r0 == CocoonTokens.Colors.BrandAccentEnd.R && g0 == CocoonTokens.Colors.BrandAccentEnd.G && b0 == CocoonTokens.Colors.BrandAccentEnd.B, "Perimeter phase=0, s=0 hits AccentEnd");
var (rHalf, gHalf, bHalf) = CocoonBracketMath.SamplePerimeterColor(0.5f, 0f);
Check(rHalf == CocoonTokens.Colors.BrandAccentStart.R && gHalf == CocoonTokens.Colors.BrandAccentStart.G && bHalf == CocoonTokens.Colors.BrandAccentStart.B, "Perimeter phase=0, s=0.5 hits AccentStart");

// 3. Spring period math
var period600 = CocoonTokens.Motion.CompositionSpringPeriod(CocoonTokens.Motion.SpringFocusStiffness);
CheckNear(period600.TotalSeconds, 1.0 / Math.Sqrt(600.0), 1e-6, "CompositionSpringPeriod(600) == 1/sqrt(600)s");

// 4. Dual-mode InputState
var input = new CocoonInputState();
bool? lastMode = null;
input.TouchModeChanged += m => lastMode = m;
Check(input.SetTouchMode(true) && lastMode == true, "Entering TouchMode raises event");
Check(!input.SetTouchMode(true), "Setting identical TouchMode is a no-op");
Check(input.IsEdgeBackTouch(12f, isTouch: true) && !input.IsEdgeBackTouch(12f, isTouch: false), "Edge-back guard applies only to touch");
CheckNear(CocoonInputState.ApplyOverscroll(-30f, 500f), -10.0, 0.02, "Negative overscroll uses 1/3 resistance");
CheckNear(CocoonInputState.ApplyOverscroll(530f, 500f), 510.0, 0.02, "Positive overscroll uses 1/3 resistance");
var (inertiaTarget, inertiaDur) = CocoonInputState.ComputeInertia(100f, 3000f, 500f);
CheckNear(inertiaTarget, 0f, 0.001, "Inertia target clamps to [0, maxScroll]");
CheckNear(inertiaDur, 650.0, 0.001, "Inertia duration clamps at 650 ms max");

// 5. Fixed-bucket ActivityBars stability
var samplesA = new List<double> { 10, 20, 30, 40 }; // seq = 4 (even: [10,20], [30,40])
var bucketsA = CocoonChartPainter.ComputeActivityBuckets(samplesA, sampleSeq: 4, barCount: 30);
CheckNear(bucketsA[28]!.Value, 15.0, 0.001, "Bucket 28 holds average of [10, 20]");
CheckNear(bucketsA[29]!.Value, 35.0, 0.001, "Bucket 29 holds average of [30, 40]");

var samplesB = new List<double> { 10, 20, 30, 40, 50 }; // seq = 5 (odd: [10,20], [30,40], [50])
var bucketsB = CocoonChartPainter.ComputeActivityBuckets(samplesB, sampleSeq: 5, barCount: 30);
CheckNear(bucketsB[27]!.Value, 15.0, 0.001, "Historical bucket [10,20] stays invariant on odd sample");
CheckNear(bucketsB[28]!.Value, 35.0, 0.001, "Historical bucket [30,40] stays invariant on odd sample");
CheckNear(bucketsB[29]!.Value, 50.0, 0.001, "Newest bucket holds single odd sample [50]");

// 6. Row model construction
var row = new CocoonRowItem
{
    Id = "tdp",
    Kind = CocoonRowKind.Slider,
    Title = "目标功耗",
    Min = 8,
    Max = 35,
    Value = 20,
    ValueText = "20 W",
};
Check(row.Kind == CocoonRowKind.Slider && row.Value == 20, "CocoonRowItem initializes cleanly");

Console.WriteLine($"All {passed} CocoonUI tests passed.");
