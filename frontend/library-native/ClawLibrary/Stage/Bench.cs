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
                s.SwitchTab(1); await Wait(1500);
                if (s.FocusFirstCollection()) { await Wait(600); s.Activate(); await Wait(1800); s.Back(); await Wait(1800); }
                s.SwitchTab(-1); await Wait(1500);
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
    /// <summary>压力测试：经手柄投递路径反复 墙 ⇄ 控制中心，换焦点、快速连按、动画中途按；每次打开控制中心后检查游戏库卡封面。</summary>
    private static async Task CycleAsync(WallStage s)
    {
        await Task.Delay(1500);
        var pad = s.Pad;
        if (pad == null) { Log.Warn("cycle: no pad"); return; }
        var rnd = new Random(7);
        int bad = 0, runs = 0;
        for (int i = 0; i < 100; i++)
        {
            // 墙上换焦点（偶尔走远一点，让卡被回收 / 重建）
            int steps = rnd.Next(0, i % 10 == 9 ? 14 : 4);
            for (int k = 0; k < steps; k++) { s.Move((Direction)rnd.Next(0, 4)); await Task.Delay(140); }
            await Task.Delay(rnd.Next(50, 400));
            pad.InjectPress(Input.GamepadPoller.PadMenu);           // 出场：墙 → 控制中心
            int mode = i % 4;
            if (mode == 1) { await Task.Delay(rnd.Next(60, 300)); pad.InjectPress(Input.GamepadPoller.PadB); }   // 动画中途按一下（快进）
            await Task.Delay(1300);
            if (!s.ControlCenterOpen) { pad.InjectPress(Input.GamepadPoller.PadMenu); await Task.Delay(1300); }
            var (ok, info) = s.CcCoverState();
            runs++;
            if (!ok) bad++;
            Log.Info($"cycle {i}: {(ok ? "ok" : "MISSING")} {info}");
            // 回墙：A（进场）或 B；偶尔动画中途再按 ≡ 打断
            pad.InjectPress(mode == 2 ? Input.GamepadPoller.PadB : Input.GamepadPoller.PadA);
            if (mode == 3) { await Task.Delay(rnd.Next(40, 250)); pad.InjectPress(Input.GamepadPoller.PadMenu); await Task.Delay(1300); pad.InjectPress(Input.GamepadPoller.PadA); }
            await Task.Delay(1200);
        }
        Log.Info($"cycle done: {runs} opens, {bad} missing covers");
    }

    /// <summary>保存测试（可逆）：进性能页，走到「短时加成」，+1 → +2，等它存上，再改回 +1。</summary>
    private static async Task PerfSaveTestAsync(WallStage s)
    {
        await Task.Delay(1500);
        async Task Step(int ms, Action a) { a(); await Task.Delay(ms); }
        await Step(1500, s.ToggleControlCenter);
        await Step(700, () => s.Move(Direction.Right));
        await Step(2500, s.Activate);
        for (int i = 0; i < 6; i++) await Step(200, () => s.Move(Direction.Down));
        Log.Info("perfsave: +1");
        await Step(2500, () => s.Move(Direction.Right));
        Log.Info("perfsave: -1");
        await Step(2500, () => s.Move(Direction.Left));
        Log.Info("perfsave: done");
    }

    /// <summary>性能页：开控制中心 → 右一格到「性能」→ A 进入 → 往下走到「编辑这档曲线」→ A 进编辑 → 选点（只换点，不改值，不会保存）→ B 退出 → B 回控制中心。</summary>
    private static async Task RecPerfAsync(WallStage s)
    {
        await Task.Delay(1500);
        var rec = new FrameRecorder();
        if (!rec.Start(s.HostWindow)) return;
        await Task.Delay(300);
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        async Task Step(int ms, Action a) { a(); await Task.Delay(ms); }
        await Step(1500, s.ToggleControlCenter);
        await Step(700, () => s.Move(Direction.Right));
        await Step(3000, s.Activate);
        for (int i = 0; i < 10; i++) await Step(220, () => s.Move(Direction.Down));
        await Step(800, s.Activate);
        await Step(500, () => s.Move(Direction.Left));
        await Step(800, () => s.Move(Direction.Left));
        await Step(800, s.Back);
        await Step(1200, s.Back);
        int n = await rec.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", "perf"), mark);
        Log.Info($"rec perf: {n} frames");
    }

    /// <summary>可打断性：开 → 80 ms 内连按两次右 → ↓ 后 120 ms 反悔 ↑ → 再 ↓ 停住。</summary>
    private static async Task RecSpringAsync(WallStage s)
    {
        await Task.Delay(1500);
        var rec = new FrameRecorder();
        if (!rec.Start(s.HostWindow)) return;
        await Task.Delay(300);
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        async Task Step(int ms, Action a) { a(); await Task.Delay(ms); }
        await Step(1500, s.ToggleControlCenter);
        await Step(80, () => s.Move(Direction.Right));
        await Step(900, () => s.Move(Direction.Right));
        await Step(120, () => s.Move(Direction.Down));
        await Step(120, () => s.Move(Direction.Up));
        await Step(900, () => s.Move(Direction.Down));
        int n = await rec.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", "spring"), mark);
        Log.Info($"rec spring: {n} frames");
    }

    /// <summary>控制中心外围：开 → 滑条右两格（圆角卡）→ ↓ 功能坞 → → ← → A 电源菜单 → ↑ → B → 一路右到头像。配合 --power-dry。</summary>
    private static async Task RecChromeAsync(WallStage s)
    {
        await Task.Delay(1500);
        var rec = new FrameRecorder();
        if (!rec.Start(s.HostWindow)) return;
        await Task.Delay(300);
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        async Task Step(int ms, Action a) { a(); await Task.Delay(ms); }
        await Step(1500, s.ToggleControlCenter);
        await Step(500, () => s.Move(Direction.Right));
        await Step(800, () => s.Move(Direction.Right));
        await Step(600, () => s.Move(Direction.Down));
        await Step(500, () => s.Move(Direction.Right));
        await Step(500, () => s.Move(Direction.Left));
        await Step(800, s.Activate);
        await Step(800, () => s.Move(Direction.Up));
        await Step(600, s.Back);
        for (int i = 0; i < 4; i++) await Step(300, () => s.Move(Direction.Right));
        await Task.Delay(600);
        int n = await rec.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", "chrome"), mark);
        Log.Info($"rec chrome: {n} frames");
    }

    /// <summary>录真实速度的每一帧：经手柄投递路径按 ≡（出场）/ A（进场），各录 1.6 s。</summary>
    private static async Task RecordAsync(WallStage s, string state)
    {
        await Task.Delay(1500);   // 等控制中心预热完（真实使用时早就预热好了）
        var pad = s.Pad;
        // --recmoves=RRD…：录之前先在墙上按方向走几步（换不同形状的 hero 卡）
        if (Program.Args.FirstOrDefault(a => a.StartsWith("--recmoves=")) is { } mv)
            foreach (char ch in mv[11..]) { s.Move(ch switch { 'L' => Direction.Left, 'U' => Direction.Up, 'D' => Direction.Down, _ => Direction.Right }); await Task.Delay(400); }
        void Press(ushort b) { if (pad != null) pad.InjectPress(b); }
        async Task One(string name, ushort button)
        {
            var rec = new FrameRecorder();
            if (!rec.Start(s.HostWindow)) return;
            await Task.Delay(300);
            long mark = System.Diagnostics.Stopwatch.GetTimestamp();
            Log.Info($"rec {name}: press");
            Press(button);
            await Task.Delay(1600);
            int n = await rec.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", name), mark);
            Log.Info($"rec {name}: {n} frames");
        }
        if (state is "recidle") { await One("idle", 0); return; }
        if (state is "recexit" or "recboth") await One("exit", Input.GamepadPoller.PadMenu);
        if (state is "recboth") await Task.Delay(1500);
        if (state is "recenter") { Press(Input.GamepadPoller.PadMenu); await Task.Delay(2500); }
        if (state is "recenter" or "recboth") await One("enter", Input.GamepadPoller.PadA);
    }

    public static async Task StateAsync(WallStage s, string state)
    {
        try
        {
            await Task.Delay(1800);
            switch (state)
            {
                case "expand": s.Activate(); break;
                case "collapse": s.Activate(); await Task.Delay(1500); Log.Info("state collapse now"); s.Back(); break;
                case "collection": s.SwitchTab(1); await Task.Delay(1500); if (s.FocusFirstCollection()) { await Task.Delay(700); s.Activate(); } break;
                case "styleb": s.ToggleTitleStyle(); break;
                case "tab": s.SwitchTab(1); break;
                case "control": s.ToggleControlCenter(); break;
                case "cycle": await CycleAsync(s); break;
                case "recchrome": await RecChromeAsync(s); break;
                case "recspring": await RecSpringAsync(s); break;
                case "recperf": await RecPerfAsync(s); break;
                case "perfsave": await PerfSaveTestAsync(s); break;
                case "recexit" or "recenter" or "recboth" or "recidle": await RecordAsync(s, state); break;
                case "ccslow": s.ToggleControlCenter(); await Task.Delay(7000); Log.Info("state ccslow enter"); s.Activate(); break;
                case "ccenter": s.ToggleControlCenter(); await Task.Delay(2500); Log.Info("state ccenter now"); s.Activate(); break;
                case "control2": s.ToggleControlCenter(); await Task.Delay(900); s.Move(Direction.Right); await Task.Delay(260); s.Move(Direction.Right); break;
                case "nav": for (int i = 0; i < 3; i++) { s.Move(Direction.Right); await Task.Delay(250); } break;
                case "rail" or "railtab" or "settings" or "search" or "searchresults": s.RailTestState(state); break;
            }
            Log.Info($"state {state} reached");
        }
        catch (Exception ex) { Log.Error("state", ex); }
    }
}
