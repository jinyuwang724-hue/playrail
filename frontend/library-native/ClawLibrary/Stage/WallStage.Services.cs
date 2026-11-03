using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private string _steamAccount = "正在读取…";
    private bool _serviceActionBusy;
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _trayTimers = new();

    public void SuspendToTray()
    {
        foreach (var timer in new[] { _ccTimer, _stTimer, _perfTimer, _dsTimer, _syTimer, _gpLive, _railTimer })
            if (timer?.IsRunning == true) { timer.Stop(); _trayTimers.Add(timer); }
        _windowActive = false;
        UpdateBackdropRunning();
    }

    public void ResumeFromTray()
    {
        foreach (var timer in _trayTimers) timer.Start();
        _trayTimers.Clear();
    }

    private void BuildStServices()
    {
        _stRows.Add(new PerfRow { Section = true, Title = "Steam" });
        _stRows.Add(StInfo("本机账户", _steamAccount));
        _stRows.Add(StInfo("客户端", Steam.Root() == null ? "未安装" : PlatformClients.SteamRunning() ? "运行中" : "未运行"));
        _stRows.Add(StToggle("显示 Steam 游戏", _prefs.SourceSteam, on =>
        {
            SaveUi(_prefs with { SourceSteam = on }); _stNeedsReload = true; Repaint();
        }, true, "重新载入后生效"));
        _stRows.Add(StToggle("打开游戏库时在托盘启动 Steam", _prefs.PreloadSteam, on =>
        {
            SaveUi(_prefs with { PreloadSteam = on }); Repaint();
            if (on) _ = RunServiceAction(() => PlatformClients.StartSteamInTray());
        }, true));
        _stRows.Add(StAction("立即在托盘启动 Steam", "steam-tray", _serviceActionBusy ? "处理中…" : "", () =>
            _ = RunServiceAction(() => PlatformClients.StartSteamInTray())));
        _stRows.Add(StAction("打开 Steam / 管理账户", "steam-open", "在 Steam 中登录或切换账户", () =>
            _ = RunServiceAction(() => { PlatformClients.Open("steam://open/main"); return "已打开 Steam"; })));
        _stRows.Add(StInfo("游戏库范围", "本机缓存与安装记录，可能不含全部已购游戏"));

        _stRows.Add(new PerfRow { Section = true, Title = "Xbox" });
        var xbox = XboxGames.Current;
        _stRows.Add(StInfo("账户", "在 Xbox 客户端中登录或切换账户"));
        _stRows.Add(StToggle("显示 Xbox 游戏", _prefs.SourceXbox, on =>
        {
            SaveUi(_prefs with { SourceXbox = on }); _stNeedsReload = true; Repaint();
        }, true, "重新载入后生效"));
        _stRows.Add(StAction("打开 Xbox / 管理账户", "xbox-open", "", () =>
            _ = RunServiceAction(() => { PlatformClients.Open("msxbox://"); return "已请求打开 Xbox，请在客户端中登录"; })));
        _stRows.Add(StInfo("本机已安装游戏", !_prefs.SourceXbox ? "来源已关闭" : xbox.Error ?? $"识别到 {xbox.Games.Count} 款"));
        if (xbox.Unreadable > 0) _stRows.Add(StInfo("部分应用无法读取", "扫描可能不完整，可在 Xbox 客户端中查看"));
        _stRows.Add(StInfo("同步范围", "已注册的 GDK / Xbox Live 游戏，不含云端完整库"));
        _stRows.Add(new PerfRow { Section = true, Title = "刷新" });
        _stRows.Add(StAction("重新扫描平台游戏库", "services-reload", _stNeedsReload ? "有改动待生效" : "重新载入游戏库", RestartLibrary));
    }

    private async Task RunServiceAction(Func<string> action)
    {
        if (_serviceActionBusy) return;
        _serviceActionBusy = true;
        try { Toast(await Task.Run(action)); }
        catch (Exception ex) { Toast("没有做成：" + ex.Message); }
        finally
        {
            _serviceActionBusy = false;
            _steamAccount = Steam.LocalAccount();
            if (_stOpen) Repaint();
        }
    }
}
