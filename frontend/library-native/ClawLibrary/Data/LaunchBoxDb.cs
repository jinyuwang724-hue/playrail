// LaunchBox Games DB（刮削游戏的一个来源）：不用账号。官方把整库打成一个 Metadata.zip（约 110 MB，每天更新），
// 第一次用时下载下来，只挑 Windows 平台的游戏（约 4 万款）建一份小索引：名字 / 别名、简介、开发商、发行商、发行日期、类型，
// 以及封面（Box - Front）、背景（Fanart - Background）、标志（Clear Logo）、横幅（Banner / Steam Banner）的图片文件名，
// 图片地址是 https://images.launchbox-app.com/<文件名>。索引放在缓存目录，超过 30 天自动重建。
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace ClawLibrary.Data;

public sealed class LbGame
{
    public string Id = "", Name = "";
    public string? Overview, Developer, Publisher, Date, Genres, SteamAppId;
    public List<string> Alt = new();
    /// <summary>图种（p / hero / logo / header）→ 图片完整地址。</summary>
    public Dictionary<string, string> Images = new();
}

public static class LaunchBoxDb
{
    private const string ZipUrl = "https://gamesdb.launchbox-app.com/Metadata.zip";
    private const string ImageBase = "https://images.launchbox-app.com/";
    private static string Dir => Path.Combine(App.CacheDir, "launchbox");
    private static string IndexPath => Path.Combine(Dir, "windows.json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<string, List<LbGame>>? _byName;
    private static Dictionary<string, LbGame>? _bySteam;

    public static bool Indexed => File.Exists(IndexPath);

    /// <summary>名字归一：小写，去掉标点、空白和 ™®，罗马数字不动。</summary>
    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>准备好索引（没有或过期就下载重建）。status 报进度（「正在下载 45%」）。失败返回 false。</summary>
    public static async Task<bool> EnsureAsync(Action<string> status)
    {
        await Gate.WaitAsync();
        try
        {
            if (_byName != null) return true;
            bool stale = !File.Exists(IndexPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(IndexPath) > TimeSpan.FromDays(30);
            if (stale)
            {
                try { await Task.Run(() => BuildAsync(status)); }
                catch (Exception ex) when (File.Exists(IndexPath)) { Log.Warn($"launchbox rebuild: {ex.Message}"); }   // 旧索引还能用
            }
            await Task.Run(Load);
            return _byName != null;
        }
        catch (Exception ex) { Log.Warn($"launchbox: {ex.Message}"); return false; }
        finally { Gate.Release(); }
    }

    /// <summary>按 Steam AppID（库里少数条目有）或名字 / 别名找；几款同名时取资料最全的。</summary>
    public static LbGame? Find(IEnumerable<string> names, string? steamAppId)
    {
        if (_byName == null) return null;
        if (steamAppId != null && _bySteam!.TryGetValue(steamAppId, out var bySteam)) return bySteam;
        foreach (var n in names)
            if (Norm(n) is { Length: > 0 } k && _byName.TryGetValue(k, out var list))
                return list.OrderByDescending(g => g.Images.Count + (g.Overview != null ? 2 : 0)).First();
        return null;
    }

    private static async Task BuildAsync(Action<string> status)
    {
        Directory.CreateDirectory(Dir);
        string zip = Path.Combine(Dir, "Metadata.zip");
        status("正在下载 LaunchBox 资料库（约 110 MB）");
        using (var resp = await Http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zip + ".part");
            var buf = new byte[1 << 16];
            long got = 0; int last = -1, n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                got += n;
                int pct = total > 0 ? (int)(got * 100 / total) : -1;
                if (pct != last && pct % 5 == 0) { last = pct; status($"正在下载 LaunchBox 资料库 {pct}%"); }
            }
        }
        File.Move(zip + ".part", zip, overwrite: true);
        status("正在整理 LaunchBox 资料库");
        var games = new Dictionary<string, LbGame>();
        var alt = new List<(string Id, string Name)>();
        // 图种：同一种取第一张（北美 / 世界 / 无地区优先，后面再看到这些地区的会替换别的地区）
        var imgRank = new Dictionary<(string, string), int>();
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.GetEntry("Metadata.xml") ?? throw new InvalidDataException("Metadata.xml missing");
            using var xs = entry.Open();
            using var xr = XmlReader.Create(xs, new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Ignore });
            xr.MoveToContent();
            xr.Read();
            while (!xr.EOF)
            {
                if (xr.NodeType != XmlNodeType.Element || xr.Depth != 1) { xr.Read(); continue; }
                string kind = xr.Name;
                if (kind is not ("Game" or "GameImage" or "GameAlternateName")) { xr.Skip(); continue; }
                var f = ReadFields(xr);
                xr.Read();   // 越过 </Game>

                if (kind == "Game")
                {
                    string plat = f.GetValueOrDefault("Platform", "");
                    if (plat is not ("Windows" or "Windows 3.X")) continue;
                    string id = f.GetValueOrDefault("DatabaseID", "");
                    if (id.Length == 0) continue;
                    string? date = f.GetValueOrDefault("ReleaseDate") is { Length: >= 10 } d ? d[..10] : f.GetValueOrDefault("ReleaseYear");
                    games[id] = new LbGame
                    {
                        Id = id, Name = f.GetValueOrDefault("Name", ""), Overview = Nz(f.GetValueOrDefault("Overview")),
                        Developer = Nz(f.GetValueOrDefault("Developer")), Publisher = Nz(f.GetValueOrDefault("Publisher")),
                        Date = Nz(date), Genres = Nz(f.GetValueOrDefault("Genres")?.Replace("; ", "、")), SteamAppId = Nz(f.GetValueOrDefault("SteamAppId")),
                    };
                }
                else if (kind == "GameAlternateName")
                {
                    if (f.GetValueOrDefault("DatabaseID") is { } id && f.GetValueOrDefault("AlternateName") is { Length: > 0 } an) alt.Add((id, an));
                }
                else if (f.GetValueOrDefault("DatabaseID") is { } id && games.TryGetValue(id, out var g) && f.GetValueOrDefault("FileName") is { Length: > 0 } file)
                {
                    string? k = f.GetValueOrDefault("Type") switch
                    {
                        "Box - Front" => "p", "Fanart - Background" => "hero", "Clear Logo" => "logo", "Steam Banner" or "Banner" => "header", _ => null,
                    };
                    if (k == null) continue;
                    int rank = f.GetValueOrDefault("Region") switch { null or "" => 0, "World" => 0, "North America" => 1, "United States" => 1, _ => 2 };
                    if (!imgRank.TryGetValue((id, k), out var old) || rank < old) { imgRank[(id, k)] = rank; g.Images[k] = ImageBase + file; }
                }
            }
        }
        foreach (var (id, name) in alt) if (games.TryGetValue(id, out var g)) g.Alt.Add(name);
        await using (var fs = File.Create(IndexPath + ".part"))
        using (var w = new Utf8JsonWriter(fs))
        {
            w.WriteStartArray();
            foreach (var g in games.Values)
            {
                w.WriteStartObject();
                w.WriteString("id", g.Id); w.WriteString("n", g.Name);
                if (g.Overview != null) w.WriteString("o", g.Overview);
                if (g.Developer != null) w.WriteString("d", g.Developer);
                if (g.Publisher != null) w.WriteString("p", g.Publisher);
                if (g.Date != null) w.WriteString("r", g.Date);
                if (g.Genres != null) w.WriteString("g", g.Genres);
                if (g.SteamAppId != null) w.WriteString("s", g.SteamAppId);
                if (g.Alt.Count > 0) { w.WriteStartArray("a"); foreach (var a in g.Alt) w.WriteStringValue(a); w.WriteEndArray(); }
                if (g.Images.Count > 0) { w.WriteStartObject("i"); foreach (var (k, v) in g.Images) w.WriteString(k, v); w.WriteEndObject(); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        File.Move(IndexPath + ".part", IndexPath, overwrite: true);
        try { File.Delete(zip); } catch { }
        Log.Info($"launchbox index: {games.Count} Windows games");
    }

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>读一个 &lt;Game&gt; 之类元素的直接子元素（都是纯文本）。</summary>
    private static Dictionary<string, string> ReadFields(XmlReader xr)
    {
        var f = new Dictionary<string, string>();
        if (xr.IsEmptyElement) return f;
        int depth = xr.Depth;
        xr.Read();
        while (!xr.EOF && !(xr.NodeType == XmlNodeType.EndElement && xr.Depth == depth))
        {
            if (xr.NodeType != XmlNodeType.Element) { xr.Read(); continue; }
            string name = xr.Name;
            if (xr.IsEmptyElement) { f[name] = ""; xr.Read(); }
            else f[name] = xr.ReadElementContentAsString();   // 读完停在下一个兄弟节点上
        }
        return f;
    }

    private static void Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(IndexPath));
        var byName = new Dictionary<string, List<LbGame>>();
        var bySteam = new Dictionary<string, LbGame>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string? S(string k) => e.TryGetProperty(k, out var v) ? v.GetString() : null;
            var g = new LbGame
            {
                Id = S("id") ?? "", Name = S("n") ?? "", Overview = S("o"), Developer = S("d"), Publisher = S("p"), Date = S("r"), Genres = S("g"), SteamAppId = S("s"),
            };
            if (e.TryGetProperty("a", out var a)) foreach (var x in a.EnumerateArray()) g.Alt.Add(x.GetString() ?? "");
            if (e.TryGetProperty("i", out var im)) foreach (var p in im.EnumerateObject()) g.Images[p.Name] = p.Value.GetString() ?? "";
            foreach (var n in g.Alt.Prepend(g.Name))
                if (Norm(n) is { Length: > 0 } k) { if (!byName.TryGetValue(k, out var l)) byName[k] = l = new(); l.Add(g); }
            if (g.SteamAppId != null) bySteam[g.SteamAppId] = g;
        }
        _byName = byName;
        _bySteam = bySteam;
    }
}
