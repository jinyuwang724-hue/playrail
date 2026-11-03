using System;
using System.Drawing;
using System.Windows.Forms;

// Disposable foreground application for the Game Bar close tile. No user data.
class CloseTargetProbe : Form {
    CloseTargetProbe() {
        Text = "Game Bar close target verification";
        ClientSize = new Size(720, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 18),
            Text = "Game Bar close target verification\nDisposable test window - safe to close" });
        var timer = new Timer { Interval = 600000 };
        timer.Tick += (s, e) => Close();
        timer.Start();
    }
    [STAThread] static void Main() { Application.EnableVisualStyles(); Application.Run(new CloseTargetProbe()); }
}
