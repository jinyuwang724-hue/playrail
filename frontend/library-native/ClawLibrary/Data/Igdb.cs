// IGDB（刮削游戏的一个来源）：资料和图都很全，但要用户自己在 dev.twitch.tv 建一个应用，拿 Client ID / Secret。
// 先用它们换一个应用令牌（client_credentials，有效期约两个月，缓存在内存里），再查 /v4/games。
// 有 Steam AppID 时按 external_games（Steam = 1）精确找，否则按名字搜 PC（平台 6）上的游戏。
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClawLibrary.Data;

public sealed class IgdbGame
{
    public string Name = "";
    public string? Summary, Developer, Publisher, Date, Genres;
    public Dictionary<string, string> Images = new();
}

public static class Igdb
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static (string Id, string Token, DateTime Until)? _token;

    private static async Task<string> TokenAsync(string id, string secret)
    {
        if (_token is { } t && t.Id == id && t.Until > DateTime.UtcNow.AddMinutes(5)) return t.Token;
        var url = $"https://id.twitch.tv/oauth2/token?client_id={Uri.EscapeDataString(id)}&client_secret={Uri.EscapeDataString(secret)}&grant_type=client_credentials";
        using var resp = await Http.PostAsync(url, null);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(resp.StatusCode == System.Net.HttpStatusCode.BadRequest || resp.StatusCode == System.Net.HttpStatusCode.Forbidden ? "IGDB 密钥不对" : $"IGDB 登录失败 {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(body);
        string token = doc.RootElement.GetProperty("access_token").GetString()!;
        int ttl = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _token = (id, token, DateTime.UtcNow.AddSeconds(ttl));
        return token;
    }

    private const string Fields = "fields name,summary,first_release_date,genres.name,involved_companies.company.name,involved_companies.developer,involved_companies.publisher,cover.image_id,artworks.image_id,screenshots.image_id;";

    /// <summary>找一款：先按 Steam AppID，再按名字（只看 PC）。没找到返回 null；密钥不对抛异常（带中文说明）。</summary>
    public static async Task<IgdbGame?> FindAsync(string id, string secret, IEnumerable<string> names, string? steamAppId)
    {
        string token = await TokenAsync(id, secret);
        async Task<JsonElement[]> Query(string q)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games") { Content = new StringContent(q, Encoding.UTF8, "text/plain") };
            req.Headers.Add("Client-ID", id);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"IGDB 查询失败 {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        }
        JsonElement? hit = null;
        if (steamAppId != null)
            hit = (await Query($"{Fields} where external_games.uid = \"{steamAppId}\" & external_games.category = 1; limit 1;")).FirstOrDefault() is { ValueKind: JsonValueKind.Object } a ? a : null;
        foreach (var n in names)
        {
            if (hit != null || string.IsNullOrWhiteSpace(n)) break;
            string q = n.Replace("\"", " ");
            var list = await Query($"search \"{q}\"; {Fields} where platforms = (6); limit 5;");
            string want = LaunchBoxDb.Norm(n);
            hit = list.FirstOrDefault(x => x.TryGetProperty("name", out var nm) && LaunchBoxDb.Norm(nm.GetString() ?? "") == want) is { ValueKind: JsonValueKind.Object } ex ? ex
                : list.Length > 0 ? list[0] : null;
        }
        return hit is { } h ? Parse(h) : null;
    }

    private static IgdbGame Parse(JsonElement g)
    {
        string? S(string k) => g.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var r = new IgdbGame { Name = S("name") ?? "", Summary = S("summary") };
        if (g.TryGetProperty("first_release_date", out var d) && d.TryGetInt64(out var unix))
            r.Date = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd");
        if (g.TryGetProperty("genres", out var gs))
            r.Genres = string.Join("、", gs.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).Where(x => x != null).Take(3));
        if (g.TryGetProperty("involved_companies", out var ic))
        {
            string? Pick(string role) => ic.EnumerateArray().Where(x => x.TryGetProperty(role, out var b) && b.ValueKind == JsonValueKind.True)
                .Select(x => x.TryGetProperty("company", out var c) && c.TryGetProperty("name", out var n) ? n.GetString() : null).FirstOrDefault(x => x != null);
            r.Developer = Pick("developer");
            r.Publisher = Pick("publisher");
        }
        string Img(string size, string imageId) => $"https://images.igdb.com/igdb/image/upload/{size}/{imageId}.jpg";
        if (g.TryGetProperty("cover", out var cv) && cv.TryGetProperty("image_id", out var ci)) r.Images["p"] = Img("t_cover_big_2x", ci.GetString()!);
        var art = g.TryGetProperty("artworks", out var aw) ? aw.EnumerateArray().FirstOrDefault() : default;
        if (art.ValueKind != JsonValueKind.Object && g.TryGetProperty("screenshots", out var ss)) art = ss.EnumerateArray().FirstOrDefault();
        if (art.ValueKind == JsonValueKind.Object && art.TryGetProperty("image_id", out var ai)) r.Images["hero"] = Img("t_1080p", ai.GetString()!);
        return r;
    }
}
