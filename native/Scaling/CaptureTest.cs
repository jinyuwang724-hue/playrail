using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClawScaling;

internal static class CaptureTest
{
    [StructLayout(LayoutKind.Sequential)] struct Paint { public nint Dc; public int Erase; public Native.Rect Rect; public int Restore, IncUpdate; public long R1, R2, R3, R4; }
    [DllImport("user32.dll")] static extern nint BeginPaint(nint window, out Paint paint);
    [DllImport("user32.dll")] static extern bool EndPaint(nint window, ref Paint paint);
    [DllImport("user32.dll")] static extern int FillRect(nint dc, ref Native.Rect rect, nint brush);
    [DllImport("gdi32.dll")] static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    static nint PaintWindow(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0f) {
            nint dc = BeginPaint(hwnd, out var paint);
            for (int y = 0; y < 240; y += 10) for (int x = 0; x < 320; x += 10) {
                var rect = new Native.Rect { Left = x, Top = y, Right = x + 10, Bottom = y + 10 };
                nint brush = CreateSolidBrush(((x / 10 + y / 10) % 2 == 0) ? 0x0033ddeeU : 0x00cc3311U);
                FillRect(dc, ref rect, brush); DeleteObject(brush);
            }
            EndPaint(hwnd, ref paint); return 0;
        }
        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory); nint source = Native.TestWindow(PaintWindow, 320, 240);
        try {
            Native.Pump();
            using var command = new AutoResetEvent(false);
            // Exercise capture creation twice to verify HWND dispatch and cleanup across game changes.
            var results = new List<object>();
            for (int iteration = 0; iteration < 2; iteration++) {
                using var capture = new Capture(source); var timer = Stopwatch.StartNew();
                var settings = new Settings(true, "fit", iteration == 0 ? "fsr" : "nis", .5f, source.ToInt64());
                while (capture.Frames == 0 && timer.ElapsedMilliseconds < 10000) { Native.Pump(); capture.Draw(settings, false, true); Native.Wait(command, capture.FrameReady, 30); }
                if (capture.Frames == 0 || capture.Width != 320 || capture.Height != 240) throw new Exception("Window capture did not produce correct client-size frames");
                capture.SaveDiagnosticFrame(Path.Combine(directory, "wgc-" + settings.Filter + ".bmp"));
                if (capture.Draw(settings, true, true) || capture.Visible) throw new Exception("Menu pause did not release output");
                if (!capture.Draw(settings with { Mode = "fill" }, false, true)) throw new Exception("Static frame did not recover after pause");
                results.Add(new { iteration, filter = settings.Filter, input = new[] { capture.Width, capture.Height }, output = new[] { capture.OutputWidth, capture.OutputHeight }, frames = capture.Frames });
            }
            File.WriteAllText(Path.Combine(directory, "capture-test.json"), JsonSerializer.Serialize(new { success = true, realWgcCapture = true, titleBarExcluded = true, menuPause = true, staticFrameResume = true, recreatedSession = true, results }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Real window capture test passed."); return 0;
        } finally { Native.DestroyWindow(source); Native.Pump(); }
    }
}
