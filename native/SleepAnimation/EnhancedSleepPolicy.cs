namespace ClawSleepAnimation;

internal enum SleepSignal { DisplayOff, DisplayOn, Suspend, ResumeAutomatic, ResumeInteractive, Locked, Unlocked, LidOpened, UserPresent, Probe, Tick, Cancel }
internal enum SleepEffect { Prepare, RestoreFingerprint, ProbeReason, Restore, Resleep }

// One consumer owns this state. Times are supplied by the host so races and
// delayed notifications can be exercised without changing the machine's power.
internal sealed class EnhancedSleepPolicy
{
    public long Cycle { get; private set; }
    public bool Sleeping { get; private set; }
    public bool DisplayOn { get; private set; } = true;
    public bool Locked { get; private set; }
    public bool LockedAtEntry { get; private set; }
    public bool Unlocked { get; private set; }
    public bool WakeDetected { get; private set; }
    public bool UserProof { get; private set; }
    public bool ObservedSuspend { get; private set; }
    public int? Reason { get; private set; }
    public bool ReasonReady { get; private set; }
    public bool Automatic { get; private set; }
    private DateTime? resleepAt;
    private DateTime lastResleep = DateTime.MinValue;

    public static bool IsPersonReason(int? reason) => reason is 1 or 14 or 15 or 25 or 44;
    public static bool IsUnattendedReason(int? reason) => reason.HasValue && !IsPersonReason(reason) && reason is not (0 or 20 or 29);
    public static bool Withhold(int? reason, bool unlocked, bool proof) => IsUnattendedReason(reason) && !unlocked && !proof;
    public bool IsUnattended => IsUnattendedReason(Reason) ? !Unlocked && !UserProof :
        WakeDetected ? false : LockedAtEntry ? !Unlocked : !DisplayOn && Automatic;
    public void ResetReason() { Reason = null; ReasonReady = false; }
    public static bool EntryIsCurrent(SleepSignal signal, long queuedEpoch, long currentEpoch) =>
        signal is not (SleepSignal.DisplayOff or SleepSignal.Suspend) || queuedEpoch == currentEpoch;

    public IReadOnlyList<SleepEffect> Apply(SleepSignal signal, DateTime now, int? reason = null)
    {
        var effects = new List<SleepEffect>();
        if (signal == SleepSignal.Locked) Locked = true;
        if (signal == SleepSignal.Unlocked) { Locked = false; Unlocked = UserProof = true; }
        // The service treats presence after an observed wake as session proof;
        // an isolated presence notification cannot create a wake by itself.
        if (signal == SleepSignal.UserPresent && WakeDetected) Unlocked = true;
        if (signal == SleepSignal.LidOpened) UserProof = true;
        if (signal == SleepSignal.DisplayOff) DisplayOn = false;
        if (signal == SleepSignal.DisplayOn) DisplayOn = true;
        if (signal is SleepSignal.DisplayOff or SleepSignal.Suspend && !Sleeping)
        {
            Cycle++; Sleeping = true; LockedAtEntry = Locked;
            Unlocked = UserProof = WakeDetected = ReasonReady = Automatic = ObservedSuspend = false;
            Reason = null; resleepAt = null; effects.Add(SleepEffect.Prepare);
        }
        if (signal == SleepSignal.Suspend) ObservedSuspend = true;
        if (!Sleeping) return effects;
        if (signal == SleepSignal.Cancel)
        {
            Sleeping = false; Cycle++; resleepAt = null; effects.Add(SleepEffect.Restore); return effects;
        }
        if (signal is SleepSignal.ResumeAutomatic or SleepSignal.ResumeInteractive or SleepSignal.DisplayOn or SleepSignal.Unlocked or SleepSignal.LidOpened or SleepSignal.UserPresent)
        {
            effects.Add(SleepEffect.RestoreFingerprint); // Authentication must work before unlock.
            if (signal == SleepSignal.ResumeAutomatic) Automatic = true;
            else if (signal is SleepSignal.ResumeInteractive or SleepSignal.Unlocked || signal == SleepSignal.DisplayOn && !ObservedSuspend) WakeDetected = true;
            if (!ReasonReady && !UserProof) effects.Add(SleepEffect.ProbeReason);
        }
        if (signal == SleepSignal.Probe) { Reason = reason; ReasonReady = true; }
        if (UserProof || Unlocked) resleepAt = null;
        bool withholding = Withhold(Reason, Unlocked, UserProof);
        if (WakeDetected && DisplayOn && (!LockedAtEntry || Unlocked) && (ReasonReady || UserProof) && !withholding)
        {
            Sleeping = false; Cycle++; resleepAt = null; effects.Add(SleepEffect.Restore); return effects;
        }
        if (!UserProof && !Unlocked && resleepAt == null && (withholding || Automatic))
            resleepAt = now.AddSeconds(withholding ? 2 : 20);
        if (withholding && resleepAt > now.AddSeconds(2)) resleepAt = now.AddSeconds(2);
        if (signal == SleepSignal.Tick && resleepAt <= now)
        {
            resleepAt = null;
            if (IsUnattended && now - lastResleep >= TimeSpan.FromSeconds(30))
            { lastResleep = now; effects.Add(SleepEffect.Resleep); }
        }
        return effects;
    }
}
