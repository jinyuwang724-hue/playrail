namespace ClawSleepAnimation;

// S0 resume can leave the UI dispatcher paused while the guard and display
// notification wake first. Allow the UI to recover before treating its old
// heartbeat as a hang; a dead owner is still handled immediately by the guard.
internal sealed class HeartbeatWatchdog
{
    private DateTime last;
    private DateTime graceUntil = DateTime.MinValue;
    internal static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);
    public HeartbeatWatchdog(DateTime now) => last = now;
    public bool IsUnresponsive(DateTime now, bool displayOn, DateTime displayOnAt, DateTime heartbeatAt)
    {
        if (now - last > TimeSpan.FromSeconds(3)) graceUntil = now + ResumeGrace;
        last = now;
        return displayOn && now > displayOnAt + ResumeGrace && now > graceUntil && now - heartbeatAt > HeartbeatTimeout;
    }
}
