using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClawSleepAnimation;

internal static class StartupLoading
{
    private const string Home = "ClawToolbox.Library_ba3r68zr466za!App";
    private const string Family = "ClawToolbox.Library_ba3r68zr466za";
    private const string ReadyProperty = "ClawToolbox.Library.Ready.v1";
    private delegate bool EnumWindow(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(nint process, ref uint length, StringBuilder? family);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);

    private static object? Setting(string name)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration");
            if (key?.GetValue(name) is object value) return value;
        }
        return null;
    }
    internal static bool Enabled()
    {
        if (Setting("GamingHomeApp") as string != Home || Setting("StartupToGamingHome") is not int enabled || enabled != 1) return false;
        string prefs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", Family, "LocalCache", "ui.ini");
        return !File.Exists(prefs) || !File.ReadLines(prefs).Any(line => line.Trim().Equals("fseSteam=True", StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsOwnLibrary(nint hwnd)
    {
        var title = new StringBuilder(100); GetWindowText(hwnd, title, title.Capacity);
        if (title.ToString() != "Claw 游戏库") return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        nint process = OpenProcess(0x1000, false, pid);
        if (process == 0) return false;
        try
        {
            uint length = 0;
            if (GetPackageFamilyName(process, ref length, null) != 122 || length > 512) return false;
            var family = new StringBuilder((int)length);
            return GetPackageFamilyName(process, ref length, family) == 0 && family.ToString() == Family;
        }
        finally { CloseHandle(process); }
    }
    internal static nint FindReadyWindow()
    {
        nint result = 0;
        EnumWindows((hwnd, _) =>
        {
            if (GetProp(hwnd, ReadyProperty) == 0 || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) != 0 || cloaked != 0 || !IsOwnLibrary(hwnd)) return true;
            result = hwnd; return false;
        }, 0);
        return result;
    }
    public static async Task RunAsync(bool preview)
    {
        using var single = new Mutex(false, @"Local\ClawToolbox.StartupLoading");
        if (!single.WaitOne(0)) { Program.Log("startup-overlay-already-running"); return; }
        try
        {
            if (!preview && !Enabled()) { Program.Log("startup-overlay-skipped-home-choice"); return; }
            if (!preview && FindReadyWindow() != 0) { Program.Log("startup-overlay-skipped-already-ready"); return; }
            Program.Log("startup-overlay-start", new { preview });
            await SleepOverlay.PlayLoadingAsync(async (overlay, token) =>
            {
                if (preview) { await Task.Delay(3500, token); return "preview"; }
                await Task.Run(() =>
                {
                    using var activation = Process.Start(new ProcessStartInfo("claw-library://library") { UseShellExecute = true });
                }, token).WaitAsync(token);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    nint library = FindReadyWindow();
                    if (library != 0)
                    {
                        bool foreground = SetForegroundWindow(library);
                        SetWindowPos(overlay, -1, 0, 0, 0, 0, 0x13);
                        Program.Log("startup-library-ready", new { hwnd = (long)library, foreground });
                        return "library-ready";
                    }
                    // Stay above the library's initial activation without fighting
                    // for keyboard focus or touching the Windows secure desktop.
                    SetWindowPos(overlay, -1, 0, 0, 0, 0, 0x13);
                    await Task.Delay(150, token);
                }
            });
        }
        finally { single.ReleaseMutex(); }
    }
}
