using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// An owned, disposable game-like window for end-to-end capture checks.
class ScalingTestGame : Form
{
    ScalingTestGame()
    {
        Text = "Claw Scaling Verification"; ClientSize = new Size(640, 480);
        FormBorderStyle = FormBorderStyle.FixedToolWindow; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen; DoubleBuffered = true;
        Shown += (sender, args) => { BeginInvoke((Action)(() => { ShowWindow(Handle, 9); WindowState = FormWindowState.Normal; ClientSize = new Size(640, 480); })); Console.WriteLine(Handle.ToInt64()); Console.Out.Flush(); };
        var timer = new Timer { Interval = 60000 };
        timer.Tick += (sender, args) => Close(); timer.Start();
        Task.Run(() => {
            string command;
            while ((command = Console.ReadLine()) != null) {
                string received = command;
                try { BeginInvoke((Action)(() => { if (received == "focus") { ShowWindow(Handle, 9); WindowState = FormWindowState.Normal; ClientSize = new Size(640, 480); FocusOwnedWindow(); } else if (received == "close") Close(); })); }
                catch (InvalidOperationException) { break; }
            }
            try { BeginInvoke((Action)Close); } catch (InvalidOperationException) { }
        });
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(20, 40, 60));
        for (int y = 0; y < ClientSize.Height; y += 20)
            for (int x = 0; x < ClientSize.Width; x += 20)
                e.Graphics.FillRectangle(((x / 20 + y / 20) % 2 == 0) ? Brushes.Gold : Brushes.RoyalBlue, x, y, 20, 20);
        e.Graphics.DrawString("Claw scaling verification — 640 × 480", Font, Brushes.White, 10, 10);
    }
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    void FocusOwnedWindow()
    {
        uint process, foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out process), ownThread = GetCurrentThreadId();
        bool attached = foregroundThread != ownThread && AttachThreadInput(ownThread, foregroundThread, true);
        try { bool focused = SetForegroundWindow(Handle); Activate(); Console.Error.WriteLine("focus=" + focused + ",attached=" + attached + ",foreground=" + GetForegroundWindow().ToInt64() + ",own=" + Handle.ToInt64()); }
        finally { if (attached) AttachThreadInput(ownThread, foregroundThread, false); }
    }
    [STAThread] static void Main() { SetProcessDPIAware(); Application.EnableVisualStyles(); Application.Run(new ScalingTestGame()); }
}
