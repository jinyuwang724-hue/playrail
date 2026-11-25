// 录屏自测（--state=reccur）：各处焦点框的静帧，只看不改——首页卡片、右侧栏、左侧栏、游戏菜单、编辑二级页。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecCurAsync()
    {
        await Task.Delay(2500);
        _spDry = true;
        string dir = Path.Combine(App.CacheDir, "rec");
        async Task Still(string name)
        {
            await Task.Delay(1100);
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(250);
            Log.Info($"reccur {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "cur-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { Log.Info("reccur: home is not Row, skipped"); return; }
            await Still("home");
            PadRT(); await Task.Delay(800); await Still("side");
            Move(Direction.Down); await Task.Delay(400); Move(Direction.Down); await Task.Delay(400); await Still("side-rows");
            PadRT(); await Task.Delay(800);
            if (!LsOpenNow) { PadView(); await Task.Delay(900); }
            await Still("left");
            if (LsOpenNow) { PadView(); await Task.Delay(700); }
            CsGameSettings(); await Task.Delay(900); await Still("menu");
            CsGameSettings(); await Task.Delay(1200); await Still("sheet");
            Back(); await Task.Delay(500); Back(); await Task.Delay(600);
        }
        catch (Exception ex) { Log.Error("reccur", ex); }
        _spDry = false;
        Log.Info("reccur done");
    }
}
