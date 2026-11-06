// 游戏模式（保留 explorer 版）下的系统浮层：开始菜单、搜索。底边上滑、Ctrl+Esc 之类还是能把它们叫出来，
// 而且它们一开着就攥着前台，别的窗口（切换器要切过去的目标）拿不到前台 —— 切换失败、留下空屏。
// 这里认出它们（按进程名），在前台时发一个 Esc 收起（和用户自己按 Esc 一样）。
using System.Runtime.InteropServices;
using System.Text;

namespace ClawLibrary.Data;

internal static class ShellSurfaces
{
    private static readonly string[] Hosts = { "StartMenuExperienceHost", "SearchHost", "SearchApp" };

    /// <summary>这个窗口是开始菜单 / 搜索。</summary>
    public static bool Is(nint hwnd)
    {
        if (hwnd == 0) return false;
        string? name = ProcessName(hwnd);
        return name != null && Hosts.Any(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>前台是开始菜单 / 搜索就发 Esc 收起，最多等 waitMs 看它让出前台。返回前台是否已经不是它们。</summary>
    public static bool DismissForeground(int waitMs = 300, string why = "")
    {
        nint fg = GetForegroundWindow();
        if (!Is(fg)) return true;
        Log.Info($"shell surface in front ({ProcessName(fg)}): dismissing {why}");
        for (int t = 0; t < 2; t++)
        {
            SendEscape();
            for (int i = 0; i < waitMs / 20; i++)
            {
                Thread.Sleep(20);
                if (!Is(GetForegroundWindow())) return true;
            }
        }
        Log.Warn("shell surface still in front after Esc");
        return false;
    }

    private static string? ProcessName(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        nint p = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (p == 0) return null;
        try
        {
            var sb = new StringBuilder(512);
            int len = sb.Capacity;
            return QueryFullProcessImageName(p, 0, sb, ref len) ? Path.GetFileNameWithoutExtension(sb.ToString()) : null;
        }
        finally { CloseHandle(p); }
    }

    private static void SendEscape()
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1; inputs[0].ki.wVk = 0x1B;
        inputs[1].type = 1; inputs[1].ki.wVk = 0x1B; inputs[1].ki.dwFlags = 0x2;   // KEYEVENTF_KEYUP
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint p, uint flags, StringBuilder name, ref int size);
}
