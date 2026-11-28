// 主题色页静帧（--state=recaccent）：侧栏进设置 › 外观与主题；焦点在颜色珠上、在「自定义」上各录一张（只移焦点不按 A）；
// 打开取色板录一张，挪色相、换端点、挪画布再录一张，不保存。帧存 rec\accent-*。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecAccentAsync()
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
            Log.Info($"recaccent {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "accent-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        try
        {
            VerifyCocoonColorScience();
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            PadView(); await Task.Delay(900);
            if (!LsOpenNow) throw new InvalidOperationException("panel not open");
            for (int i = 0; i < 7; i++) { Move(Direction.Up); await Task.Delay(110); }
            for (int i = 0; i < 5; i++) { Move(Direction.Down); await Task.Delay(180); }
            if (LsOpenNow) Activate();
            await Task.Delay(1500);
            if (!_stOpen) throw new InvalidOperationException("settings not open");
            StCategoryJump(2); Repaint(); await Task.Delay(400);
            Move(Direction.Right); await Task.Delay(500);
            await Still("page");
            Move(Direction.Down); await Task.Delay(350);
            Move(Direction.Right); await Task.Delay(200); Move(Direction.Right); await Task.Delay(350);
            await Still("orbs");
            Move(Direction.Down); await Task.Delay(400);
            await Still("custom");
            CtpOpen(); await Task.Delay(700);
            await Still("picker");
            for (int k = 0; k < 10; k++) { CtpMove(Direction.Left); await Task.Delay(60); }
            CtpSwitchTab(1); await Task.Delay(200);
            CtpMove(Direction.Up); await Task.Delay(200);
            for (int k = 0; k < 6; k++) { CtpMove(Direction.Right); await Task.Delay(60); }
            await Still("picker2");
            CtpClose(commit: false); await Task.Delay(500);
            // 暗色模式：切过去录设置页和取色板，再切回原样
            bool wasDark = _prefs.CocoonDark;
            SetCocoonDark(true); await Task.Delay(600);
            await Still("dark-page");
            CtpOpen(); await Task.Delay(700);
            await Still("dark-picker");
            CtpClose(commit: false); await Task.Delay(500);
            SetCocoonDark(wasDark); await Task.Delay(400);
            for (int i = 0; i < 4 && !LsOpenNow; i++) { if (!_ccOpen) break; Back(); await Task.Delay(500); }
            if (LsOpenNow) { PadView(); await Task.Delay(500); }
        }
        catch (Exception ex) { Log.Error("recaccent", ex); }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recaccent done");
    }
}
