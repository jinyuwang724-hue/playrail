using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClawLibrary.Data;

internal static class ToolboxActivation
{
    public static bool IsRoute(string route) => route is "library" or "control-center"
        or "restore-library" or "restore-control-center" or "minimize-library" or "minimize-control-center";
    public static string Target(string route) => route.StartsWith("restore-", StringComparison.Ordinal) ? route[8..]
        : route.StartsWith("minimize-", StringComparison.Ordinal) ? route[9..] : route;
    public static bool ShouldMinimize(string route, bool? xboxFse, bool? steamBigPicture) =>
        route is "minimize-library" or "minimize-control-center";
    public static bool PreservePage(string route) => route.StartsWith("restore-", StringComparison.Ordinal);
    public static nint ReturnWindow(string query)
    {
        foreach (var part in query.TrimStart('?').Split('&'))
            if (part.StartsWith("returnWindow=", StringComparison.Ordinal) &&
                long.TryParse(part[13..], out long value) && value > 0) return (nint)value;
        return 0;
    }
}

internal static class ToolboxWindow
{
    /// <summary>
    /// 全屏体验主页选了「Steam 大屏幕」时：游戏库被当主页打开（普通启动、全屏体验里），就打开 Steam 大屏幕、自己收起来。
    /// 用 claw-library:// 打开（Claw 键、控制中心）不算，照常显示，所以游戏库和设置还能进得去。
    /// </summary>
    public static bool SteamHomeRedirect(string route, nint window)
    {
        if (route != "activate" || XboxFseActive() != true) return false;
        if (!ClawLibrary.Stage.UiPrefs.Load().FseSteam) return false;
        try { Process.Start(new ProcessStartInfo("steam://open/bigpicture") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"steam home: {ex.Message}"); return false; }
        Minimize(window);
        Log.Info("home → Steam Big Picture");
        return true;
    }

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsGamingFullScreenExperienceActive();
    public static bool? XboxFseActive()
    {
        try { return IsGamingFullScreenExperienceActive(); }
        catch { return null; }
    }

    private delegate bool EnumWindow(nint window, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindow callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int length);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern int ShowWindow(nint window, int command);
    public static void Minimize(nint window) => ShowWindow(window, 6);
    public static void Restore(nint window) { if (IsIconic(window)) ShowWindow(window, 9); }

    public static bool? SteamBigPictureOpen()
    {
        try
        {
            bool found = false;
            bool completed = EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                var title = new StringBuilder(256);
                GetWindowText(window, title, title.Capacity);
                if (title.ToString() is not ("Steam 大屏幕模式" or "Steam Big Picture Mode" or "Steam Big Picture")) return true;
                GetWindowThreadProcessId(window, out uint pid);
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    if (process.ProcessName is not ("steamwebhelper" or "steam")) return true;
                    found = true;
                    return false;
                }
                catch { return true; }
            }, 0);
            return found ? true : completed ? false : null;
        }
        catch { return null; }
    }
}
