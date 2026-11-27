// Cocoon 相册静帧自测（--state=recalbum）：侧栏进「相册」，录 左列 / 网格焦点 / 大图 / Y 菜单 / 删除确认 / 换筛选。
// 只走方向键、A（打开大图、在菜单「删除」上只弹确认小窗）、Y 和 B；确认小窗里只按 B，不删任何东西。帧存 rec\album-*。
using ClawLibrary.Engine;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecAlbumAsync()
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
            Log.Info($"recalbum {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "album-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        try
        {
            if (_cs != null && _prefs.Home != HomeStyle.Row) { SaveUi(_prefs with { Home = HomeStyle.Row }); CsMorphStyle(); await Task.Delay(1500); }
            PadView(); await Task.Delay(900);
            if (!LsOpenNow) throw new InvalidOperationException("panel not open");
            for (int i = 0; i < 7; i++) { Move(Direction.Up); await Task.Delay(110); }
            for (int i = 0; i < 4; i++) { Move(Direction.Down); await Task.Delay(180); }
            if (LsOpenNow) Activate();
            await Task.Delay(2500);
            if (!CshActive) throw new InvalidOperationException("album not open");
            await Still("left");
            Move(Direction.Right); await Task.Delay(400);
            Move(Direction.Right); await Task.Delay(300);
            Move(Direction.Down); await Task.Delay(400);
            await Still("grid");
            if (_cshZone != 1) throw new InvalidOperationException("not in grid");
            Activate(); await Task.Delay(900);
            await Still("viewer");
            Move(Direction.Right); await Task.Delay(500);
            await Still("viewer2");
            PadY(); await Task.Delay(600);
            await Still("menu");
            if (!_cshMenuOpen) throw new InvalidOperationException("menu not open");
            for (int i = 0; i < 3; i++) { Move(Direction.Down); await Task.Delay(250); }
            if (_cshMenuSel != 3) throw new InvalidOperationException("not on delete");
            Activate(); await Task.Delay(600);   // 只弹确认小窗
            await Still("dialog");
            if (!_cshDlgOpen) throw new InvalidOperationException("dialog not open");
            Back(); await Task.Delay(400);       // 取消，不删
            Back(); await Task.Delay(500);       // 回网格
            Back(); await Task.Delay(400);       // 回左列
            for (int i = 0; i < 2; i++) { Move(Direction.Down); await Task.Delay(400); }
            await Still("filter");
            for (int i = 0; i < 4 && !LsOpenNow; i++) { if (!_ccOpen) break; Back(); await Task.Delay(500); }
            if (LsOpenNow) { PadView(); await Task.Delay(500); }
        }
        catch (Exception ex) { Log.Error("recalbum", ex); }
        if (_cs != null && style != HomeStyle.Row) { SaveUi(_prefs with { Home = style }); CsMorphStyle(); }
        Log.Info("recalbum done");
    }
}
