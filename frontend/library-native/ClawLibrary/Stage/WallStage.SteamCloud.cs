using ClawLibrary.Data;
using System.Text.Json.Nodes;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private SteamCloudStore _steamCloud = null!;
    private SteamCloudSnapshot? _steamCloudSnapshot;
    private readonly CancellationTokenSource _steamCloudLifetime = new();

    private Library SteamCloudLibrary(SteamCloudSnapshot snapshot, string? root, string? user)
    {
        return Library.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "library.json"), snapshot.Stats,
            id => ArtKitBuilder.TryLoad(Path.Combine(ArtRoot, id)), Steam.ReadCollections(user), snapshot.Apps,
            root == null ? new HashSet<string>() : Steam.ReadInstalled(root), steamOwnedOnly: true);
    }

    private void LoadSteamCloudCache()
    {
        var account = _connections.Steam;
        _steamCloudSnapshot = account != null && _steamCloud.HasKey(account.Id) ? _steamCloud.Load(account.Id) : null;
        if (_steamCloudSnapshot != null) _lib = _lib.ReplacePlatform("steam", SteamCloudLibrary(_steamCloudSnapshot, _steamRoot, _steamUser));
    }

    private async Task RefreshSteamCloud(string accountId, string? newKey = null)
    {
        string key = newKey ?? _steamCloud.ReadKey(accountId);
        var snapshot = await SteamCloudClient.FetchAsync(accountId, key, _steamCloudLifetime.Token);
        if (_servicesDisposed || _connections.Steam?.Id != accountId) return;
        string? root = Steam.Root();
        string? user = root == null ? null : Steam.UserDirForAccount(root, accountId);
        var library = await Task.Run(() => SteamCloudLibrary(snapshot, root, user));
        if (_servicesDisposed || _connections.Steam?.Id != accountId) return;
        _steamCloud.Save(snapshot);
        if (newKey != null) _steamCloud.SaveKey(accountId, newKey);
        _steamCloudSnapshot = snapshot;
        _steamRoot = root; _steamUser = user;
        SaveUi(_prefs with { SourceSteam = true }); Library.IncludeSteam = true;
        ApplyLibrarySnapshot(_lib.ReplacePlatform("steam", library));
    }

    private void ConfigureSteamCloud()
    {
        if (_connections.Steam is not { } account || _steamRefreshing) return;
        if (AkShow(account.Id)) return;   // Cocoon 样式的设置页：弹窗
        StEditSecret(text =>
        {
            string key = text.Trim();
            if (!SteamCloudClient.ValidKey(key)) { Toast("请输入 Steam 提供的 32 位 Web API Key"); return; }
            _ = SaveSteamCloudKey(account.Id, key);
        });
    }

    private async Task SaveSteamCloudKey(string accountId, string key)
    {
        if (_steamRefreshing || _connections.Steam?.Id != accountId) return;
        _steamRefreshing = true; _steamRefreshError = null; Repaint();
        try
        {
            await RefreshSteamCloud(accountId, key);
            if (!_servicesDisposed) Toast($"Steam 云端游戏库已更新 · {_lib.IdentifiedCount("steam")} 款");
        }
        catch (Exception ex)
        {
            if (!_servicesDisposed) { _steamRefreshError = ex is InvalidOperationException ? ex.Message : "云端配置未能保存，请重试"; Toast(_steamRefreshError); }
        }
        finally { _steamRefreshing = false; if (!_servicesDisposed && _stOpen) Repaint(); }
    }

    // ---------------- 本机安装状态：自动跟着 Steam 变（11-26 jinyu：不要手动「更新本机安装状态」） ----------------

    private readonly List<FileSystemWatcher> _steamInstallWatch = new();
    private HashSet<string>? _steamInstalledLast;
    private int _steamInstallGen;

    /// <summary>盯着各个库目录的 appmanifest：Steam 装上 / 卸掉 / 装完一款游戏时，停 2 秒后后台重读安装状态，变了才刷新游戏库。</summary>
    private void WatchSteamInstalls()
    {
        foreach (var w in _steamInstallWatch) w.Dispose();
        _steamInstallWatch.Clear();
        if (Steam.Root() is not { } root) return;
        foreach (var dir in Steam.LibraryDirs(root))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var w = new FileSystemWatcher(dir, "appmanifest_*.acf") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler h = (_, _) => _dq.TryEnqueue(SteamInstallsChanged);
                w.Changed += h; w.Created += h; w.Deleted += h;
                w.Renamed += (_, _) => _dq.TryEnqueue(SteamInstallsChanged);
                w.EnableRaisingEvents = true;
                _steamInstallWatch.Add(w);
            }
            catch (Exception ex) { Log.Warn($"watch {dir}: {ex.Message}"); }
        }
        Log.Info($"steam install watch: {_steamInstallWatch.Count} folders");
        // 记下启动时的安装状态，之后只有真的变了才刷新
        _ = Task.Run(() => Steam.ReadInstalled(root)).ContinueWith(t => { if (!t.IsFaulted) _dq.TryEnqueue(() => _steamInstalledLast ??= t.Result); });
    }

    private void SteamInstallsChanged()
    {
        int gen = ++_steamInstallGen;
        After(2000, () => { if (gen == _steamInstallGen && !_servicesDisposed) RefreshSteamInstallation(); });
    }

    private void RefreshSteamInstallation()
    {
        if (_steamRefreshing || _servicesDisposed) return;
        string? root = Steam.Root();
        if (root == null) return;
        _ = Task.Run(() => Steam.ReadInstalled(root)).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            var set = t.Result;
            _dq.TryEnqueue(() =>
            {
                if (_servicesDisposed) return;
                if (_steamInstalledLast != null && _steamInstalledLast.SetEquals(set)) return;
                _steamInstalledLast = set;
                _steamRoot = root;
                Log.Info($"steam installs changed: {set.Count}");
                ApplyLibrarySnapshot(_lib.Refilter(set));
            });
        });
    }
}
