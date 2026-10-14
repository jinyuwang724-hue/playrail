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

    public static BatteryState Battery()
    {
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
}
