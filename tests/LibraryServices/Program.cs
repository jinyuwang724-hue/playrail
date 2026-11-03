using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawLibrary;
using ClawLibrary.Data;
using ClawLibrary.Stage;

internal static class Program
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    [STAThread]
    private static void Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "ClawLibrary-services-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        App.CacheDir = temp;
        try
        {
            Check(UiPrefs.Load().PreloadSteam && UiPrefs.Load().BackgroundRun && UiPrefs.Load().SourceXbox, "new settings have defaults");
            File.WriteAllText(Path.Combine(temp, "ui.ini"), "sourceSteam=False\nboot=Settings\nunknown=value\n");
            var prefs = UiPrefs.Load();
            Check(!prefs.SourceSteam && prefs.PreloadSteam && prefs.Boot == BootTarget.Settings, "older preferences migrate without losing boot/source");
            (prefs with { PreloadSteam = false, BackgroundRun = false, SourceXbox = false }).Save();
            var saved = UiPrefs.Load();
            Check(!saved.PreloadSteam && !saved.BackgroundRun && !saved.SourceXbox && !saved.SourceSteam, "all service switches survive restart");
            File.AppendAllText(Path.Combine(temp, "ui.ini"), "backgroundRun=invalid\npreloadSteam=invalid\n");
            Check(!UiPrefs.Load().BackgroundRun && !UiPrefs.Load().PreloadSteam, "malformed values do not overwrite saved preferences");

            Check(XboxGames.HasGameDeclaration("<Package xmlns:uap='urn:test'><uap:Extension Category='windows.gameExplorer'/></Package>"), "legacy game marker handles XML namespaces");
            Check(!XboxGames.HasGameDeclaration("<Package><Extension Category='windows.protocol'/></Package>"), "ordinary Store apps are not imported as games");
            Library.XboxLibrary = new() { new("Test.Game_abc!Game", "Xbox Test", "Test.Game_abc") };
            string seed = Path.Combine(temp, "seed.json");
            File.WriteAllText(seed, "{\"games\":[]}");
            Library.IncludeSteam = false;
            var lib = Library.Load(seed, new(), _ => null, owned: new() { new("10", "Steam Test", "game", null, null) });
            Check(lib.Games.Count == 1 && lib.Games[0].XboxAumid == "Test.Game_abc!Game", "Xbox launch identity survives library merge with Steam disabled");
            Check(lib.HomeTabs.Single(t => t.Id == "xbox").Items().Count == 1 && lib.HomeTabs.Single(t => t.Id == "local").Items().Count == 0, "Xbox and local tabs remain separate");
            Library.XboxLibrary.Clear();
            Check(Library.Load(seed, new(), _ => null).Games.Count == 0, "disabling Xbox source removes its games");

            var hwnd = CreateWindowEx(0, "STATIC", "Claw tray smoke test", 0, 0, 0, 1, 1, 0, 0, 0, 0);
            Check(hwnd != 0, "create invisible tray test window");
            int restores = 0, exits = 0;
            try
            {
                using var tray = new LibraryTray(hwnd, () => restores++, () => exits++);
                Check(tray.EnsureIcon() && tray.EnsureIcon(), "tray add and repeated update succeed");
                SendMessage(hwnd, 0x8000 + 81, 1, 0x203);
                Check(restores == 1 && exits == 0, "double-click restores without exiting");
                SendMessage(hwnd, 0x8000 + 81, 1, 0x405);
                Check(restores == 2, "keyboard selection restores");
                tray.Dispose();
                Check(!tray.EnsureIcon(), "disposed tray never recreates an icon");
            }
            finally { DestroyWindow(hwnd); }

            var timer = Stopwatch.StartNew();
            var xbox = Task.Run(XboxGames.ReadAsync).GetAwaiter().GetResult();
            Check(xbox.Error == null, "current-user package enumeration succeeds");
            Console.WriteLine($"INFO Xbox client={xbox.ClientInstalled} games={xbox.Games.Count} unreadable={xbox.Unreadable} scan={timer.ElapsedMilliseconds}ms");
            Console.WriteLine("INFO Steam installed=" + (Steam.Root() != null) + " running=" + PlatformClients.SteamRunning());
        }
        finally { Directory.Delete(temp, true); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nuint wp, nint lp);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
}
