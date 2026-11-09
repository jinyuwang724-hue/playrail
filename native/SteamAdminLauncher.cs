using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

// Playhub starts Steam with UseShellExecute=false, bypassing the user's
// RUNASADMIN compatibility preference. Its custom-app stage uses the shell.
// This launcher has an administrator manifest and starts Steam before that stage ends.
internal static class SteamAdminLauncher {
    [STAThread] static int Main() {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawButtonRouter");
        Directory.CreateDirectory(root);
        try {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Steam launcher was not elevated.");
            // Never replace an existing Steam session or interrupt a running game.
            var existing=Process.GetProcessesByName("steam");
            foreach(var process in existing)process.Dispose();
            if(existing.Length>0)return 0;
            string steam = SteamPaths.Executable();
            if (!File.Exists(steam)) throw new FileNotFoundException("Steam executable was not found.", steam);
            using (var process = Process.Start(new ProcessStartInfo(steam, "-gamepadui") {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(steam)
            })) {
                File.AppendAllText(Path.Combine(root, "steam-launcher.log"), DateTime.UtcNow.ToString("o") + " Started elevated Steam PID=" + process.Id + Environment.NewLine);
            }
            return 0;
        } catch (Exception error) {
            File.AppendAllText(Path.Combine(root, "steam-launcher.log"), DateTime.UtcNow.ToString("o") + " " + error.Message + Environment.NewLine);
            return 1;
        }
    }
}
