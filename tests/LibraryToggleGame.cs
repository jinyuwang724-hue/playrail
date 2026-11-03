using System;
using System.Drawing;
using System.Windows.Forms;

// Owned borderless/topmost test surface. Escape closes it without changing a game.
class LibraryToggleGame : Form
{
    LibraryToggleGame()
    {
        Text = "Claw Library Toggle Verification";
        FormBorderStyle = FormBorderStyle.None;
        Bounds = Screen.PrimaryScreen.Bounds;
        TopMost = true;
        BackColor = Color.FromArgb(24, 48, 72);
        KeyPreview = true;
        Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 28),
            Text = "Claw 按键切换验证\n\n全屏置顶测试窗口\n再次按下应回到这里\n\nEsc 结束验证" });
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        var timer = new Timer { Interval = 300000 };
        timer.Tick += (s, e) => Close(); timer.Start();
    }
    [STAThread] static void Main() { Application.EnableVisualStyles(); Application.Run(new LibraryToggleGame()); }
}
