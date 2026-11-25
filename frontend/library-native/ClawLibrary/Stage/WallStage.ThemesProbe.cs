// 三套主题定版前的截图（--state=recthemes）：各种首页样式和海报墙的几种背景各截一张，看完恢复原来的设置。
// 帧存到 %LOCALAPPDATA%\ClawLibrary\rec\theme-*。只改界面偏好，不碰后台。
namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal async Task RecThemesAsync()
    {
        await Task.Delay(2500);
        var prefs = _prefs;
        var bg = _backgroundSettings;
        string dir = Path.Combine(App.CacheDir, "rec");
        async Task Still(string name)
        {
            await Task.Delay(2200);
            var r = new FrameRecorder { Full = true };
            if (!r.Start(HostWindow)) return;
            await Task.Delay(200);
            Log.Info($"recthemes {name}: {await r.StopAndSaveAsync(Path.Combine(dir, "theme-" + name), System.Diagnostics.Stopwatch.GetTimestamp())} frames");
        }
        try
        {
            foreach (var s in new[] { HomeStyle.Stand, HomeStyle.Stack, HomeStyle.Shelf, HomeStyle.Hero })
            {
                SaveUi(_prefs with { Home = s }); CsMorphStyle();
                await Still(s.ToString().ToLowerInvariant());
            }
            SaveUi(_prefs with { Home = HomeStyle.Wall }); CsMorphStyle();
            foreach (var k in new[] { BackgroundKind.Latent, BackgroundKind.Monet, BackgroundKind.Nomand })
            {
                _backgroundSettings = bg with { Kind = k }; ApplyBackgroundSettings();
                await Still("wall-" + k.ToString().ToLowerInvariant());
            }
        }
        catch (Exception ex) { Log.Error("recthemes", ex); }
        _backgroundSettings = bg; ApplyBackgroundSettings();
        SaveUi(prefs); CsMorphStyle();
        Log.Info("recthemes done");
    }
}
