using System.Diagnostics;
using System.Text.Json;
using ClawSleepAnimation;

string Option(string key) => args[Array.IndexOf(args, key) + 1];
if (args.Contains("--enhanced-guard")) return EnhancedSleepGuard.Run(Option("--state-dir"), Option("--guard-id"));
if (args.Contains("--fixture"))
{
    string path = Option("--fixture");
    for (int i = 0; ; i++) { File.WriteAllText(path, i.ToString()); Thread.Sleep(50); }
}
if (args.Contains("--fixture-host"))
{
    string path = Option("--state-dir"); Directory.CreateDirectory(path);
    using var current = Process.GetCurrentProcess();
    using var guard = await EnhancedSleepGuard.StartAsync(path);
    var hostJournal = new SleepJournal(path);
    // Isolate activity notification from the real user's elevated sleep host.
    var hostRecord = new SleepRecovery(Guid.NewGuid().ToString("N"), current.Id, current.StartTime.ToUniversalTime().Ticks, int.MaxValue,
        PowerPolicy.ActiveScheme(), Guid.NewGuid(), null, []);
    hostJournal.Locked(() => { hostJournal.Write(hostRecord); GameSleep.Suspend(GameSleep.Validate(int.Parse(Option("--fixture-host"))), hostJournal, () => false); return true; });
    File.WriteAllText(Path.Combine(path, "host.ready"), "ready");
    Thread.Sleep(Timeout.Infinite); return 0;
}

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
void Throws<T>(Action action, string message) where T : Exception
{ try { action(); } catch (T) { Check(true, message); return; } throw new Exception("Expected failure: " + message); }
DateTime now = DateTime.UtcNow;
EnhancedSleepPolicy Cycle(bool locked = false)
{
    var p = new EnhancedSleepPolicy();
    if (locked) p.Apply(SleepSignal.Locked, now);
    Check(p.Apply(SleepSignal.DisplayOff, now).Contains(SleepEffect.Prepare), "display-off prepares S0 sleep");
    Check(!p.Apply(SleepSignal.Suspend, now).Contains(SleepEffect.Prepare), "duplicate suspend does not prepare twice");
    return p;
}
foreach (int reason in new[] { 1, 14, 15, 25, 44 })
{
    var p = Cycle(); p.Apply(SleepSignal.ResumeAutomatic, now); p.Apply(SleepSignal.ResumeInteractive, now); p.Apply(SleepSignal.Probe, now, reason);
    Check(p.Apply(SleepSignal.DisplayOn, now).Contains(SleepEffect.Restore), "person wake restores original state");
    Check(!p.Apply(SleepSignal.ResumeInteractive, now).Contains(SleepEffect.Restore), "duplicate wake does not restore twice");
}
foreach (int? reason in new int?[] { null, 0, 20, 29 })
{
    var p = Cycle(); p.Apply(SleepSignal.ResumeAutomatic, now); p.Apply(SleepSignal.ResumeInteractive, now); p.Apply(SleepSignal.Probe, now, reason);
    Check(p.Apply(SleepSignal.DisplayOn, now).Contains(SleepEffect.Restore), "undecided reason is not fabricated as unattended");
}
var locked = Cycle(true); locked.Apply(SleepSignal.Probe, now, 1); locked.Apply(SleepSignal.DisplayOn, now);
Check(locked.Sleeping, "locked-at-entry waits for authentication");
Check(locked.Apply(SleepSignal.Unlocked, now).Contains(SleepEffect.Restore), "unlock restores even when events arrive out of order");
var unattended = Cycle(true); unattended.Apply(SleepSignal.ResumeAutomatic, now); unattended.Apply(SleepSignal.Probe, now, 7);
Check(unattended.Apply(SleepSignal.DisplayOn, now).Contains(SleepEffect.RestoreFingerprint), "fingerprint restored before unlock");
Check(unattended.Sleeping, "unattended screen-on does not restore games/power prematurely");
Check(!unattended.Apply(SleepSignal.UserPresent, now).Contains(SleepEffect.Restore), "presence notification before an interactive wake cannot override an unattended reason");
Check(!unattended.Unlocked && !unattended.UserProof, "isolated presence does not fabricate session proof");
Check(!unattended.Apply(SleepSignal.Tick, now.AddMilliseconds(1999)).Contains(SleepEffect.Resleep), "known unattended wake waits two seconds");
Check(unattended.Apply(SleepSignal.Tick, now.AddSeconds(2)).Contains(SleepEffect.Resleep), "known unattended wake re-quiesces");
for (int i = 3; i < 32; i++) Check(!unattended.Apply(SleepSignal.Tick, now.AddSeconds(i)).Contains(SleepEffect.Resleep), "shared 30-second resleep rate limit");
Check(unattended.Apply(SleepSignal.Unlocked, now.AddSeconds(31)).Contains(SleepEffect.Restore), "user unlock overrides known unattended reason");
var lid = Cycle(true); lid.Apply(SleepSignal.Probe, now, 7); lid.Apply(SleepSignal.DisplayOn, now);
Check(!lid.Apply(SleepSignal.LidOpened, now).Contains(SleepEffect.Restore), "lid proof does not bypass a session locked at entry");
Check(lid.Apply(SleepSignal.Unlocked, now).Contains(SleepEffect.Restore), "unlock completes the locked lid-open wake");
Check(!lid.Apply(SleepSignal.Tick, now.AddMinutes(1)).Contains(SleepEffect.Resleep), "proof cancels delayed resleep");
var presence = Cycle(true); presence.Apply(SleepSignal.ResumeInteractive, now); presence.Apply(SleepSignal.Probe, now, 7); presence.Apply(SleepSignal.DisplayOn, now);
Check(presence.Apply(SleepSignal.UserPresent, now).Contains(SleepEffect.Restore), "presence after a genuine wake releases the session as in the original service");
var falseAlarm = new EnhancedSleepPolicy(); falseAlarm.Apply(SleepSignal.DisplayOff, now); falseAlarm.Apply(SleepSignal.DisplayOn, now);
Check(falseAlarm.Apply(SleepSignal.Probe, now, null).Contains(SleepEffect.Restore), "a display-only cycle can recover even when no system suspend occurred");
var auto = Cycle(); auto.Apply(SleepSignal.ResumeAutomatic, now); auto.Apply(SleepSignal.Probe, now, null);
Check(!auto.Apply(SleepSignal.Tick, now.AddSeconds(19)).Contains(SleepEffect.Resleep), "unknown automatic wake has a confirmation period");
Check(auto.Apply(SleepSignal.Tick, now.AddSeconds(20)).Contains(SleepEffect.Resleep), "automatic wake still off at 20 seconds re-quiesces");
long cycle = auto.Cycle;
Check(auto.Apply(SleepSignal.Cancel, now).Contains(SleepEffect.Restore) && auto.Cycle > cycle, "cancel invalidates queued work");
Check(!auto.Apply(SleepSignal.Cancel, now).Contains(SleepEffect.Restore), "cancel is idempotent");
Check(EnhancedSleepOptions.HibernateSeconds == 172800, "requested 48-hour setting");
Check(!EnhancedSleepPolicy.EntryIsCurrent(SleepSignal.Suspend, 1, 2), "timed-out queued suspend cannot prepare after a wake");
Check(!EnhancedSleepPolicy.EntryIsCurrent(SleepSignal.DisplayOff, 1, 2), "buffered display-off cannot disable hardware after a newer wake");
Check(EnhancedSleepPolicy.EntryIsCurrent(SleepSignal.DisplayOff, 2, 2), "current sleep request can prepare");
Check(EnhancedSleepPolicy.EntryIsCurrent(SleepSignal.Unlocked, 1, 2), "late unlock remains useful proof instead of being discarded");
string signalName = @"Local\ClawEnhancedSleep.Test-" + Guid.NewGuid().ToString("N");
using (var activity = new EventWaitHandle(true, EventResetMode.ManualReset, signalName))
using (var inputGate = new ClawInput.EnhancedSleepGate(signalName))
{
    Check(!inputGate.Sleeping, "input polling runs while awake");
    activity.Reset(); Check(inputGate.Sleeping, "input polling parks on the shared sleep signal");
    var waiting = Task.Run(() => inputGate.Wait(2000)); await Task.Delay(50);
    Check(!waiting.IsCompleted, "input loop waits without polling XInput");
    activity.Set(); await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    Check(!inputGate.Sleeping, "valid wake releases parked input immediately");
}
Check(!FingerprintSleep.Physical(@"ROOT\foo") && !FingerprintSleep.Physical(@"swd\bar") && FingerprintSleep.Physical(@"USB\sensor"), "physical fingerprint filter");
Check(GameSleep.NeverFreeze("ClawPower") && GameSleep.NeverFreeze("svchost") && GameSleep.NeverFreeze("EasyAntiCheat_EOS"), "protected and anti-cheat processes never freeze");
var identity = new FrozenGame(123, 456, @"C:\Games\game.exe");
Check(!GameSleep.Matches(identity, identity with { Started = 789 }) && !GameSleep.Matches(identity, identity with { Image = @"C:\other.exe" }), "PID reuse cannot resume an unrelated process");

string temporary = Path.Combine(Path.GetTempPath(), "ClawEnhancedTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
using var self = Process.GetCurrentProcess();
SleepRecovery Record() => new(Guid.NewGuid().ToString("N"), self.Id, self.StartTime.ToUniversalTime().Ticks, int.MaxValue, Guid.NewGuid(), Guid.NewGuid(), null, []);
var fake = new FakeResources(); var journal = new SleepJournal(temporary, fake); fake.Journal = journal;
var transaction = Record(); fake.Current = transaction.OriginalScheme;
SleepPreparation.Run(journal, fake, transaction, new(true), identity, () => false);
Check(fake.Mutations.SequenceEqual(new[] { "clone", "activate", "suspend", "disable-fingerprint" }), "transaction applies resources in order");
Check(journal.Read()?.Games.Length == 2 && journal.Read()?.Fingerprint == "sensor", "all owned resources survive in the durable journal");
Check(!journal.Recover() && fake.Resumed.Count == 0, "boot recovery leaves a live owner alone");
fake.FailFingerprint = true; fake.FailGame = 12;
Throws<AggregateException>(() => journal.Recover(false), "one failed resource does not stop the others restoring");
Check(fake.Resumed.SequenceEqual(new[] { 11 }) && fake.Current == transaction.OriginalScheme, "plan restores despite fingerprint and child failures");
Check(journal.Read()?.Games.Single().Pid == 12 && journal.Read()?.Fingerprint == "sensor", "only failed resources remain pending");
fake.FailFingerprint = false; fake.FailGame = 0; journal.Recover(false);
Check(journal.Read() == null && fake.Resumed.SequenceEqual(new[] { 11, 12 }), "retry resumes only the remaining game process");
journal.Recover(false); Check(fake.Resumed.Count == 2, "completed recovery is idempotent");

transaction = Record(); fake.Reset(transaction.OriginalScheme); bool cancelled = false;
fake.AfterClone = () => cancelled = true;
Throws<OperationCanceledException>(() => SleepPreparation.Run(journal, fake, transaction, new(true), null, () => cancelled), "wake during cloning cancels late activation");
journal.Recover(false);
Check(!fake.Mutations.Contains("activate") && fake.Current == transaction.OriginalScheme && journal.Read() == null, "cancelled preparation restores and deletes its clone");

transaction = Record(); fake.Reset(transaction.OriginalScheme); Guid external = Guid.NewGuid();
fake.AfterClone = () => fake.Current = external;
Throws<OperationCanceledException>(() => SleepPreparation.Run(journal, fake, transaction, new(true), null, () => false), "external plan switch wins the race with preparation");
journal.Recover(false); Check(fake.Current == external, "recovery preserves a user-selected third plan");

transaction = Record(); fake.Reset(transaction.OriginalScheme);
SleepPreparation.Run(journal, fake, transaction, new(true, false, false), identity, () => false);
Check(!fake.Mutations.Contains("suspend") && !fake.Mutations.Contains("disable-fingerprint"), "participant overrides are honored");
journal.Recover(false);
File.WriteAllText(Path.Combine(temporary, "enhanced-recovery.json"), "{}");
int before = fake.Mutations.Count;
Throws<InvalidDataException>(() => journal.Recover(false), "corrupt recovery data does not mutate the machine");
Check(fake.Mutations.Count == before, "corrupt recovery is fail-closed");
File.Delete(Path.Combine(temporary, "enhanced-recovery.json"));
var settings = new EnhancedSleepOptions(true, false, true); settings.Save(temporary);
Check(EnhancedSleepOptions.Load(temporary) == settings, "independent enhanced settings persist without changing animation settings");

if (args.Contains("--native"))
{
    Guid original = PowerPolicy.ActiveScheme(), owned = Guid.NewGuid();
    try { SleepPowerPlan.Create(original, owned); Check(PowerPolicy.ActiveScheme() == original, "native clone/settings readback does not activate a plan"); }
    finally { SleepPowerPlan.Restore(original, owned); }
    SleepPowerPlan.Restore(original, owned);
    Check(PowerPolicy.ActiveScheme() == original, "native clone deletion and repeated recovery preserve current power");
    string heartbeat = Path.Combine(temporary, "fixture.counter"), hostState = Path.Combine(temporary, "native-recovery");
    Process Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in arguments) info.ArgumentList.Add(a);
        return Process.Start(info)!;
    }
    using var fixture = Start("--fixture", heartbeat);
    Process? host = null;
    try
    {
        for (int i = 0; i < 100 && !File.Exists(heartbeat); i++) await Task.Delay(50);
        Check(File.Exists(heartbeat), "controlled test process started");
        host = Start("--fixture-host", fixture.Id.ToString(), "--state-dir", hostState);
        string ready = Path.Combine(hostState, "host.ready");
        for (int i = 0; i < 150 && !File.Exists(ready) && !host.HasExited; i++) await Task.Delay(50);
        Check(File.Exists(ready), "real guard started before the controlled fixture froze; " + (host.HasExited ? host.StandardError.ReadToEnd() : "host still running"));
        string frozen = File.ReadAllText(heartbeat); await Task.Delay(300);
        Check(File.ReadAllText(heartbeat) == frozen, "native process suspension stops the controlled heartbeat");
        if (args.Contains("--hang"))
        {
            using var display = new DisplayPower(); await Task.Delay(100);
            if (!display.On) throw new InvalidOperationException("Awake-worker hang test requires an already-on display");
            Console.WriteLine("Testing a stuck worker while the host UI heartbeat stays fresh...");
            for (int i = 0; i < 450 && !host.HasExited; i++)
            {
                File.WriteAllText(Path.Combine(hostState, "enhanced-heartbeat"), "responsive-ui");
                await Task.Delay(100);
            }
            Check(host.HasExited, "guard detects a stuck worker even with a responsive UI");
            Check(File.Exists(Path.Combine(hostState, "enhanced-fault.json")), "guard pauses faulty enhancement until explicitly re-enabled");
        }
        else { host.Kill(); await host.WaitForExitAsync(); }
        bool resumed = false;
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            if (File.ReadAllText(heartbeat) != frozen) { resumed = true; break; }
        }
        Check(resumed, "independent guard resumes the fixture after the host is killed");
        Check(PowerPolicy.ActiveScheme() == original, "host-crash recovery leaves actual active plan unchanged");
    }
    finally
    {
        if (host != null && !host.HasExited) { host.Kill(); await host.WaitForExitAsync(); }
        if (host != null && host.ExitCode != 0) Console.Error.WriteLine(host.StandardError.ReadToEnd());
        host?.Dispose(); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); }
        if (Directory.Exists(hostState)) new SleepJournal(hostState).Recover(false);
    }
}
if (!Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(temporary).StartsWith("ClawEnhancedTests-"))
    throw new InvalidOperationException("Unexpected test cleanup directory");
Directory.Delete(temporary, true);
Console.WriteLine($"PASS: {checks} enhanced-sleep checks" + (args.Contains("--native") ? "; native plan readback and real host-crash recovery verified" : " (no hardware mutations)"));
return 0;

sealed class FakeResources : ISleepResources
{
    public SleepJournal Journal = null!;
    public Guid Current;
    public List<string> Mutations = [];
    public List<int> Resumed = [];
    public bool FailFingerprint;
    public int FailGame;
    public Action? AfterClone;
    public void Reset(Guid plan) { Current = plan; Mutations.Clear(); Resumed.Clear(); AfterClone = null; }
    public Guid ActivePlan() => Current;
    public void CreatePlan(Guid original, Guid owned) { if (Journal.Read()?.SleepScheme != owned) throw new Exception("clone was not journalled"); Mutations.Add("clone"); AfterClone?.Invoke(); }
    public void ActivatePlan(Guid plan) { if (Journal.Read()?.SleepScheme != plan) throw new Exception("activation was not journalled"); Current = plan; Mutations.Add("activate"); }
    public void RestorePlan(Guid original, Guid owned) { if (SleepPowerPlan.ShouldRestore(Current, owned)) Current = original; Mutations.Add("restore-plan"); }
    public string? FingerprintCandidate() => "sensor";
    public void DisableFingerprint(string id) { if (Journal.Read()?.Fingerprint != id) throw new Exception("disable was not journalled"); Mutations.Add("disable-fingerprint"); }
    public void RestoreFingerprint(string id) { if (FailFingerprint) throw new Exception("injected fingerprint failure"); Mutations.Add("restore-fingerprint"); }
    public void SuspendGame(FrozenGame game, SleepJournal journal, Func<bool> cancelled)
    { var r = journal.Read()!; journal.Write(r with { Games = [new(11, 1, "root"), new(12, 2, "child")] }); Mutations.Add("suspend"); }
    public void ResumeGame(FrozenGame game) { if (FailGame == game.Pid) throw new Exception("injected game failure"); Resumed.Add(game.Pid); }
}
