using System.IO;
using System.Text.Json;

namespace ClawSleepAnimation;

internal sealed record AnimationOptions(bool SuspendEnabled = true, bool ResumeEnabled = true)
{
    public static AnimationOptions Load(string state)
    {
        string path = Path.Combine(state, "settings.json");
        if (!File.Exists(path)) return new();
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return new(Flag(json.RootElement, "suspendEnabled"), Flag(json.RootElement, "resumeEnabled"));
    }
    internal static bool Flag(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("开关值必须为开或关");
        return value.GetBoolean();
    }
    public void Save(string state)
    {
        string path = Path.Combine(state, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { suspendEnabled = SuspendEnabled, resumeEnabled = ResumeEnabled }));
        File.Move(path + ".tmp", path, true);
    }
}
