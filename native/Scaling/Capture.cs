using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace ClawScaling;

internal sealed class Capture : IDisposable
{
    public nint Target { get; }
    readonly Gpu gpu = null!;
    readonly nint output;
    readonly GraphicsCaptureItem item = null!;
    readonly IDirect3DDevice captureDevice = null!;
    readonly Direct3D11CaptureFramePool pool = null!;
    readonly GraphicsCaptureSession session = null!;
    readonly IDXGISwapChain1 swap = null!;
    readonly Stopwatch firstFrame = Stopwatch.StartNew();
    ID3D11RenderTargetView? targetView;
    ID3D11Texture2D? source;
    ID3D11ShaderResourceView? sourceView;
    Native.Rect monitor;
    Layout? layout;
    int width, height, poolWidth, poolHeight;
    bool shown, disposed;
    Settings? lastSettings;
    byte[]? diagnosticPixels;
    public bool Closed { get; private set; }
    public AutoResetEvent FrameReady { get; } = new(false);
    public long Frames { get; private set; }
    public string EffectiveFilter => gpu.EffectiveFilter;
    public string Note => gpu.Note;
    public int Width => width;
    public int Height => height;
    public int OutputWidth => monitor.Width;
    public int OutputHeight => monitor.Height;
    public bool Visible => shown;
    public Capture(nint window)
    {
        try {
        if (!GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("系统不支持窗口捕获");
        Target = window; monitor = Native.Monitor(window);
        gpu = new Gpu();
        output = Native.OutputWindow(WindowMessage);
        Native.SetWindowPos(output, 0, monitor.Left, monitor.Top, monitor.Width, monitor.Height, 0x14);
        using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        swap = factory.CreateSwapChainForHwnd(gpu.Device, output, new SwapChainDescription1 {
            Width = (uint)monitor.Width, Height = (uint)monitor.Height, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0), BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
            SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
        });
        using (var display = swap.GetContainingOutput()) {
            using var display6 = display.QueryInterfaceOrNull<IDXGIOutput6>();
            if (display6 != null && display6.Description1.ColorSpace.ToString().Contains("G2084"))
                throw new InvalidOperationException("当前缩放后台支持 SDR；请先关闭此显示器的 HDR，再开启缩放");
        }
        using (var texture = swap.GetBuffer<ID3D11Texture2D>(0)) targetView = gpu.Device.CreateRenderTargetView(texture);
        using var dxgi = gpu.Device.QueryInterface<IDXGIDevice>();
        captureDevice = Native.CaptureDevice(dxgi);
        item = Native.CaptureItem(window);
        poolWidth = item.Size.Width; poolHeight = item.Size.Height;
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        pool.FrameArrived += OnFrame; item.Closed += OnClosed;
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();
        } catch { Dispose(); throw; }
    }
    void OnFrame(Direct3D11CaptureFramePool sender, object args) { if (!disposed) try { FrameReady.Set(); } catch (ObjectDisposedException) { } }
    void OnClosed(GraphicsCaptureItem sender, object args) { Closed = true; }
    nint WindowMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x21) return 3; // MA_NOACTIVATE: the game keeps keyboard and controller focus.
        if (message is >= 0x200 and <= 0x20E && layout != null && width > 0 && height > 0) {
            int x = (short)(lParam.ToInt64() & 0xffff), y = (short)((lParam.ToInt64() >> 16) & 0xffff);
            if (message is 0x20A or 0x20E) { var p = new Native.Point { X = x, Y = y }; Native.ScreenToClient(hwnd, ref p); x = p.X; y = p.Y; }
            var mapped = Geometry.MapPoint(layout, x, y, width, height);
            var client = new Native.Point { X = mapped.X, Y = mapped.Y };
            if (message is 0x20A or 0x20E) Native.ClientToScreen(Target, ref client);
            if (message is 0x201 or 0x204 or 0x207) Native.SetCapture(hwnd);
            if (message is 0x202 or 0x205 or 0x208) Native.ReleaseCapture();
            Native.PostMessage(Target, message, wParam, (nint)((client.Y << 16) | (client.X & 0xffff)));
            return 0;
        }
        if (message == 0x10) { Closed = true; return 0; }
        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }
    public void Hide() { if (shown) Native.ShowWindow(output, 0); shown = false; }
    public bool Draw(Settings settings, bool paused, bool diagnostic = false)
    {
        if (disposed || Closed || !Native.IsWindow(Target)) { Hide(); return false; }
        if (paused || Native.IsIconic(Target) || (!diagnostic && Native.GetForegroundWindow() != Target)) { firstFrame.Restart(); Hide(); DrainFrames(); return false; }
        using var frame = pool.TryGetNextFrame();
        if (frame == null) {
            if (Frames == 0 && firstFrame.Elapsed.TotalSeconds > 15) throw new InvalidOperationException("未收到游戏画面，请改用窗口或无边框模式后重新开启缩放");
            if (sourceView != null && (lastSettings != settings || !shown)) return Render(settings, diagnostic);
            return shown;
        }
        if (frame.ContentSize.Width != poolWidth || frame.ContentSize.Height != poolHeight) {
            poolWidth = frame.ContentSize.Width; poolHeight = frame.ContentSize.Height;
            if (poolWidth > 0 && poolHeight > 0) pool.Recreate(captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, frame.ContentSize);
            Hide(); return false;
        }
        if (!Native.GetClientRect(Target, out var client) || client.Width <= 0 || client.Height <= 0) { Hide(); return false; }
        var origin = new Native.Point(); Native.ClientToScreen(Target, ref origin);
        var bounds = Native.Bounds(Target);
        using var captured = Native.Texture(frame.Surface);
        int x = Math.Clamp(origin.X - bounds.Left, 0, Math.Max(0, frame.ContentSize.Width - 1));
        int y = Math.Clamp(origin.Y - bounds.Top, 0, Math.Max(0, frame.ContentSize.Height - 1));
        int w = Math.Min(client.Width, frame.ContentSize.Width - x), h = Math.Min(client.Height, frame.ContentSize.Height - y);
        if (w <= 0 || h <= 0) return false;
        if (w != width || h != height || source == null) {
            sourceView?.Dispose(); source?.Dispose(); width = w; height = h;
            source = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));
            sourceView = gpu.Device.CreateShaderResourceView(source);
        }
        gpu.Context.CopySubresourceRegion(source, 0, 0, 0, 0, captured, 0, new Box(x, y, 0, x + w, y + h, 1));
        return Render(settings, diagnostic);
    }
    bool Render(Settings settings, bool diagnostic)
    {
        var currentMonitor = Native.Monitor(Target);
        if (currentMonitor.Width != monitor.Width || currentMonitor.Height != monitor.Height) {
            targetView?.Dispose();
            swap.ResizeBuffers(2, (uint)currentMonitor.Width, (uint)currentMonitor.Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            using var back = swap.GetBuffer<ID3D11Texture2D>(0); targetView = gpu.Device.CreateRenderTargetView(back);
        }
        monitor = currentMonitor;
        layout = Geometry.Calculate(width, height, monitor.Width, monitor.Height, settings.Mode);
        var view = gpu.Process(sourceView!, width, height, layout, settings);
        gpu.Present(view, targetView!, monitor.Width, monitor.Height, layout, gpu.EffectiveFilter);
        if (diagnostic) {
            using var rendered = swap.GetBuffer<ID3D11Texture2D>(0);
            diagnosticPixels = SelfTest.Read(gpu, rendered, monitor.Width, monitor.Height);
            for (int i = 0; i < diagnosticPixels.Length; i += 4) (diagnosticPixels[i], diagnosticPixels[i + 2]) = (diagnosticPixels[i + 2], diagnosticPixels[i]);
        }
        swap.Present(1, PresentFlags.None).CheckError(); Frames++;
        if (!diagnostic) { Native.SetWindowPos(output, (nint)(-1), monitor.Left, monitor.Top, monitor.Width, monitor.Height, 0x10 | 0x40); shown = true; }
        lastSettings = settings; return true;
    }
    public void SaveDiagnosticFrame(string path)
    {
        byte[] pixels = diagnosticPixels ?? throw new InvalidOperationException("No diagnostic frame");
        if (pixels.Where((_, i) => i % 4 != 3).Max() < 100) throw new InvalidOperationException("Captured GPU output is blank");
        SelfTest.SaveBmp(path, pixels, monitor.Width, monitor.Height);
    }
    void DrainFrames() { for (int i = 0; i < 2; i++) { using var frame = pool.TryGetNextFrame(); if (frame == null) break; } }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Hide(); if (pool != null) pool.FrameArrived -= OnFrame; if (item != null) item.Closed -= OnClosed;
        session?.Dispose(); pool?.Dispose(); targetView?.Dispose(); sourceView?.Dispose(); source?.Dispose();
        swap?.Dispose(); gpu?.Dispose(); if (output != 0) Native.DestroyWindow(output); captureDevice?.Dispose(); FrameReady.Dispose();
    }
}
