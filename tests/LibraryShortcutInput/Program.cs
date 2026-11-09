using System.Collections.Concurrent;
using System.Numerics;
using ClawLibrary.Input;

int checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
Check(GamepadSourceChoice.Select(3, 2, 0) == 1, "neutral stale slot yields to a new active virtual pad");
Check(GamepadSourceChoice.Select(3, 3, 0) == 0, "duplicate physical/virtual input retains one source");
Check(GamepadSourceChoice.Select(2, 0, 0) == 1, "removed source yields to connected replacement");
Check(GamepadSourceChoice.Select(0, 0, 0) == -1, "all pads removed");
for (int connected = 0; connected < 16; connected++)
    for (int active = 0; active < 16; active++)
        for (int previous = -1; previous < 4; previous++)
        {
            int selected = GamepadSourceChoice.Select(connected, active, previous);
            Check(connected == 0 ? selected == -1 : selected >= 0 && (connected & (1 << selected)) != 0, "selection always connected");
            if ((active & connected) != 0) Check((active & (1 << selected)) != 0, "live input cannot be hidden by a neutral slot");
        }

var gate = new object();
var slots = new Dictionary<uint, GamepadPoller.Snapshot> { [0] = default, [1] = default };
bool foreground = true;
var stage = new ClawLibrary.Stage.WallStage();
var dispatcher = new Microsoft.UI.Dispatching.DispatcherQueue();
using var poller = new GamepadPoller(dispatcher, stage, () => foreground,
    index => { lock (gate) return slots.TryGetValue(index, out var sample) ? sample : null; });
async Task Until(Func<bool> ready, string label)
{
    for (int i = 0; i < 100 && !ready(); i++) await Task.Delay(10);
    Check(ready(), label);
}
void Set(uint slot, ushort buttons) { lock (gate) slots[slot] = new(buttons, 0, 0, 0, 0, 0, 0); }
poller.Start();
await Until(() => poller.Connected, "poller connected");
Set(1, 0x1001); // Up + A on virtual pad while the old physical slot stays neutral.
await Until(() => stage.Actions.Contains("A") && stage.Actions.Contains("Up"), "replacement pad reaches actual navigation/A dispatch");
Set(0, 0x1001); // Both copies now report the same held input.
await Task.Delay(40);
Check(stage.Actions.Count(a => a == "A") == 1, "one A edge despite two copies");
Set(0, 0); Set(1, 0); await Task.Delay(40);
lock (gate) slots.Remove(1);
poller.RequestRescan();
await Task.Delay(40); Set(0, 0x2000);
await Until(() => stage.Actions.Contains("B"), "handoff/rescan restores B on physical pad");
Set(0, 0); await Task.Delay(40);
foreground = false; await Task.Delay(300); int count = stage.Actions.Count;
Set(0, 0x1000); await Task.Delay(60);
Check(stage.Actions.Count == count && poller.Last.Buttons == 0, "background window receives no gamepad actions");
Set(0, 0); foreground = true; await Task.Delay(40);
dispatcher.Defer = true;
Set(0, 0x1000); await Task.Delay(40);
foreground = false; count = stage.Actions.Count;
dispatcher.Drain();
Check(stage.Actions.Count == count, "queued navigation is rejected after focus leaves the library");
dispatcher.Defer = false;
Set(0, 0); await Task.Delay(40);
int backs = stage.Actions.Count(a => a == "B");
foreground = true; Set(0, 0x2000);
var latency = System.Diagnostics.Stopwatch.StartNew();
await Until(() => stage.Actions.Count(a => a == "B") > backs, "controller resumes after activation");
Check(latency.ElapsedMilliseconds < 120, "foreground handoff no longer waits 250ms");
Console.WriteLine($"PASS {checks} gamepad selection and real poller dispatch checks");

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public volatile bool Defer;
        private readonly ConcurrentQueue<Action> _pending = new();
        public bool TryEnqueue(Action action) { if (Defer) _pending.Enqueue(action); else action(); return true; }
        public void Drain() { while (_pending.TryDequeue(out var action)) action(); }
    }
}
namespace ClawLibrary.Engine
{
    public enum Direction { Up, Down, Left, Right }
    public static class Motion { public const double PanTimeConstantMs = 80; }
}
namespace ClawLibrary.Stage
{
    public sealed class WallStage
    {
        public bool GamepadInputBlocked;
        public Vector2 PanVelocity;
        public readonly ConcurrentQueue<string> Actions = new();
        public void GamepadActivity(bool active) { }
        public void Activate() => Actions.Enqueue("A");
        public void Back() => Actions.Enqueue("B");
        public void Move(ClawLibrary.Engine.Direction d) => Actions.Enqueue(d.ToString());
        public void SwitchTab(int direction) { }
        public void PadY() { }
        public void PadX() { }
        public void PadView() { }
        public void ToggleControlCenter() { }
        public void RequestPanLoop() { }
    }
}
