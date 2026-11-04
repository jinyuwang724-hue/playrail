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

        /// <summary>藏任务栏（主屏和副屏的）、工作区设成整块屏幕。已经藏好了就什么都不做（监视循环每 0.1 秒调一次）。</summary>
        public void HideDesktopUi()
        {
            foreach (IntPtr tray in Trays())
                if (IsWindowVisible(tray)) { ShowWindow(tray, 0); Log("taskbar hidden"); }
            RECT screen = PrimaryScreen(), work;
            if (SystemParametersInfoGet(0x0030 /* SPI_GETWORKAREA */, 0, out work, 0) && !Same(work, screen))
                SystemParametersInfo(0x002F /* SPI_SETWORKAREA */, 0, ref screen, 0x2 /* SPIF_SENDCHANGE */);
        }

        /// <summary>回桌面：任务栏放回来，工作区让出任务栏那一条。</summary>
        public void ShowDesktopUi()
        {
            RECT screen = PrimaryScreen(), work = screen;
            foreach (IntPtr tray in Trays()) ShowWindow(tray, 8 /* SW_SHOWNA */);
            IntPtr main = FindWindow("Shell_TrayWnd", null);
            RECT t;
            if (main != IntPtr.Zero && GetWindowRect(main, out t))
            {
                if (t.Top > screen.Top && t.Bottom >= screen.Bottom) work.Bottom = t.Top;
                else if (t.Top <= screen.Top && t.Bottom < screen.Bottom) work.Top = t.Bottom;
                else if (t.Left > screen.Left) work.Right = t.Left;
                else if (t.Right < screen.Right) work.Left = t.Right;
            }
            SystemParametersInfo(0x002F, 0, ref work, 0x2);
            Log("taskbar shown");
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

        private static RECT PrimaryScreen() { return new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) }; }
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
