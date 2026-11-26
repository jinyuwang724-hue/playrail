// Steam API Key 弹窗静帧自测（--state=recakey）：侧栏进设置 › 游戏库，直接弹出小窗，
// 依次录空的、输到一半的、32 位格式正确的三张，最后取消（不保存、不改任何设置）。帧存到 rec\akey-*。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecAkeyAsync()
    {
        await Task.Delay(2500);
        var style = _prefs.Home;
        string dir = Path.Combine(App.CacheDir, "rec");
        async Task Still(string name)
        {
            await Task.Delay(500);
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(200);
            Log.Info($"recakey {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "akey-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            PadView(); await Task.Delay(900);
            if (!LsOpenNow) throw new InvalidOperationException("panel not open");
            for (int i = 0; i < 7; i++) { Move(Direction.Up); await Task.Delay(110); }
            for (int i = 0; i < 4; i++) { Move(Direction.Down); await Task.Delay(180); }
            if (LsOpenNow) Activate();
            await Task.Delay(1500);
            if (!_stOpen) throw new InvalidOperationException("settings not open");
            StCategoryJump(1); Repaint(); await Task.Delay(500);
            await Still("page");
            if (!AkShowSteam(_connections.Steam?.Id ?? "76561197960287930")) throw new InvalidOperationException("dialog not shown");
            await Task.Delay(300);
            _akBoxes[0].Text = "";
            await Still("empty");
            _akBoxes[0].Text = "0123456789ABCDEF";
            await Still("half");
            _akBoxes[0].Text = "0123456789ABCDEF0123456789ABCDEF";
            await Still("valid");
            AkClose(); await Task.Delay(400);
            // IGDB：两个框，第一个填好、第二个空着（不保存）
            if (!AkShowIgdb()) throw new InvalidOperationException("igdb dialog not shown");
            await Task.Delay(300);
            string id0 = _akBoxes[0].Text, sec0 = _akBoxes[1].Text;
            _akBoxes[0].Text = "abcdefghijklmnopqrstuvwxyz0123"; _akBoxes[1].Text = "";
            await Still("igdb");
            AkClose(); await Task.Delay(400);
            await Still("lib");
            for (int i = 0; i < 4 && !LsOpenNow; i++) { if (!_ccOpen) break; Back(); await Task.Delay(500); }
            if (LsOpenNow) { PadView(); await Task.Delay(500); }
        }
        catch (Exception ex) { Log.Error("recakey", ex); }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recakey done");
    }
}
