// 竖条里的系统信息：电池、当前 Windows 用户（显示名与头像）。都是 Win32 直接读，读不到就给空值，不抛出。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClawLibrary.Data;

public readonly record struct BatteryState(bool HasBattery, int Percent, bool Charging, bool OnAc);

public static class SystemInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserNameEx(int format, StringBuilder buffer, ref uint size);

    /// <summary>录屏自测（--state=recstatus）临时换成指定的电池状态，核对充电 / 插电 / 低电量的图标；平时 null。</summary>
    public static BatteryState? Override;

    public static BatteryState Battery()
    {
        if (Override is { } o) return o;
        try
        {
            if (!GetSystemPowerStatus(out var s)) return default;
            bool has = (s.BatteryFlag & 128) == 0 && s.BatteryLifePercent <= 100;
            return new BatteryState(has, has ? s.BatteryLifePercent : 0, (s.BatteryFlag & 8) != 0, s.ACLineStatus == 1);
        }
        catch { return default; }
    }

    /// <summary>显示名（微软账户 / 本地账户的全名）；取不到用登录名。</summary>
    public static string UserName()
    {
        try
        {
            var b = new StringBuilder(256);
            uint n = 256;
            if (GetUserNameEx(3, b, ref n) && b.Length > 0) return b.ToString();   // NameDisplay
        }
        catch { }
        return Environment.UserName;
    }

    /// <summary>账户头像（设置里换过的那张），没有就是 null。</summary>
    public static string? AvatarPath()
    {
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return null;
            using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
            foreach (var name in new[] { "Image192", "Image208", "Image240", "Image96" })
                if (key?.GetValue(name) is string p && File.Exists(p)) return p;
        }
        catch (Exception ex) { Log.Warn($"avatar: {ex.Message}"); }
        return null;
    }

    /// <summary>用外壳打开（文件管理器、ms-settings: 链接）。</summary>
    public static void Open(string target, string? args = null)
    {
        try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"open {target}: {ex.Message}"); }
    }

    // ---------------- 控制中心状态栏：网络、蓝牙 ----------------

    /// <summary>当前连网方式：Wi-Fi（信号 0–5 格）/ 有线 / 没有网络。</summary>
    public readonly record struct NetState(bool Online, bool Wlan, int Bars, bool Wired);

    public static NetState Network()
    {
        try
        {
            // 不能只看「上网用的那个」连接：代理的 TUN 虚拟网卡开着时它才是上网连接，Wi-Fi 反而不是。所以先找连着的 Wi-Fi。
            var net = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            bool online = net != null && net.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
            foreach (var p in Windows.Networking.Connectivity.NetworkInformation.GetConnectionProfiles())
                if (p.IsWlanConnectionProfile && p.GetNetworkConnectivityLevel() != Windows.Networking.Connectivity.NetworkConnectivityLevel.None)
                    return new NetState(online || p.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess, true, p.GetSignalBars() ?? 0, false);
            return net != null ? new NetState(online, false, 0, true) : default;
        }
        catch (Exception ex) { Log.Warn($"network: {ex.Message}"); return default; }
    }

    /// <summary>蓝牙开没开；没有蓝牙 / 读不到 = null。</summary>
    public static async Task<bool?> BluetoothOn()
    {
        try
        {
            foreach (var r in await Windows.Devices.Radios.Radio.GetRadiosAsync())
                if (r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth) return r.State == Windows.Devices.Radios.RadioState.On;
        }
        catch (Exception ex) { Log.Warn($"bluetooth: {ex.Message}"); }
        return null;
    }

    // ---------------- 电源：睡眠 / 休眠 / 重启 / 关机 ----------------

    public readonly record struct PowerCapabilities(bool S3, bool Hibernate, bool ModernStandby);

    [DllImport("powrprof.dll")]
    private static extern unsafe byte GetPwrCapabilities(byte* caps);

    /// <summary>SYSTEM_POWER_CAPABILITIES 前面一串 BOOLEAN：[5] S3、[6] S4、[8] 休眠文件、[20] AoAc（现代待机）。</summary>
    public static unsafe PowerCapabilities PowerCaps()
    {
        try
        {
            byte* c = stackalloc byte[256];
            if (GetPwrCapabilities(c) == 0) return default;
            return new PowerCapabilities(c[5] != 0, c[6] != 0 && c[8] != 0, c[20] != 0);
        }
        catch { return default; }
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    /// <summary>前台窗口所属进程的名字（不带 .exe）；读不到给 null。</summary>
    public static string? ForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out uint pid) == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    [DllImport("powrprof.dll")]
    private static extern byte SetSuspendState(byte hibernate, byte force, byte wakeupEventsDisabled);

    /// <summary>
    /// 睡眠：现代待机的机器（掌机）没有 S3，开始菜单的「睡眠」就是关屏进入 S0 低电量待机，这里同样发关屏；
    /// 有 S3 的机器才调 SetSuspendState。
    /// </summary>
    public static void Sleep(nint hwnd)
    {
        var caps = PowerCaps();
        Log.Info($"power: sleep ({caps})");
        if (caps.S3 && !caps.ModernStandby && EnableShutdownPrivilege()) { SetSuspendState(0, 0, 0); return; }
        PostMessage(hwnd, 0x0112, 0xF170, 2);   // WM_SYSCOMMAND / SC_MONITORPOWER / 关
    }

    public static void Hibernate() => RunShutdown("/h");
    public static void Restart() => RunShutdown("/r /t 0");
    public static void Shutdown() => RunShutdown("/s /t 0");

    private static void RunShutdown(string args)
    {
        Log.Info($"power: shutdown.exe {args}");
        try { Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { Log.Warn($"shutdown {args}: {ex.Message}"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges state, uint len, nint prev, nint retLen);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint h);

    private static bool EnableShutdownPrivilege()
    {
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out var token)) return false;   // ADJUST_PRIVILEGES | QUERY
            try
            {
                if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return false;
                var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };   // SE_PRIVILEGE_ENABLED
                return AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0);
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }

    // ---------------- 默认浏览器 ----------------

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);

    /// <summary>打开默认浏览器（它自己的主页）；找不到默认浏览器的程序就开一个网页链接。</summary>
    public static void OpenBrowser()
    {
        string? exe = null;
        try
        {
            uint n = 1024;
            var sb = new StringBuilder(1024);
            if (AssocQueryString(0, 2, "http", "open", sb, ref n) == 0) exe = sb.ToString();   // ASSOCSTR_EXECUTABLE
        }
        catch { }
        Log.Info($"browser: {exe ?? "(none)"}");
        if (exe != null && File.Exists(exe)) Open(exe);
        else Open("https://www.bing.com");
    }
}
