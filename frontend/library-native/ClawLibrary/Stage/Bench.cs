// 自测（--bench）：按固定脚本跑一遍（静止、右摇杆平移、十字键走位、展开 / 收起、换页签、进出合集），
// 每段用 DWM 合成时间戳量帧间隔（后台线程 2 ms 轮询 qpcCompose），同时记进程 CPU 与内存，写进日志和 logs\bench.json。
// --state=expand|collection|styleb|tab 则只把墙摆到某个状态停住（截图对比用）。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public static class Bench
{
    private sealed class FrameProbe
    {
        private readonly List<double> _intervals = new();
        private volatile bool _stop;
        private Thread? _t;
        public void Start()
        {
            _t = new Thread(() =>
            {
                ulong lastFrame = 0; long lastQpc = 0;
                while (!_stop)
                {
                    var (frame, qpc) = ReadCompose();
                    if (frame != lastFrame)
                    {
                        if (lastFrame != 0 && lastQpc != 0 && qpc > lastQpc)
                            lock (_intervals) _intervals.Add((qpc - lastQpc) * 1000.0 / Stopwatch.Frequency);
                        lastFrame = frame; lastQpc = qpc;
                    }
                    Thread.Sleep(1);
                }
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            _t.Start();
        }
        public List<double> Stop() { _stop = true; _t?.Join(); lock (_intervals) return new List<double>(_intervals); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct TimingHead
    {
        public uint cbSize;
        public uint rn, rd;
        public ulong qpcRefreshPeriod;
        public uint cn, cd;
        public ulong qpcVBlank, cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose, cFrame;
        public fixed byte rest[400];
    }

    [DllImport("dwmapi.dll")] private static extern unsafe int DwmGetCompositionTimingInfo(IntPtr hwnd, void* info);

    private static unsafe (ulong frame, long qpc) ReadCompose()
    {
        TimingHead h = default;
        // cbSize 必须等于完整结构体大小（与 Dwm.TimingInfo 一致）
        h.cbSize = DwmInfoSize;
        if (DwmGetCompositionTimingInfo(IntPtr.Zero, &h) != 0) return (0, 0);
        return (h.cFrame, (long)h.qpcCompose);
    }

    private static uint DwmInfoSize => Dwm.InfoSize;

    private sealed record Phase(string Name, double Ms, double FpsAvg, double P50, double P95, double P99, double Max, int Frames, int Over20, int Over34, double CpuPct, long WorkingSetMb, long PrivateMb);

    public static async Task RunAsync(WallStage s)
    {
        var results = new List<Phase>();
        try
        {
            await Task.Delay(2500);
            int cores = Environment.ProcessorCount;
            async Task Measure(string name, Func<Task> body)
            {
                var proc = Process.GetCurrentProcess();
                proc.Refresh();
                var cpu0 = proc.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                var probe = new FrameProbe();
                probe.Start();
                await body();
                var iv = probe.Stop();
                sw.Stop();
                proc.Refresh();
                double cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / cores * 100;
                iv.Sort();
                double P(double q) => iv.Count == 0 ? 0 : iv[Math.Min(iv.Count - 1, (int)Math.Floor(q * iv.Count))];
                var ph = new Phase(name, sw.Elapsed.TotalMilliseconds, iv.Count == 0 ? 0 : iv.Count / (iv.Sum() / 1000), P(0.5), P(0.95), P(0.99), iv.Count == 0 ? 0 : iv[^1], iv.Count,
                    iv.Count(v => v > 20), iv.Count(v => v > 34), cpu, proc.WorkingSet64 >> 20, proc.PrivateMemorySize64 >> 20);
                results.Add(ph);
                Log.Info($"bench {ph}");
            }
            Task Wait(int ms) => Task.Delay(ms);

            await Measure("idle", () => Wait(4000));
            await Measure("pan-right-stick", async () => { s.PanVelocity = new System.Numerics.Vector2(1700, 0); s.RequestPanLoop(); await Wait(3000); s.PanVelocity = default; await Wait(300); });
            await Measure("pan-diagonal-stick", async () => { s.PanVelocity = new System.Numerics.Vector2(-1200, 900); s.RequestPanLoop(); await Wait(2500); s.PanVelocity = default; await Wait(300); });
            await Measure("dpad-nav", async () =>
            {
                for (int i = 0; i < 8; i++) { s.Move(Direction.Right); await Wait(180); }
                for (int i = 0; i < 4; i++) { s.Move(Direction.Down); await Wait(180); }
                for (int i = 0; i < 6; i++) { s.Move(Direction.Left); await Wait(180); }
                await Wait(500);
            });
            await Measure("expand-collapse", async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    s.Activate(); await Wait(1100);
                    s.Back(); await Wait(1100);
                    s.Move(Direction.Right); await Wait(300);
                }
            });
            await Measure("tab-switch-wave", async () =>
            {
                s.SwitchTab(1); await Wait(1600);
                s.SwitchTab(1); await Wait(1600);
                s.SwitchTab(-2); await Wait(1600);
            });
            await Measure("collection-push-pop", async () =>
            {
                if (s.FocusFirstCollection()) { await Wait(600); s.Activate(); await Wait(1800); s.Back(); await Wait(1800); }
            });
            await Measure("idle-after", () => Wait(4000));
            var sb = new StringBuilder("[\n");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.Append(FormattableString.Invariant($"  {{\"phase\":\"{r.Name}\",\"ms\":{r.Ms:F0},\"frames\":{r.Frames},\"fps\":{r.FpsAvg:F1},\"p50\":{r.P50:F2},\"p95\":{r.P95:F2},\"p99\":{r.P99:F2},\"max\":{r.Max:F2},\"over20ms\":{r.Over20},\"over34ms\":{r.Over34},\"cpuPct\":{r.CpuPct:F2},\"workingSetMB\":{r.WorkingSetMb},\"privateMB\":{r.PrivateMb}}}"));
                sb.Append(i < results.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("]\n");
            await File.WriteAllTextAsync(Path.Combine(App.CacheDir, "logs", "bench.json"), sb.ToString());
            Log.Info("bench done");
            if (Program.Args.Contains("--exit")) Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex) { Log.Error("bench", ex); }
    }

    /// <summary>把墙摆到某个状态（截图用）。</summary>
    public static async Task StateAsync(WallStage s, string state)
    {
        try
        {
            await Task.Delay(1800);
            switch (state)
            {
                case "expand": s.Activate(); break;
                case "collection": if (s.FocusFirstCollection()) { await Task.Delay(700); s.Activate(); } break;
                case "styleb": s.ToggleTitleStyle(); break;
                case "tab": s.SwitchTab(1); break;
                case "nav": for (int i = 0; i < 3; i++) { s.Move(Direction.Right); await Task.Delay(250); } break;
                case "rail" or "railtab" or "settings" or "search": s.RailTestState(state); break;
            }
            Log.Info($"state {state} reached");
        }
        catch (Exception ex) { Log.Error("state", ex); }
    }
}
