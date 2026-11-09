using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Pad-mode notice, after MSI Center M's mode OSD (which shows a pad / desktop icon and a red bar):
// a small dark translucent rounded box at the top centre, icon on the left, 「手柄模式」/「桌面模式」 on
// the right, about 1.5 s with a fade. Top-most, never takes the focus, clicks go through it. Shows over
// windowed and borderless games; an exclusive full-screen game owns the display and hides it.
namespace ClawInput {
    public sealed class ModeOsd : Form {
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        static readonly IntPtr HwndTopMost = new IntPtr(-1);
        const double FadeIn = 150, Hold = 1150, FadeOut = 350, Peak = 0.92;

        readonly bool desktop;
        readonly float scale;
        readonly Stopwatch clock = new Stopwatch();
        readonly Timer timer = new Timer { Interval = 15 };
        readonly Font icon, text;

        public static int Run(string mode) {
            if (mode != "gamepad" && mode != "desktop") throw new ArgumentException("模式只能是 gamepad 或 desktop");
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { } // per-monitor v2
            Application.EnableVisualStyles();
            Application.Run(new ModeOsd(mode == "desktop"));
            return 0;
        }

        ModeOsd(bool desktop) {
            this.desktop = desktop;
            var screen = Screen.PrimaryScreen.Bounds;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; BackColor = Color.FromArgb(28, 28, 30); Opacity = 0;
            DoubleBuffered = true;
            Size = new Size((int)(232 * scale), (int)(64 * scale));
            Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (int)(36 * scale));
            icon = new Font(Installed("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 22 * scale, GraphicsUnit.Pixel);
            text = new Font("Microsoft YaHei UI", 19 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            timer.Tick += Tick;
        }

        static bool Installed(string family) {
            foreach (var f in FontFamily.Families) if (f.Name == family) return true;
            return false;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams {
            get {
                var p = base.CreateParams;
                // NOACTIVATE | TOOLWINDOW | TRANSPARENT (click-through) | TOPMOST | LAYERED
                p.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x8 | 0x80000;
                return p;
            }
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            int round = 2; // DWMWCP_ROUND (Windows 11)
            if (DwmSetWindowAttribute(Handle, 33, ref round, 4) != 0) {
                using (var path = Rounded(new Rectangle(0, 0, Width, Height), (int)(14 * scale))) Region = new Region(path);
            }
        }

        protected override void OnShown(EventArgs e) {
            base.OnShown(e);
            clock.Start(); timer.Start();
        }

        void Tick(object sender, EventArgs e) {
            double t = clock.Elapsed.TotalMilliseconds;
            if (t >= FadeIn + Hold + FadeOut) { timer.Stop(); Close(); return; }
            Opacity = t < FadeIn ? Peak * t / FadeIn : t < FadeIn + Hold ? Peak : Peak * (1 - (t - FadeIn - Hold) / FadeOut);
            // A borderless game that raises itself would otherwise cover the notice.
            SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040); // NOSIZE NOMOVE NOACTIVATE SHOWWINDOW
        }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            string glyph = desktop ? "" : ""; // TVMonitor / Game
            string label = desktop ? "桌面模式" : "手柄模式";
            // Icon, gap, label as one group centred in the box.
            float iconWidth = 34 * scale, gap = 12 * scale, textWidth = g.MeasureString(label, text).Width;
            float x = (Width - iconWidth - gap - textWidth) / 2;
            var iconBox = new RectangleF(x, 0, iconWidth, Height);
            var textBox = new RectangleF(x + iconWidth + gap, 0, textWidth + 4 * scale, Height);
            using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center }) {
                g.DrawString(glyph, icon, Brushes.White, iconBox, center);
                g.DrawString(label, text, Brushes.White, textBox, left);
            }
        }

        static GraphicsPath Rounded(Rectangle r, int radius) {
            var path = new GraphicsPath(); int d = radius * 2;
            path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing) {
            if (disposing) { timer.Dispose(); icon.Dispose(); text.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
