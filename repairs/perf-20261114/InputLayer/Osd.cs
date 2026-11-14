using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Windows.Forms;

// Folia's top StatusToast. Per-pixel alpha preserves its soft shadow and edges.
// Neither the content nor its live backdrop activates or captures input.
namespace ClawInput {
    public sealed class ModeOsd : Form {
        [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct Extent { public int X, Y; public Extent(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr window, IntPtr targetDc,
            ref Point position, ref Extent size, IntPtr sourceDc, ref Point source, int key, ref Blend blend, int flags);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

        const double FadeIn = 320, Hold = 2460, FadeOut = 220;
        readonly Stopwatch clock = new Stopwatch();
        readonly Timer timer = new Timer { Interval = 15 };
        readonly Bitmap art;
        readonly float scale;
        readonly bool reduced;
        readonly int restingTop;
        NoticeBackdrop backdrop;
        IntPtr memoryDc, bitmapHandle, previousBitmap;
        int lastAlpha = -1, lastDy;

        // An invisible PMv2 window reads the display's DPI independently of a
        // game's DPI awareness. It is destroyed before either notice is shown.
        sealed class DpiProbe : NativeWindow, IDisposable {
            public DpiProbe(Rectangle screen) {
                CreateHandle(new CreateParams { X = screen.Left + screen.Width / 2, Y = screen.Top + screen.Height / 2,
                    Width = 1, Height = 1, Style = unchecked((int)0x80000000), ExStyle = 0x08000080 });
            }
            public void Dispose() { DestroyHandle(); }
        }

        static int ShowNotice(string kind, string title, string detail) {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            Application.Run(new ModeOsd(kind, title, detail));
            return 0;
        }
        public static int Run(string mode) {
            if (mode != "gamepad" && mode != "desktop") throw new ArgumentException("模式只能是 gamepad 或 desktop");
            return ShowNotice(mode, mode == "desktop" ? "桌面模式" : "手柄模式", "");
        }
        public static int Notice(string json) {
            var n = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(json);
            string kind = n["kind"], title = n["title"], detail = n["detail"];
            if (Array.IndexOf(new[] { "gamepad", "desktop", "charging", "battery", "success", "error", "warning", "info", "game", "library", "account", "settings", "update", "image", "sound", "power" }, kind) < 0
                || string.IsNullOrEmpty(title) || title.Length > 300 || detail == null || detail.Length > 140)
                throw new ArgumentException("通知格式错误");
            return ShowNotice(kind, title, detail);
        }
        ModeOsd(string kind, string label, string detail) {
            var screen = Screen.FromHandle(GetForegroundWindow()).Bounds;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            // A DPI-unaware game reports 96 through GetDpiForWindow even on a
            // 150% screen. Query our own window under the PMv2 context instead.
            try {
                using (var probe = new DpiProbe(screen)) { uint dpi = GetDpiForWindow(probe.Handle); if (dpi > 0) scale = dpi / 96f; }
            } catch (EntryPointNotFoundException) { }
            try {
                var prefs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Packages\ClawToolbox.Library_ba3r68zr466za\LocalCache\ui.ini");
                if (File.Exists(prefs)) foreach (string line in File.ReadAllLines(prefs))
                    if (line.Equals("reduced=True", StringComparison.OrdinalIgnoreCase)) reduced = true;
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            art = NoticeArt.Render(kind, label, detail, scale);
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            Text = "Claw 全局提示";
            StartPosition = FormStartPosition.Manual;
            Size = art.Size;
            restingTop = screen.Top + (int)((64 - NoticeArt.Margin) * scale);   // 让开性能 OSD 二档那一条
            Location = new System.Drawing.Point(screen.Left + (screen.Width - Width) / 2, restingTop);
            timer.Tick += Tick;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get {
                var p = base.CreateParams;
                p.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x8 | 0x80000;
                return p;
            }
        }
        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            IntPtr screen = GetDC(IntPtr.Zero);
            try { memoryDc = CreateCompatibleDC(screen); }
            finally { ReleaseDC(IntPtr.Zero, screen); }
            if (memoryDc == IntPtr.Zero) throw new Win32Exception();
            bitmapHandle = art.GetHbitmap(Color.FromArgb(0));
            previousBitmap = SelectObject(memoryDc, bitmapHandle);
            Present(0, 0);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnShown(EventArgs e) {
            base.OnShown(e);
            backdrop = new NoticeBackdrop(new Rectangle(Left + (int)(NoticeArt.Margin * scale),
                restingTop + (int)(NoticeArt.Margin * scale), Width - (int)(NoticeArt.Margin * 2 * scale),
                (int)(NoticeArt.Height * scale)), scale);
            try { backdrop.Show(); }
            // Keep the ARGB tint/text/shadow when host backdrops are unavailable.
            // Never bring back the window-wide Acrylic that leaks at the corners.
            catch (COMException) { backdrop.Dispose(); backdrop = null; }
            catch (EntryPointNotFoundException) { backdrop.Dispose(); backdrop = null; }
            catch (TypeLoadException) { backdrop.Dispose(); backdrop = null; }
            clock.Start(); timer.Start();
        }
        void Tick(object sender, EventArgs e) {
            double t = clock.Elapsed.TotalMilliseconds;
            double enterMs = reduced ? 150 : FadeIn, exitMs = reduced ? 150 : FadeOut;
            if (t >= enterMs + Hold + exitMs) { timer.Stop(); Close(); return; }
            double enter = Bezier(Math.Min(1, t / enterMs), .2, .8, .2, 1);
            double exit = Bezier(Math.Max(0, (t - enterMs - Hold) / exitMs), .4, 0, 1, 1);
            timer.Interval = t >= enterMs && t < enterMs + Hold ? 80 : 15;
            Present(t < enterMs ? enter : 1 - exit,
                reduced ? 0 : (int)Math.Round(scale * (t < enterMs ? -18 * (1 - enter) : -10 * exit)));
        }
        // Match Framer Motion's cubic-bezier curves by solving x(t) before y(t).
        static double Bezier(double x, double x1, double y1, double x2, double y2) {
            if (x <= 0) return 0; if (x >= 1) return 1;
            double low = 0, high = 1, t = x;
            for (int i = 0; i < 18; i++) {
                double a = 1 - t, value = 3 * a * a * t * x1 + 3 * a * t * t * x2 + t * t * t;
                if (value < x) low = t; else high = t;
                t = (low + high) / 2;
            }
            return 3 * (1 - t) * (1 - t) * t * y1 + 3 * (1 - t) * t * t * y2 + t * t * t;
        }
        void Present(double alpha, int dy) {
            var position = new Point(Left, restingTop + dy);
            var size = new Extent(art.Width, art.Height);
            var source = new Point(0, 0);
            var blend = new Blend { Alpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(alpha * 255))), Format = 1 };
            if (lastAlpha != blend.Alpha || lastDy != dy) {
                if (!UpdateLayeredWindow(Handle, IntPtr.Zero, ref position, ref size, memoryDc, ref source, 0, ref blend, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                lastAlpha = blend.Alpha; lastDy = dy;
            }
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
            if (backdrop != null) backdrop.Present(Handle, restingTop + (int)(NoticeArt.Margin * scale) + dy, blend.Alpha);
        }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; }
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
            base.WndProc(ref m);
        }
        protected override void Dispose(bool disposing) {
            if (disposing) {
                timer.Dispose();
                if (backdrop != null) { backdrop.Close(); backdrop.Dispose(); backdrop = null; }
                if (memoryDc != IntPtr.Zero) {
                    SelectObject(memoryDc, previousBitmap);
                    DeleteObject(bitmapHandle); DeleteDC(memoryDc);
                    memoryDc = bitmapHandle = previousBitmap = IntPtr.Zero;
                }
                art.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
