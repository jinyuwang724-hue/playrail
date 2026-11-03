using System.Xml.Linq;
using Windows.Management.Deployment;

namespace ClawLibrary.Data;

/// <summary>当前 Windows 用户已注册的 Xbox / Store 游戏。只读包元数据，启动交给系统 AppListEntry。</summary>
internal static class XboxGames
{
    public sealed record Game(string Aumid, string Name, string Family);
    public sealed record Snapshot(List<Game> Games, bool ClientInstalled, int Unreadable, string? Error);

    public static Snapshot Current { get; private set; } = new(new(), false, 0, null);

    // 不根据名称猜游戏：配置文件是 GDK / Xbox Live 的标记；旧包也可声明 gameExplorer。
    internal static bool HasGameDeclaration(string manifest) => XDocument.Parse(manifest).Descendants()
        .Any(e => e.Name.LocalName == "Extension" && (string?)e.Attribute("Category") == "windows.gameExplorer");

    public static async Task<Snapshot> ReadAsync()
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        bool client = false;
        int unreadable = 0;
        try
        {
            var pm = new PackageManager();
            foreach (var package in pm.FindPackagesForUser(""))
            {
                try
                {
                    if (package.Id.Name == "Microsoft.GamingApp") client = true;
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    string folder = package.InstalledLocation.Path;
                    bool game = File.Exists(Path.Combine(folder, "MicrosoftGame.config")) ||
                        File.Exists(Path.Combine(folder, "xboxservices.config"));
                    if (!game)
                    {
                        string manifest = Path.Combine(folder, "AppxManifest.xml");
                        if (File.Exists(manifest)) game = HasGameDeclaration(File.ReadAllText(manifest));
                    }
                    if (!game) continue;
                    var entries = await package.GetAppListEntriesAsync();
                    foreach (var entry in entries)
                    {
                        string id = entry.AppUserModelId;
                        string name = entry.DisplayInfo.DisplayName;
                        if (string.IsNullOrWhiteSpace(name)) name = package.DisplayName;
                        games.TryAdd(id, new Game(id, name, package.Id.FamilyName));
                    }
                }
                catch (Exception ex)
                {
                    unreadable++;
                    Log.Warn($"Xbox package {package.Id.Name}: {ex.Message}");
                }
            }
            Current = new(games.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), client, unreadable, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Xbox library: {ex.Message}");
            Current = new(new(), client, unreadable, "无法读取本机游戏，请重新扫描");
        }
        Log.Info($"Xbox local library: {Current.Games.Count} entries, unreadable={unreadable}");
        return Current;
    }

    public static async Task LaunchAsync(string aumid)
    {
        string family = aumid.Split('!')[0];
        foreach (var package in new PackageManager().FindPackagesForUser("", family))
            foreach (var entry in await package.GetAppListEntriesAsync())
                if (entry.AppUserModelId == aumid)
                {
                    if (!await entry.LaunchAsync()) throw new InvalidOperationException("Windows 未能启动此游戏");
                    return;
                }
        throw new InvalidOperationException("此游戏已被移除，请重新载入游戏库");
    }
}
