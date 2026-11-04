// 卡盒版的录屏自测（--state=reccases）：首页展架的进场和翻页（含中途打断）、信息区按钮、切到分类页走一圈、回首页、
// 切到叠放翻几张、插卡（只放启动画面，不真的启动游戏）后按 B 退卡，最后切回原来的首页样式。
// 每段用 FrameRecorder 录本窗口合成出来的每一帧，存到 rec\cases-*，再拼成 mp4。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>录屏时插卡不真的启动游戏：插到底只放启动画面。</summary>
    private bool _csDemo;

    internal async Task RecCasesAsync()
    {
        if (_cs == null) { Log.Warn("reccases: cases view off"); return; }
        await Task.Delay(1500);
        var style = _prefs.Home;
        _csDemo = true;
        async Task Clip(string name, Func<Func<int, string, Action, Task>, Task> body)
        {
            var rec = new FrameRecorder();
            if (!rec.Start(HostWindow)) return;
            await Task.Delay(300);
            long mark = System.Diagnostics.Stopwatch.GetTimestamp();
            async Task Step(int ms, string what, Action a)
            {
                try { a(); } catch (Exception ex) { Log.Error($"reccases {what}", ex); }
                Log.Info($"reccases {name} {what} @{FrameRecorder.QpcMs(System.Diagnostics.Stopwatch.GetTimestamp() - mark):F0}ms");
                await Task.Delay(ms);
            }
            await body(Step);
            string dir = Path.Combine(App.CacheDir, "rec", "cases-" + name);
            int n = await rec.StopAndSaveAsync(dir, mark);
            Log.Info($"reccases {name}: {n} frames");
            try { await Bench.EncodeMp4(dir, Path.Combine(App.CacheDir, "rec", $"cases-{name}.mp4")); }
            catch (Exception ex) { Log.Error("reccases mp4", ex); }
        }
        if (style != HomeStyle.Shelf) { SaveUi(_prefs with { Home = HomeStyle.Shelf }); CsMorphStyle(); await Task.Delay(800); }
        await Clip("shelf", async Step =>
        {
            await Step(1300, "entry", () => CsShowTab(0, 0, entry: true));
            await Step(700, "right", () => Move(Direction.Right));
            await Step(700, "right", () => Move(Direction.Right));
            await Step(90, "right", () => Move(Direction.Right));
            await Step(90, "left", () => Move(Direction.Left));
            await Step(800, "left", () => Move(Direction.Left));
            await Step(600, "up", () => Move(Direction.Up));
            await Step(500, "right", () => Move(Direction.Right));
            await Step(700, "down", () => Move(Direction.Down));
        });
        await Clip("tabs", async Step =>
        {
            await Step(1200, "tab all", () => SwitchTab(1));
            await Step(600, "down", () => Move(Direction.Down));
            await Step(600, "right", () => Move(Direction.Right));
            await Step(600, "down", () => Move(Direction.Down));
            await Step(600, "up", () => Move(Direction.Up));
            await Step(800, "up", () => Move(Direction.Up));
            await Step(600, "right", () => Move(Direction.Right));
            await Step(1000, "tab steam", () => SwitchTab(1));
            await Step(150, "tab local", () => SwitchTab(1));
            await Step(1000, "tab steam", () => SwitchTab(-1));
            await Step(1200, "back home", Back);
        });
        await Clip("stack", async Step =>
        {
            await Step(1400, "style stack", () => { SaveUi(_prefs with { Home = HomeStyle.Stack }); CsMorphStyle(); });
            await Step(1300, "entry", () => CsShowTab(0, 0, entry: true));
            await Step(800, "down", () => Move(Direction.Down));
            await Step(800, "down", () => Move(Direction.Down));
            await Step(120, "up", () => Move(Direction.Up));
            await Step(1000, "up", () => Move(Direction.Up));
            for (int i = 0; i < 3; i++) await Step(450, "left (buttons)", () => Move(Direction.Left));
            for (int i = 0; i < 3; i++) await Step(450, "right (buttons)", () => Move(Direction.Right));
            await Step(1400, "style shelf", () => { SaveUi(_prefs with { Home = HomeStyle.Shelf }); CsMorphStyle(); });
        });
        await Clip("insert", async Step =>
        {
            await Step(3400, "insert", Activate);
            await Step(2900, "cancel", Back);
            await Step(700, "insert again", Activate);
            await Step(2800, "back mid-way", Back);
        });
        SaveUi(_prefs with { Home = style });
        if (style != HomeStyle.Shelf) CsMorphStyle();
        _csDemo = false;
        Log.Info("reccases done");
    }
}
