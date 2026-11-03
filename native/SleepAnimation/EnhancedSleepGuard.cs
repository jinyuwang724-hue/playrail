using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ClawSleepAnimation;

internal sealed record SleepGuardOwner(string Id, int Pid, long Started);
internal sealed class EnhancedSleepGuard : IDisposable
{
    private readonly string state, id;
    private readonly Process guard;
    public bool Healthy => !guard.HasExited;
    private EnhancedSleepGuard(string state, string id, Process guard) { this.state = state; this.id = id; this.guard = guard; }
    public static async Task<EnhancedSleepGuard> StartAsync(string state)
    {
        using var owner = Process.GetCurrentProcess();
        string id = Guid.NewGuid().ToString("N");
        new SleepJournal(state).Locked(() =>
        {
            SleepJournal.Atomic(Path.Combine(state, "enhanced-guard-owner.json"), new SleepGuardOwner(id, owner.Id, owner.StartTime.ToUniversalTime().Ticks));
            return true;
        });
        File.WriteAllText(Path.Combine(state, "enhanced-heartbeat"), "ready");
        File.WriteAllText(Path.Combine(state, "enhanced-worker-heartbeat"), "ready");
        var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--enhanced-guard --state-dir \"" + state + "\" --guard-id " + id)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new InvalidOperationException("增强睡眠守护未启动");
        var result = new EnhancedSleepGuard(state, id, process);
        for (int i = 0; i < 60 && !process.HasExited; i++)
        {
            if (File.Exists(Path.Combine(state, "enhanced-guard-" + id + ".ready"))) return result;
            await Task.Delay(50);
        }
        result.Dispose(); throw new InvalidOperationException("增强睡眠守护未就绪");
    }
    private static bool Alive(SleepGuardOwner owner)
    {
        try { using var process = Process.GetProcessById(owner.Pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == owner.Started; }
        catch (ArgumentException) { return false; }
    }
    public static int Run(string state, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return 2;
        var owner = JsonSerializer.Deserialize<SleepGuardOwner>(File.ReadAllText(Path.Combine(state, "enhanced-guard-owner.json")));
        if (owner == null || owner.Id != id || !Alive(owner)) return 2;
        using var display = new DisplayPower();
        var journal = new SleepJournal(state);
        var watch = new HeartbeatWatchdog(DateTime.UtcNow);
        bool hung = false;
        string ready = Path.Combine(state, "enhanced-guard-" + id + ".ready"), stop = Path.Combine(state, "enhanced-guard-" + id + ".stop");
        File.WriteAllText(ready, Environment.ProcessId.ToString());
        try
        {
            while (!File.Exists(stop) && Alive(owner))
            {
                DateTime now = DateTime.UtcNow;
                DateTime uiBeat = File.GetLastWriteTimeUtc(Path.Combine(state, "enhanced-heartbeat"));
                DateTime workerBeat = File.GetLastWriteTimeUtc(Path.Combine(state, "enhanced-worker-heartbeat"));
                if (watch.IsUnresponsive(now, display.On, display.OnAt, uiBeat < workerBeat ? uiBeat : workerBeat)) { hung = true; break; }
                Thread.Sleep(500);
            }
            if (hung && Alive(owner))
            {
                // End only our own verified host, releasing a stuck native
                // call/mutex before recovery. Never end a game or another app.
                using var process = Process.GetProcessById(owner.Pid);
                if (!string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("增强睡眠宿主身份不匹配");
                SleepJournal.Atomic(Path.Combine(state, "enhanced-fault.json"), new { reason = "host-unresponsive", at = DateTime.UtcNow });
                process.Kill(); process.WaitForExit(5000);
            }
            // Restore every resource independently. Keep the journal until ALL
            // recovery succeeds; a later boot/host can continue failed entries.
            for (int attempt = 0; attempt < 15; attempt++)
            {
                try
                {
                    journal.Locked(() =>
                    {
                        var current = JsonSerializer.Deserialize<SleepGuardOwner>(File.ReadAllText(Path.Combine(state, "enhanced-guard-owner.json")));
                        // An older guard must never recover a newly enabled cycle.
                        return current?.Id != id || journal.Recover(false);
                    });
                    return 0;
                }
                catch (Exception ex) { Program.Log("enhanced-guard-retry", ex.Message); Thread.Sleep(2000); }
            }
            return 1;
        }
        finally { File.Delete(ready); File.Delete(stop); }
    }
    public void Heartbeat() => File.WriteAllText(Path.Combine(state, "enhanced-heartbeat"), DateTime.UtcNow.ToString("o"));
    public void Dispose()
    {
        File.WriteAllText(Path.Combine(state, "enhanced-guard-" + id + ".stop"), "stop");
        guard.Dispose();
    }
}
