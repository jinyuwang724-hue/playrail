using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClawLibrary.Data;

public sealed record SteamCloudGame(string Id, string Name, int Minutes, long? LastPlayed);
public sealed record SteamCloudSnapshot(string SteamId, DateTimeOffset Updated, List<SteamCloudGame> Games)
{
    public List<SteamApp> Apps => Games.Select(g => new SteamApp(g.Id, g.Name, "game", null, null)).ToList();
    public Dictionary<string, PlayStats> Stats => Games.ToDictionary(g => g.Id, g => new PlayStats(g.LastPlayed, g.Minutes));
}

/// <summary>官方用户接口。凭据只放请求头，不进入 URL、日志或缓存。</summary>
public static class SteamCloudClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35) };
    public static bool ValidKey(string key) => key.Length == 32 && key.All(Uri.IsHexDigit);

    public static async Task<SteamCloudSnapshot> FetchAsync(string steamId, string key, CancellationToken token, HttpClient? client = null)
    {
        if (!SteamOpenId.IsSteamId(steamId) || !ValidKey(key)) throw new InvalidOperationException("请输入有效的 Steam Web API Key（32 位）");
        var input = new JsonObject { ["steamid"] = steamId, ["include_appinfo"] = true, ["include_played_free_games"] = true };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?input_json=" + Uri.EscapeDataString(input.ToJsonString()));
        request.Headers.Add("x-webapi-key", key);
        try
        {
            using var response = await (client ?? Http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Steam 拒绝了此 Key，请检查或重新生成");
            if ((int)response.StatusCode == 429) throw new InvalidOperationException("Steam 请求过于频繁，请稍后更新");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Steam 云端暂时无法读取，请稍后更新");
            if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new InvalidOperationException("Steam 返回的数据过大");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            byte[] block = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(block, token)) != 0)
            {
                if (buffer.Length + read > 16 * 1024 * 1024) throw new InvalidOperationException("Steam 返回的数据过大");
                buffer.Write(block, 0, read);
            }
            return Parse(steamId, Encoding.UTF8.GetString(buffer.ToArray()), DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接 Steam 云端，请检查网络后重试"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidOperationException("Steam 云端读取超时，请重试"); }
        catch (JsonException) { throw new InvalidOperationException("Steam 返回的数据无法读取，请稍后重试"); }
    }

    public static SteamCloudSnapshot Parse(string steamId, string json, DateTimeOffset updated)
    {
        if (!SteamOpenId.IsSteamId(steamId)) throw new InvalidDataException("Invalid Steam identity");
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("game_count", out var countNode) || countNode.ValueKind != JsonValueKind.Number || !countNode.TryGetInt32(out int count))
            throw new InvalidOperationException("无法读取此账户的游戏详情，请检查 Steam 隐私设置和 Key 所属账户");
        if (count < 0 || count > 100000) throw new InvalidDataException("Invalid Steam game count");
        if (count == 0)
        {
            if (response.TryGetProperty("games", out var empty) && (empty.ValueKind != JsonValueKind.Array || empty.GetArrayLength() != 0))
                throw new InvalidDataException("Invalid empty Steam library");
            return new(steamId, updated, new());
        }
        if (!response.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array || games.GetArrayLength() != count)
            throw new InvalidOperationException("Steam 游戏列表不完整，已保留上次的游戏库");
        var result = new List<SteamCloudGame>();
        var seen = new HashSet<uint>();
        foreach (var game in games.EnumerateArray())
        {
            if (game.ValueKind != JsonValueKind.Object || !game.TryGetProperty("appid", out var idNode) || idNode.ValueKind != JsonValueKind.Number || !idNode.TryGetUInt32(out uint id) || id == 0 || !seen.Add(id)
                || !game.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameNode.GetString()))
                throw new InvalidOperationException("Steam 游戏资料不完整，已保留上次的游戏库");
            int minutes = game.TryGetProperty("playtime_forever", out var time) && time.ValueKind == JsonValueKind.Number && time.TryGetInt32(out int m) ? Math.Max(0, m) : 0;
            long? last = game.TryGetProperty("rtime_last_played", out var played) && played.ValueKind == JsonValueKind.Number && played.TryGetInt64(out long t) && t > 0 ? t : null;
            result.Add(new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), nameNode.GetString()!, minutes, last));
        }
        return new(steamId, updated, result);
    }
}

/// <summary>按验证后的账户隔离缓存；Key 用当前 Windows 用户的 DPAPI 加密。</summary>
public sealed class SteamCloudStore(string directory)
{
    private string PathFor(string id, string extension)
    {
        if (!SteamOpenId.IsSteamId(id)) throw new ArgumentException("Invalid Steam identity");
        return Path.Combine(directory, "steam-cloud", id + extension);
    }
    public bool HasKey(string id) => File.Exists(PathFor(id, ".key"));
    public void RemoveKey(string id) => File.Delete(PathFor(id, ".key"));
    public string ReadKey(string id)
    {
        byte[] plain = UserSecret.Unprotect(File.ReadAllBytes(PathFor(id, ".key")));
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void SaveKey(string id, string key)
    {
        if (!SteamCloudClient.ValidKey(key)) throw new ArgumentException("Invalid Steam API key");
        byte[] plain = Encoding.UTF8.GetBytes(key);
        try { Write(PathFor(id, ".key"), UserSecret.Protect(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, true);
    }
    public void Save(SteamCloudSnapshot snapshot)
    {
        var games = new JsonArray();
        foreach (var g in snapshot.Games) games.Add((JsonNode)new JsonObject
            { ["appid"] = uint.Parse(g.Id), ["name"] = g.Name, ["playtime_forever"] = g.Minutes, ["rtime_last_played"] = g.LastPlayed });
        var data = new JsonObject { ["steamid"] = snapshot.SteamId, ["updated"] = snapshot.Updated.ToString("O"),
            ["response"] = new JsonObject { ["game_count"] = games.Count, ["games"] = games } };
        Write(PathFor(snapshot.SteamId, ".json"), Encoding.UTF8.GetBytes(data.ToJsonString()));
    }
    public SteamCloudSnapshot? Load(string id)
    {
        try
        {
            string path = PathFor(id, ".json");
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("steamid").GetString() != id) return null;
            var updated = doc.RootElement.GetProperty("updated").GetDateTimeOffset();
            return SteamCloudClient.Parse(id, json, updated);
        }
        catch { return null; }
    }
}

internal static class UserSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] value) => Transform(value, true);
    public static byte[] Unprotect(byte[] value) => Transform(value, false);
    private static byte[] Transform(byte[] value, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var input = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            bool ok = protect ? CryptProtectData(ref input, "Claw Steam cloud", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("无法读取或保存本机加密凭据，请重新配置 Key");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
}
