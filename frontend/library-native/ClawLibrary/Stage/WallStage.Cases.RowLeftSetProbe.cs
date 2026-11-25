// 设置页逐个分类的录屏自测（--state=recset）：左侧栏进设置，每个分类在左列停一下、进右列往下走三行、B 回左列。
// 只走方向键（右列里不按左右、不按 A），不会改任何设置。帧存到 %LOCALAPPDATA%\ClawLibrary\rec\left-set-*。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecSetAsync()
    {
        await Task.Delay(2500);
        var style = _prefs.Home;
        string dir = Path.Combine(App.CacheDir, "rec");
        async Task Still(string name)
        {
            await Task.Delay(700);
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(200);
            Log.Info($"recset {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "left-set-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            PadView(); await Task.Delay(900);
            if (!LsOpenNow) throw new InvalidOperationException("panel not open");
            for (int i = 0; i < 6; i++) { Move(Direction.Up); await Task.Delay(120); }
            for (int i = 0; i < 4; i++) { Move(Direction.Down); await Task.Delay(200); }
            if (LsOpenNow) Activate();
            await Task.Delay(1400);
            if (!_stOpen) throw new InvalidOperationException("settings not open");
            for (int c = 0; c < StCats.Length; c++)
            {
                await Still($"{c:00}");
                Move(Direction.Right); await Task.Delay(500);
                for (int k = 0; k < 3; k++) { Move(Direction.Down); await Task.Delay(350); }
                await Still($"{c:00}-in");
                if (_stZone == 1) { Back(); await Task.Delay(500); }
                if (!_stOpen || _stZone != 0) throw new InvalidOperationException($"lost zone at {c}");
                Move(Direction.Down); await Task.Delay(600);
            }
            for (int i = 0; i < 4 && !LsOpenNow; i++) { if (!_ccOpen) break; Back(); await Task.Delay(500); }
            if (LsOpenNow) { PadView(); await Task.Delay(500); }
        }
        catch (Exception ex) { Log.Error("recset", ex); }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recset done");
    }
}
