using System.Diagnostics;

namespace ClawLibrary.Data;

/// <summary>交给平台客户端处理登录和授权；这里只启动客户端，不读取登录令牌。</summary>
internal static class PlatformClients
{
    public static bool SteamRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        try { return processes.Length > 0; }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    public static string StartSteamInTray()
    {
        if (SteamRunning()) return "Steam 已在运行";
        var root = Steam.Root();
        if (root == null || !File.Exists(Path.Combine(root, "steam.exe")))
            throw new InvalidOperationException("未找到 Steam，请先安装并登录 Steam");
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "steam.exe"))
        {
            Arguments = "-silent", UseShellExecute = false, WorkingDirectory = root,
        });
        if (process == null) throw new InvalidOperationException("Steam 没能启动");
        Log.Info("Steam tray startup requested");
        return "已请求 Steam 在托盘启动，首次使用请在 Steam 中登录";
    }

    public static void Open(string uri)
    {
        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        if (process == null) throw new InvalidOperationException("没有找到可打开此平台的应用");
    }
}
