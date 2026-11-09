// 窗口切换页的数据：现在打开着的顶层窗口（按 Alt+Tab 的规矩挑）、应用图标、关窗口、DWM 实时缩略图。
// 挑窗口的规则参考 Playhub v2.1.0 GamingWindowFocusService.IsCandidateWindow / IsAuxiliaryWindow（MIT，
// Copyright (c) 2026 Andrea Sgarro (LoZazaMastro)），这里还收最小化的窗口，因为它是切换器不是游戏识别。
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

    /// <summary>按 Z 序（最近用过的在前）列出可以切过去的窗口。</summary>
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

    /// <summary>窗口管理用：这个窗口是不是切换器里会出现的那种应用窗口（不是弹窗、工具窗、外壳、输入法）。</summary>
    public static OpenWindow? AppWindow(nint hwnd) { try { return Candidate(hwnd, (uint)Environment.ProcessId, new()); } catch { return null; } }

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

    /// <summary>像点右上角 × 一样请它自己关（能存档）。以管理员身份运行的窗口收不到（返回 false）。</summary>
    public static bool Close(nint hwnd) => PostMessage(hwnd, 0x0010 /* WM_CLOSE */, 0, 0);

    public static bool Matches(OpenWindow window) => IsWindow(window.Hwnd) && GetWindowThreadProcessId(window.Hwnd, out uint pid) != 0 && pid == window.Pid;

    public static bool Alive(nint hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd);

    /// <summary>
    /// 应用图标的 BGRA 像素（预乘 alpha，正方形 size×size）：先问窗口要大图标，没有就用窗口类的，再没有就从 exe 里取。
    /// </summary>
    public static byte[]? IconPixels(nint hwnd, uint pid, out int size)
    {
        size = 0;
        nint icon = 0;
        bool owned = false;
        if (SendMessageTimeout(hwnd, 0x007F /* WM_GETICON */, 1 /* ICON_BIG */, 0, 0x0002 /* SMTO_ABORTIFHUNG */, 120, out nint big) != 0) icon = big;
        if (icon == 0) icon = GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
        // 窗口给的大图标常常只有 32 px：exe 里的 256 px 更清楚，取得到就用它
        if (ExePath(pid) is { } exe)
        {
            var large = new nint[1];
            if (PrivateExtractIcons(exe, 0, 128, 128, large, null, 1, 0) == 1 && large[0] != 0) { icon = large[0]; owned = true; }
        }
        if (icon == 0) return null;
        try { return Pixels(icon, out size); }
        finally { if (owned) DestroyIcon(icon); }
    }

    internal static string Elevated(uint pid)
    {
        nint process = OpenProcess(0x1000, false, pid);
        if (process == 0) return "unknown";
        try
        {
            if (!OpenProcessToken(process, 8, out nint token)) return "unknown";
            try { return GetTokenInformation(token, 20, out int elevated, sizeof(int), out _) ? (elevated != 0 ? "true" : "false") : "unknown"; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(nint token, int type, out int value, int size, out int written);

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

    private static byte[]? Pixels(nint icon, out int size)
    {
        size = 0;
        if (!GetIconInfo(icon, out var info)) return null;
        try
        {
            if (info.hbmColor == 0) return null;
            GetObject(info.hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP bm);
            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0 || w != h) return null;
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            nint dc = GetDC(0);
            try { if (GetDIBits(dc, info.hbmColor, 0, (uint)h, px, ref bmi, 0) == 0) return null; }
            finally { ReleaseDC(0, dc); }
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = anyAlpha ? px[i + 3] : 255;
                px[i] = (byte)(px[i] * a / 255); px[i + 1] = (byte)(px[i + 1] * a / 255); px[i + 2] = (byte)(px[i + 2] * a / 255); px[i + 3] = (byte)a;
            }
            size = w;
            return px;
        }
        finally
        {
            if (info.hbmColor != 0) DeleteObject(info.hbmColor);
            if (info.hbmMask != 0) DeleteObject(info.hbmMask);
        }
    }

    private delegate bool EnumProc(nint hwnd, nint lp);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public nint hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public nint bmBits; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint lp);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern nint GetClassLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wp, nint lp, uint flags, uint ms, out nint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, nint[] icons, int[]? ids, uint n, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern int GetObject(nint h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint h);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint h, int flags, StringBuilder name, ref int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
}

/// <summary>DWM 实时缩略图：把别的窗口的画面直接合成到我们窗口里的一块矩形上（物理像素、客户区坐标），压在我们自己的内容上面。</summary>
internal sealed class DwmThumb : IDisposable
{
    private nint _id;
    public int SourceW { get; }
    public int SourceH { get; }

    private DwmThumb(nint id, int w, int h) { _id = id; SourceW = w; SourceH = h; }

    public static DwmThumb? Register(nint dest, nint source)
    {
        if (DwmRegisterThumbnail(dest, source, out nint id) != 0 || id == 0) return null;
        if (DwmQueryThumbnailSourceSize(id, out SIZE s) != 0 || s.cx <= 0 || s.cy <= 0) { DwmUnregisterThumbnail(id); return null; }
        return new DwmThumb(id, s.cx, s.cy);
    }

    /// <summary>摆到 (x, y, w, h)（物理像素），透明度 0–1；opacity ≤ 0 或面积为 0 就藏起来。</summary>
    public void Place(int x, int y, int w, int h, double opacity)
    {
        if (_id == 0) return;
        bool visible = opacity > 0.004 && w > 0 && h > 0;
        var p = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = 0x1 | 0x4 | 0x8 | 0x10,   // RECTDESTINATION | OPACITY | VISIBLE | SOURCECLIENTAREAONLY
            rcDestination = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h },
            opacity = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255),
            fVisible = visible,
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(_id, ref p);
    }

    /// <summary>只显示源窗口的 (sx, sy, sw, sh) 那一块（源窗口像素），摆到目标矩形上。</summary>
    public void PlaceCropped(int x, int y, int w, int h, int sx, int sy, int sw, int sh, double opacity = 1)
    {
        if (_id == 0) return;
        bool visible = w > 0 && h > 0 && sw > 0 && sh > 0;
        var p = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10,   // RECTDESTINATION | RECTSOURCE | OPACITY | VISIBLE | SOURCECLIENTAREAONLY
            rcDestination = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h },
            rcSource = new RECT { Left = sx, Top = sy, Right = sx + sw, Bottom = sy + sh },
            opacity = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255),
            fVisible = visible,
            fSourceClientAreaOnly = false,
        };
        DwmUpdateThumbnailProperties(_id, ref p);
    }

    public void Dispose() { if (_id != 0) { DwmUnregisterThumbnail(_id); _id = 0; } }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags; public RECT rcDestination, rcSource; public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint id);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint id);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint id, out SIZE size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint id, ref DWM_THUMBNAIL_PROPERTIES p);
}
