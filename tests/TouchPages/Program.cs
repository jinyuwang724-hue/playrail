using ClawLibrary.Engine;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name); checks++;
}
var page = new TouchSurface(1, 2, 3, "game-A");
var g = new TouchPointerGesture();
Check(g.Begin(1, 10, 10, page), "first pointer owns the gesture");
Check(!g.Begin(2, 40, 40, page), "second finger cannot replace ownership");
Check(g.Move(2, 100, 100, page) == TouchResult.None, "foreign move is ignored");
Check(g.End(2, 100, 100, page) == TouchResult.None && g.Owns(1), "foreign release cannot end the gesture");
g.Cancel(2);
Check(g.Active, "foreign cancellation cannot cancel the gesture");
Check(g.End(1, 14, 15, page) == TouchResult.Tap && !g.Active, "small jitter releases exactly one tap");
Check(g.End(1, 14, 15, page) == TouchResult.None, "duplicate release cannot tap again");
g.Begin(3, 0, 0, page);
Check(g.Move(3, 8, 0, page) == TouchResult.Horizontal, "horizontal threshold locks direction");
Check(g.Move(3, 9, 200, page) == TouchResult.Horizontal, "cross-axis drift does not change intent");
Check(g.End(3, 0, 0, page) == TouchResult.Horizontal, "returning to start after dragging cannot become a tap");
g.Begin(4, 0, 0, page);
Check(g.End(4, 2, 80, page) == TouchResult.Vertical, "movement first observed on release still suppresses tap");
g.Begin(5, 0, 0, page);
Check(g.Move(5, 0, 0, page with { Page = 2 }) == TouchResult.Canceled, "page changes cancel pending activation");
Check(g.End(5, 0, 0, page) == TouchResult.Canceled, "returning to previous page cannot revive a canceled tap");
foreach (var changed in new[] { page with { Detail = 8 }, page with { Version = 9 }, page with { Item = "game-B" } })
{
    g.Begin(6, 0, 0, page);
    Check(g.End(6, 0, 0, changed) == TouchResult.Canceled, "changed route, instance or selected item cancels release");
}
g.Begin(7, 0, 0, page); g.Cancel(7);
Check(!g.Active && g.End(7, 0, 0, page) == TouchResult.None, "capture loss never activates");
g.Begin(8, 0, 0, page); g.Reset();
Check(g.Begin(9, 0, 0, page) && g.End(9, 0, 0, page) == TouchResult.Tap, "a fresh gesture works after interruption");
Console.WriteLine($"{checks} checks passed");
