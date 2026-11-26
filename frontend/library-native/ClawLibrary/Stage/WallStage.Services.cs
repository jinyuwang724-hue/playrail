using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private PlatformConnectionStore _connections = null!;
    private CancellationTokenSource? _connectCancel;
    private bool _steamRefreshing, _xboxRefreshing, _servicesDisposed;
    private string? _steamRefreshError, _xboxRefreshError;
    private bool _serviceActionBusy;
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _trayTimers = new();

    public void SuspendToTray()
    {
        foreach (var timer in new[] { _ccTimer, _stTimer, _perfTimer, _dsTimer, _syTimer, _gpLive, _railTimer })
            if (timer?.IsRunning == true) { timer.Stop(); _trayTimers.Add(timer); }
        _windowActive = false;
        UpdateBackdropRunning();
        // 托盘里没人看：无限循环的合成动画（横幅慢推、1 px 节拍点）会让合成线程照样按刷新率出帧，
        // 隐藏的游戏库因此常驻吃掉约 14 % 的核。收起时一起暂停，出来时接着走
        SetForeverAnimationsPaused(true);
    }

    public void ResumeFromTray()
    {
        foreach (var timer in _trayTimers) timer.Start();
        _trayTimers.Clear();
        SetForeverAnimationsPaused(false);
    }

    private void SetForeverAnimationsPaused(bool paused)
    {
        var drift = _bnClock?.TryGetAnimationController("t");
        if (drift != null) { if (paused) drift.Pause(); else drift.Resume(); }
        // 节拍点只跟着动态背景跑（UpdateBackdropRunning 负责恢复），这里只管停
        if (paused) _pacer?.TryGetAnimationController("Opacity")?.Pause();
    }

    /// <summary>Steam / Xbox 的账户和游戏库。oneCard：并进设置 › 游戏库的第一张卡里（不另起分组）。</summary>
    private void BuildStServices(bool oneCard = false)
    {
        if (!oneCard) _stRows.Add(new PerfRow { Section = true, Title = "Steam" });
        var account = _connections.Steam;
        _stRows.Add(new PerfRow { Title = "账户", Value = account?.Name ?? "未连接", Skip = true });
        _stRows.Add(new PerfRow
        {
            Sub = _connectCancel == null,   // 打开 Steam 登录网页
            Title = _connectCancel != null ? "取消连接" : account == null ? "连接 Steam" : "更换 Steam 账户",
            Disabled = _steamRefreshing,
            Value = _connectCancel != null ? "正在等待网页确认…" : "",
            Press = () => { if (_connectCancel != null) _connectCancel.Cancel(); else _ = ConnectSteam(); },
        });
        if (account != null)
        {
            bool cloud = _steamCloud.HasKey(account.Id);
            _stRows.Add(new PerfRow { Sub = true, Title = "Steam 云端同步", Value = cloud ? "已配置" : "需配置 Web API Key", Disabled = _steamRefreshing, Press = ConfigureSteamCloud });
            if (_steamCloudSnapshot?.SteamId == account.Id)
                _stRows.Add(new PerfRow { Title = "上次云端同步", Value = _steamCloudSnapshot.Updated.ToLocalTime().ToString("MM-dd HH:mm"), Skip = true });
            // 本机安装状态自动跟着 Steam 变（WatchSteamInstalls），不再列手动更新；云端配置不单独移除（11-26 jinyu）
        }
        _stRows.Add(new PerfRow { Title = _steamCloudSnapshot == null ? "本机已识别游戏" : "云端已识别游戏", Value = $"{_lib.IdentifiedCount("steam")} 款", Skip = true, BrightValue = true });
        _stRows.Add(new PerfRow { Title = "更新 Steam 游戏库", Value = _steamRefreshing ? "更新中…" : "", Disabled = _steamRefreshing,
            Press = () => _ = RefreshPlatformLibrary("steam") });
        if (account != null) _stRows.Add(new PerfRow { Title = "解除 Steam 账号绑定", Disabled = _connectCancel != null || _steamRefreshing,
            Press = () => { try { _connections.SetSteam(null); _steamCloudSnapshot = null;
                ApplyLibrarySnapshot(_lib.ReplacePlatform("steam", Library.Build(new(), new())));
                _ = RefreshPlatformLibrary("steam"); Repaint(); } catch { Toast("连接记录未能保存，请重试"); } } });
        if (_steamRefreshError != null) _stRows.Add(new PerfRow { Title = "更新状态", Value = _steamRefreshError, Skip = true });

        if (!oneCard) _stRows.Add(new PerfRow { Section = true, Title = "Xbox" });
        _stRows.Add(new PerfRow { Title = "账户", Value = "未连接", Skip = true });
        // 尚无本应用的 Microsoft Client ID；不以打开客户端冒充账户授权。
        _stRows.Add(new PerfRow { Title = "连接 Xbox", Value = "暂未开放", Press = () => Toast("Xbox 账户连接尚未开放，本机游戏可正常识别和启动") });
        _stRows.Add(new PerfRow { Title = "本机已识别游戏", Value = $"{_lib.IdentifiedCount("xbox")} 款", Skip = true, BrightValue = true });
        _stRows.Add(new PerfRow { Title = "更新 Xbox 游戏库", Value = _xboxRefreshing ? "更新中…" : "", Disabled = _xboxRefreshing,
            Press = () => _ = RefreshPlatformLibrary("xbox") });
        if (_xboxRefreshError != null) _stRows.Add(new PerfRow { Title = "更新状态", Value = _xboxRefreshError, Skip = true });
    }

    private async Task ConnectSteam()
    {
        if (_connectCancel != null) return;
        using var cancellation = new CancellationTokenSource();
        _connectCancel = cancellation;
        Repaint();
        try
        {
            var account = await SteamOpenId.ConnectAsync(uri => PlatformClients.Open(uri.AbsoluteUri), cancellation.Token);
            if (_servicesDisposed) return;
            string? previousId = _connections.Steam?.Id;
            _connections.SetSteam(account);
            if (previousId != account.Id)
            {
                _steamCloudSnapshot = null;
                _steamUser = null;
                _lib = _lib.ReplacePlatform("steam", Library.Build(new(), new()));
                LoadSteamCloudCache();
                ApplyLibrarySnapshot(_lib);
            }
            if (_window is MainWindow main) main.ActivateRoute("restore-library");
            Toast($"已连接 Steam：{account.Name}");
            await RefreshPlatformLibrary("steam");
        }
        catch (OperationCanceledException) { if (!_servicesDisposed) Toast(cancellation.IsCancellationRequested ? "已取消连接" : "连接已取消或超时，请重试"); }
        catch (Exception ex) { Log.Warn($"Steam connection: {ex.GetType().Name}"); if (!_servicesDisposed) Toast("Steam 连接未完成，请检查网络后重试"); }
        finally { _connectCancel = null; if (!_servicesDisposed && _stOpen) Repaint(); }
    }

    private async Task RefreshPlatformLibrary(string platform, bool quiet = false)
    {
        bool steam = platform == "steam";
        if (steam ? _steamRefreshing : _xboxRefreshing) return;
        if (steam) { _steamRefreshing = true; _steamRefreshError = null; }
        else { _xboxRefreshing = true; _xboxRefreshError = null; }
        if (_stOpen) Repaint();
        try
        {
            if (steam)
            {
                string? linkedId = _connections.Steam?.Id;
                if (linkedId != null && _steamCloud.HasKey(linkedId))
                {
                    await RefreshSteamCloud(linkedId);
                    if (!quiet && !_servicesDisposed) Toast($"Steam 云端游戏库已更新 · {_lib.IdentifiedCount("steam")} 款");
                    return;
                }
                var result = await Task.Run(() =>
                {
                    string root = Steam.Root() ?? throw new InvalidOperationException("请先在本机安装 Steam");
                    string? user = linkedId == null ? Steam.UserDir(root) : Steam.UserDirForAccount(root, linkedId);
                    if (linkedId != null && user == null) throw new InvalidOperationException("请先在本机 Steam 登录此账户，再更新游戏库");
                    var library = Library.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "library.json"), Steam.ReadPlayStats(user),
                        id => ArtKitBuilder.TryLoad(Path.Combine(ArtRoot, id)), Steam.ReadCollections(user), Steam.ReadOwnedApps(root),
                        Steam.ReadInstalled(root));
                    return (library, root, user);
                });
                if (_servicesDisposed) return;
                SaveUi(_prefs with { SourceSteam = true });
                Library.IncludeSteam = true;
                _steamRoot = result.root; _steamUser = result.user;
                _steamCloudSnapshot = null;
                ApplyLibrarySnapshot(_lib.ReplacePlatform("steam", result.library));
            }
            else
            {
                var result = await Task.Run(XboxGames.ReadAsync);
                if (_servicesDisposed) return;
                if (result.Error != null) throw new InvalidOperationException(result.Error);
                SaveUi(_prefs with { SourceXbox = true });
                Library.XboxLibrary = result.Games;
                if (_lib.IdentifiedCount("xbox") != 0 || result.Games.Count != 0)
                    ApplyLibrarySnapshot(_lib.ReplaceXbox(result.Games));
                if (result.Unreadable > 0) _xboxRefreshError = "部分应用无法读取";
            }
            if (!quiet) Toast($"{(steam ? "Steam" : "Xbox")} 游戏库已更新 · {_lib.IdentifiedCount(platform)} 款");
        }
        catch (Exception ex)
        {
            Log.Warn($"{platform} library refresh: {ex.GetType().Name}");
            string error = ex is InvalidOperationException ? ex.Message : "未能更新，请稍后重试";
            if (steam) _steamRefreshError = error; else _xboxRefreshError = error;
            if (!_servicesDisposed && !quiet) Toast(error);
        }
        finally
        {
            if (steam) _steamRefreshing = false; else _xboxRefreshing = false;
            if (!_servicesDisposed && _stOpen) Repaint();
        }
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
            if (!_servicesDisposed && _stOpen) Repaint();
        }
    }
}
