// 左侧栏的录屏自测（--state=recleft）：View 打开、上下走、进性能 / 显示与声音 / 系统 / 设置（浅色皮）再退回侧栏、关闭、≡ 开游戏菜单。
// 帧存到 %LOCALAPPDATA%\ClawLibrary\rec\left-*。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecLeftAsync()
    {
        await Task.Delay(2500);
        var style = _prefs.Home;
        string dir = Path.Combine(App.CacheDir, "rec");
        async Task Still(string name)
        {
            await Task.Delay(900);
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(250);
            Log.Info($"recleft {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "left-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        async Task Rec(string name, Func<Task> act, int tail)
        {
            var r = new FrameRecorder();
            if (!r.Start(HostWindow)) { await act(); await Task.Delay(tail); return; }
            long m = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Delay(120);
            await act();
            await Task.Delay(tail);
            Log.Info($"recleft {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "left-" + name), m)} frames");
        }
        async Task Keys(params (Direction D, int Ms)[] steps) { foreach (var (d, ms) in steps) { Move(d); await Task.Delay(ms); } }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            await Still("home");
            await Rec("open", () => { PadView(); return Task.CompletedTask; }, 900);
            await Still("panel");
            await Rec("rows", () => Keys((Direction.Down, 300), (Direction.Down, 300), (Direction.Down, 300), (Direction.Up, 300), (Direction.Up, 300), (Direction.Up, 300)), 400);
            // 安全：只在侧栏开着时按 A（侧栏关着时 A 会启动首页焦点游戏）；退回侧栏用 Back，看到侧栏就停
            async Task<bool> ToPanel()
            {
                for (int i = 0; i < 6 && !LsOpenNow; i++) { if (!_ccOpen) break; Back(); await Task.Delay(500); }
                await Task.Delay(600);
                return LsOpenNow;
            }
            async Task Open(int n)
            {
                if (!LsOpenNow) return;
                for (int i = 0; i < 6; i++) { Move(Direction.Up); await Task.Delay(120); }
                for (int i = 0; i < n; i++) { Move(Direction.Down); await Task.Delay(200); }
                if (LsOpenNow) Activate();
            }
            if (!LsOpenNow) throw new InvalidOperationException("panel not open");
            await Rec("page", () => Open(0), 1100);
            await Still("perf");
            await Rec("perf-cat", () => Keys((Direction.Down, 500), (Direction.Down, 500), (Direction.Up, 500), (Direction.Up, 500)), 400);
            await Rec("perf-in", () => Keys((Direction.Right, 500), (Direction.Down, 500)), 400);
            await Still("perf-row");
            await Rec("pageback", async () => { await ToPanel(); }, 300);
            await Still("back-panel");
            foreach (var (n, name) in new[] { (2, "disp"), (3, "sys"), (4, "set") })
            {
                if (!LsOpenNow) break;
                await Open(n); await Task.Delay(1300);
                await Still(name);
                Move(Direction.Right); await Task.Delay(400); Move(Direction.Down); await Task.Delay(400); Move(Direction.Down); await Task.Delay(400);
                await Still(name + "-focus");
                if (!await ToPanel()) break;
            }
            if (!LsOpenNow) throw new InvalidOperationException("lost the panel");
            await Rec("close", () => { PadView(); return Task.CompletedTask; }, 500);
            await Rec("menu", () => { ToggleControlCenter(); return Task.CompletedTask; }, 700);
            await Still("menu");
            Back(); await Task.Delay(600);
            await Still("closed");
        }
        catch (Exception ex) { Log.Error("recleft", ex); }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recleft done");
    }
}
