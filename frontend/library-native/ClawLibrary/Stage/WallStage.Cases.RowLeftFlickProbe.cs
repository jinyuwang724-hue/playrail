// 左列连按换分类的录屏自测（--state=recflick）：左侧栏依次进 性能 / 显示与声音 / 系统 / 设置，
// 每页：左列每 90 ms 往下走到底、再往上走回来（录一段）；进右列往下走 8 行让列表滚起来，回左列换一个分类（录一段，看滚动位置有没有归零）。
// 只走方向键和 B（左列 A 只在侧栏开着时按），不改任何设置。帧存到 %LOCALAPPDATA%\ClawLibrary\rec\left-flick-*。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecFlickAsync()
    {
        await Task.Delay(2500);
        var style = _prefs.Home;
        string dir = Path.Combine(App.CacheDir, "rec");
        async Task Rec(string name, Func<Task> act)
        {
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) { await act(); return; }
            await Task.Delay(60);
            await act();
            await Task.Delay(500);
            Log.Info($"recflick {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "left-flick-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            foreach (var (name, idx) in new[] { ("perf", 0), ("disp", 2), ("sys", 3), ("set", 5) })
            {
                if (!LsOpenNow) { PadView(); await Task.Delay(900); }
                if (!LsOpenNow) throw new InvalidOperationException("panel not open");
                for (int i = 0; i < 7; i++) { Move(Direction.Up); await Task.Delay(110); }
                for (int i = 0; i < idx; i++) { Move(Direction.Down); await Task.Delay(180); }
                if (LsOpenNow) Activate();
                await Task.Delay(1500);
                if (!CpActive) throw new InvalidOperationException($"{name}: page not open");
                int n = CpHost is { } h && _cpViews.TryGetValue(h, out var v) ? v.Cats.Count : 6;
                await Rec($"{name}-down", async () => { for (int i = 0; i < n; i++) { Move(Direction.Down); await Task.Delay(90); } });
                await Rec($"{name}-up", async () => { for (int i = 0; i < n; i++) { Move(Direction.Up); await Task.Delay(90); } });
                Move(Direction.Right); await Task.Delay(400);
                for (int i = 0; i < 8; i++) { Move(Direction.Down); await Task.Delay(140); }
                await Task.Delay(400);
                await Rec($"{name}-scroll", async () =>
                {
                    Back(); await Task.Delay(300);
                    Move(Direction.Down); await Task.Delay(600);
                    Move(Direction.Up); await Task.Delay(300);
                });
                for (int i = 0; i < 4 && !LsOpenNow; i++) { if (!_ccOpen) break; Back(); await Task.Delay(500); }
            }
            if (LsOpenNow) { PadView(); await Task.Delay(500); }
        }
        catch (Exception ex) { Log.Error("recflick", ex); }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recflick done");
    }
}
