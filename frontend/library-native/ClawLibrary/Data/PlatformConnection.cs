using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace ClawLibrary.Data;

public sealed record PlatformAccount(string Id, string Name);

/// <summary>只保存完成提供方验证的绑定标识和显示名；不保存浏览器登录信息。</summary>
public sealed class PlatformConnectionStore(string directory)
{
    private string FilePath => Path.Combine(directory, "platform-accounts.json");
    public PlatformAccount? Steam { get; private set; }

    public void Load()
    {
        Steam = null;
        try
        {
            if (!File.Exists(FilePath)) return;
            var data = JsonNode.Parse(File.ReadAllText(FilePath));
            string? id = data?["steam"]?["id"]?.GetValue<string>();
            string? name = data?["steam"]?["name"]?.GetValue<string>();
            if (SteamOpenId.IsSteamId(id) && !string.IsNullOrWhiteSpace(name)) Steam = new(id!, name);
        }
        catch (Exception ex) { Log.Warn($"account record load: {ex.GetType().Name}"); }
    }

    public void SetSteam(PlatformAccount? account)
    {
        if (account != null && (!SteamOpenId.IsSteamId(account.Id) || string.IsNullOrWhiteSpace(account.Name)))
            throw new ArgumentException("Invalid verified Steam identity");
        var data = new JsonObject();
        if (account != null) data["steam"] = new JsonObject { ["id"] = account.Id, ["name"] = account.Name };
        Directory.CreateDirectory(directory);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, data.ToJsonString());
        File.Move(temp, FilePath, true);
        Steam = account;
    }
}

/// <summary>系统浏览器 + 短期本机回调，无嵌入浏览器、常驻 HTTP 服务或 URL ACL。</summary>
internal sealed class AccountLoopback : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    public string State { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public Uri ReturnTo { get; }

    public AccountLoopback()
    {
        _listener.Start(4);
        ReturnTo = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/steam/callback?state={State}");
    }

    internal static Dictionary<string, string> Query(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            string value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!result.TryAdd(key, value)) throw new InvalidDataException("Duplicate callback parameter");
        }
        return result;
    }

    public async Task<PlatformAccount> ReceiveAsync(Func<Dictionary<string, string>, Task<PlatformAccount>> verify, CancellationToken token)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(token);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stream = client.GetStream();
            var bytes = new byte[16384];
            int total = 0;
            Dictionary<string, string> query;
            try
            {
                while (total < bytes.Length)
                {
                    int n = await stream.ReadAsync(bytes.AsMemory(total), requestTimeout.Token);
                    if (n == 0) break;
                    total += n;
                    if (Encoding.ASCII.GetString(bytes, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var request = Encoding.ASCII.GetString(bytes, 0, total).Split("\r\n", 2)[0].Split(' ');
                if (request.Length != 3 || request[0] != "GET" || !request[1].StartsWith('/'))
                { await Respond(stream, false, "无法处理此请求", token); continue; }
                var uri = new Uri(ReturnTo, request[1]);
                query = Query(uri.Query);
                if (uri.Host != ReturnTo.Host || uri.Port != ReturnTo.Port || uri.AbsolutePath != ReturnTo.AbsolutePath ||
                    !query.TryGetValue("state", out var state) || state != State)
                { await Respond(stream, false, "连接请求已过期，请回到游戏库重新连接", token); continue; }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
            catch (InvalidDataException) { await Respond(stream, false, "无效的连接请求", token); continue; }
            catch (UriFormatException) { await Respond(stream, false, "无效的连接请求", token); continue; }
            catch (IOException) { continue; }
            // 校验失败必须结束本次连接；不能当成无关浏览器请求继续等待五分钟。
            try
            {
                var account = await verify(query);
                token.ThrowIfCancellationRequested();
                try { await Respond(stream, true, "连接成功，可以返回 Claw 游戏库", token); } catch (IOException) { }
                return account;
            }
            catch
            {
                try { await Respond(stream, false, "连接未完成，请返回游戏库重试", token); } catch (IOException) { }
                throw;
            }
        }
    }

    private static async Task Respond(NetworkStream stream, bool success, string message, CancellationToken token)
    {
        string link = success ? "<p><a href=\"claw-library://restore-library\">返回游戏库</a></p>" : "";
        string html = "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\"><title>Claw 游戏库</title><body style=\"font:18px system-ui;background:#111;color:#eee;padding:8vw\"><h1>" + message + "</h1>" + link + "</body></html>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
    }

    public void Dispose() => _listener.Stop();
}

internal static class SteamOpenId
{
    private const string Endpoint = "https://steamcommunity.com/openid/login";
    private const string Namespace = "http://specs.openid.net/auth/2.0";
    private const string IdentityPrefix = "https://steamcommunity.com/openid/id/";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    public static bool IsSteamId(string? id) => id is { Length: 17 } && id.All(char.IsAsciiDigit)
        && ulong.TryParse(id, out var value) && value > 76561197960265728UL;

    public static Uri AuthorizationUri(Uri returnTo)
    {
        var values = new Dictionary<string, string>
        {
            ["openid.ns"] = Namespace, ["openid.mode"] = "checkid_setup",
            ["openid.return_to"] = returnTo.AbsoluteUri, ["openid.realm"] = returnTo.GetLeftPart(UriPartial.Authority) + "/",
            ["openid.identity"] = "http://specs.openid.net/auth/2.0/identifier_select",
            ["openid.claimed_id"] = "http://specs.openid.net/auth/2.0/identifier_select",
        };
        return new Uri(Endpoint + "?" + string.Join('&', values.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value))));
    }

    internal static string ValidateAssertion(Dictionary<string, string> query, Uri returnTo, DateTimeOffset now)
    {
        string Get(string key) => query.GetValueOrDefault("openid." + key, "");
        if (Get("mode") == "cancel") throw new OperationCanceledException("Steam 连接已取消");
        string claim = Get("claimed_id");
        if (Get("ns") != Namespace || Get("mode") != "id_res" || Get("op_endpoint") != Endpoint ||
            Get("return_to") != returnTo.AbsoluteUri || Get("identity") != claim || !claim.StartsWith(IdentityPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("Steam 返回的账户信息未通过检查");
        string id = claim[IdentityPrefix.Length..];
        if (!IsSteamId(id)) throw new InvalidDataException("无效的 Steam 账户标识");
        var signed = Get("signed").Split(',').ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(Get("sig")) || string.IsNullOrEmpty(Get("assoc_handle")) ||
            !new[] { "op_endpoint", "claimed_id", "identity", "return_to", "response_nonce", "assoc_handle" }.All(signed.Contains))
            throw new InvalidDataException("Steam 返回的签名信息不完整");
        string nonce = Get("response_nonce");
        if (nonce.Length < 21 || !DateTimeOffset.TryParseExact(nonce[..20], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time) || time < now.AddMinutes(-5) || time > now.AddSeconds(30))
            throw new InvalidDataException("Steam 连接请求已过期");
        return id;
    }

    public static async Task<PlatformAccount> ConnectAsync(Action<Uri> openBrowser, CancellationToken token, HttpClient? http = null)
    {
        using var callback = new AccountLoopback();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var client = http ?? Http;
        var pending = callback.ReceiveAsync(async query =>
        {
            string id = ValidateAssertion(query, callback.ReturnTo, DateTimeOffset.UtcNow);
            var values = query.Where(kv => kv.Key.StartsWith("openid.", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
            values["openid.mode"] = "check_authentication";
            using var response = await client.PostAsync(Endpoint, new FormUrlEncodedContent(values), timeout.Token);
            string verification = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode || !verification.Split('\n').Any(line => line.TrimEnd('\r') == "is_valid:true"))
                throw new InvalidDataException("Steam 未确认此次连接，请重试");
            string name = Steam.AccountName(id) ?? "Steam " + id;
            // 本机没记录该账户时才读 Steam 的公开资料；身份始终以 OpenID 签名确认的 ID 为准。
            if (name.StartsWith("Steam ", StringComparison.Ordinal))
            {
                try
                {
                    string xml = await client.GetStringAsync($"https://steamcommunity.com/profiles/{id}/?xml=1", timeout.Token);
                    var profile = XDocument.Parse(xml).Root;
                    if (profile?.Element("steamID64")?.Value == id && profile.Element("steamID")?.Value is { Length: > 0 } nickname)
                        name = nickname.Length <= 128 ? nickname : nickname[..128];
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("Steam profile name unavailable"); }
            }
            return new PlatformAccount(id, name);
        }, timeout.Token);
        try { openBrowser(AuthorizationUri(callback.ReturnTo)); return await pending; }
        catch
        {
            await timeout.CancelAsync();
            try { await pending; } catch { }
            throw;
        }
        finally { await timeout.CancelAsync(); }
    }
}
