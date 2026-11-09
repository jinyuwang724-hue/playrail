using System.Text;
using ClawSleepAnimation;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
DateTime now = DateTime.UtcNow;
var watchdog = new HeartbeatWatchdog(now);
Check(!watchdog.IsUnresponsive(now.AddSeconds(10), false, now, now), "a sleeping display must not trigger a hang recovery");
Check(!watchdog.IsUnresponsive(now.AddSeconds(11), true, now.AddSeconds(11), now), "resume must allow the UI to recover");
for (int i = 12; i <= 41; i++) Check(!watchdog.IsUnresponsive(now.AddSeconds(i), true, now.AddSeconds(11), now), "the resume grace period must prevent a stale-heartbeat false positive");
Check(watchdog.IsUnresponsive(now.AddSeconds(42), true, now.AddSeconds(11), now), "a truly unresponsive UI must still trigger recovery after the grace period");
var working = new HeartbeatWatchdog(now);
for (int i = 1; i <= 40; i++) Check(!working.IsUnresponsive(now.AddSeconds(i), true, now, now.AddSeconds(i)), "fresh heartbeats must stay healthy");
for (int i = 41; i <= 56; i++) { bool failed = working.IsUnresponsive(now.AddSeconds(i), true, now, now.AddSeconds(40)); Check(failed == (i > 55), "an awake hung UI must recover after the heartbeat timeout"); }
ButtonSignal Press(uint mask = 1, int id = 24, DateTime? at = null) => new(id, "ACPI\\FIXEDBUTTON\\test", mask, at ?? now, 0);
var gate = new ButtonGate();
Check(!gate.TryBegin(Press(), now), "must not react before arming");
gate.Arm(now);
foreach (uint mask in new uint[] { 0, 2, 4, 0x80000000, 0x80000001, 3 })
    Check(!gate.TryBegin(Press(mask), now), "must ignore wake, lid, sleep and combined events");
Check(!gate.TryBegin(Press(id: 25), now), "completion must not duplicate the notification");
Check(!gate.TryBegin(Press(at: now.AddSeconds(-1)), now), "must ignore events queued before arming");
Check(!gate.TryBegin(Press(at: now.AddSeconds(1)), now), "must ignore future timestamps");
Check(!gate.TryBegin(Press(at: now), now.AddSeconds(3)), "must ignore stale buffered events");
Check(gate.TryBegin(Press(), now), "fresh power notification should start animation");
Check(!gate.TryBegin(Press(), now), "must ignore repeat while animation is running");
gate.End(now);
Check(!gate.TryBegin(Press(at: now.AddSeconds(1)), now.AddSeconds(1)), "must debounce repeats");
Check(gate.TryBegin(Press(at: now.AddSeconds(2)), now.AddSeconds(2)), "next deliberate press should work");
gate.End(now.AddSeconds(2));
gate.Resume(now.AddSeconds(20));
Check(!gate.TryBegin(Press(at: now.AddSeconds(20)), now.AddSeconds(20)), "wake press must not immediately rehibernate");
Check(!gate.TryBegin(Press(at: now), now.AddSeconds(24)), "pre-hibernate ETW buffer must stay ignored");
Check(gate.TryBegin(Press(at: now.AddSeconds(24)), now.AddSeconds(24)), "fresh press after resume grace should work");

byte[] Payload(int id, uint mask)
{
    byte[] name = Encoding.Unicode.GetBytes("ACPI\\FIXEDBUTTON\\test\0");
    using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
    writer.Write((ushort)(name.Length / 2)); writer.Write(name);
    if (id == 24) writer.Write((uint)1);
    writer.Write(mask); if (id == 25) writer.Write((ulong)0x0123456789abcdef);
    return stream.ToArray();
}
foreach (int id in new[] { 24, 25 })
{
    var signal = ButtonSignal.Parse(id, Payload(id, 1), now, now.AddMilliseconds(230));
    Check(signal.Device == "ACPI\\FIXEDBUTTON\\test" && signal.Mask == 1 && signal.EventId == id && signal.DeliveryMs == 230,
        "must parse the instance, button mask and event time from both ACPI layouts");
}
foreach (byte[] bad in new[] { Array.Empty<byte>(), new byte[3], new byte[12], new byte[10] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 } })
{
    bool rejected = false; try { ButtonSignal.Parse(24, bad, now, now); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "must reject malformed payloads");
}
var fields = new Dictionary<string, object?>
{
    ["Reason"] = "0", ["TriggerFlags"] = "131", ["UserNotify"] = "0", ["PowerAction"] = "0",
    ["PowerActionFlags"] = "0", ["PowerActionEventCode"] = "0", ["MinState"] = "4", ["SubstitutionPolicy"] = "1",
    ["LocalPowerAction"] = "0", ["LocalPowerActionFlags"] = "0", ["LocalPowerActionEventCode"] = "0",
    ["Disabled"] = "False", ["RequesterName"] = "System"
};
Check(PowerButtonMatcher.Matches(1, fields), "observed Claw power-button signature should match");
Check(!PowerButtonMatcher.Matches(0, fields) && !PowerButtonMatcher.Matches(2, fields), "unknown schema versions must not match");
foreach (string key in fields.Keys.ToArray())
{
    var incomplete = new Dictionary<string, object?>(fields); incomplete.Remove(key);
    Check(!PowerButtonMatcher.Matches(1, incomplete), "missing signature fields must not match");
    var different = new Dictionary<string, object?>(fields) { [key] = key == "Disabled" ? "True" : "99" };
    Check(!PowerButtonMatcher.Matches(1, different), "other actions, triggers and requesters must not match");
}
var qpcGate = new PowerButtonGate(1000);
Check(!qpcGate.TryBegin(new(1000, 1000)), "QPC gate must start disarmed"); qpcGate.Arm(1000);
Check(!qpcGate.TryBegin(new(999, 1000)), "QPC gate must reject queued startup events");
Check(!qpcGate.TryBegin(new(1000, 3100)), "QPC gate must reject old events");
Check(!qpcGate.TryBegin(new(1200, 1000)), "QPC gate must reject future events");
Check(qpcGate.TryBegin(new(1100, 1190)), "fresh button should start");
Check(!qpcGate.TryBegin(new(1101, 1191)), "burst must not schedule duplicate hibernations");
qpcGate.End(2000);
Check(!qpcGate.TryBegin(new(2001, 2100)), "wake press should not trigger a new hibernation");
Check(!qpcGate.TryBegin(new(1100, 5100)), "buffered pre-hibernate press must stay rejected after wake");
Check(qpcGate.TryBegin(new(5100, 5190)), "fresh post-resume press should work");
var settingsDir = Path.Combine(Path.GetTempPath(), "ClawAnimationOptions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(settingsDir);
try
{
    Check(AnimationOptions.Load(settingsDir) == new AnimationOptions(true, true), "first install should retain suspend animation and enable wake animation");
    foreach (bool suspend in new[] { false, true }) foreach (bool resume in new[] { false, true })
    {
        var settings = new AnimationOptions(suspend, resume); settings.Save(settingsDir);
        Check(AnimationOptions.Load(settingsDir) == settings, "both switches must persist independently");
    }
    foreach (string bad in new[] { "{}", "{\"enabled\":1}", "{\"enabled\":\"true\"}", "{\"enabled\":null}" })
    {
        using var doc = System.Text.Json.JsonDocument.Parse(bad); bool rejected = false;
        try { AnimationOptions.Flag(doc.RootElement, "enabled"); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "non-boolean switch values must be rejected");
    }
}
finally { foreach (string file in Directory.GetFiles(settingsDir)) File.Delete(file); Directory.Delete(settingsDir); }
qpcGate.Disarm(); Check(!qpcGate.TryBegin(new(10000,10050)), "disabled animation must not consume a power-button signal");
var shortcut = new TransitionShortcutState();
Check(shortcut.Key(0x5b, false), "animation must consume firmware Win down");
Check(shortcut.Key(0x47, false), "animation must consume firmware G down");
Check(shortcut.Key(0x47, true), "animation must consume its paired G up");
Check(shortcut.Key(0x5b, true), "animation must consume its paired Win up without opening Start");
Check(shortcut.BlockedWindowsPresses == 1, "must count the intercepted physical chord");
Check(!shortcut.Key(0x47, false) && !shortcut.Key(0x47, true), "ordinary G typing must pass");
Check(!shortcut.Key(0x11, false) && !shortcut.Key(0x12, false), "ordinary Control and Alt must pass");
Check(shortcut.Key(0x5c, false) && shortcut.Key(0x47, false), "right Win chord must be consumed");
Check(shortcut.Key(0x5c, true) && shortcut.Key(0x47, true), "reversed release order must not leak a G up");
var heldBefore = new TransitionShortcutState(1);
Check(!heldBefore.Key(0x5b, false), "preexisting Win repeats must not become a consumed modifier");
Check(heldBefore.Key(0x47, false) && heldBefore.Key(0x47, true), "block G even when Win was held before the animation");
Check(!heldBefore.Key(0x5b, true), "release an inherited modifier to its original recipient");
shortcut.End();
Check(!shortcut.Key(0x5b, false) && !shortcut.Key(0x47, false) && !shortcut.Key(0x47, true) && !shortcut.Key(0x5b, true), "all shortcuts must pass after the animation");
int injected = 0;
Task FastDelay(int _) => Task.CompletedTask;
var dismissResult = await GameBarDismissGate.RunAsync(() => false, () => false, () => { injected++; return true; }, FastDelay);
Check(injected == 0 && !dismissResult.Remaining, "no overlay must never send Escape to the active app");
bool barVisible = true;
dismissResult = await GameBarDismissGate.RunAsync(() => barVisible, () => false, () => { injected++; barVisible = false; return true; }, FastDelay);
Check(injected == 1 && dismissResult.Presses == 1 && !dismissResult.Remaining, "dismiss exactly once when the overlay closes");
injected = 0; barVisible = true;
dismissResult = await GameBarDismissGate.RunAsync(() => barVisible, () => false, () => { barVisible = ++injected < 2; return true; }, FastDelay);
Check(injected == 2 && !dismissResult.Remaining, "an open widget flyout can consume the first Escape without leaking a third");
injected = 0;
dismissResult = await GameBarDismissGate.RunAsync(() => true, () => true, () => { injected++; return true; }, FastDelay);
Check(injected == 0 && dismissResult.Remaining, "held modifiers or Escape must never be disturbed");
int visibilityReads = 0; injected = 0;
dismissResult = await GameBarDismissGate.RunAsync(() => ++visibilityReads == 1, () => false, () => { injected++; return true; }, FastDelay);
Check(injected == 0, "recheck visibility immediately before input so Escape cannot escape to the underlying game");
injected = 0;
dismissResult = await GameBarDismissGate.RunAsync(() => true, () => false, () => { injected++; return false; }, FastDelay);
Check(injected == 1 && dismissResult.Presses == 0 && dismissResult.Remaining, "input failure must stop retries");
injected = 0;
dismissResult = await GameBarDismissGate.RunAsync(() => true, () => false, () => { injected++; return true; }, FastDelay);
Check(injected == 3 && dismissResult.Remaining, "an unresponsive Game Bar must have a bounded retry count");
Console.WriteLine($"PASS: {checks} power-button, settings, transition-shortcut, Game Bar dismissal and suspend/resume safety checks");
