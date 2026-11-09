using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClawSleepAnimation;

internal sealed record SleepRecovery(string Id, int OwnerPid, long OwnerStarted, int Session, Guid OriginalScheme, Guid SleepScheme,
    string? Fingerprint, FrozenGame[] Games);

// Write-ahead ownership journal. Shared by the host, the independent guard, and
// boot recovery. All native mutations and record updates use this same mutex.
internal sealed class SleepJournal
{
    private readonly string state;
    private readonly ISleepResources resources;
    private string PathName => Path.Combine(state, "enhanced-recovery.json");
    public SleepJournal(string state, ISleepResources? resources = null) { this.state = state; this.resources = resources ?? new NativeSleepResources(); }
    public T Locked<T>(Func<T> action)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(state).ToUpperInvariant())))[..24];
        using var gate = new Mutex(false, @"Global\ClawEnhancedSleep." + key);
        bool acquired = false;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("增强睡眠正在恢复，请稍候");
            return action();
        }
        finally { if (acquired) gate.ReleaseMutex(); }
    }
    public static void Atomic(string path, object value)
    {
        string temporary = path + ".tmp";
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value);
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { file.Write(data); file.Flush(true); }
        File.Move(temporary, path, true);
    }
    public SleepRecovery? Read()
    {
        if (!File.Exists(PathName)) return null;
        var r = JsonSerializer.Deserialize<SleepRecovery>(File.ReadAllBytes(PathName)) ?? throw new InvalidDataException("增强睡眠恢复记录损坏");
        if (!Guid.TryParseExact(r.Id, "N", out _) || r.OwnerPid <= 0 || r.OwnerStarted <= 0 || r.Session <= 0 ||
            r.OriginalScheme == Guid.Empty || r.SleepScheme == Guid.Empty || r.SleepScheme == r.OriginalScheme || r.Games == null || r.Games.Length > 128 ||
            r.Fingerprint?.Length > 4000 || r.Games.Any(p => p.Pid <= 4 || p.Started <= 0 || string.IsNullOrWhiteSpace(p.Image)))
            throw new InvalidDataException("增强睡眠恢复记录无效");
        return r;
    }
    public void Write(SleepRecovery r) => Atomic(PathName, r);
    public static string AwakeEvent(int session) => @"Global\ClawEnhancedSleep.Awake-" + session;
    private void MarkAwake(int session)
    {
        Atomic(Path.Combine(state, "enhanced-runtime.json"), new { sleeping = false });
        try { using var awake = EventWaitHandle.OpenExisting(AwakeEvent(session)); awake.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }
    public static bool OwnerAlive(SleepRecovery r)
    {
        try { using var p = Process.GetProcessById(r.OwnerPid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == r.OwnerStarted; }
        catch (ArgumentException) { return false; }
    }
    public bool IsOwnedPlan(Guid original, Guid active)
    {
        // Read-only: caller may be a button guard. Atomic replacement means no
        // partially written records are observable, even during preparation.
        var r = Read(); return r != null && r.OriginalScheme == original && r.SleepScheme == active && OwnerAlive(r);
    }
    public bool Recover(bool onlyIfOrphan = true, bool fingerprintOnly = false) => Locked(() =>
    {
        var r = Read();
        if (r == null) return true;
        if (onlyIfOrphan && OwnerAlive(r)) return false;
        var errors = new List<Exception>();
        if (!fingerprintOnly)
        {
            var pending = r.Games.ToList();
            foreach (var game in r.Games.Reverse())
            {
                try { resources.ResumeGame(game); pending.Remove(game); r = r with { Games = pending.ToArray() }; Write(r); }
                catch (Exception ex) { errors.Add(ex); }
            }
            try { resources.RestorePlan(r.OriginalScheme, r.SleepScheme); }
            catch (Exception ex) { errors.Add(ex); }
        }
        // Restore games/power before a potentially slow hardware driver. The
        // early authentication path explicitly requests fingerprintOnly.
        if (r.Fingerprint != null)
        {
            try { resources.RestoreFingerprint(r.Fingerprint); r = r with { Fingerprint = null }; Write(r); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (!fingerprintOnly && errors.Count == 0) { File.Delete(PathName); MarkAwake(r.Session); }
        if (errors.Count != 0) throw new AggregateException("增强睡眠恢复尚未完成", errors);
        return true;
    });
}
