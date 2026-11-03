using ClawLibrary.Engine;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var state = new ShortcutMotionState();
Check(state.Show(false, true, false) == ShortcutEntrance.None && state.Direction == ShortcutMotionDirection.None,
    "A visible library underneath another app must only regain foreground without blur/scale or a fade");
Check(state.Show(false, false, false) == ShortcutEntrance.Reveal, "A real hidden-to-visible entrance remains animated");
int first = state.Version;
Check(state.Show(false, true, true) == ShortcutEntrance.KeepCurrent && state.Version == first,
    "Duplicate activation during entrance must not rewind the animation");
Check(state.Complete(first), "Current entrance may complete once");
Check(!state.Complete(first), "A duplicate completion cannot clean up a later visual tree");
Check(state.Show(false, true, true) == ShortcutEntrance.None && state.Direction == ShortcutMotionDirection.None,
    "Already foreground library must not replay motion or reparent its effect layer");
int leave = state.Hide();
Check(state.Show(false, true, true) == ShortcutEntrance.Reverse,
    "A deliberate restore during departure reverses from the current frame");
int restore = state.Version;
Check(!state.Complete(leave), "Old departure callback cannot hide a newly restored window");
Check(state.Complete(restore), "The reversing entrance owns final cleanup");
state.Show(false, false, false);
int interrupted = state.Version;
state.Cancel();
Check(!state.Complete(interrupted) && state.Direction == ShortcutMotionDirection.None,
    "Switching to another app cancels old entrance callbacks before the next recall");
Check(state.Show(false, true, false) == ShortcutEntrance.None,
    "Returning after interruption must settle the covered window without replaying an entrance");
state.Show(false, false, false);
int recalled = state.Version;
int hideDuringEntrance = state.Hide();
Check(!state.Complete(recalled), "A cancelled entrance cannot reset a newer departure");
Check(state.Complete(hideDuringEntrance), "New departure may complete");
Check(state.Show(false, false, false) == ShortcutEntrance.Reveal,
    "A genuinely hidden or minimized library retains the original reveal effect");
int hiddenEntry = state.Version;
state.Cancel();
Check(!state.Complete(hiddenEntry), "Immediate background hide invalidates its old callback");
Check(state.Show(true, false, false) == ShortcutEntrance.Reveal, "Cold shortcut launch retains its entrance");
int oldEntrance = state.Version;
Check(state.Show(false, true, false) == ShortcutEntrance.None,
    "An old enter still running after occlusion must be cancelled rather than continued on foreground return");
Check(!state.Complete(oldEntrance), "Cancelled occluded entrance cannot modify the newly foreground window");
Console.WriteLine($"PASS: {checks} covered-window, duplicate activation, reversal and stale-callback regressions");
