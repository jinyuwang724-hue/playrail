// 测试用：用 Windows.Graphics.Capture 录本窗口 DWM 合成出来的每一帧（显示器刷新率），半分辨率存 JPEG。
// 用来逐帧检查形变动画（真实速度下的闪烁、跳变），而不是靠截屏工具慢慢截。只由 Bench 的 rec* 状态调用。
using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ClawLibrary.Stage;

public sealed class FrameRecorder
{
    private readonly List<(CanvasRenderTarget rt, long qpc)> _frames = new();
    private readonly object _gate = new();
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private CanvasDevice _dev = null!;
    private bool _on;
    public long StartQpc;
    /// <summary>原分辨率（看清晰度用）；默认半分辨率。</summary>
    public bool Full;

    public static double QpcMs(long qpc) => qpc * 1000.0 / Stopwatch.Frequency;

    public bool Start(Window window)
    {
        try
        {
            _dev = CanvasDevice.GetSharedDevice();
            var item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId { Value = window.AppWindow.Id.Value });
            if (item == null) { Log.Warn("recorder: no capture item"); return false; }
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_dev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(item);
            try { _session.IsCursorCaptureEnabled = false; } catch { }
            try { _session.IsBorderRequired = false; } catch { }
            StartQpc = Stopwatch.GetTimestamp();
            _on = true;
            _session.StartCapture();
            return true;
        }
        catch (Exception ex) { Log.Error("recorder start", ex); return false; }
    }

    private void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null || !_on) return;
        try
        {
            long qpc = (long)(frame.SystemRelativeTime.TotalSeconds * Stopwatch.Frequency);
            using var bmp = CanvasBitmap.CreateFromDirect3D11Surface(_dev, frame.Surface);
            var sz = bmp.SizeInPixels;
            uint div = Full ? 1u : 2u;
            var rt = new CanvasRenderTarget(_dev, sz.Width / div, sz.Height / div, 96);
            using (var ds = rt.CreateDrawingSession())
                ds.DrawImage(bmp, new Windows.Foundation.Rect(0, 0, sz.Width / div, sz.Height / div), new Windows.Foundation.Rect(0, 0, sz.Width, sz.Height));
            lock (_gate) _frames.Add((rt, qpc));
        }
        catch (Exception ex) { Log.Error("recorder frame", ex); }
    }

    /// <summary>停下并把帧写到 dir（文件名带相对 marks 的毫秒），返回帧数。</summary>
    public async Task<int> StopAndSaveAsync(string dir, long markQpc)
    {
        _on = false;
        _session?.Dispose();
        _pool?.Dispose();
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        List<(CanvasRenderTarget rt, long qpc)> frames;
        lock (_gate) frames = _frames.ToList();
        int i = 0;
        var lines = new List<string>();
        foreach (var (rt, qpc) in frames)
        {
            double ms = QpcMs(qpc - markQpc);
            string name = $"f{i:D3}_{ms:+0000;-0000}ms.jpg";
            await rt.SaveAsync(Path.Combine(dir, Full ? Path.ChangeExtension(name, ".png") : name), Full ? CanvasBitmapFileFormat.Png : CanvasBitmapFileFormat.Jpeg, 0.85f);
            lines.Add($"{i}\t{ms:F1}");
            rt.Dispose();
            i++;
        }
        File.WriteAllLines(Path.Combine(dir, "frames.tsv"), lines);
        return frames.Count;
    }
}
