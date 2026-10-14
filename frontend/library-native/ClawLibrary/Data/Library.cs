// 游戏库数据：种子清单（Assets\library.json，与网页预览同一份）+ 本机 Steam 的上次游玩 / 时长 + 封面套件。
// 条目、合集、首页页签的规则与 src/data/real.ts 相同。
using System.Text.Json;

namespace ClawLibrary.Data;

public enum ItemKind { Game, Collection }

/// <summary>封面套件：Dir 下的 p/h/s/x(/v/n).jpg；Fx 是背景图的水平焦点，Ha 是背景图宽高比，C 是主色。</summary>
public sealed class ArtKit
{
    public required string Dir;
    public bool P, V, N, H, S, X;
    public double? Fx, Ha;
    public string? C;
    public bool Has(char kind) => kind switch { 'p' => P, 'v' => V, 'n' => N, 'h' => H, 's' => S, 'x' => X, _ => false };
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

    public static Library Load(string seedPath, Dictionary<string, PlayStats> stats, Func<string, ArtKit?> art)
    {
        var games = new List<LibraryItem>();
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
        return Build(games);
    }

    public static Library Build(List<LibraryItem> games)
    {
        var lib = new Library { Games = games };
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var g in games)
            g.LastPlayedDays = g.LastPlayed is long l ? (int)Math.Max(0, (now - l) / Day) : null;
        var byGame = games.ToDictionary(g => g.Id);

        var recent = games.Where(g => g.LastPlayed != null).OrderByDescending(g => g.LastPlayed).ToList();
        var rest = games.Where(g => g.LastPlayed == null).ToList();
        var ordered = recent.Concat(rest).ToList();

        // 合集封面借一张成员的图，避开首页最前面那几张，免得同一张图在眼前出现两次
        var front = ordered.Take(6).Select(g => g.Id).ToHashSet();
        LibraryItem Collection(string id, string title, string kicker, List<LibraryItem> children) => new()
        {
            Id = id, Kind = ItemKind.Collection, Title = title, Main = title, Kicker = kicker,
            Subtitle = $"{children.Count} 款游戏", Badge = $"合集 · {children.Count}", Hue = HueOf(title),
            Children = children.Select(c => c.Id).ToList(),
            ArtFrom = children.FirstOrDefault(c => !front.Contains(c.Id)) ?? children.FirstOrDefault(),
        };
        var series = new List<(string name, List<LibraryItem> list)>();
        foreach (var g in games)
        {
            if (g.Series == null) continue;
            var entry = series.FindIndex(s => s.name == g.Series);
            if (entry < 0) series.Add((g.Series, new List<LibraryItem> { g }));
            else series[entry].list.Add(g);
        }
        lib.Collections = new List<LibraryItem> { Collection("c-gal", "Galgame", "VISUAL NOVELS", ordered.Where(g => g.Genre == "Galgame").ToList()) };
        lib.Collections.AddRange(series.Where(s => s.list.Count >= 3).Select(s => Collection($"c-{s.name}", s.name, "SERIES", s.list)));
        foreach (var it in games) lib.ById[it.Id] = it;
        foreach (var it in lib.Collections) lib.ById[it.Id] = it;

        lib.HomeTabs = new List<HomeTab>
        {
            new("all", "全部", () => ordered.Take(6).Concat(lib.Collections).Concat(ordered.Skip(6)).ToList()),
            new("recent", "最近", () => recent),
            new("steam", "Steam", () => ordered.Where(g => g.Platform == "steam").ToList()),
            new("other", "非 Steam", () => ordered.Where(g => g.Platform == "other").ToList()),
            new("gal", "Galgame", () => ordered.Where(g => g.Genre == "Galgame").ToList()),
        };
        return lib;
    }
}
