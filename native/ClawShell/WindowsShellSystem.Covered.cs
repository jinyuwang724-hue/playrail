// 「保留 explorer 的游戏模式」（试验，shell\keep-explorer.flag）：不结束 explorer，只把任务栏藏起来、工作区铺满整屏。
// 输入法候选框、触摸键盘、Game Bar 截图这些都是 explorer 带的，关掉 explorer 就没了；这样它们照常能用，桌面由游戏库盖住。
// 状态文件里 mode=game 且 explorer 还在 = 这种模式（WriteState 写 covered:true），后台和游戏库都按游戏模式处理。
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClawShell
{
    public sealed partial class WindowsShellSystem : ICoveredShell
    {
        // 属性而不是静态字段：分部类之间静态字段的初始化顺序不定，ShellDir 可能还是 null
        public static string CoverFlagPath { get { return Path.Combine(ShellDir, "keep-explorer.flag"); } }

        public bool CoverWanted() { return File.Exists(CoverFlagPath); }

        public void SetCoverWanted(bool on)
        {
            Directory.CreateDirectory(ShellDir);
            if (on) File.WriteAllText(CoverFlagPath, DateTime.Now.ToString("s"));
            else if (File.Exists(CoverFlagPath)) File.Delete(CoverFlagPath);
            Log("keep explorer " + (on ? "on" : "off"));
        }

        public bool CoveredActive()
        {
            if (!ExplorerRunning()) return false;
            try { return File.ReadAllText(StatePath).Contains("\"mode\":\"game\""); } catch { return false; }
        }

        /// <summary>
        /// 藏任务栏（主屏和副屏的）。工作区不自己改：explorer 还把任务栏当成占位的应用栏，我们改了它马上改回来，
        /// 每改一次所有全屏窗口都重排一次 = 整屏闪（11-04 实测游戏库窗口来回变了六百多次）。
        /// 所以进来时把任务栏设成系统的「自动隐藏」（explorer 自己让出那一条），原来的设置记下来，回桌面再还原。
        /// 监视循环每 0.1 秒调一次：已经藏好就什么都不做。
        /// </summary>
        public void HideDesktopUi()
        {
            if (!File.Exists(AutoHideSavedPath))
            {
                int was = AppBarState();
                try { File.WriteAllText(AutoHideSavedPath, was.ToString()); } catch { }
                if ((was & 1) == 0) { SetAppBarState(was | 1); Log("taskbar auto-hide on (was " + was + ")"); }
            }
            foreach (IntPtr tray in Trays())
                if (IsWindowVisible(tray)) { ShowWindow(tray, 0); Log("taskbar hidden"); }
        }

        /// <summary>回桌面：任务栏放回来，「自动隐藏」还原成进来之前的样子。</summary>
        public void ShowDesktopUi()
        {
            foreach (IntPtr tray in Trays()) ShowWindow(tray, 8 /* SW_SHOWNA */);
            if (File.Exists(AutoHideSavedPath))
            {
                int was;
                if (int.TryParse(File.ReadAllText(AutoHideSavedPath).Trim(), out was)) SetAppBarState(was);
                try { File.Delete(AutoHideSavedPath); } catch { }
                Log("taskbar auto-hide restored (" + was + ")");
                RestoreWorkArea();
            }
            Log("taskbar shown");
        }

        /// <summary>回桌面：工作区让出任务栏那一条（广播一次，窗口按新工作区重排）。</summary>
        private static void RestoreWorkArea()
        {
            RECT screen = PrimaryScreen(), work = screen, t;
            IntPtr main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero && GetWindowRect(main, out t))
            {
                if (t.Top > screen.Top && t.Bottom >= screen.Bottom) work.Bottom = t.Top;
                else if (t.Top <= screen.Top && t.Bottom < screen.Bottom) work.Top = t.Bottom;
                else if (t.Left > screen.Left) work.Right = t.Left;
                else if (t.Right < screen.Right) work.Left = t.Right;
            }
            SystemParametersInfo(0x002F, 0, ref work, 0x2);
        }

        private static string AutoHideSavedPath { get { return Path.Combine(ShellDir, "taskbar-autohide-before.txt"); } }

        [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

        private static int AppBarState()
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = FindWindow("Shell_TrayWnd", null) };
            return (int)SHAppBarMessage(0x4 /* ABM_GETSTATE */, ref d).ToUInt32();
        }

        private static void SetAppBarState(int state)
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = FindWindow("Shell_TrayWnd", null), lParam = new IntPtr(state) };
            SHAppBarMessage(0xA /* ABM_SETSTATE */, ref d);
        }

        private static System.Collections.Generic.List<IntPtr> Trays()
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            IntPtr main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);
            IntPtr next = IntPtr.Zero;
            while ((next = FindWindowEx(IntPtr.Zero, next, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(next);
            return list;
        }

        /// <summary>主屏的物理像素大小。外壳进程默认不感知 DPI，GetSystemMetrics 给的是缩放后的 1280x800，拿它设工作区就错了（11-04 闪屏的另一半原因），先声明按显示器感知 DPI。</summary>
        private static RECT PrimaryScreen()
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            return new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
        }
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        private static bool Same(RECT a, RECT b) { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint flags);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static extern bool SystemParametersInfoGet(uint action, uint param, out RECT rect, uint flags);
    }
}
