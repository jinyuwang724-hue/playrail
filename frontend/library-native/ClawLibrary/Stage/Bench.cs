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

    /// <summary>手柄页：开 → 一路 ↓ 到底（看滚动和左栏描圈）→ 再回顶 → B。只挪焦点，不改设置。</summary>
    private static async Task Go2(WallStage s, Func<int, Action, Task> step, params Direction[] ds) { foreach (var d in ds) await step(650, () => s.Move(d)); }

    private static async Task RecGamepadAsync(WallStage s)
    {
        await Task.Delay(1500);
        // 录不了（窗口捕获被拒）也照走一遍：每步打一行日志，外面按时间截屏对照
        var rec = new FrameRecorder();
        bool recording = rec.Start(s.HostWindow);
        await Task.Delay(300);
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        int step = 0;
        async Task Step(int ms, Action a) { a(); Log.Info($"bench step {++step}"); await Task.Delay(ms); }
        await Step(1500, s.ToggleControlCenter);
        await Step(500, () => s.Move(Direction.Right));
        await Step(700, () => s.Move(Direction.Right));
        await Step(3000, s.Activate);
        // 映射：左列走几行（LT 换侧视图），跳到右列打开一个键的按键页再关掉（不改任何东西），一路往下到 M2（背面）；再把其余分类过一遍
        var D = Direction.Down; var R = Direction.Right;
        await Step(1200, () => s.Move(R));                 // 进：LT（侧视图）
        await Step(900, () => s.Move(D));                  // LB
        await Step(900, () => s.Move(D));                  // View
        await Step(900, () => s.Move(R));                  // 右列
        await Step(1500, s.Activate);                      // 按键页
        await Step(900, s.Back);
        for (int i = 0; i < 8; i++) await Step(500, () => s.Move(D));   // 一路到 M2（背面）
        await Step(700, s.Back);                           // 回分类
        for (int i = 0; i < 5; i++) await Step(1100, () => s.Move(D));   // 摇杆、扳机、震动、桌面按键映射、鼠标
        await Step(1000, s.Back);
        await Step(1200, s.Back);
        if (!recording) return;
        int n = await rec.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", "gamepad"), mark);
        Log.Info($"rec gamepad: {n} frames");
    }

    /// <summary>
    /// 映射的「选择指令」五个页签挨个看一遍（只看不选）：控制中心 → 手柄 → 映射里 LT → 按键页 → 指令 → 手柄 / 鼠标 / 键盘 / 数字键盘 / 系统。
    /// 每步打一行日志，外面按时间截屏。
    /// </summary>
    private static async Task RecPickerAsync(WallStage s)
    {
        await Task.Delay(1500);
        int step = 0;
        async Task Step(int ms, Action a) { a(); Log.Info($"bench step {++step}"); await Task.Delay(ms); }
        await Step(1500, s.ToggleControlCenter);
        await Step(500, () => s.Move(Direction.Right));
        await Step(700, () => s.Move(Direction.Right));
        await Step(3000, s.Activate);                          // 手柄页（按键映射）
        await Step(1000, () => s.Move(Direction.Right));       // LT
        await Step(1000, s.Activate);                          // 按键页
        await Step(600, () => s.Move(Direction.Down));         // 指令
        await Step(2000, s.Activate);                          // 选择指令：手柄
        await Step(1000, () => s.Move(Direction.Down));        // 焦点挪一格
        for (int t = 1; t <= 4; t++) await Step(2000, () => s.SwitchTab(1));   // 鼠标、键盘、数字键盘、系统
        for (int i = 0; i < 4; i++) await Step(600, s.Back);
        await Step(800, s.Back);
    }

    /// <summary>功耗模式来回切（智能 → 按电源 → 手动 → 回智能，假定开始时是智能）：看高度不跳、焦点跳过用不上的行；再一路 ↓ 到底看滚动。</summary>
    private static async Task RecPerfModesAsync(WallStage s)
    {
        await Task.Delay(1500);
        var rec = new FrameRecorder();
        if (!rec.Start(s.HostWindow)) return;
        await Task.Delay(300);
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        async Task Step(int ms, Action a) { a(); await Task.Delay(ms); }
        await Step(1500, s.ToggleControlCenter);
        await Step(700, () => s.Move(Direction.Right));
        await Step(3500, s.Activate);
        await Step(600, () => s.Move(Direction.Down));
        await Step(1500, () => s.Move(Direction.Right));
        await Step(1500, () => s.Move(Direction.Right));
        await Step(600, () => s.Move(Direction.Down));
        await Step(600, () => s.Move(Direction.Down));
        await Step(600, () => s.Move(Direction.Up));
        await Step(600, () => s.Move(Direction.Up));
        await Step(1500, () => s.Move(Direction.Left));
        await Step(1500, () => s.Move(Direction.Left));
        for (int i = 0; i < 14; i++) await Step(250, () => s.Move(Direction.Down));
        await Step(800, () => { });
        for (int i = 0; i < 14; i++) await Step(120, () => s.Move(Direction.Up));
        await Step(1000, s.Back);
        await Step(1200, s.Back);
        int n = await rec.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", "perfmodes"), mark);
        Log.Info($"rec perfmodes: {n} frames");
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

    /// <summary>录正在游戏面板：弹出 → 右移两格 → 回到「继续游戏」→ 游戏结束（已退出，封面飞回卡片）。</summary>
    private static async Task RecPlayingAsync(WallStage s)
    {
        await Task.Delay(5000);
        if (Program.Args.FirstOrDefault(a => a.StartsWith("--recmoves=")) is { } mv)
            foreach (char ch in mv[11..]) { s.Move(ch switch { 'L' => Direction.Left, 'U' => Direction.Up, 'D' => Direction.Down, _ => Direction.Right }); await Task.Delay(450); }
        var rec = new FrameRecorder();
        if (!rec.Start(s.HostWindow)) return;
        await Task.Delay(300);
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        s.DemoNowPlaying(); await Task.Delay(1800);
        s.Move(Direction.Right); await Task.Delay(700);
        s.Move(Direction.Right); await Task.Delay(700);
        s.Move(Direction.Left); await Task.Delay(500);
        s.Move(Direction.Left); await Task.Delay(900);
        s.DemoGameEnded(); await Task.Delay(3200);
        string dir = Path.Combine(App.CacheDir, "rec", "launch");
        int n = await rec.StopAndSaveAsync(dir, mark);
        Log.Info($"rec playing: {n} frames");
        s.DemoNowPlaying(); await Task.Delay(1500);
        var still = new FrameRecorder { Full = true };
        if (still.Start(s.HostWindow)) { await Task.Delay(300); await still.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", "launch-still"), System.Diagnostics.Stopwatch.GetTimestamp()); }
        // 第二种：墙面 + 顶部「正在游戏」条
        s.DemoGameEnded(); await Task.Delay(2600);
        s.DemoNowPlaying(bar: true); await Task.Delay(1200);
        var still2 = new FrameRecorder { Full = true };
        if (still2.Start(s.HostWindow)) { await Task.Delay(300); await still2.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", "launch-slow"), System.Diagnostics.Stopwatch.GetTimestamp()); }
        try { await EncodeMp4Async(dir, Path.Combine(App.CacheDir, "rec", "launch.mp4")); }
        catch (Exception ex) { Log.Error("encode mp4", ex); }
    }

    /// <summary>
    /// 录启动画面：展开聚焦卡 → 启动画面（不真的启动）→ 45 秒提示 → B 收回；再开一次，静止后存两张原分辨率截图，最后拼成 mp4。
    /// </summary>
    private static async Task RecLaunchAsync(WallStage s)
    {
        await Task.Delay(5000);
        if (Program.Args.FirstOrDefault(a => a.StartsWith("--recmoves=")) is { } mv)
            foreach (char ch in mv[11..]) { s.Move(ch switch { 'L' => Direction.Left, 'U' => Direction.Up, 'D' => Direction.Down, _ => Direction.Right }); await Task.Delay(450); }
        Log.Info($"reclaunch game: {s.DemoLaunchTitle}");
        var rec = new FrameRecorder();
        if (!rec.Start(s.HostWindow)) return;
        await Task.Delay(300);
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        s.Activate(); await Task.Delay(1100);
        s.DemoLaunchStart(); await Task.Delay(3600);
        s.DemoLaunchSlow(); await Task.Delay(1800);
        s.Back(); await Task.Delay(1200);
        string dir = Path.Combine(App.CacheDir, "rec", "launch");
        int n = await rec.StopAndSaveAsync(dir, mark);
        Log.Info($"rec launch: {n} frames");
        // 原分辨率静帧：画面停稳后各录 0.3 秒，取最后一帧
        s.DemoLaunchStart(); await Task.Delay(2600);
        async Task Still(string name)
        {
            var r = new FrameRecorder { Full = true };
            if (!r.Start(s.HostWindow)) return;
            await Task.Delay(300);
            await r.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", name), System.Diagnostics.Stopwatch.GetTimestamp());
        }
        await Still("launch-still");
        s.DemoLaunchSlow(); await Task.Delay(500);
        await Still("launch-slow");
        s.DemoLaunchHandoff(); await Task.Delay(800);
        try { await EncodeMp4Async(dir, Path.Combine(App.CacheDir, "rec", "launch.mp4")); }
        catch (Exception ex) { Log.Error("encode mp4", ex); }
    }

    /// <summary>把录下的 JPEG 帧按真实时间戳拼成 mp4（系统自带的 MediaComposition，不需要 ffmpeg）。</summary>
    internal static Task EncodeMp4(string dir, string output) => EncodeMp4Async(dir, output);

    private static async Task EncodeMp4Async(string dir, string output)
    {
        var frames = Directory.GetFiles(dir, "f*.jpg").OrderBy(f => f).ToList();
        if (frames.Count < 2) return;
        double Ms(string f) { var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"_([+-]\d+)ms"); return double.Parse(m.Groups[1].Value); }
        var comp = new Windows.Media.Editing.MediaComposition();
        // 30 fps：按时间取帧，避免 60+ 帧时剪辑太多
        double t0 = Ms(frames[0]), last = Ms(frames[^1]);
        int k = 0;
        for (double t = t0; t < last; t += 1000.0 / 30)
        {
            while (k + 1 < frames.Count && Ms(frames[k + 1]) <= t) k++;
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(frames[k]);
            comp.Clips.Add(await Windows.Media.Editing.MediaClip.CreateFromImageFileAsync(file, TimeSpan.FromMilliseconds(1000.0 / 30)));
        }
        if (File.Exists(output)) File.Delete(output);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
        var outFile = await folder.CreateFileAsync(Path.GetFileName(output), Windows.Storage.CreationCollisionOption.ReplaceExisting);
        var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(Windows.Media.MediaProperties.VideoEncodingQuality.HD1080p);
        profile.Video!.Width = 960; profile.Video.Height = 600;
        profile.Audio = null;
        var result = await comp.RenderToFileAsync(outFile, Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
        Log.Info($"mp4 {output}: {result} clips={comp.Clips.Count}");
    }

    /// <summary>
    /// 录墙面外观（叠色、关灯）和焦点切换的每一帧：关灯 → 走两步 → 快速左右来回（打断）→ 开灯 → 往下走；
    /// recstack 换成 Steam 页签里的集合（叠页边的收放、叠色、熄灯）。
    /// 灯光开关两次，结束时设置和开始时一样。
    /// </summary>
    private static async Task RecLookAsync(WallStage s, bool stacks)
    {
        await Task.Delay(1500);
        var rec = new FrameRecorder();
        if (!rec.Start(s.HostWindow)) return;
        await Task.Delay(300);
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        async Task Step(int ms, string name, Action a)
        {
            a();
            Log.Info($"reclook {name} @{FrameRecorder.QpcMs(System.Diagnostics.Stopwatch.GetTimestamp() - mark):F0}ms");
            await Task.Delay(ms);
        }
        if (stacks)
        {
            // 集合的叠页边：Steam 页签里的分类
            await Step(1600, "tab", () => s.SwitchTab(1));
            await Step(900, "collection", () => s.FocusFirstCollection());
            await Step(900, "lights", s.ToggleLights);
            await Step(900, "right", () => s.Move(Direction.Right));
            await Step(900, "lights", s.ToggleLights);
            await Step(900, "left", () => s.Move(Direction.Left));
        }
        else
        {
            await Step(900, "lights", s.ToggleLights);
            await Step(800, "right", () => s.Move(Direction.Right));
            await Step(800, "right", () => s.Move(Direction.Right));
            await Step(120, "left", () => s.Move(Direction.Left));
            await Step(120, "right", () => s.Move(Direction.Right));
            await Step(900, "left", () => s.Move(Direction.Left));
            await Step(900, "lights", s.ToggleLights);
            await Step(900, "down", () => s.Move(Direction.Down));
        }
        int n = await rec.StopAndSaveAsync(Path.Combine(App.CacheDir, "rec", "look"), mark);
        Log.Info($"rec look: {n} frames");
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
                case "styleb": s.SetTitleStyle(TitleStyle.B); break;
                case "style-folia": s.SetTitleStyle(TitleStyle.Folia); break;
                case "style-quiet": s.SetTitleStyle(TitleStyle.Quiet); break;
                case "style-editorial": s.SetTitleStyle(TitleStyle.Editorial); break;
                case "rail-hidden": s.ToggleLibraryRail(); break;
                case "screensaver": s.ScreensaverTestState(); break;
                case "appearance-probe": await s.AppearanceProbeAsync(); break;
                case "library-filter-probe": await s.LibraryFilterProbeAsync(); break;
                case "local-library-probe": await s.LocalLibraryProbeAsync(); break;
                case "lighting-probe": await s.LightingProbeAsync(); break;
                case "desktop-mapping-probe": await s.DesktopMappingProbeAsync(); break;
                case "idle-screen-probe": await s.IdleScreensaverProbeAsync(); break;
                case "tab": s.SwitchTab(1); break;
                case "control": s.ToggleControlCenter(); break;
                case "cycle": await CycleAsync(s); break;
                case "recchrome": await RecChromeAsync(s); break;
                case "recspring": await RecSpringAsync(s); break;
                case "recperf": await RecPerfAsync(s); break;
                case "recperfmodes": await RecPerfModesAsync(s); break;
                case "recgamepad": await RecGamepadAsync(s); break;
                case "recpicker": await RecPickerAsync(s); break;
                case "recdisplay":
                {
                    // 显示与声音：进页面，焦点从上走到下（只挪焦点，不改亮度、音量）
                    await Task.Delay(1500);
                    int step = 0;
                    async Task Step(int ms, Action a) { a(); Log.Info($"bench step {++step}"); await Task.Delay(ms); }
                    await Step(1500, s.ToggleControlCenter);
                    for (int i = 0; i < 3; i++) await Step(500, () => s.Move(Direction.Right));
                    await Step(3000, s.Activate);
                    for (int i = 0; i < 10; i++) await Step(1200, () => s.Move(Direction.Down));
                    await Step(800, s.Back);
                    await Step(800, s.Back);
                    break;
                }
                case "perfsave": await PerfSaveTestAsync(s); break;
                case "recexit" or "recenter" or "recboth" or "recidle": await RecordAsync(s, state); break;
                case "reclook" or "recstack": await RecLookAsync(s, state == "recstack"); break;
                case "reclaunch": await RecLaunchAsync(s); break;
                case "reccases": await s.RecCasesAsync(); break;
                case "recstatus": await s.RecStatusAsync(); break;
                case "recqam": await s.RecQamAsync(); break;
                case "gstest": s.DemoGameSessionProbe(); break;
                case "npattach": s.DemoAttachRunningGame(); break;
                case "recplaying": await RecPlayingAsync(s); break;
                case "stlook": s.LookTestState(); break;
                case "sfx": s.SoundTestState(); await Task.Delay(11000); break;
                case "ccslow": s.ToggleControlCenter(); await Task.Delay(7000); Log.Info("state ccslow enter"); s.Activate(); break;
                case "ccenter": s.ToggleControlCenter(); await Task.Delay(2500); Log.Info("state ccenter now"); s.Activate(); break;
                case "control2": s.ToggleControlCenter(); await Task.Delay(900); s.Move(Direction.Right); await Task.Delay(260); s.Move(Direction.Right); break;
                case "nav": for (int i = 0; i < 3; i++) { s.Move(Direction.Right); await Task.Delay(250); } break;
                case "rail" or "railtab" or "settings" or "search" or "searchresults": s.RailTestState(state); break;
                case "ccpage1" or "ccpage2" or "ccpage3" or "ccpage4" or "ccpage5":
                    // 控制中心第 N 块的页面（1 性能 … 5 设置），对齐参照用
                    s.ToggleControlCenter();
                    await Task.Delay(1500);
                    for (int i = 0; i < state[6] - '0'; i++) { s.Move(Direction.Right); await Task.Delay(300); }
                    await Task.Delay(800);
                    s.Activate();
                    break;
                case "add0" or "add1" or "add2" or "add3" or "add4" or "add5" or "add6" or "add7" or "add8" or "add9":
                {
                    // 添加游戏：add0 匹配页，add1 封面图片，add2 封面概览，add3 分类，add4 背景，add5 Steam 官方封面，add6 改名称，add7 匹配页里打字
                    s.ToggleControlCenter();
                    await Task.Delay(1500);
                    s.AddGameTestState(Program.Args.FirstOrDefault(a => a.StartsWith("--exe="))?[6..] ?? @"C:\Windows\notepad.exe");
                    await Task.Delay(12000);
                    if (state == "add7") { s.Move(Direction.Up); await Task.Delay(300); s.Activate(); await Task.Delay(1500); break; }
                    if (state != "add0") { s.Activate(); await Task.Delay(9000); }
                    if (state == "add2" || state == "add8") { s.SwitchTab(-1); await Task.Delay(2500); }
                    if (state == "add8") { s.Move(Direction.Right); await Task.Delay(300); for (int i = 0; i < 4; i++) { s.Move(Direction.Down); await Task.Delay(300); } await Task.Delay(1500); }
                    if (state == "add3") { s.PadX(); await Task.Delay(1000); }
                    if (state == "add4") { s.SwitchTab(1); await Task.Delay(2500); }
                    if (state == "add5") { s.SwitchTab(-1); await Task.Delay(300); s.SwitchTab(-1); await Task.Delay(2500); }
                    if (state == "add6") { s.Move(Direction.Left); await Task.Delay(400); s.Activate(); await Task.Delay(1500); }
                    if (state == "add9") { await Task.Delay(8000); s.Activate(); await Task.Delay(6000); s.Move(Direction.Right); await Task.Delay(300); s.Move(Direction.Up); await Task.Delay(300); s.Activate(); await Task.Delay(2500); }
                    break;
                }
                case "gpm" or "gpmd" or "gpg":
                    // 手柄页按键映射，焦点在右侧 RB
                    s.ToggleControlCenter(); await Task.Delay(1500);
                    for (int i = 0; i < 2; i++) { s.Move(Direction.Right); await Task.Delay(300); }
                    await Task.Delay(800); s.Activate(); await Task.Delay(2500);
                    s.Move(Direction.Right); await Task.Delay(400); s.Move(Direction.Right); await Task.Delay(400); s.Move(Direction.Down); await Task.Delay(800);
                    if (state == "gpmd") for (int i = 0; i < 5; i++) { s.Move(Direction.Down); await Task.Delay(400); }
                    if (state == "gpg") { s.Activate(); await Task.Delay(800); }
                    await Task.Delay(800);
                    break;
                case "gs0" or "gs1" or "gs2" or "gs3" or "gs4" or "gsk" or "gsc" or "gsb" or "gsm" or "gsmd":
                {
                    // 游戏设置：gs0–gs4 各分类，gsk 手柄里某个键的按键页，gsc 选择指令（键盘页），gsb 通用里浏览启动程序
                    s.ToggleControlCenter();
                    await Task.Delay(1500);
                    int cat = state is "gsk" or "gsc" or "gsm" or "gsmd" ? 2 : state == "gsb" ? 0 : state[2] - '0';
                    s.GameSettingsTestState(Program.Args.FirstOrDefault(a => a.StartsWith("--game="))?[7..] ?? "", cat);
                    await Task.Delay(6000);
                    s.Move(Direction.Right);
                    await Task.Delay(800);
                    if (state is "gsk" or "gsc")
                    {
                        for (int i = 0; i < 6; i++) { s.Move(Direction.Down); await Task.Delay(250); }
                        s.Activate(); await Task.Delay(1000);
                        if (state == "gsc") { s.Move(Direction.Down); await Task.Delay(250); s.Activate(); await Task.Delay(600); s.SwitchTab(1); await Task.Delay(300); s.SwitchTab(1); await Task.Delay(800); }
                    }
                    if (state is "gsm" or "gsmd") { for (int i = 0; i < (state == "gsm" ? 4 : 10); i++) { s.Move(Direction.Down); await Task.Delay(300); } await Task.Delay(600); }
                    if (state == "gsb") { s.Activate(); await Task.Delay(1200); }
                    break;
                }
                case "qsapply":
                {
                    // 快捷设置：性能显示磁贴按 5 次（关 → 1 → 2 → 3 → 4 → 关 一整圈，回到原值），每次等后台回读
                    await Task.Delay(1500);
                    s.QuickPanelTest(0);
                    await Task.Delay(2500);
                    string start = s.QuickPanelTestValue("osd");
                    for (int i = 0; i < 5; i++) { s.QuickPanelTestPress("osd"); await Task.Delay(2200); Log.Info($"quick panel test osd readback {i + 1}: {s.QuickPanelTestValue("osd")}"); }
                    Log.Info($"quick panel test osd start={start} end={s.QuickPanelTestValue("osd")}");
                    s.QuickPanelTestClose();
                    break;
                }
                case "qsset":
                {
                    // 快捷设置：按 --qs=<焦点 id | power | arm | edit | sec=分区> 摆好状态停住（截图用）
                    await Task.Delay(1500);
                    s._qsTestGame = Program.Args.FirstOrDefault(a => a.StartsWith("--qsgame="))?[9..];
                    s.QuickPanelTest(0);
                    await Task.Delay(1800);
                    s.QuickPanelTestState(Program.Args.FirstOrDefault(a => a.StartsWith("--qs="))?[5..] ?? "tdp");
                    break;
                }
                case "qsrules":
                {
                    // 快捷设置导航规则：逐步按方向键，日志记每步焦点（--qsgame= 时带退出按钮）
                    s._qsTestGame = Program.Args.FirstOrDefault(a => a.StartsWith("--qsgame="))?[9..];
                    await Task.Delay(1500);
                    s.QuickPanelTest(0);
                    await Task.Delay(1800);
                    Log.Info($"qsrules start {s.QsFocusTest}");
                    var seq = Program.Args.FirstOrDefault(a => a.StartsWith("--seq="))?[6..] ?? "LRDLRDLLRDDDDDUUUUUURUL";
                    foreach (char c in seq)
                    {
                        var d = c switch { 'L' => Direction.Left, 'R' => Direction.Right, 'U' => Direction.Up, _ => Direction.Down };
                        s.Move(d); await Task.Delay(250);
                        Log.Info($"qsrules {c} -> {s.QsFocusTest}");
                    }
                    s.SwitchTab(1); await Task.Delay(300); Log.Info($"qsrules RB -> page {s.QsFocusTest}");
                    s.Move(Direction.Left); await Task.Delay(250); Log.Info($"qsrules L -> {s.QsFocusTest}");
                    s.Move(Direction.Down); await Task.Delay(250); Log.Info($"qsrules D -> {s.QsFocusTest}");
                    s.Move(Direction.Right); await Task.Delay(250); Log.Info($"qsrules R -> {s.QsFocusTest}");
                    s.QuickPanelTestClose();
                    break;
                }
                case "qs" or "qsnav":
                {
                    // 快捷设置：qs = 打开停住，qsnav = 走 12 步焦点后收起（游戏库里开，没有游戏画面）
                    await Task.Delay(1500);
                    s.QuickPanelTest(state == "qsnav" ? 12 : 0);
                    if (state == "qsnav") { await Task.Delay(4600); s.QuickPanelTestClose(); await Task.Delay(1000); }
                    break;
                }
                case "wsgo" or "wsx":
                {
                    // 窗口切换页：焦点停在标题含 --window= 的窗口上，wsgo = A 切过去，wsx = X 请它关
                    string want = Program.Args.FirstOrDefault(a => a.StartsWith("--window="))?[9..] ?? "";
                    s.ToggleControlCenter();
                    await Task.Delay(1500);
                    s.SwitcherTestState();
                    await Task.Delay(1200);
                    s.SwitcherTestPick(want, close: state == "wsx");
                    await Task.Delay(1500);
                    break;
                }
                case "ws" or "wsnav":
                {
                    // 窗口切换页：ws = 打开停住，wsnav = 打开、往右走两格、往回一格、B 出场
                    s.ToggleControlCenter();
                    await Task.Delay(1500);
                    s.SwitcherTestState();
                    await Task.Delay(1200);
                    if (state == "wsnav")
                    {
                        s.Move(Direction.Right); await Task.Delay(500); s.Move(Direction.Right); await Task.Delay(500);
                        s.Move(Direction.Left); await Task.Delay(800); s.SwitcherTestBack(); await Task.Delay(900);
                    }
                    break;
                }
                case "shots0" or "shots1" or "shots2" or "shotsv" or "shotsnav" or "shotsf":
                {
                    // 截图页：shots0 / 1 / 2 = 日历 / 放映 / 冲印（焦点往右挪一格），shotsv = 大图，shotsnav = 日历里走一圈，shotsf = Steam 页签 + 筛选面板
                    s.ToggleControlCenter();
                    await Task.Delay(1500);
                    s.ShotsTestState(state == "shotsv" || state == "shotsnav" ? 0 : state[5] - '0');
                    await Task.Delay(1500);
                    s.Move(Direction.Right);
                    await Task.Delay(400);
                    if (state == "shotsv") s.Activate();
                    if (state == "shotsf") { s.SwitchTab(1); await Task.Delay(1200); s.PadView(); await Task.Delay(300); s.Move(Direction.Down); }
                    if (state == "shotsnav") { s.Move(Direction.Down); await Task.Delay(400); s.Move(Direction.Down); await Task.Delay(400); s.Move(Direction.Down); }
                    break;
                }
            }
            Log.Info($"state {state} reached");
        }
        catch (Exception ex) { Log.Error("state", ex); }
    }
}
