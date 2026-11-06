// 游戏模式的截图：Game Bar 的 Win+Alt+PrintScreen 要 explorer 接，游戏模式下没有反应。
// 这里改用系统的画面捕获（Windows.Graphics.Capture，和 Game Bar 一样取 DWM 合成好的那一帧）截游戏窗口，
// 存进 Game Bar 的捕获文件夹、按它的命名（「窗口标题 年_月_日 时_分_秒.png」）——后台照旧把新图记到当时的游戏下面，截图页直接能看到。
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private bool _shotBusy;
    /// <summary>上一次截图失败是因为还没有屏幕截图许可（提示里说清楚要去哪儿开）。</summary>
    internal bool _shotNeedsPermission;

    /// <summary>
    /// 截 target 窗口。先按窗口截；管理员权限的游戏（Steam 以管理员运行时游戏也是）不让普通权限程序按窗口截，
    /// 会直接「拒绝访问」—— 这时改截整块显示器（快捷面板窗口排除在外），再裁到游戏窗口那一块。成功返回保存的路径。
    /// </summary>
    internal async Task<string?> CaptureScreenshotAsync(nint target)
    {
        if (_shotBusy) return null;
        _shotBusy = true;
        _shotNeedsPermission = false;
        try
        {
            string title = target != 0 ? OpenWindows.List().FirstOrDefault(w => w.Hwnd == target)?.Title ?? "" : "";
            CanvasBitmap? frame = null;
            string how = "window";
            bool alive = target != 0 && OpenWindows.Alive(target) && !ToolboxWindow.IsIconic(target);
            if (alive)
            {
                try
                {
                    var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(target);
                    frame = await QsGrab(GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId { Value = id.Value }), 1000);
                }
                catch (Exception ex) { Log.Info($"screenshot: window capture refused ({ex.Message}); using the monitor"); }
            }
            if (frame == null)
            {
                how = "monitor";
                var (mon, rect) = await CaptureMonitorFrameAsync(_qsWin?.Hwnd ?? 0, 1500, alive ? target : 0);
                if (mon != null && alive && GetWindowRect(target, out var wr))
                {
                    // 裁到游戏窗口（和显示器求交）
                    int l = Math.Max(wr.Left, rect.L), tp = Math.Max(wr.Top, rect.T), r = Math.Min(wr.Right, rect.L + rect.W), bt = Math.Min(wr.Bottom, rect.T + rect.H);
                    if (r - l > 16 && bt - tp > 16 && (r - l < rect.W || bt - tp < rect.H))
                    {
                        var crop = new CanvasRenderTarget(mon.Device, r - l, bt - tp, 96);
                        using (var ds = crop.CreateDrawingSession())
                            ds.DrawImage(mon, new Windows.Foundation.Rect(0, 0, r - l, bt - tp), new Windows.Foundation.Rect(l - rect.L, tp - rect.T, r - l, bt - tp));
                        mon.Dispose();
                        mon = crop;
                    }
                }
                frame = mon;
                if (title.Length == 0) title = "Screenshot";
            }
            if (frame == null)
            {
                // 还没有截屏许可：交给 Game Bar 截（游戏模式保留 explorer 时 Win+Alt+PrtScn 有人接），图同样进「捕获」文件夹
                Log.Warn($"screenshot: no frame (consent {CaptureConsent()}); asking Game Bar");
                try
                {
                    var since = DateTime.Now.AddSeconds(-1);
                    _agent ??= new AgentClient(); await _agent.Call("screenshot");
                    // Game Bar 截没截到看文件：3 秒内「捕获」文件夹里出现新图才算
                    string dir = Captures.GameBarFolder();
                    for (int i = 0; i < 15; i++)
                    {
                        await Task.Delay(200);
                        var hit = Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles().Where(fi => fi.LastWriteTime > since).OrderByDescending(fi => fi.LastWriteTime).FirstOrDefault() : null;
                        if (hit != null) { Log.Info($"screenshot saved (Game Bar) {hit.FullName}"); return hit.FullName; }
                    }
                    Log.Warn("screenshot: Game Bar saved nothing");
                    _shotNeedsPermission = CaptureConsent() != "Allow";
                    return null;
                }
                catch (Exception ex) { Log.Warn($"screenshot via Game Bar: {ex.Message}"); return null; }
            }
            using var _f = frame;
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
            Log.Info($"screenshot saved ({how}) {path} ({frame.SizeInPixels.Width}x{frame.SizeInPixels.Height})");
            return path;
        }
        catch (Exception ex) { Log.Warn($"screenshot: {ex.Message}"); return null; }
        finally { _shotBusy = false; }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
}
