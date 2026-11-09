// Quick settings saves the composed monitor, including the scaled output of windowed games.
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const string MagpieScalingClass = "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";
    private bool _shotBusy;
    internal bool _shotNeedsPermission;

    private async Task QsTakeScreenshot()
    {
        if (_shotBusy) return;
        nint target = _qsGame;
        CloseQuickPanel(hideWindow: target != 0, instant: true);
        // The fallback panel is drawn inside the library window. Hide its composition tree as well.
        if (_qs != null) _qs.IsVisible = false;
        string? path = await CaptureScreenshotAsync(target);
        if (path == null) Toast(_shotNeedsPermission ? "截图没能保存：在游戏库里允许「屏幕截图」权限" : "截图没能保存");
        else { Cue(Sfx.Shutter); Toast("已截图"); }
    }

    internal async Task<string?> CaptureScreenshotAsync(nint target)
    {
        if (_shotBusy) return null;
        _shotBusy = true;
        _shotNeedsPermission = false;
        try
        {
            if (!await QsCaptureAccess(mayPrompt: false))
            {
                _shotNeedsPermission = true;
                return null;
            }

            bool alive = target != 0 && OpenWindows.Alive(target) && !ToolboxWindow.IsIconic(target);
            if (!alive) target = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            nint magpie = FindShotWindow(MagpieScalingClass, null);
            if (!IsWindowVisible(magpie) || (magpie != target && GetShotWindowProp(magpie, "Magpie.SrcHWND") != target)) magpie = 0;
            nint monitor = MonitorFromWindow(magpie != 0 ? magpie : target, 2);
            string title = alive ? OpenWindows.List().FirstOrDefault(w => w.Hwnd == target)?.Title ?? "" : "";
            nint osd = FindShotWindow(null, "Claw 性能显示");
            if (!IsWindowVisible(osd) || !IsPerformanceOverlay(osd)) osd = 0;

            CanvasBitmap? frame;
            bool restoreMagpie = magpie != 0 && GetWindowDisplayAffinity(magpie, out uint affinity) && affinity != 0;
            try
            {
                if (osd != 0)
                {
                    ShowShotWindowAsync(osd, 0); // SW_HIDE
                    for (int i = 0; i < 15 && IsWindowVisible(osd); i++) await Task.Delay(15);
                    if (IsWindowVisible(osd)) throw new InvalidOperationException("无法隐藏性能显示");
                }

                await Task.Run(() => FlushScreenshotDwm());
                if (restoreMagpie)
                {
                    // Desktop Duplication may have baked the menu into the last scaled frame.
                    // Let the cleared source reach Magpie's output before holding it for the capture.
                    await Task.Delay(200);
                    if (!SetMagpieScreenshot(magpie, true))
                        throw new InvalidOperationException("Magpie 未能准备缩放画面截图");
                }
                (frame, _) = await CaptureMonitorFrameAsync(_qsWin?.Hwnd ?? 0, 1250, monitor: monitor);
            }
            finally
            {
                if (restoreMagpie) SetMagpieScreenshot(magpie, false);
                if (osd != 0) ShowShotWindowAsync(osd, 4); // SW_SHOWNOACTIVATE
            }
            if (frame == null) return null;

            using (frame)
            {
                string folder = Captures.GameBarFolder();
                Directory.CreateDirectory(folder);
                string name = new string(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
                if (name.Length == 0) name = "Screenshot";
                if (name.Length > 80) name = name[..80].Trim();
                var now = DateTime.Now;
                string stem = $"{name} {now.Year}_{now.Month}_{now.Day} {now.Hour}_{now.Minute:00}_{now.Second:00}";
                string path = Path.Combine(folder, stem + ".png");
                for (int i = 1; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}).png");
                await frame.SaveAsync(path, CanvasBitmapFileFormat.Png);
                Log.Info($"screenshot saved (monitor{(magpie != 0 ? ", Magpie" : "")}) {path} ({frame.SizeInPixels.Width}x{frame.SizeInPixels.Height})");
                return path;
            }
        }
        catch (Exception ex) { Log.Warn($"screenshot: {ex.Message}"); return null; }
        finally { _shotBusy = false; }
    }

    private static bool IsPerformanceOverlay(nint hwnd)
    {
        GetWindowThreadProcessIdQs(hwnd, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("ClawPerformanceOverlay", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool SetMagpieScreenshot(nint hwnd, bool begin)
    {
        uint message = RegisterShotWindowMessage("ClawToolbox.Magpie.MonitorScreenshot.v1");
        return message != 0 && SendShotMessage(hwnd, message, begin ? 1 : 0, 0, 2 /* SMTO_ABORTIFHUNG */, 450, out nint result) != 0 && result == 1;
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern nint FindShotWindow(string? cls, string? title);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint GetShotWindowProp(nint hwnd, string name);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterShotWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint SendShotMessage(nint hwnd, uint message, nint wp, nint lp, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "ShowWindowAsync")] private static extern bool ShowShotWindowAsync(nint hwnd, int command);
    [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")] private static extern int FlushScreenshotDwm();
}
