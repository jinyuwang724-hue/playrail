namespace ClawSleepAnimation;

// Event timestamps matter: an ETW buffer from before hibernation must never
// cause a second hibernation when it is finally delivered after resume.
internal sealed class ButtonGate
{
    private readonly object gate = new();
    private DateTime notBefore = DateTime.MaxValue;
    private bool busy;
    public void Arm(DateTime now) { lock (gate) notBefore = now; }
    public void Resume(DateTime now) { lock (gate) notBefore = now.AddSeconds(3); }
    public bool TryBegin(ButtonSignal signal, DateTime now)
    {
        lock (gate)
        {
            if (busy || signal.EventId != 24 || signal.Mask != 1 ||
                signal.EventTime < notBefore || signal.EventTime > now.AddMilliseconds(100) ||
                now - signal.EventTime > TimeSpan.FromSeconds(2)) return false;
            busy = true;
            return true;
        }
    }
    public void End(DateTime now) { lock (gate) { busy = false; notBefore = now.AddSeconds(2); } }
}
