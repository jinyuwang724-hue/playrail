using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ClawShell
{
    internal sealed class ModeTransition : IModeTransitionScope
    {
        private readonly bool _toGame;
        private readonly IntPtr _previousWindow;
        private readonly Thread _thread;
        private readonly Action _reveal;
        private volatile bool _covered, _finished, _succeeded;

        private ModeTransition(bool toGame, Action reveal)
        {
            _toGame = toGame;
            _reveal = reveal;
            _previousWindow = GetForegroundWindow();
            _thread = new Thread(Run) { IsBackground = true, Name = "Claw mode transition" };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        internal static IModeTransitionScope Show(bool toGame, Action reveal = null, bool waitForCover = true)
        {
            var overlay = new ModeTransition(toGame, reveal);
            overlay._thread.Start();
            if (!waitForCover) return overlay;
            var wait = Stopwatch.StartNew();
            while (!overlay._covered && overlay._thread.IsAlive && wait.ElapsedMilliseconds < 280)
                Thread.Sleep(8);
            return overlay;
        }

        private void Run()
        {
            try
            {
                SetThreadDpiAwarenessContext(new IntPtr(-4));
                using (var window = new Surface(this))
                    Application.Run(window);
            }
            catch { /* A visual failure must not prevent a shell switch. */ }
        }

        public void Complete() { _succeeded = true; }

        public void Dispose()
        {
            _finished = true;
            _thread.Join(1100);
        }

        private sealed class Surface : Form
        {
            private const int CoverMs = 95;
            private const int EnterMs = 210;
            private const int DesktopExitMs = 360;
            private const int GameExitMs = 650;
            private readonly ModeTransition _owner;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly System.Windows.Forms.Timer _frame = new System.Windows.Forms.Timer { Interval = 15 };
            private long _exit = -1;
            private Bitmap _backdrop;
            private Rectangle _loader, _content;
            private float _scale;
            private float _contentOffset, _contentScale = 1;
            private bool _contentInMotion;

            internal Surface(ModeTransition owner)
            {
                _owner = owner;
                Text = owner._toGame ? "ClawToolbox.GameModeTransition" : "ClawToolbox.DesktopModeTransition";
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromHandle(owner._previousWindow).Bounds;
                ShowInTaskbar = false;
                MinimizeBox = MaximizeBox = false;
                TopMost = true;
                DoubleBuffered = true;
                BackColor = Color.FromArgb(9, 9, 11);
                Cursor = Cursors.Default;
                Opacity = 0;
                _frame.Tick += DrawFrame;
            }

            protected override void OnLoad(EventArgs e)
            {
                base.OnLoad(e);
                int disable = 1;
                DwmSetWindowAttribute(Handle, 3, ref disable, sizeof(int)); // Use only our own show/hide animation.
                // The handle now has its final borderless, per-monitor-DPI client size.
                _scale = Math.Min(ClientSize.Width / 1280f, ClientSize.Height / 800f);
                int size = (int)Math.Ceiling(60 * _scale);
                _loader = new Rectangle((ClientSize.Width - size) / 2, ClientSize.Height / 2 - size, size, size);
                _content = Rectangle.Inflate(new Rectangle(0, _loader.Top, ClientSize.Width, size), 0,
                    (int)Math.Ceiling(60 * _scale));
                _contentOffset = 18 * _scale;
                _contentScale = 0.985f;
                _backdrop = LoadBackdrop(ClientSize);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= 0x08000000 | 0x80; // NOACTIVATE | TOOLWINDOW
                    return parameters;
                }
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                _clock.Restart();
                _frame.Start();
            }

            private void DrawFrame(object sender, EventArgs e)
            {
                long now = _clock.ElapsedMilliseconds;
                // Guard against the game's startup shield. The desktop handoff must never raise the overlay again.
                if (_owner._toGame && _exit < 0)
                    SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0213);
                if (_owner._finished && _exit < 0 && now >= EnterMs)
                {
                    _exit = now;
                    if (_owner._succeeded && _owner._reveal != null) _owner._reveal();
                }
                bool moving = now < EnterMs || (_exit >= 0 && now >= _exit);
                if (_exit < 0)
                {
                    double t = Math.Min(1, (double)now / EnterMs);
                    float remaining = (float)Math.Pow(1 - t, 3);
                    double cover = Math.Min(1, (double)now / CoverMs);
                    Opacity = 1 - (1 - cover) * (1 - cover);
                    _contentOffset = 18 * _scale * remaining;
                    _contentScale = 1 - 0.015f * remaining;
                    if (cover >= 1) _owner._covered = true;
                }
                if (_exit >= 0 && now >= _exit)
                {
                    int exitMs = _owner._toGame ? GameExitMs : DesktopExitMs;
                    double t = Math.Min(1, (double)(now - _exit) / exitMs);
                    if (t >= 1) { Close(); return; }
                    float progress = (float)(t * t);
                    Opacity = 1 - t * t * (3 - 2 * t);
                    _contentOffset = 12 * _scale * progress;
                    _contentScale = 1 - 0.01f * progress;
                }
                if (now > 140000) { Close(); return; }
                Invalidate(moving || _contentInMotion ? _content : _loader);
                _contentInMotion = moving;
            }

            private static Bitmap LoadBackdrop(Size size)
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Packages", WindowsShellSystem.LibraryFamily, "LocalCache", "mode-transition-background.png");
                try
                {
                    // Close the file before drawing so the library can atomically replace it.
                    byte[] pixels = File.ReadAllBytes(path);
                    using (var stream = new MemoryStream(pixels))
                    using (var image = Image.FromStream(stream))
                    {
                        var result = new Bitmap(size.Width, size.Height);
                        try
                        {
                            float scale = Math.Max((float)size.Width / image.Width, (float)size.Height / image.Height);
                            float width = image.Width * scale, height = image.Height * scale;
                            using (var g = Graphics.FromImage(result))
                            using (var shade = new SolidBrush(Color.FromArgb(110, 8, 10, 15)))
                            using (var edges = new System.Drawing.Imaging.ImageAttributes())
                            {
                                edges.SetWrapMode(WrapMode.TileFlipXY);
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                var destination = Rectangle.FromLTRB(
                                    (int)Math.Floor((size.Width - width) / 2), (int)Math.Floor((size.Height - height) / 2),
                                    (int)Math.Ceiling((size.Width + width) / 2), (int)Math.Ceiling((size.Height + height) / 2));
                                g.DrawImage(image, destination, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, edges);
                                g.FillRectangle(shade, new Rectangle(Point.Empty, size));
                            }
                            return result;
                        }
                        catch { result.Dispose(); throw; }
                    }
                }
                catch (Exception) { return null; }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                if (_backdrop == null) base.OnPaintBackground(e);
                else e.Graphics.DrawImageUnscaledAndClipped(_backdrop, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float centerX = ClientSize.Width / 2f;
                float centerY = _loader.Top + _loader.Height / 2f + _contentOffset;
                float scale = _scale * _contentScale;
                double phase = _clock.Elapsed.TotalSeconds * (2 * Math.PI / 1.1);
                using (var bar = new Pen(Color.FromArgb(230, 244, 247, 250), 3.2f * scale))
                {
                    bar.StartCap = bar.EndCap = LineCap.Round;
                    for (int i = -3; i <= 3; i++)
                    {
                        float x = centerX + i * 7 * scale;
                        float pulse = (float)(0.5 + 0.5 * Math.Sin(phase - i * 0.85));
                        float halfHeight = (2 + (11 - Math.Abs(i) * 1.6f) * pulse) * scale;
                        g.DrawLine(bar, x, centerY - halfHeight, x, centerY + halfHeight);
                    }
                }

                if (e.ClipRectangle.Bottom > _loader.Bottom)
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (var font = new Font("Segoe UI", 25 * scale, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var ink = new SolidBrush(Color.FromArgb(245, 245, 247)))
                    using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(_owner._toGame ? "正在切换到游戏模式" : "正在切换到桌面模式", font, ink,
                            new PointF(centerX, _loader.Bottom + 16 * _scale + _contentOffset), center);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _frame.Dispose();
                    if (_backdrop != null) _backdrop.Dispose();
                }
                base.Dispose(disposing);
            }

        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }

    public sealed partial class WindowsShellSystem : IModeTransition
    {
        public IModeTransitionScope BeginModeTransition(bool toGame) { return BeginModeTransition(toGame, true); }

        internal IModeTransitionScope BeginModeTransition(bool toGame, bool waitForCover)
        {
            try
            {
                bool desktop = ExplorerRunning() && !CoveredActive();
                if (desktop != toGame || !SystemInformation.IsMenuAnimationEnabled) return null;
                return ModeTransition.Show(toGame, toGame ? (Action)RevealGameModeLibrary : null, waitForCover);
            }
            catch (Exception ex) { Log("mode transition: " + ex.Message); return null; }
        }
    }
}
