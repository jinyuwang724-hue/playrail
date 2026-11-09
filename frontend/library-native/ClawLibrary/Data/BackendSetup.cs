using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace ClawLibrary.Data;

internal static class BackendSetup
{
    internal static string Home { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawToolbox");

    internal static string StatusPath(string run) => Path.Combine(Home, "setup", $"status-{run}.json");

    internal static Process Start(string run)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "Payload", "Install-Components.ps1");
        using var identity = WindowsIdentity.GetCurrent();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
            "-File", script, "-AgentHome", Home, "-User", identity.Name, "-UserSid", identity.User!.Value, "-Run", run })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start backend setup.");
    }

    internal static string? InstalledRelease()
    {
        try
        {
            var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(Home, "setup", "backend.json"))) as JsonObject;
            return receipt?["release"] is JsonValue version && version.TryGetValue<string>(out var text) ? text : null;
        }
        catch (IOException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
