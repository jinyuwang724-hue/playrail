// 手柄页静帧自测（--state=recgpc）：侧栏进「手柄」，按键映射页、进右列走到 M1 背键、桌面模式页各录一张。只走方向键，不改设置。帧存 rec\gpc-*。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecGpcAsync()
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
            Log.Info($"recgpc {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "gpc-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            PadView(); await Task.Delay(900);
            if (!LsOpenNow) throw new InvalidOperationException("panel not open");
            for (int i = 0; i < 7; i++) { Move(Direction.Up); await Task.Delay(110); }
            Move(Direction.Down); await Task.Delay(200);
            if (LsOpenNow) Activate();
            await Task.Delay(2000);
            if (!_gpOpen) throw new InvalidOperationException("gamepad not open");
            await Still("map");
            Move(Direction.Right); await Task.Delay(400);
            for (int i = 0; i < 3; i++) { Move(Direction.Down); await Task.Delay(300); }
            await Still("map-in");
            Back(); await Task.Delay(400);
            for (int i = 0; i < 4; i++) { Move(Direction.Down); await Task.Delay(300); }
            await Still("desk");
            Move(Direction.Right); await Task.Delay(400);
            await Still("desk-in");
            for (int i = 0; i < 4 && !LsOpenNow; i++) { if (!_ccOpen) break; Back(); await Task.Delay(500); }
            if (LsOpenNow) { PadView(); await Task.Delay(500); }
        }
        catch (Exception ex) { Log.Error("recgpc", ex); }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recgpc done");
    }
}
