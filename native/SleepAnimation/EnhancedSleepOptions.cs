using System.IO;
using System.Text.Json;

namespace ClawSleepAnimation;

internal sealed record EnhancedSleepOptions(bool Enabled = false, bool SuspendGames = true, bool Fingerprint = true)
{
    public const uint HibernateSeconds = 48 * 60 * 60;
    public static EnhancedSleepOptions Load(string state)
    {
        string path = Path.Combine(state, "enhanced-settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<EnhancedSleepOptions>(File.ReadAllText(path)) ?? throw new InvalidDataException("增强睡眠设置损坏") : new();
    }
    public void Save(string state) => SleepJournal.Atomic(Path.Combine(state, "enhanced-settings.json"), this);
}
