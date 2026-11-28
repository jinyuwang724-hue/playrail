// 单行「正在玩」自测（--state=recplay）：拿焦点卡假装在跑（进程借资源管理器），录 光晕 + 跳动竖条 + ✕、↑ 到 ✕、A 弹退出小窗（只按 B，不强制退出）。
using ClawLibrary.Data;
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecPlayAsync()
    {
        await Task.Delay(2500);
        var style = _prefs.Home;
        string dir = Path.Combine(App.CacheDir, "rec");
        async Task Rec(string name, int ms)
        {
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(ms);
            Log.Info($"recplay {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "play-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            await Task.Delay(800);
            if (_cs?.Page is not { Style: HomeStyle.Row, Tab: 0 } rp || rp.Sel >= rp.Items.Count) throw new InvalidOperationException("no row page");
            var shell = System.Diagnostics.Process.GetProcessesByName("explorer").FirstOrDefault() ?? throw new InvalidOperationException("no explorer");
            var item = rp.Items[rp.Sel];
            _game = new GameSession { Item = item, Source = SessionSource.Window, Pending = false, Pid = shell.Id, ProcessStart = shell.StartTime, Started = DateTime.Now.AddMinutes(-12) };
            RwMarkRunning(rp);
            await Task.Delay(600);
            await Rec("running", 1800);
            Move(Direction.Up); await Task.Delay(500);
            await Rec("xfocus", 600);
            Activate(); await Task.Delay(600);
            await Rec("quit", 400);
            Back(); await Task.Delay(500);
            Move(Direction.Down); await Task.Delay(500);
            await Rec("back", 400);
            _game = null;
            RwMarkRunning(rp);
        }
        catch (Exception ex) { Log.Error("recplay", ex); _game = null; }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recplay done");
    }
}
