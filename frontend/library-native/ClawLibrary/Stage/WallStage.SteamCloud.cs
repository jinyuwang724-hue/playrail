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

    private void RefreshSteamInstallation()
    {
        if (_steamRefreshing) return;
        string? root = Steam.Root();
        if (root == null) { Toast("本机尚未安装 Steam"); return; }
        _steamRoot = root;
        ApplyLibrarySnapshot(_lib.Refilter(Steam.ReadInstalled(root)));
    }
}
