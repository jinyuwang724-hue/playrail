namespace ClawLibrary.Engine;

public enum StartupReveal { Hold, Ready, Timeout }

// Time spent behind Winlogon must never consume the visible loading animation.
public sealed class StartupVisibilityGate
{
    private long? visibleSince;
    public StartupReveal Observe(long now, bool visibleOnUserDesktop, bool sceneReady)
    {
        if (!visibleOnUserDesktop) { visibleSince = null; return StartupReveal.Hold; }
        visibleSince ??= now;
        long visibleFor = Math.Max(0, now - visibleSince.Value);
        if (visibleFor >= 45000) return StartupReveal.Timeout;
        return sceneReady && visibleFor >= 1100 ? StartupReveal.Ready : StartupReveal.Hold;
    }
}
