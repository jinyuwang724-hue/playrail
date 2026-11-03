namespace ClawSleepAnimation;

// The policy and transaction tests run through these same boundaries without
// suspending a user process, disabling hardware, or activating a power plan.
internal interface ISleepResources
{
    Guid ActivePlan();
    void CreatePlan(Guid original, Guid owned);
    void ActivatePlan(Guid plan);
    void RestorePlan(Guid original, Guid owned);
    string? FingerprintCandidate();
    void DisableFingerprint(string id);
    void RestoreFingerprint(string id);
    void SuspendGame(FrozenGame game, SleepJournal journal, Func<bool> cancelled);
    void ResumeGame(FrozenGame game);
}

internal sealed class NativeSleepResources : ISleepResources
{
    public Guid ActivePlan() => PowerPolicy.ActiveScheme();
    public void CreatePlan(Guid original, Guid owned) => SleepPowerPlan.Create(original, owned);
    public void ActivatePlan(Guid plan) => SleepPowerPlan.Activate(plan);
    public void RestorePlan(Guid original, Guid owned) => SleepPowerPlan.Restore(original, owned);
    public string? FingerprintCandidate() => FingerprintSleep.Candidate();
    public void DisableFingerprint(string id) => FingerprintSleep.Disable(id);
    public void RestoreFingerprint(string id) => FingerprintSleep.Restore(id);
    public void SuspendGame(FrozenGame game, SleepJournal journal, Func<bool> cancelled) => GameSleep.Suspend(game, journal, cancelled);
    public void ResumeGame(FrozenGame game) => GameSleep.Resume(game);
}

internal static class SleepPreparation
{
    public static void Run(SleepJournal journal, ISleepResources resources, SleepRecovery record, EnhancedSleepOptions options, FrozenGame? game, Func<bool> cancelled)
    {
        void Check()
        {
            if (cancelled()) throw new OperationCanceledException("睡眠准备已过期");
        }
        journal.Locked(() =>
        {
            if (journal.Read() != null) throw new InvalidOperationException("上次增强睡眠尚未恢复");
            Check(); journal.Write(record);
            resources.CreatePlan(record.OriginalScheme, record.SleepScheme);
            Check();
            if (resources.ActivePlan() != record.OriginalScheme) throw new OperationCanceledException("电源方案已变化");
            resources.ActivatePlan(record.SleepScheme);
            Check();
            if (options.SuspendGames && game != null)
                try { resources.SuspendGame(game, journal, cancelled); }
                catch (Exception ex)
                {
                    if (journal.Read()?.Games.Length > 0) throw new InvalidOperationException("游戏暂停失败，正在恢复", ex);
                    Program.Log("enhanced-game-skipped", ex.Message);
                }
            Check();
            if (options.Fingerprint)
            {
                string? candidate = resources.FingerprintCandidate();
                Check();
                if (candidate != null)
                {
                    var current = journal.Read()! with { Fingerprint = candidate };
                    journal.Write(current); Check(); resources.DisableFingerprint(candidate);
                }
            }
            Check(); return true;
        });
    }
}
