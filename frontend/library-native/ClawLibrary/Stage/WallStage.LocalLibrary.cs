using System.Security.Cryptography;
using System.Text;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private bool _localLibraryRefreshing, _localLibraryRefreshAgain;
    private string? _localLibraryStamp;
    private Dictionary<string, string> _localArtDirs = new();

    private string ArtDirectory(string id) => _localArtDirs.TryGetValue(id, out var dir) ? dir : Path.Combine(ArtRoot, id);

    private string LocalArtDirectory(LocalGame game)
    {
        string? portrait = LocalGames.ArtFile(game.Id, "p"), hero = LocalGames.ArtFile(game.Id, "_hero");
        if (portrait == null && hero == null) return Path.Combine(ArtRoot, game.Id);
        string Stamp(string? path) => path == null ? "-" : $"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path).Ticks}";
        string version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Stamp(portrait)}|{Stamp(hero)}")))[..16];
        return Path.Combine(ArtRoot, game.Id, "local-" + version);
    }

    /// <summary>保存与 Steam 同步共用的库内刷新；并发请求合并，窗口和导航状态保持原样。</summary>
    private async Task RefreshLocalLibraryAsync()
    {
        if (_servicesDisposed) return;
        if (_localLibraryRefreshing) { _localLibraryRefreshAgain = true; return; }
        _localLibraryRefreshing = true;
        try
        {
            do
            {
                _localLibraryRefreshAgain = false;
                string[] folders = _prefs.Folders.ToArray();
                string? root = _steamRoot ?? Steam.Root();
                string? user = _steamUser ?? (root == null ? null : _connections.Steam is { } account
                    ? Steam.UserDirForAccount(root, account.Id) : Steam.UserDir(root));
                var result = await Task.Run(async () =>
                {
                    var local = LocalGames.Read();
                    var shortcuts = Steam.ShortcutTargets(user);
                    var excluded = shortcuts.Select(s => s.Exe).Concat(local.Select(g => g.Exe)).ToList();
                    var played = FolderGames.ReadPlayed();
                    var scanned = folders.SelectMany(f => FolderGames.Scan(f, excluded))
                        .Select(g => (g.Title, g.Exe, Last: played.TryGetValue(FolderGames.IdOf(g.Exe), out var t) ? (long?)t : null)).ToList();
                    var artDirs = local.ToDictionary(g => g.Id, LocalArtDirectory);
                    foreach (var game in local)
                    {
                        string? portrait = LocalGames.ArtFile(game.Id, "p"), hero = LocalGames.ArtFile(game.Id, "_hero");
                        if (portrait == null && hero == null) continue;
                        // 换图使用新的缓存地址，现有卡面可以一直使用旧图直到新图生成完。
                        string dir = artDirs[game.Id];
                        if (ArtKitBuilder.TryLoad(dir) == null) await ArtKitBuilder.BuildAsync(dir, portrait, hero, 0.5, null);
                    }
                    // 只读取本地来源；不会重新请求云端所有权或阻塞等待 Steam 启动。
                    Library.FolderGames = scanned;
                    var library = Library.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "library.json"), Steam.ReadPlayStats(user),
                        id => ArtKitBuilder.TryLoad(artDirs.TryGetValue(id, out var dir) ? dir : Path.Combine(ArtRoot, id)),
                        Steam.ReadCollections(user), shortcuts: shortcuts, local: local);
                    return (Library: library, ArtDirs: artDirs);
                });
                if (_servicesDisposed) return;
                _localArtDirs = result.ArtDirs;
                ApplyLibrarySnapshot(_lib.ReplaceLocalSources(result.Library), preservePosition: true);
                Log.Info("local library refreshed in place");
            }
            while (_localLibraryRefreshAgain && !_servicesDisposed);
        }
        catch (Exception ex)
        {
            _localLibraryStamp = null; // 下次读状态时可以重试，保存成功不被误报成保存失败。
            Log.Error("local library refresh", ex);
            if (!_servicesDisposed) Toast("游戏库未能刷新，请稍后重试");
        }
        finally { _localLibraryRefreshing = false; }
    }
}
