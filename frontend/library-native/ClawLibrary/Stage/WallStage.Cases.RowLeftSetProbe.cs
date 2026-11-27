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
            for (int i = 0; i < 5; i++) { Move(Direction.Down); await Task.Delay(200); }
            if (LsOpenNow) Activate();
            await Task.Delay(1400);
            if (!_stOpen) throw new InvalidOperationException("settings not open");
            for (int c = 0; c < CpSettingsIds.Length; c++)
            {
                await Still($"{c:00}");
                Move(Direction.Right); await Task.Delay(500);
                for (int k = 0; k < 3; k++) { Move(Direction.Down); await Task.Delay(350); }
                await Still($"{c:00}-in");
                if (c == 0)
                {
                    // 下拉：回到第一行「开机进入」，A 展开，↓ 走一项，B 收起（不选，不改设置）
                    for (int k = 0; k < 4; k++) { Move(Direction.Up); await Task.Delay(200); }
                    Activate(); await Task.Delay(500);
                    await Still("00-drop");
                    Move(Direction.Down); await Task.Delay(400);
                    await Still("00-drop2");
                    Back(); await Task.Delay(400);
                    // 连按：↓ 8 下、每 90 ms 一下（看卡不卡：日志里 cp paint 的耗时）
                    for (int k = 0; k < 8; k++) { Move(Direction.Down); await Task.Delay(90); }
                    await Still("00-fast");
                }
                if (_stZone == 1) { Back(); await Task.Delay(500); }
                if (!_stOpen || _stZone != 0) throw new InvalidOperationException($"lost zone at {c}");
                Move(Direction.Down); await Task.Delay(600);
            }
            for (int i = 0; i < 4 && !LsOpenNow; i++) { if (!_ccOpen) break; Back(); await Task.Delay(500); }
            // 显示与声音（组合页）：左列逐个分类走一遍，只看不改。
            if (LsOpenNow)
            {
                for (int i = 0; i < 6; i++) { Move(Direction.Up); await Task.Delay(120); }
                for (int i = 0; i < 2; i++) { Move(Direction.Down); await Task.Delay(200); }
                if (LsOpenNow) Activate();
                await Task.Delay(1400);
                int count = CpCats(SettingsRows).Count;
                for (int c = 0; c < count; c++) { await Still($"disp-{c}"); Move(Direction.Down); await Task.Delay(500); }
                for (int c = 1; c < count; c++) { Move(Direction.Up); await Task.Delay(80); }
                await Still("disp-fast");
                for (int i = 0; i < 4 && !LsOpenNow; i++) { if (!_ccOpen) break; Back(); await Task.Delay(500); }
            }
            if (LsOpenNow) { PadView(); await Task.Delay(500); }
        }
        catch (Exception ex) { Log.Error("recset", ex); }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recset done");
    }
}
