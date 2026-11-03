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

    /// <summary>截 target 窗口（没有就截整块屏幕）。成功返回保存的路径。</summary>
    internal async Task<string?> CaptureScreenshotAsync(nint target)
    {
        if (_shotBusy) return null;
        _shotBusy = true;
        try
        {
            var dev = G.Device;
            GraphicsCaptureItem? item = null;
            string title = "";
            if (target != 0 && OpenWindows.Alive(target) && !ToolboxWindow.IsIconic(target))
            {
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(target);
                item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId { Value = id.Value });
                title = OpenWindows.List().FirstOrDefault(w => w.Hwnd == target)?.Title ?? "";
            }
            if (item == null)
            {
                nint self = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                var display = Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor(MonitorFromWindow(self, 2));
                item = GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId { Value = display.Value });
                title = "Screenshot";
            }
            if (item == null) { Log.Warn("screenshot: no capture item"); return null; }
            var done = new TaskCompletionSource<CanvasRenderTarget?>();
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(dev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            using var session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch { }
            try { session.IsBorderRequired = false; } catch { }
            pool.FrameArrived += (p, _) =>
            {
                using var f = p.TryGetNextFrame();
                if (f == null) return;
                try
                {
                    using var src = CanvasBitmap.CreateFromDirect3D11Surface(dev, f.Surface);
                    var copy = new CanvasRenderTarget(dev, src.SizeInPixels.Width, src.SizeInPixels.Height, 96);
                    using (var ds = copy.CreateDrawingSession()) ds.DrawImage(src);
                    done.TrySetResult(copy);
                }
                catch (Exception ex) { done.TrySetException(ex); }
            };
            session.StartCapture();
            using var frame = await Task.WhenAny(done.Task, Task.Delay(1500)) == done.Task ? await done.Task : null;
            if (frame == null) { Log.Warn("screenshot: no frame"); return null; }
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
            Log.Info($"screenshot saved {path} ({frame.SizeInPixels.Width}x{frame.SizeInPixels.Height})");
            return path;
        }
        catch (Exception ex) { Log.Warn($"screenshot: {ex.Message}"); return null; }
        finally { _shotBusy = false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
}
