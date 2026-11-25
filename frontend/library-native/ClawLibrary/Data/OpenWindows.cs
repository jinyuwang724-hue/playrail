// 现在打开着的顶层窗口（按 Alt+Tab 的规矩挑）：正在游戏画面找游戏窗口、截图取窗口标题用。
// 挑窗口的规则参考 Playhub v2.1.0 GamingWindowFocusService.IsCandidateWindow / IsAuxiliaryWindow（MIT，
// Copyright (c) 2026 Andrea Sgarro (LoZazaMastro)），最小化的窗口也收。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClawLibrary.Data;

internal sealed record OpenWindow(nint Hwnd, uint Pid, string Title, string Process, bool Minimized);

internal static class OpenWindows
{
    /// <summary>不进列表的进程：自己、Game Bar、系统外壳宿主、输入法 / 触摸键盘。</summary>
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClawLibrary", "ClawPerformanceOverlay", "ClawWidget", "GameBar", "GameBarFTServer", "GameBarPresenceWriter",
        "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp", "osk", "TabTip",
    };

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "#32770", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    /// <summary>按 Z 序（最近用过的在前）列出应用窗口。</summary>
    public static List<OpenWindow> List()
    {
        var result = new List<OpenWindow>();
        uint self = (uint)Environment.ProcessId;
        var names = new Dictionary<uint, string>();
        EnumWindows((hwnd, _) =>
        {
            try { if (Candidate(hwnd, self, names) is { } w) result.Add(w); } catch { }
            return true;
        }, 0);
        return result;
    }

    private static OpenWindow? Candidate(nint hwnd, uint self, Dictionary<uint, string> names)
    {
        if (!IsWindowVisible(hwnd)) return null;
        if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return null;
        long style = GetWindowLongPtr(hwnd, -16).ToInt64(), ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        bool appWindow = (ex & 0x40000) != 0;   // WS_EX_APPWINDOW
        if (!appWindow)
        {
            if ((ex & 0x80) != 0) return null;           // WS_EX_TOOLWINDOW
            if ((ex & 0x08000000) != 0) return null;     // WS_EX_NOACTIVATE
            if (GetWindow(hwnd, 4 /* GW_OWNER */) != 0) return null;   // 被拥有的弹窗、对话框
        }
        if ((style & 0x40000000) != 0) return null;      // WS_CHILD
        var cls = new StringBuilder(128);
        GetClassName(hwnd, cls, cls.Capacity);
        if (IgnoredClasses.Contains(cls.ToString())) return null;
        int len = GetWindowTextLength(hwnd);
        if (len == 0) return null;
        var title = new StringBuilder(len + 1);
        GetWindowText(hwnd, title, title.Capacity);
        bool iconic = IsIconic(hwnd);
        if (!iconic && GetWindowRect(hwnd, out var r) && (r.Right - r.Left < 2 || r.Bottom - r.Top < 2)) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == self) return null;
        if (!names.TryGetValue(pid, out string? name))
        {
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { name = ""; }
            names[pid] = name;
        }
        if (IgnoredProcesses.Contains(name)) return null;
        return new OpenWindow(hwnd, pid, title.ToString(), name, iconic);
    }

    public static bool Alive(nint hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd);

    internal static string? ExePath(uint pid)
    {
        nint h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private delegate bool EnumProc(nint hwnd, nint lp);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint lp);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder name, ref int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
}
