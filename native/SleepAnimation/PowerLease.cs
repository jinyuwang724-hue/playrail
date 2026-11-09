using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Session;

namespace ClawSleepAnimation;

internal sealed record LeaseRecord(string Id, int Pid, long StartTicks, string Run, PolicyBackup Policy);

// The durable record is committed before PBUTTONACTION changes. The independent
// guard restores on host exit/hang; --recover-only handles a lost host AND guard
// at boot, before the interactive logon task starts.
internal sealed class PowerLease : IDisposable
{
    private readonly string state;
    private readonly LeaseRecord record;
    private readonly Process guard;
    private bool disposed;
    public int GuardPid => guard.Id;
    public PolicyBackup Original => record.Policy;
    public bool Healthy => !disposed && !guard.HasExited && SameRecord(state, record) && PowerPolicy.StillClaimed(record.Policy, state);

    private PowerLease(string state, LeaseRecord record, Process guard) { this.state = state; this.record = record; this.guard = guard; }
    private static string Active(string state) => Path.Combine(state, "active-lease.json");
    private static void Atomic(string path, object value)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value)); File.Move(path + ".tmp", path, true);
    }
    private static LeaseRecord Load(string state)
    {
        var record = JsonSerializer.Deserialize<LeaseRecord>(File.ReadAllText(Active(state))) ?? throw new InvalidDataException("Missing lease record");
        string runs = Path.GetFullPath(Path.Combine(state, "runs")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(record.Run).StartsWith(runs, StringComparison.OrdinalIgnoreCase) ||
            record.Policy.Scheme == Guid.Empty || record.Policy.Ac is not (1 or 2) || record.Policy.Dc is not (1 or 2) || record.Pid <= 0)
            throw new InvalidDataException("Invalid power lease record");
        return record;
    }
    private static bool SameRecord(string state, LeaseRecord record)
    {
        try { return File.Exists(Active(state)) && Load(state).Id == record.Id; } catch (IOException) { return false; }
    }
    private static bool OwnerAlive(LeaseRecord record)
    {
        try { using var process = Process.GetProcessById(record.Pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.StartTicks; }
        catch (ArgumentException) { return false; }
    }
    public static bool Recover(string state, bool requireNoOwner = true)
    {
        if (!File.Exists(Active(state))) return true;
        var record = Load(state);
        if (requireNoOwner && OwnerAlive(record)) { Program.Log("recovery-skipped-live-owner", new { record.Pid }); return false; }
        Restore(state, record, "recovery"); return true;
    }
    private static void Restore(string state, LeaseRecord record, string reason)
    {
        if (!SameRecord(state, record)) return;
        PowerPolicy.Restore(record.Policy);
        File.WriteAllText(Path.Combine(record.Run, "stopped"), reason);
        File.Delete(Active(state));
        Program.Log("lease-restored", new { reason, record.Id, policy = PowerPolicy.Read() });
        if (!OwnerAlive(record))
            try { using var trace = new TraceEventSession("ClawSleepDiagnostic-" + record.Pid, TraceEventSessionOptions.Attach); trace.Stop(); } catch { }
    }
    public static async Task<PowerLease> StartAsync(string state)
    {
        if (!Recover(state)) throw new InvalidOperationException("Another session already owns the power button");
        var policy = PowerPolicy.Read();
        if (policy.Ac is not (1 or 2) || policy.Dc is not (1 or 2)) throw new InvalidOperationException("Animation requires a sleep or hibernate power-button action");
        using var owner = Process.GetCurrentProcess();
        string run = Path.Combine(state, "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var record = new LeaseRecord(Guid.NewGuid().ToString("N"), owner.Id, owner.StartTime.ToUniversalTime().Ticks, run, policy);
        Atomic(Active(state), record);
        File.WriteAllText(Path.Combine(run, "heartbeat"), "ready");
        Process? guard = null;
        try
        {
            guard = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--lease-guard --state-dir \"" + state + "\" --lease-id " + record.Id)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new InvalidOperationException("Guard did not start");
            string ready = Path.Combine(run, "guard.ready");
            for (int i = 0; i < 80 && !File.Exists(ready) && !guard.HasExited; i++) await Task.Delay(50);
            if (!File.Exists(ready) || guard.HasExited || File.ReadAllText(ready) != guard.Id.ToString())
                throw new InvalidOperationException("Guard did not confirm ready");
            PowerPolicy.Claim(policy);
            File.WriteAllText(Path.Combine(run, "armed"), "true");
            Program.Log("lease-armed", new { record.Id, record.Pid, guardPid = guard.Id, original = policy });
            return new(state, record, guard);
        }
        catch
        {
            Restore(state, record, "activation-failed"); guard?.Dispose(); throw;
        }
    }
    public void Heartbeat() => File.WriteAllText(Path.Combine(record.Run, "heartbeat"), DateTime.UtcNow.ToString("o"));
    public static int Guard(string state, string id)
    {
        var record = Load(state);
        if (record.Id != id || !OwnerAlive(record)) return 1;
        using var display = new DisplayPower();
        File.WriteAllText(Path.Combine(record.Run, "guard.ready"), Environment.ProcessId.ToString());
        var watchdog = new HeartbeatWatchdog(DateTime.UtcNow);
        string reason = "owner-exited";
        try
        {
            while (SameRecord(state, record) && !File.Exists(Path.Combine(record.Run, "stopped")))
            {
                DateTime now = DateTime.UtcNow;
                if (!OwnerAlive(record)) break;
                if (watchdog.IsUnresponsive(now, display.On, display.OnAt, File.GetLastWriteTimeUtc(Path.Combine(record.Run, "heartbeat"))))
                { reason = "owner-unresponsive"; break; }
                if (File.Exists(Path.Combine(record.Run, "armed")) && !PowerPolicy.StillClaimed(record.Policy, state))
                { reason = "power-policy-changed"; break; }
                Thread.Sleep(500);
            }
        }
        finally
        {
            // Retry restoration briefly if a concurrent Settings write is in progress.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { Restore(state, record, reason); break; }
                catch (Exception ex) { Program.Log("guard-restore-retry", ex.Message); if (attempt == 2) throw; Thread.Sleep(200); }
            }
        }
        return 0;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Restore(state, record, "monitor-stopped");
        guard.Dispose();
    }
}
