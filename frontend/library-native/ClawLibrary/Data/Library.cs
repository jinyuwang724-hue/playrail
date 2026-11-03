// 游戏库数据：种子清单（Assets\library.json，与网页预览同一份）+ 本机 Steam 的上次游玩 / 时长 + 封面套件。
// 条目、合集、首页页签的规则与 src/data/real.ts 相同。
using System.Text.Json;

namespace ClawLibrary.Data;

public enum ItemKind { Game, Collection }

/// <summary>封面套件：Dir 下的 p/h/s/x(/v/n).jpg；Fx 是背景图的水平焦点，Ha 是背景图宽高比，C 是主色。</summary>
public sealed class ArtKit
{
    public required string Dir;
    public bool P, Q, G, V, N, H, S, X;
    public double? Fx, Ha;
    public string? C;
    public bool Has(char kind) => kind switch { 'p' => P, 'q' => Q, 'g' => G, 'v' => V, 'n' => N, 'h' => H, 's' => S, 'x' => X, _ => false };
    public string File(char kind) => System.IO.Path.Combine(Dir, kind + ".jpg");
}

public sealed class LibraryItem
{
    public required string Id;
    public ItemKind Kind;
    public required string Title;
    /// <summary>第二行：开发商 · 年份，或集合的游戏数。</summary>
    public string Subtitle = "";
    public string Badge = "";
    public int Hue;
    public string? Platform;
    public double? PlaytimeH;
    public int? LastPlayedDays;
    public string? Genre;
    public List<string>? Children;
    /// <summary>排版标题：小字原名 / 主标题 / 副标题。</summary>
    public string? Kicker, Main, Tail;
    public ArtKit? OwnArt;
    /// <summary>合集借用成员的封面（套件晚到时跟着更新）。</summary>
    public LibraryItem? ArtFrom;
    public ArtKit? Art => OwnArt ?? ArtFrom?.Art;
    public long? LastPlayed;
    /// <summary>Steam 游戏装好了没有（本地游戏总是 true）。没装的卡上写「未安装」，A 键去 Steam 安装。</summary>
    public bool Installed = true;
    /// <summary>手动加的文件夹里的游戏：直接启动这个 exe。</summary>
    public string? ExePath;
    public string? ExeStartDir, ExeArgs;
    /// <summary>本地条目的稳定程序身份；同步后即使改用 Steam 启动也保留，供原位刷新匹配。</summary>
    public string? LocalExePath;
    public string? XboxAumid;
    public string? Series;
    public double SeedFx;
    public string? SeedColor;
}

public sealed record HomeTab(string Id, string Label, Func<List<LibraryItem>> Items);

public sealed class Library
{
    public List<LibraryItem> Games = new();
    public List<LibraryItem> Collections = new();
    public Dictionary<string, LibraryItem> ById = new();
    public List<HomeTab> HomeTabs = new();
    private List<LibraryItem> _sourceGames = new();
    private List<SteamCollection> _sourceCollections = new();

    public int IdentifiedCount(string platform) => _sourceGames.Count(g => g.Platform == platform);

    /// <summary>只替换本次刷新的平台，保留其他平台和本地条目，以及完整的筛选前来源。</summary>
    public Library ReplacePlatform(string platform, Library replacement)
    {
        var incoming = replacement._sourceGames.Where(g => g.Platform == platform).ToList();
        foreach (var game in incoming)
            if (ById.TryGetValue(game.Id, out var previous)) game.OwnArt ??= previous.OwnArt;
        var games = _sourceGames.Where(g => g.Platform != platform).Concat(incoming).ToList();
        return Build(games, platform == "steam" ? replacement._sourceCollections : _sourceCollections);
    }

    /// <summary>本地编辑/同步只更新本地来源，保留云端所有权、安装范围和 Xbox 扫描结果。</summary>
    public Library ReplaceLocalSources(Library replacement)
    {
        static bool Local(LibraryItem game) => game.Platform is not ("steam" or "xbox");
        return Build(_sourceGames.Where(g => !Local(g)).Concat(replacement._sourceGames.Where(Local)).ToList(),
            replacement._sourceCollections);
    }

    internal Library ReplaceXbox(List<XboxGames.Game> entries)
    {
        var games = entries.Select(x => new LibraryItem
        {
            Id = "xbox-" + x.Aumid, Kind = ItemKind.Game, Title = x.Name, Main = x.Name,
            Platform = "xbox", Badge = "XBOX", Subtitle = "Xbox / Microsoft Store",
            XboxAumid = x.Aumid, Hue = HueOf(x.Name), SeedFx = 0.5,
        }).ToList();
        return ReplacePlatform("xbox", Build(games, new()));
    }

    /// <summary>保留完整来源以便即时切换筛选，并恢复刚才被筛掉的游戏。</summary>
    public Library Refilter(HashSet<string>? installed = null)
    {
        if (installed != null)
            foreach (var game in _sourceGames.Where(g => g.Platform == "steam"))
            {
                game.Installed = installed.Contains(game.Id);
                game.Badge = game.Installed ? "STEAM" : "STEAM · 未安装";
            }
        return Build(_sourceGames, _sourceCollections);
    }

    private static int HueOf(string s)
    {
        int sum = 7;
        foreach (var r in s.EnumerateRunes())
        {
            int code = r.IsBmp ? r.Value : char.ConvertFromUtf32(r.Value)[0];
            sum = (sum * 31 + code) % 360;
        }
        return sum;
    }

    private const long Day = 86400;

    public static Library Load(string seedPath, Dictionary<string, PlayStats> stats, Func<string, ArtKit?> art,
        List<SteamCollection>? steamCollections = null, List<SteamApp>? owned = null, HashSet<string>? installed = null,
        List<(string AppId, string Name, string Exe, string StartDir)>? shortcuts = null, List<LocalGame>? local = null, bool steamOwnedOnly = false)
    {
        var games = new List<LibraryItem>();
        steamCollections ??= new();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(seedPath));
            foreach (var row in doc.RootElement.GetProperty("games").EnumerateArray())
            {
                string S(int i) => System.Net.WebUtility.HtmlDecode(row[i].GetString() ?? "");
                var id = S(0);
                stats.TryGetValue(id, out var st);
                var platform = S(6);
                var main = S(3);
                var g = new LibraryItem
                {
                    Id = id,
                    Kind = ItemKind.Game,
                    Title = S(1),
                    Kicker = string.IsNullOrEmpty(S(2)) ? null : S(2),
                    Main = main,
                    Tail = string.IsNullOrEmpty(S(4)) ? null : S(4),
                    Subtitle = S(5),
                    Platform = platform,
                    Badge = platform == "steam" ? "STEAM" : "PC",
                    Genre = row[7].GetInt32() == 1 ? "Galgame" : null,
                    Series = string.IsNullOrEmpty(S(8)) ? null : S(8),
                    SeedFx = row[9].GetDouble(),
                    SeedColor = S(10),
                    LastPlayed = st?.Last,
                    PlaytimeH = st?.Minutes is int m ? Math.Round(m / 60.0) : null,
                };
                g.Hue = HueOf(g.Title);
                g.OwnArt = art(id);
                games.Add(g);
            }
        }
        catch (Exception ex) { Log.Error("library seed", ex); }

        // 种子清单以外、Steam 库里的其余游戏：只要类型是游戏的（工具、DLC、原声不要）；
        // 用户自己放进分类里的软件（比如「工具」分类里的 Wallpaper Engine）也收进来。
        if (owned != null)
        {
            var seen = games.Select(g => g.Id).ToHashSet();
            var inCollections = steamCollections.Where(c => c.Id != "hidden").SelectMany(c => c.Added).ToHashSet();
            foreach (var app in owned)
            {
                if (seen.Contains(app.Id)) continue;
                bool game = app.Type.Equals("game", StringComparison.OrdinalIgnoreCase);
                bool soft = app.Type.Equals("application", StringComparison.OrdinalIgnoreCase) && inCollections.Contains(app.Id);
                if (!game && !soft) continue;
                stats.TryGetValue(app.Id, out var st);
                var sub = string.Join(" · ", new[] { app.Developer, app.Year?.ToString() }.Where(x => !string.IsNullOrEmpty(x)));
                var g = new LibraryItem
                {
                    Id = app.Id, Kind = ItemKind.Game, Title = app.Name, Main = app.Name, Subtitle = sub,
                    Platform = "steam", Badge = "STEAM", SeedFx = 0.5,
                    LastPlayed = st?.Last,
                    PlaytimeH = st?.Minutes is int m ? Math.Round(m / 60.0) : null,
                };
                g.Hue = HueOf(g.Title);
                g.OwnArt = art(app.Id);
                games.Add(g);
                seen.Add(app.Id);
            }
        }
        // 设置里「添加游戏」加的本地游戏：id 就是它的快捷方式 appid，已经同步进 Steam 的走 Steam 启动（有游玩记录、Steam 覆盖层），
        // 还没同步的直接启动 exe。排在快捷方式之前，下面收快捷方式时同一个 id / 同一个 exe 不会再收一次。
        if (local != null)
        {
            var seen = games.Select(g => g.Id).ToHashSet();
            var inSteam = (shortcuts ?? new()).Select(s => s.AppId).ToHashSet();
            var played = ClawLibrary.Data.FolderGames.ReadPlayed();
            foreach (var lg in local)
            {
                if (!seen.Add(lg.Id) || string.IsNullOrWhiteSpace(lg.Name)) continue;
                bool synced = inSteam.Contains(lg.Id);
                stats.TryGetValue(lg.Id, out var st);
                var g = new LibraryItem
                {
                    Id = lg.Id, Kind = ItemKind.Game, Title = lg.Name, Main = lg.Name, Subtitle = "本地",
                    Platform = "local", Badge = "PC", SeedFx = 0.5, ExePath = synced ? null : lg.Exe, LocalExePath = lg.Exe,
                    ExeStartDir = lg.StartDir, ExeArgs = lg.Args,
                    LastPlayed = synced ? st?.Last : played.TryGetValue(lg.Id, out var t) ? t : null,
                    PlaytimeH = synced && st?.Minutes is int m ? Math.Round(m / 60.0) : null,
                };
                g.Hue = HueOf(g.Title);
                g.OwnArt = art(lg.Id);
                games.Add(g);
            }
        }
        // 种子清单以外、后来加进 Steam 的非 Steam 快捷方式：名字用快捷方式自己的名字。
        // 同一个程序加了两次快捷方式的（比如鸣潮有两条）只收一条，已经在清单里的那条优先。
        if (shortcuts != null)
        {
            var seen = games.Select(g => g.Id).ToHashSet();
            static string ExeKey(string exe) => exe.Trim().Trim('"').Replace('/', '\\').ToLowerInvariant();
            var taken = shortcuts.Where(s => seen.Contains(s.AppId) && s.Exe.Length > 0).Select(s => ExeKey(s.Exe)).ToHashSet();
            foreach (var (id, name, exe, _) in shortcuts)
            {
                if (seen.Contains(id) || string.IsNullOrWhiteSpace(name)) continue;
                if (exe.Length > 0 && !taken.Add(ExeKey(exe))) continue;
                stats.TryGetValue(id, out var st);
                var g = new LibraryItem
                {
                    Id = id, Kind = ItemKind.Game, Title = name.Trim(), Main = name.Trim(), Subtitle = "非 Steam",
                    Platform = "other", Badge = "PC", SeedFx = 0.5,
                    LastPlayed = st?.Last,
                    PlaytimeH = st?.Minutes is int m ? Math.Round(m / 60.0) : null,
                };
                g.Hue = HueOf(g.Title);
                g.OwnArt = art(id);
                games.Add(g);
                seen.Add(id);
            }
        }
        if (steamOwnedOnly)
        {
            var cloudIds = (owned ?? new()).Select(g => g.Id).ToHashSet();
            games.RemoveAll(g => g.Platform == "steam" && !cloudIds.Contains(g.Id));
        }
        if (installed != null)
            foreach (var g in games)
                if (g.Platform == "steam")
                {
                    g.Installed = installed.Contains(g.Id);
                    if (!g.Installed) g.Badge = "STEAM · 未安装";
                }
        foreach (var (title, exe, last) in FolderGames)
        {
            var g = new LibraryItem
            {
                Id = ClawLibrary.Data.FolderGames.IdOf(exe), Kind = ItemKind.Game, Title = title, Main = title,
                Subtitle = Path.GetDirectoryName(Path.GetDirectoryName(exe) ?? "") ?? "", Platform = "folder", Badge = "PC · 文件夹",
                ExePath = exe, LastPlayed = last, SeedFx = 0.5,
            };
            g.Hue = HueOf(g.Title);
            g.OwnArt = art(g.Id);
            games.Add(g);
        }
        foreach (var x in XboxLibrary)
        {
            string id = "xbox-" + x.Aumid;
            games.Add(new LibraryItem
            {
                Id = id, Kind = ItemKind.Game, Title = x.Name, Main = x.Name,
                Platform = "xbox", Badge = "XBOX", Subtitle = "Xbox / Microsoft Store",
                XboxAumid = x.Aumid, Hue = HueOf(x.Name), SeedFx = 0.5,
            });
        }
        return Build(games, steamCollections);
    }

    /// <summary>
    /// 首页三个页签：全部、Steam、本地（非 Steam）。合集只用 Steam 库里用户自己建的分类（收藏夹、自建分类），
    /// 只在 Steam 页签里出现；不自己编分类。Steam 里「已隐藏」的游戏不上墙。空的分类不显示。
    /// </summary>
    /// <summary>设置页「游戏库」：隐藏游戏、Steam 来源及安装范围；安装范围支持即时重新筛选。</summary>
    public static bool ShowHidden, IncludeSteam = true, SteamInstalledOnly;
    /// <summary>设置页手动加的文件夹里扫出来的游戏（载入前填好）。</summary>
    public static List<(string Title, string Exe, long? Last)> FolderGames = new();
    internal static List<XboxGames.Game> XboxLibrary = new();

    public static Library Build(List<LibraryItem> games, List<SteamCollection> steamCollections)
    {
        GameNames.Apply(games);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var g in games)
            g.LastPlayedDays = g.LastPlayed is long l ? (int)Math.Max(0, (now - l) / Day) : null;
        var hidden = ShowHidden ? new HashSet<string>() : steamCollections.FirstOrDefault(c => c.Id == "hidden")?.Added.ToHashSet() ?? new HashSet<string>();
        var shown = games.Where(g => !hidden.Contains(g.Id)
            && (g.Platform != "steam" || (IncludeSteam && (!SteamInstalledOnly || g.Installed)))).ToList();
        var lib = new Library { Games = shown, _sourceGames = games, _sourceCollections = steamCollections };

        var recent = shown.Where(g => g.LastPlayed != null).OrderByDescending(g => g.LastPlayed).ToList();
        var rest = shown.Where(g => g.LastPlayed == null).ToList();
        var ordered = recent.Concat(rest).ToList();
        var steamGames = ordered.Where(g => g.Platform == "steam").ToList();

        // 合集封面借一张成员的图，避开 Steam 页签最前面那几张，免得同一张图在眼前出现两次
        var front = steamGames.Take(6).Select(g => g.Id).ToHashSet();
        var rank = ordered.Select((g, i) => (g.Id, i)).ToDictionary(e => e.Id, e => e.i);
        foreach (var sc in steamCollections)
        {
            if (sc.Id == "hidden") continue;
            var members = sc.Added.Distinct().Where(rank.ContainsKey).OrderBy(id => rank[id]).Select(id => ordered[rank[id]]).ToList();
            if (members.Count == 0) continue;
            lib.Collections.Add(new LibraryItem
            {
                Id = "sc-" + sc.Id, Kind = ItemKind.Collection, Title = sc.Name, Main = sc.Name,
                Subtitle = $"{members.Count} 款游戏", Badge = $"合集 · {members.Count}", Hue = HueOf(sc.Name),
                Children = members.Select(c => c.Id).ToList(),
                ArtFrom = members.FirstOrDefault(c => !front.Contains(c.Id)) ?? members[0],
            });
        }
        foreach (var it in games) lib.ById[it.Id] = it;
        foreach (var it in lib.Collections) lib.ById[it.Id] = it;

        lib.HomeTabs = new List<HomeTab>
        {
            new("all", "全部", () => ordered),
            new("steam", "Steam", () => lib.Collections.Concat(steamGames).ToList()),   // Steam 的分类排最前，就在竖条旁边
            new("xbox", "Xbox", () => ordered.Where(g => g.Platform == "xbox").ToList()),
            new("local", "本地", () => ordered.Where(g => g.Platform != "steam" && g.Platform != "xbox").ToList()),
        };
        Log.Info($"library: {shown.Count} shown ({games.Count - shown.Count} filtered, Steam installed only={SteamInstalledOnly}), steam collections: {string.Join(", ", lib.Collections.Select(c => $"{c.Title}({c.Children!.Count})"))}");
        return lib;
    }
}
