using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClawScaling;

internal static class Program
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    static readonly ConcurrentQueue<JsonElement> Commands = new();
    static readonly AutoResetEvent CommandReady = new(false);
    static readonly object OutputGate = new();
    static void Emit(object value) { lock (OutputGate) { Console.WriteLine(JsonSerializer.Serialize(value, Json)); Console.Out.Flush(); } }
    [STAThread]
    static int Main(string[] args)
    {
        Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 2 && args[0] == "--self-test") { try { return SelfTest.Run(args[1]); } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }
        if (args.Length == 2 && args[0] == "--capture-test") { try { return CaptureTest.Run(args[1]); } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }
        Settings settings = new(); Capture? capture = null; bool paused = false, stopping = false, configured = false;
        string phase = "缩放已关闭", error = "", lastStatus = "";
        var startup = Stopwatch.StartNew(); long lastEmit = 0;
        Task.Run(() => {
            try {
                string? line;
                while ((line = Console.ReadLine()) != null) {
                    if (line.Length > 16384) continue;
                    try { using var document = JsonDocument.Parse(line); Commands.Enqueue(document.RootElement.Clone()); CommandReady.Set(); } catch (JsonException) { }
                }
            } finally { Commands.Enqueue(JsonSerializer.SerializeToElement(new { command = "stop" })); CommandReady.Set(); }
        });
        object Status() => new {
            type = "status", enabled = settings.Enabled, mode = settings.Mode, filter = settings.Filter, sharpness = settings.Sharpness,
            target = capture?.Target.ToInt64() ?? settings.Target, phase, error, active = capture?.Visible ?? false,
            effectiveFilter = capture?.EffectiveFilter, note = capture?.Note, inputWidth = capture?.Width ?? 0, inputHeight = capture?.Height ?? 0,
            outputWidth = capture?.OutputWidth ?? 0, outputHeight = capture?.OutputHeight ?? 0, frames = capture?.Frames ?? 0,
            backend = "Windows Graphics Capture / Direct3D 11", paused,
            foregroundWindow = Native.GetForegroundWindow().ToInt64(),
        };
        try {
            while (!stopping) {
                Native.Pump();
                while (Commands.TryDequeue(out var command)) {
                    string? id = command.TryGetProperty("id", out var requestId) ? requestId.GetString() : null;
                    try {
                        switch (command.GetProperty("command").GetString()) {
                            case "configure":
                                var next = Geometry.Validate(command.GetProperty("settings").Deserialize<Settings>(Json) ?? new());
                                configured = true;
                                if (next == settings) break;
                                if (!next.Enabled || (next.Target != settings.Target && next.Target != 0)) { capture?.Dispose(); capture = null; }
                                settings = next; error = "";
                                if (!settings.Enabled) phase = "缩放已关闭";
                                break;
                            case "pause": paused = command.GetProperty("paused").GetBoolean(); if (paused) capture?.Hide(); break;
                            case "targets": Emit(new { type = "targets", id, targets = Native.Targets() }); continue;
                            case "status": break;
                            case "stop": stopping = true; capture?.Dispose(); capture = null; break;
                            default: throw new ArgumentException("不支持的缩放操作");
                        }
                        if (settings.Enabled && capture == null && !stopping) {
                            var available = Native.Targets();
                            long window = settings.Target != 0 ? settings.Target : available.FirstOrDefault(x => x.Foreground)?.Hwnd ?? 0;
                            if (window == 0) { phase = "等待游戏窗口，请在游戏中开启或选择窗口"; }
                            else {
                                if (!available.Any(x => x.Hwnd == window)) throw new InvalidOperationException("所选游戏窗口已关闭，请重新选择");
                                capture = new Capture((nint)window); phase = "正在捕获游戏画面";
                            }
                        }
                        if (id != null) Emit(new { type = "reply", id, ok = true, state = Status() });
                    } catch (Exception e) {
                        capture?.Dispose(); capture = null; settings = settings with { Enabled = false }; error = e.Message; phase = "缩放已暂停";
                        Emit(new { type = "reply", id, ok = false, error, state = Status() });
                    }
                }
                if (capture != null) {
                    try {
                        if (capture.Closed || !Native.IsWindow(capture.Target)) { capture.Dispose(); capture = null; settings = settings with { Enabled = false, Target = 0 }; phase = "游戏已退出，缩放已释放"; }
                        else { bool active = capture.Draw(settings, paused); phase = active ? "画面缩放已生效" : paused ? "菜单中，缩放暂时让出画面" : "等待返回游戏"; }
                    } catch (Exception e) { capture?.Dispose(); capture = null; settings = settings with { Enabled = false }; error = e.Message; phase = "捕获失败，已释放缩放画面"; }
                }
                var status = Status(); string encoded = JsonSerializer.Serialize(status, Json);
                if (configured && (startup.ElapsedMilliseconds - lastEmit >= 750 || (lastStatus != encoded && lastStatus.Length == 0))) { Emit(status); lastStatus = encoded; lastEmit = startup.ElapsedMilliseconds; }
                // Pump window messages as well as frame and IPC events, without busy waiting.
                Native.Wait(CommandReady, capture?.FrameReady, 50);
            }
            return 0;
        } finally { capture?.Dispose(); }
    }
}
