using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace ClawSleepAnimation;

internal static class SleepOverlay
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    private static bool ActivatePowerOverlay(nint hwnd)
    {
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return true;
        uint own = GetCurrentThreadId(), foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = foreground != 0 && foreground != own && AttachThreadInput(own, foreground, true);
        try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); return GetForegroundWindow() == hwnd; }
        finally { if (attached) AttachThreadInput(own, foreground, false); }
    }
    public static async Task WarmUpAsync()
    {
        var scene = new TransitionScene(); scene.SetFrame(false, .4);
        var warmup = new Window { Width = 2, Height = 2, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, Opacity = 0,
            ShowInTaskbar = false, ShowActivated = false, Content = scene };
        try { warmup.Show(); await Task.Delay(80); } finally { warmup.Close(); }
    }
    private static Window CreateWindow(TransitionScene scene, string title) => new()
    {
        Title = title, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop,
        Width = SystemParameters.VirtualScreenWidth, Height = SystemParameters.VirtualScreenHeight,
        Topmost = true, ShowInTaskbar = false, ShowActivated = false,
        AllowsTransparency = true, Background = Brushes.Transparent,
        Content = scene, Cursor = System.Windows.Input.Cursors.None
    };
    private static async Task AnimateAsync(TransitionScene scene, bool waking, int duration, string? capture = null)
    {
        using var shortcuts = TransitionShortcutFilter.TryCreate();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew(); bool captured = false;
        int frames = 0; double lastFrame = 0, maxInterval = 0;
        EventHandler tick = (_, _) =>
        {
            double progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration, 0, 1);
            double now = clock.Elapsed.TotalMilliseconds;
            if (frames > 0) maxInterval = Math.Max(maxInterval, now - lastFrame);
            lastFrame = now; frames++;
            scene.SetFrame(waking, progress);
            if (capture != null && !captured && progress >= .4)
            {
                captured = true;
                _ = scene.Dispatcher.InvokeAsync(() => Capture(scene, capture), DispatcherPriority.Background);
            }
            if (progress >= 1) finished.TrySetResult();
        };
        scene.SetFrame(waking, 0); CompositionTarget.Rendering += tick;
        try
        {
            // Composition can stop on a desktop switch. Do not retain an overlay
            // indefinitely just because no frame notification is delivered.
            await Task.WhenAny(finished.Task, Task.Delay(duration + 350));
            scene.SetFrame(waking, 1);
            await scene.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        finally
        {
            CompositionTarget.Rendering -= tick;
            Program.Log("motion-played", new { style = "pages-v5", direction = waking ? "wake" : "suspend", frames,
                elapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1), maxFrameIntervalMs = Math.Round(maxInterval, 1),
                completed = finished.Task.IsCompleted });
        }
    }
    public static async Task PlayAsync(bool preview, string? capturePath = null, Action? blackReady = null, Task? retainUntil = null,
        bool wakeAnimation = false, Action<nint>? readyWindow = null, bool activate = false)
    {
        var scene = new TransitionScene();
        var window = CreateWindow(scene, preview ? "Claw · 开合动画预览" : "Claw · 正在休眠");
        window.ShowActivated = activate;
        try
        {
            await GameBarDismiss.PrepareAsync("suspend");
            window.Show();
            if (activate) Program.Log("power-exit-foreground", new { acquired = ActivatePowerOverlay(new WindowInteropHelper(window).Handle) });
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await AnimateAsync(scene, false, TransitionScene.SuspendMilliseconds, capturePath);
            await Task.Delay(32); // Present the final opaque frame before powering the display down.
            blackReady?.Invoke(); readyWindow?.Invoke(new WindowInteropHelper(window).Handle);
            if (retainUntil != null) await retainUntil;
            if (preview) await Task.Delay(300);
            if (wakeAnimation)
            {
                await GameBarDismiss.PrepareAsync("wake");
                await AnimateAsync(scene, true, TransitionScene.WakeMilliseconds);
            }
            else if (preview)
            {
                window.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
                await Task.Delay(200);
            }
        }
        finally { window.Close(); }
    }
    public static async Task PlayWakeAsync()
    {
        var scene = new TransitionScene(); scene.SetFrame(true, 0);
        var window = CreateWindow(scene, "Claw · 正在唤醒");
        try
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            // Cover the desktop before awaiting Game Bar inspection or input.
            await GameBarDismiss.PrepareAsync("wake");
            await AnimateAsync(scene, true, TransitionScene.WakeMilliseconds);
        }
        finally { window.Close(); }
    }
    internal static async Task<string> PlayLoadingAsync(Func<nint, CancellationToken, Task<string>> waitForReady, TimeSpan? timeout = null)
    {
        var scene = new TransitionScene(); scene.SetLoading(0);
        var window = CreateWindow(scene, "Claw"); window.ShowActivated = true;
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(45));
        window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { cancel.Cancel(); e.Handled = true; } };
        var clock = Stopwatch.StartNew(); Stopwatch? exit = null;
        EventHandler frame = (_, _) => scene.SetLoading(clock.Elapsed.TotalSeconds, exit == null ? 0 : exit.Elapsed.TotalMilliseconds / 260);
        string outcome;
        CompositionTarget.Rendering += frame;
        try
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(32);
            try { outcome = await waitForReady(new WindowInteropHelper(window).Handle, cancel.Token); }
            catch (OperationCanceledException) { outcome = "cancelled-or-timeout"; }
            if (clock.ElapsedMilliseconds < 850) await Task.Delay(850 - (int)clock.ElapsedMilliseconds);
            exit = Stopwatch.StartNew();
            await Task.Delay(280); scene.SetLoading(clock.Elapsed.TotalSeconds, 1);
            Program.Log("startup-overlay-finished", new { outcome, elapsedMs = clock.ElapsedMilliseconds });
            return outcome;
        }
        finally { CompositionTarget.Rendering -= frame; window.Close(); }
    }
    private static void Capture(FrameworkElement visual, string path, bool logicalPixels = false)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        int width = Math.Max(1, (int)(visual.ActualWidth * (logicalPixels ? 1 : dpi.DpiScaleX)));
        int height = Math.Max(1, (int)(visual.ActualHeight * (logicalPixels ? 1 : dpi.DpiScaleY)));
        var bitmap = new RenderTargetBitmap(width, height,
            logicalPixels ? 96 : dpi.PixelsPerInchX, logicalPixels ? 96 : dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    internal static void RenderPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (bool waking in new[] { false, true })
        {
            var scene = new TransitionScene { Width = 960, Height = 600 };
            scene.Measure(new Size(960, 600)); scene.Arrange(new Rect(0, 0, 960, 600));
            int frames = (int)Math.Ceiling((waking ? TransitionScene.WakeMilliseconds : TransitionScene.SuspendMilliseconds) * .03);
            for (int i = 0; i <= frames; i++)
            {
                scene.SetFrame(waking, (double)i / frames); scene.UpdateLayout();
                Capture(scene, Path.Combine(directory, $"{(waking ? "wake" : "suspend")}-{i:D3}.png"), true);
            }
        }
    }
    internal static void RenderLoading(string directory)
    {
        Directory.CreateDirectory(directory);
        var scene = new TransitionScene { Width = 768, Height = 480 };
        scene.Measure(new Size(768, 480)); scene.Arrange(new Rect(0, 0, 768, 480));
        for (int i = 0; i < 48; i++)
        {
            scene.SetLoading(2.2 + 2.2 * i / 48); scene.UpdateLayout();
            Capture(scene, Path.Combine(directory, $"loading-{i:D3}.png"), true);
        }
    }
    internal static void RenderStartupMark(string path)
    {
        var scene = new TransitionScene { Width = 1280, Height = 800 };
        scene.Measure(new Size(1280, 800)); scene.Arrange(new Rect(0, 0, 1280, 800));
        scene.SetLoading(1.1); scene.UpdateLayout(); Capture(scene, path, true);
    }
}
