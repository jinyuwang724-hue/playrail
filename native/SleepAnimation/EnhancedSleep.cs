using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace ClawSleepAnimation;

internal sealed class EnhancedSleep : IDisposable
{
    private readonly string state;
    private readonly SleepJournal journal;
    private readonly ISleepResources resources = new NativeSleepResources();
    private readonly EnhancedSleepPolicy policy = new();
    private readonly Channel<Func<Task>> queue = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly EventWaitHandle awake = new(true, EventResetMode.ManualReset, SleepJournal.AwakeEvent(Process.GetCurrentProcess().SessionId));
    private EnhancedSleepNotifications? notifications;
    private EnhancedSleepGuard? guard;
    private EnhancedSleepOptions options;
    private CancellationTokenSource? probe;
    private long probeCycle = -1, invalidation;
    private DateTime probeStarted;
    private FrozenGame? game;
    private DateTime gameSeen;
    private DateTime libraryGameSeen;
    private DateTime fingerprintRecoveryAt;
    private bool? runtimeSleeping;
    private volatile bool enabled, sleeping, disposed;
    private bool bypass;
    private string? error;
    public bool Sleeping => sleeping;
    public event Action? WakeCompleted;
    public object Status => new { enabled, sleeping, suspendGames = options.SuspendGames, fingerprint = options.Fingerprint,
        hibernateAfter = EnhancedSleepOptions.HibernateSeconds, available = guard?.Healthy == true || !enabled, error };
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeoutW(nint window, uint message, nuint w, nint l, uint flags, uint timeout, out nuint result);

    public EnhancedSleep(string state)
    {
        this.state = state; journal = new(state); options = EnhancedSleepOptions.Load(state);
        worker = Task.Run(async () =>
        {
            await foreach (var action in queue.Reader.ReadAllAsync())
                try { await action().ConfigureAwait(false); }
                catch (Exception ex) { error = ex.Message; Program.Log("enhanced-error", ex.ToString()); }
                finally { File.WriteAllText(Path.Combine(state, "enhanced-worker-heartbeat"), DateTime.UtcNow.ToString("o")); }
        });
    }
    public async Task StartAsync()
    {
        try
        {
            if (!journal.Recover()) throw new InvalidOperationException("另一个会话正在使用增强睡眠");
        }
        catch (AggregateException ex)
        {
            // A disconnected fingerprint reader must not take the animation
            // controller or ordinary Windows power-button behavior down with it.
            WriteRuntime(false); error = ex.Message; Program.Log("enhanced-startup-recovery-pending", ex.Message); return;
        }
        WriteRuntime(false);
        if (EnhancedSleepNotifications.SessionLocked()) policy.Apply(SleepSignal.Locked, DateTime.UtcNow);
        WriteRuntime(false);
        bool wanted = options.Enabled;
        if (File.Exists(Path.Combine(state, "enhanced-fault.json")))
        { error = "上次睡眠恢复异常，增强睡眠已暂停，可手动重新开启"; return; }
        if (wanted) await ConfigureAsync(true);
    }
    private Task Enqueue(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(() =>
        {
            try { action(); completion.TrySetResult(); } catch (Exception ex) { completion.TrySetException(ex); }
            return Task.CompletedTask;
        })) completion.TrySetException(new ObjectDisposedException(nameof(EnhancedSleep)));
        return completion.Task;
    }
    public async Task ConfigureAsync(bool value)
    {
        if (value && journal.Read() != null)
        {
            if (!journal.Recover()) throw new InvalidOperationException("另一个会话正在使用增强睡眠");
        }
        // Start the independent recovery process before allowing ANY native
        // mutation. Launching is asynchronous so WPF remains responsive.
        if (value && guard == null) guard = await EnhancedSleepGuard.StartAsync(state);
        try
        {
            await Enqueue(() =>
            {
                if (value)
                {
                    if (guard?.Healthy != true) throw new InvalidOperationException("增强睡眠守护不可用");
                    notifications ??= new EnhancedSleepNotifications(Signal);
                }
                else
                {
                    Interlocked.Increment(ref invalidation); ProcessSignal(SleepSignal.Cancel);
                    journal.Recover(false); WriteRuntime(false);
                }
                var next = options with { Enabled = value };
                next.Save(state); options = next; enabled = value; error = null;
                if (value) File.Delete(Path.Combine(state, "enhanced-fault.json"));
            });
        }
        catch { if (!enabled) { guard?.Dispose(); guard = null; } throw; }
        if (!value) { notifications?.Dispose(); notifications = null; guard?.Dispose(); guard = null; }
    }
    public Task SetGamesAsync(bool value) => Enqueue(() => { var next = options with { SuspendGames = value }; next.Save(state); options = next; });
    public Task SetFingerprintAsync(bool value) => Enqueue(() => { var next = options with { Fingerprint = value }; next.Save(state); options = next; });
    public Task SetGameAsync(int pid, bool library = false) => Enqueue(() =>
    {
        if (sleeping) return;
        if (library) libraryGameSeen = DateTime.UtcNow;
        else if (DateTime.UtcNow - libraryGameSeen < TimeSpan.FromSeconds(15)) return;
        if (pid == 0) { game = null; return; }
        game = GameSleep.Validate(pid); gameSeen = DateTime.UtcNow;
    });
    public Task RequestedAsync(bool hibernate)
    {
        long queuedEpoch = Volatile.Read(ref invalidation);
        return Enqueue(() =>
        {
            bypass = hibernate;
            if (hibernate) ProcessSignal(SleepSignal.Cancel);
            else if (enabled && EnhancedSleepPolicy.EntryIsCurrent(SleepSignal.DisplayOff, queuedEpoch, Volatile.Read(ref invalidation))) ProcessSignal(SleepSignal.DisplayOff);
        });
    }
    public Task CancelRequestedAsync()
    {
        Interlocked.Increment(ref invalidation);
        return Enqueue(() => ProcessSignal(SleepSignal.Cancel));
    }
    public void Signal(SleepSignal signal, bool wait = false)
    {
        if (disposed) return;
        if (signal is SleepSignal.DisplayOn or SleepSignal.ResumeAutomatic or SleepSignal.ResumeInteractive or SleepSignal.Unlocked or SleepSignal.LidOpened or SleepSignal.UserPresent or SleepSignal.Cancel)
            Interlocked.Increment(ref invalidation);
        long queuedEpoch = Volatile.Read(ref invalidation);
        Task completion = Enqueue(() =>
        {
            if (EnhancedSleepPolicy.EntryIsCurrent(signal, queuedEpoch, Volatile.Read(ref invalidation))) ProcessSignal(signal);
        });
        if (wait)
        {
            // PBT_APMSUSPEND has a bounded preparation window. A timed-out
            // transaction is invalidated before any delayed activation.
            if (!completion.Wait(1800)) Interlocked.Increment(ref invalidation);
        }
        else _ = Observe(completion);
    }
    private static async Task Observe(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception ex) { Program.Log("enhanced-signal-error", ex.Message); } }
    public void Heartbeat()
    {
        guard?.Heartbeat();
        Signal(SleepSignal.Tick);
    }
    private void ProcessSignal(SleepSignal signal)
    {
        if (bypass)
        {
            if (signal is SleepSignal.DisplayOn or SleepSignal.ResumeInteractive) bypass = false;
            if (signal is not (SleepSignal.Locked or SleepSignal.Unlocked or SleepSignal.Cancel)) return;
        }
        if (!enabled && signal is not (SleepSignal.Locked or SleepSignal.Unlocked or SleepSignal.Cancel))
        {
            if (signal == SleepSignal.Tick && journal.Read() is { } abandoned && !SleepJournal.OwnerAlive(abandoned)) TryRestore(false);
            return;
        }
        if (enabled && guard?.Healthy != true)
        { enabled = false; error = "守护进程已退出，增强睡眠已暂停"; signal = SleepSignal.Cancel; }
        long epoch = Volatile.Read(ref invalidation);
        bool wasSleeping = sleeping;
        if (!sleeping && signal is SleepSignal.DisplayOff or SleepSignal.Suspend)
            policy.Apply(EnhancedSleepNotifications.SessionLocked() ? SleepSignal.Locked : SleepSignal.Unlocked, DateTime.UtcNow);
        if (signal is SleepSignal.ResumeAutomatic or SleepSignal.ResumeInteractive or SleepSignal.DisplayOn &&
            policy.Sleeping && DateTime.UtcNow - probeStarted > TimeSpan.FromSeconds(5))
        { policy.ResetReason(); probeCycle = -1; }
        foreach (var effect in policy.Apply(signal, DateTime.UtcNow))
        {
            switch (effect)
            {
                case SleepEffect.Prepare:
                    sleeping = true; WriteRuntime(true); Prepare(epoch); break;
                case SleepEffect.ProbeReason: Probe(); break;
                case SleepEffect.RestoreFingerprint:
                    TryRestore(true); break;
                case SleepEffect.Restore:
                    probe?.Cancel(); probeCycle = -1; TryRestore(false); break;
                case SleepEffect.Resleep:
                    if (disposed || epoch != Volatile.Read(ref invalidation)) break;
                    // Broadcast has a bounded native timeout. Do not force S4 or
                    // ignore the Windows power manager's decision to stay awake.
                    Program.Log("enhanced-unattended-resleep", new { policy.Cycle, policy.Reason });
                    if (SendMessageTimeoutW(0xffff, 0x112, 0xf170, 2, 2, 1000, out _) == 0)
                        Program.Log("enhanced-resleep-not-confirmed", Marshal.GetLastWin32Error());
                    break;
            }
        }
        sleeping = policy.Sleeping;
        if (wasSleeping && !sleeping) WakeCompleted?.Invoke();
        if (signal == SleepSignal.Tick)
        {
            if (!sleeping) TryRestore(false);
            else if (policy.WakeDetected || policy.Automatic) TryRestore(true);
            // If a guard recovered a hung host, never recreate the transaction
            // from stale in-memory state on its next tick.
            if (sleeping && journal.Read() == null) ProcessSignal(SleepSignal.Cancel);
            else if (sleeping && journal.Read() is { } r && resources.ActivePlan() != r.SleepScheme)
            {
                ProcessSignal(SleepSignal.Cancel);
                error = "电源方案已由其他程序更改，本次增强睡眠已结束";
            }
        }
    }
    private void Prepare(long epoch)
    {
        var deadline = Stopwatch.StartNew();
        bool Cancelled() => disposed || epoch != Volatile.Read(ref invalidation) || deadline.Elapsed > TimeSpan.FromSeconds(2) || guard?.Healthy != true;
        try
        {
            using var owner = Process.GetCurrentProcess();
            var record = new SleepRecovery(Guid.NewGuid().ToString("N"), owner.Id, owner.StartTime.ToUniversalTime().Ticks, owner.SessionId,
                resources.ActivePlan(), Guid.NewGuid(), null, []);
            var candidate = DateTime.UtcNow - gameSeen < TimeSpan.FromSeconds(30) ? game : null;
            SleepPreparation.Run(journal, resources, record, options, candidate, Cancelled);
            Program.Log("enhanced-prepared", new { policy.Cycle, record.OriginalScheme, record.SleepScheme, games = journal.Read()?.Games.Length });
            error = null;
        }
        catch (Exception ex)
        {
            Program.Log("enhanced-prepare-recovered", ex.Message); TryRestore(false);
            error = ex is OperationCanceledException ? null : ex.Message;
        }
    }
    private void Probe()
    {
        if (probeCycle == policy.Cycle) return;
        probe?.Cancel(); probe?.Dispose(); probe = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        long cycle = probeCycle = policy.Cycle; var token = probe.Token;
        probeStarted = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                int? reason = await WakeReasonReader.ReadAsync(DateTime.UtcNow, token).ConfigureAwait(false);
                await Enqueue(() =>
                {
                    if (!token.IsCancellationRequested) CompleteProbe(cycle, reason);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Program.Log("enhanced-wake-probe-error", ex.Message);
                await Observe(Enqueue(() => CompleteProbe(cycle, null)));
            }
        });
    }
    private void CompleteProbe(long cycle, int? reason)
    {
        if (cycle != policy.Cycle) return;
        bool wasSleeping = sleeping;
        Program.Log("enhanced-wake-reason", new { cycle, reason });
        foreach (var effect in policy.Apply(SleepSignal.Probe, DateTime.UtcNow, reason))
            if (effect == SleepEffect.Restore) TryRestore(false);
        sleeping = policy.Sleeping;
        if (wasSleeping && !sleeping) WakeCompleted?.Invoke();
    }
    private void TryRestore(bool fingerprintOnly)
    {
        if (fingerprintOnly)
        {
            if (DateTime.UtcNow - fingerprintRecoveryAt < TimeSpan.FromSeconds(2)) return;
            fingerprintRecoveryAt = DateTime.UtcNow;
        }
        try
        {
            bool pending = journal.Read() != null;
            journal.Recover(false, fingerprintOnly);
            if (!fingerprintOnly && pending) error = null;
        }
        catch (Exception ex) { error = ex.Message; Program.Log("enhanced-recovery-pending", ex.Message); }
        finally { if (!fingerprintOnly) WriteRuntime(false); }
    }
    private void WriteRuntime(bool value)
    {
        if (runtimeSleeping == value) return;
        using var owner = Process.GetCurrentProcess();
        SleepJournal.Atomic(Path.Combine(state, "enhanced-runtime.json"), new
        { sleeping = value, ownerPid = owner.Id, ownerStarted = owner.StartTime.ToUniversalTime().Ticks });
        if (value) awake.Reset(); else awake.Set();
        runtimeSleeping = value;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Interlocked.Increment(ref invalidation); stop.Cancel();
        notifications?.Dispose(); notifications = null;
        queue.Writer.TryWrite(() => { ProcessSignal(SleepSignal.Cancel); return Task.CompletedTask; });
        queue.Writer.TryComplete(); worker.GetAwaiter().GetResult();
        try { journal.Recover(false); WriteRuntime(false); }
        finally { guard?.Dispose(); guard = null; probe?.Dispose(); stop.Dispose(); awake.Dispose(); }
    }
}
