using System.Globalization;

namespace ClawSleepAnimation;

internal sealed record PowerButtonEvent(long TimestampQpc, long ReceivedQpc);

internal static class PowerButtonMatcher
{
    // Exact v1 signature observed on this Claw with PBUTTONACTION=0. Other
    // power actions, user input, idle policy and wake events must not match.
    public static bool Matches(int version, IReadOnlyDictionary<string, object?> fields)
    {
        if (version != 1) return false;
        foreach (var pair in new Dictionary<string, uint>
        {
            ["Reason"] = 0, ["TriggerFlags"] = 131, ["UserNotify"] = 0,
            ["PowerAction"] = 0, ["PowerActionFlags"] = 0, ["PowerActionEventCode"] = 0,
            ["MinState"] = 4, ["SubstitutionPolicy"] = 1,
            ["LocalPowerAction"] = 0, ["LocalPowerActionFlags"] = 0, ["LocalPowerActionEventCode"] = 0
        })
            if (!fields.TryGetValue(pair.Key, out object? raw) ||
                !uint.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out uint value) || value != pair.Value) return false;
        return fields.TryGetValue("Disabled", out object? disabled) && bool.TryParse(disabled?.ToString(), out bool isDisabled) && !isDisabled &&
            fields.TryGetValue("RequesterName", out object? name) && Equals(name?.ToString(), "System");
    }
}

internal sealed class PowerButtonGate
{
    private readonly object gate = new();
    private readonly long frequency;
    private long notBefore = long.MaxValue;
    private bool busy;
    public PowerButtonGate(long frequency) => this.frequency = frequency;
    public void Arm(long now) { lock (gate) notBefore = now; }
    public void Disarm() { lock (gate) notBefore = long.MaxValue; }
    public void Resume(long now) { lock (gate) notBefore = now + 3 * frequency; }
    public bool TryBegin(PowerButtonEvent signal)
    {
        lock (gate)
        {
            if (busy || signal.TimestampQpc < notBefore || signal.TimestampQpc > signal.ReceivedQpc + frequency / 10 ||
                signal.ReceivedQpc - signal.TimestampQpc > 2 * frequency) return false;
            busy = true; return true;
        }
    }
    public void End(long now) { lock (gate) { busy = false; notBefore = now + 3 * frequency; } }
}
