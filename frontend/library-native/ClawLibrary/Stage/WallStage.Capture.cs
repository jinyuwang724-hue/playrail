// 截屏：按程序截一整块显示器（Windows.Graphics.Capture），右侧栏的截图格用。
// 原来放在快捷面板里，快捷面板 2026-11 删掉后搬到这里；右侧栏的线条图标画法（SpIcon）也在这里。
using System.Runtime.InteropServices;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static bool _captureAsking;

    /// <summary>
    /// 系统「屏幕截图访问」里本应用的许可（HKCU ConsentStore\graphicsCaptureProgrammatic\&lt;包族名&gt;）：Allow / Deny / Prompt。
    /// 只读，不改。没打包运行（自测）时是 NonPackaged。
    /// </summary>
    internal static string CaptureConsent()
    {
        try
        {
            string name = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\graphicsCaptureProgrammatic\" + name);
            return k?.GetValue("Value") as string ?? "Prompt";
        }
        catch { return "NonPackaged"; }
    }

    /// <summary>
    /// 按程序截别的窗口 / 显示器要用户许可。还没许可时只在游戏库自己在前台时向系统要一次（会弹系统提示，用户点允许）；
    /// 面板 / 游戏在前台时不弹（弹不出来，会一直挂着），直接当没许可。
    /// </summary>
    private async Task<bool> CaptureAccess(bool mayPrompt)
    {
        string c = CaptureConsent();
        if (c is "Allow" or "NonPackaged") return true;
        if (c == "Deny" || !mayPrompt || _captureAsking) return false;
        _captureAsking = true;
        try
        {
            var op = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Programmatic).AsTask();
            var done = await Task.WhenAny(op, Task.Delay(60000)) == op ? await op : Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.UserPromptRequired;
            Log.Info($"capture access request: {done} (consent now {CaptureConsent()})");
            if (done == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
                try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); } catch { }
            return done == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception ex) { Log.Warn($"capture access: {ex.Message}"); return false; }
        finally { _captureAsking = false; }
    }

    /// <summary>游戏库在前台时（启动后）先把截屏许可要下来，免得第一次截图失败。</summary>
    internal void AskCaptureAccessIfForeground()
    {
        if (CaptureConsent() != "Prompt") return;
        if (LibraryWindow.Foreground != WinRT.Interop.WindowNative.GetWindowHandle(_window)) { Log.Info("capture access: not asked (library not in front)"); return; }
        _ = CaptureAccess(mayPrompt: true);
    }

    private static async Task<CanvasBitmap?> GrabFrame(GraphicsCaptureItem? item, int timeoutMs)
    {
        if (item == null) return null;
        var dev = CanvasDevice.GetSharedDevice();
        var done = new TaskCompletionSource<CanvasBitmap?>();
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
                if (!done.TrySetResult(copy)) copy.Dispose();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        };
        session.StartCapture();
        return await Task.WhenAny(done.Task, Task.Delay(timeoutMs)) == done.Task ? await done.Task : null;
    }

    /// <summary>截一块显示器的一帧（monitor 为 0 时取 on，再不行取游戏库所在的显示器），exclude 窗口截的时候排除在外。返回帧和显示器矩形（物理像素）。</summary>
    internal async Task<(CanvasBitmap? Frame, (int L, int T, int W, int H) Rect)> CaptureMonitorFrameAsync(nint exclude, int timeoutMs, nint on = 0, nint monitor = 0)
    {
        nint self = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        nint mon = monitor != 0 ? monitor : MonitorFromWindow(on != 0 ? on : self, 2);
        var mi = new CapMonInfo { Size = Marshal.SizeOf<CapMonInfo>() };
        GetMonitorInfo(mon, ref mi);
        var rect = (mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right - mi.Monitor.Left, mi.Monitor.Bottom - mi.Monitor.Top);
        if (!await CaptureAccess(mayPrompt: false)) { Log.Warn($"capture: no screen-capture permission yet (consent {CaptureConsent()})"); return (null, rect); }
        var did = Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor(mon);
        if (exclude != 0) SetWindowDisplayAffinity(exclude, 0x11);
        try
        {
            var item = GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId { Value = did.Value });
            return (await GrabFrame(item, timeoutMs), rect);
        }
        finally { if (exclude != 0) SetWindowDisplayAffinity(exclude, 0); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CapRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CapMonInfo { public int Size; public CapRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref CapMonInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint GetWindowThreadProcessIdQs(nint hwnd, out uint pid);
    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)] private static extern nint OpenProcessQs(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] private static extern bool CloseHandleQs(nint h);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint p, out long creation, out long exit, out long kernel, out long user);

    private static void SpIcon(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, string glyph, float cx, float cy, float px, Windows.UI.Color c, float stroke = 1.5f) =>
        LineIcons.Draw(ds, glyph, cx, cy, px / 1.15f, c, stroke: stroke);
}
