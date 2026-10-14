// 读本机 Steam：安装目录、当前用户、上次游玩 / 时长（localconfig.vdf、shortcuts.vdf），以及封面原图在哪。
// 读不到就返回空，绝不抛出。
using System.Text;
using Microsoft.Win32;

namespace ClawLibrary.Data;

public sealed record PlayStats(long? Last, int? Minutes);

/// <summary>Steam 库里的一个分类：Id（favorite / hidden / uc-…）、名字、手动加进去的 appid。</summary>
public sealed record SteamCollection(string Id, string Name, List<string> Added);

/// <summary>Steam 库里的一项（appinfo.vdf）：类型 game / application / tool / dlc …</summary>
public sealed record SteamApp(string Id, string Name, string Type, string? Developer, int? Year);

public static class Steam
{
    public static string? Root()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return Path.GetFullPath(path);
        }
        catch (Exception ex) { Log.Warn($"steam path: {ex.Message}"); }
        return null;
    }

    /// <summary>userdata 下最近用过的那个账号目录（看 localconfig.vdf 的修改时间）。</summary>
    public static string? UserDir(string root)
    {
        try
        {
            var ud = Path.Combine(root, "userdata");
            if (!Directory.Exists(ud)) return null;
            return Directory.GetDirectories(ud)
                .Where(d => File.Exists(Path.Combine(d, "config", "localconfig.vdf")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "config", "localconfig.vdf")))
                .FirstOrDefault();
        }
        catch (Exception ex) { Log.Warn($"steam userdata: {ex.Message}"); return null; }
    }

    public static Dictionary<string, PlayStats> ReadPlayStats(string? userDir)
    {
        var result = new Dictionary<string, PlayStats>();
        if (userDir == null) return result;
        try
        {
            var text = File.ReadAllText(Path.Combine(userDir, "config", "localconfig.vdf"), Encoding.UTF8);
            var root = TextVdf.Parse(text);
            var apps = root.Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
            if (apps != null)
                foreach (var (id, node) in apps.Children)
                {
                    long? last = long.TryParse(node.Value("LastPlayed"), out var l) && l > 0 ? l : null;
                    int? mins = int.TryParse(node.Value("Playtime"), out var m) ? m : null;
                    result[id] = new PlayStats(last, mins);
                }
        }
        catch (Exception ex) { Log.Warn($"localconfig.vdf: {ex.Message}"); }
        try
        {
            var path = Path.Combine(userDir, "config", "shortcuts.vdf");
            if (File.Exists(path))
                foreach (var (appid, last) in BinaryShortcuts(File.ReadAllBytes(path)))
                    if (!result.ContainsKey(appid) || result[appid].Last == null)
                        result[appid] = new PlayStats(last > 0 ? last : null, result.TryGetValue(appid, out var p) ? p.Minutes : null);
        }
        catch (Exception ex) { Log.Warn($"shortcuts.vdf: {ex.Message}"); }
        return result;
    }

    /// <summary>
    /// Steam 库里用户自己建的分类（收藏夹、已隐藏、自建分类）：config\cloudstorage\cloud-storage-namespace-1.json 里
    /// user-collections.* 的那些项。删掉的、只有筛选条件（动态分类）的不要。
    /// </summary>
    public static List<SteamCollection> ReadCollections(string? userDir)
    {
        var list = new List<SteamCollection>();
        if (userDir == null) return list;
        try
        {
            var path = Path.Combine(userDir, "config", "cloudstorage", "cloud-storage-namespace-1.json");
            if (!File.Exists(path)) return list;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            foreach (var pair in doc.RootElement.EnumerateArray())
            {
                if (pair.GetArrayLength() < 2 || pair[0].GetString() is not { } key || !key.StartsWith("user-collections.")) continue;
                var meta = pair[1];
                if (meta.TryGetProperty("is_deleted", out var del) && del.ValueKind == System.Text.Json.JsonValueKind.True) continue;
                if (!meta.TryGetProperty("value", out var v) || v.GetString() is not { } json) continue;
                using var c = System.Text.Json.JsonDocument.Parse(json);
                var r = c.RootElement;
                string id = r.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
                string name = r.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                var added = new List<string>();
                if (r.TryGetProperty("added", out var ap) && ap.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var a in ap.EnumerateArray())
                        added.Add(a.ValueKind == System.Text.Json.JsonValueKind.Number ? ((uint)a.GetInt64()).ToString() : a.ToString());
                if (r.TryGetProperty("removed", out var rp) && rp.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var a in rp.EnumerateArray())
                        added.Remove(a.ValueKind == System.Text.Json.JsonValueKind.Number ? ((uint)a.GetInt64()).ToString() : a.ToString());
                if (id.Length == 0 || name.Length == 0) continue;
                list.Add(new SteamCollection(id, name, added));
            }
        }
        catch (Exception ex) { Log.Warn($"steam collections: {ex.Message}"); }
        return list;
    }

    /// <summary>
    /// Steam 库里的应用：appcache\librarycache 下每个 appid 一个目录（Steam 给库里的每一项缓存封面），
    /// 名字、类型、开发商、发行年份从 appcache\appinfo.vdf（二进制 KeyValues，v28 / v29）里取。
    /// </summary>
    public static List<SteamApp> ReadOwnedApps(string root)
    {
        var list = new List<SteamApp>();
        try
        {
            var cache = Path.Combine(root, "appcache", "librarycache");
            if (!Directory.Exists(cache)) return list;
            var ids = Directory.GetDirectories(cache).Select(Path.GetFileName).Where(n => n != null && uint.TryParse(n, out _)).ToHashSet()!;
            foreach (var f in Directory.GetFiles(cache, "*_library_600x900.jpg"))   // 老版本 Steam：平铺的文件
            {
                var n = Path.GetFileName(f).Split('_')[0];
                if (uint.TryParse(n, out _)) ids.Add(n);
            }
            var info = ReadAppInfo(Path.Combine(root, "appcache", "appinfo.vdf"), ids!);
            foreach (var id in ids) if (id != null && info.TryGetValue(id, out var app)) list.Add(app);
        }
        catch (Exception ex) { Log.Warn($"steam owned apps: {ex.Message}"); }
        return list;
    }

    /// <summary>已经装好的 appid：各个库目录（libraryfolders.vdf）里 appmanifest 的 StateFlags 带「完整安装」位。</summary>
    public static HashSet<string> ReadInstalled(string root)
    {
        var set = new HashSet<string>();
        try
        {
            var dirs = new List<string> { Path.Combine(root, "steamapps") };
            var lf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(lf))
            {
                var folders = TextVdf.Parse(File.ReadAllText(lf, Encoding.UTF8)).Path("libraryfolders");
                if (folders != null)
                    foreach (var (_, node) in folders.Children)
                        if (node.Value("path") is { } p) dirs.Add(Path.Combine(p, "steamapps"));
            }
            foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(d)) continue;
                foreach (var acf in Directory.GetFiles(d, "appmanifest_*.acf"))
                {
                    try
                    {
                        var st = TextVdf.Parse(File.ReadAllText(acf, Encoding.UTF8)).Path("AppState");
                        if (st?.Value("appid") is not { } id) continue;
                        if (int.TryParse(st.Value("StateFlags"), out var flags) && (flags & 4) != 0) set.Add(id);
                    }
                    catch (Exception ex) { Log.Warn($"{Path.GetFileName(acf)}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"steam installed: {ex.Message}"); }
        return set;
    }

    /// <summary>appinfo.vdf：头（magic、universe、v29 起还有字符串表偏移），然后每个应用一段二进制 KeyValues。</summary>
    private static Dictionary<string, SteamApp> ReadAppInfo(string path, HashSet<string> want)
    {
        var result = new Dictionary<string, SteamApp>();
        if (!File.Exists(path)) return result;
        var b = File.ReadAllBytes(path);
        int pos = 0;
        uint U32() { var v = BitConverter.ToUInt32(b, pos); pos += 4; return v; }
        uint magic = U32();
        U32();
        List<string>? strings = null;
        if (magic == 0x07564429)
        {
            long off = BitConverter.ToInt64(b, pos); pos += 8;
            strings = new();
            int p = (int)off;
            uint n = BitConverter.ToUInt32(b, p); p += 4;
            for (int i = 0; i < n && p < b.Length; i++)
            {
                int s = p;
                while (p < b.Length && b[p] != 0) p++;
                strings.Add(Encoding.UTF8.GetString(b, s, p - s));
                p++;
            }
        }
        else if (magic != 0x07564428) { Log.Warn($"appinfo.vdf: unknown magic {magic:X8}"); return result; }

        string CStr()
        {
            int s = pos;
            while (pos < b.Length && b[pos] != 0) pos++;
            var v = Encoding.UTF8.GetString(b, s, pos - s);
            pos++;
            return v;
        }
        while (pos + 8 <= b.Length)
        {
            uint id = U32();
            if (id == 0) break;
            int size = (int)U32();
            int end = pos + size;
            if (!want.Contains(id.ToString())) { pos = end; continue; }
            pos += 4 + 4 + 8 + 20 + 4 + 20;   // infoState、lastUpdated、picsToken、sha1、changeNumber、二进制 sha1
            var stack = new List<string>();
            string? name = null, type = null, dev = null, assocType = null, assocName = null;
            uint release = 0;
            while (pos < end)
            {
                byte t = b[pos++];
                if (t == 8)
                {
                    if (stack.Count == 0) break;
                    if (stack.Count == 4 && stack[2] == "associations")
                    {
                        if (assocType == "developer" && dev == null) dev = assocName;
                        assocType = assocName = null;
                    }
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                string key = strings != null ? strings[(int)U32()] : CStr();
                switch (t)
                {
                    case 0: stack.Add(key); break;
                    case 1:
                        var v = CStr();
                        if (stack.Count == 2 && stack[1] == "common")
                        {
                            if (key == "name") name = v;
                            else if (key == "type") type = v;
                        }
                        else if (stack.Count == 4 && stack[2] == "associations")
                        {
                            if (key == "type") assocType = v;
                            else if (key == "name") assocName = v;
                        }
                        break;
                    case 2:
                        uint u = U32();
                        if (stack.Count == 2 && stack[1] == "common" && (key == "steam_release_date" || (key == "original_release_date" && release == 0))) release = u;
                        break;
                    case 3: pos += 4; break;
                    case 7: pos += 8; break;
                    default: pos = end; break;
                }
            }
            pos = end;
            if (name != null && type != null)
                result[id.ToString()] = new SteamApp(id.ToString(), name, type, dev,
                    release > 0 ? DateTimeOffset.FromUnixTimeSeconds(release).Year : null);
        }
        return result;
    }

    /// <summary>二进制 shortcuts.vdf：每个条目取 appid（无符号）和 LastPlayTime。</summary>
    private static IEnumerable<(string appid, long last)> BinaryShortcuts(byte[] data)
    {
        var list = new List<(string, long)>();
        int i = 0;
        uint appid = 0; long last = 0; bool haveId = false;
        int depth = 0;
        string ReadString()
        {
            int start = i;
            while (i < data.Length && data[i] != 0) i++;
            var s = Encoding.UTF8.GetString(data, start, i - start);
            i++;
            return s;
        }
        while (i < data.Length)
        {
            byte type = data[i++];
            if (type == 0x08)
            {
                depth--;
                if (depth == 1 && haveId) { list.Add((appid.ToString(), last)); haveId = false; last = 0; }
                if (depth < 0) break;
                continue;
            }
            var name = ReadString();
            switch (type)
            {
                case 0x00: depth++; break;
                case 0x01: ReadString(); break;
                case 0x02:
                    if (i + 4 > data.Length) return list;
                    uint v = BitConverter.ToUInt32(data, i); i += 4;
                    if (name.Equals("appid", StringComparison.OrdinalIgnoreCase)) { appid = v; haveId = true; }
                    else if (name.Equals("LastPlayTime", StringComparison.OrdinalIgnoreCase)) last = v;
                    break;
                case 0x07: i += 8; break;
                default: return list;
            }
        }
        return list;
    }

    /// <summary>封面原图：竖封面与无字大背景。grid 里用户自定义的优先，其次 librarycache。</summary>
    public static (string? Portrait, string? Hero) ArtSources(string root, string? userDir, string id)
    {
        string? portrait = null, hero = null;
        try
        {
            if (userDir != null)
            {
                var grid = Path.Combine(userDir, "config", "grid");
                if (Directory.Exists(grid))
                {
                    portrait = FirstExisting(grid, id + "p");
                    hero = FirstExisting(grid, id + "_hero");
                }
            }
            var cache = Path.Combine(root, "appcache", "librarycache", id);
            if (Directory.Exists(cache) && (portrait == null || hero == null))
            {
                var files = Directory.GetFiles(cache, "*.jpg", SearchOption.AllDirectories);
                portrait ??= Pick(files, "library_600x900") ?? Pick(files, "library_capsule");
                hero ??= files.Where(f => Path.GetFileName(f).StartsWith("library_hero", StringComparison.OrdinalIgnoreCase)
                                          && !f.Contains("blur", StringComparison.OrdinalIgnoreCase))
                              .OrderBy(f => Path.GetFileNameWithoutExtension(f).Length).FirstOrDefault();
            }
            if (portrait == null || hero == null)
            {
                // 老版本 Steam：librarycache 下平铺 <id>_library_600x900.jpg
                var flat = Path.Combine(root, "appcache", "librarycache");
                portrait ??= ExistingOrNull(Path.Combine(flat, $"{id}_library_600x900.jpg"));
                hero ??= ExistingOrNull(Path.Combine(flat, $"{id}_library_hero.jpg"));
            }
        }
        catch (Exception ex) { Log.Warn($"art sources {id}: {ex.Message}"); }
        return (portrait, hero);
    }

    private static string? Pick(string[] files, string prefix) => files
        .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

    private static string? FirstExisting(string dir, string stem)
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var p = Path.Combine(dir, stem + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? ExistingOrNull(string p) => File.Exists(p) ? p : null;
}

/// <summary>Valve 文本 KeyValues 的最小解析器（只要键值和嵌套）。</summary>
public sealed class TextVdf
{
    public readonly Dictionary<string, TextVdf> Children = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);

    public string? Value(string key) => Values.TryGetValue(key, out var v) ? v : null;

    public TextVdf? Path(params string[] keys)
    {
        var node = this;
        foreach (var k in keys)
            if (!node.Children.TryGetValue(k, out node!)) return null;
        return node;
    }

    public static TextVdf Parse(string text)
    {
        int i = 0;
        var root = new TextVdf();
        var stack = new Stack<TextVdf>();
        stack.Push(root);
        string? pendingKey = null;
        while (i < text.Length)
        {
            char ch = text[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (ch == '{')
            {
                var child = new TextVdf();
                if (pendingKey != null) stack.Peek().Children[pendingKey] = child;
                stack.Push(child);
                pendingKey = null; i++; continue;
            }
            if (ch == '}') { if (stack.Count > 1) stack.Pop(); pendingKey = null; i++; continue; }
            if (ch == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { i++; sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', _ => text[i] }); }
                    else sb.Append(text[i]);
                    i++;
                }
                i++;
                var s = sb.ToString();
                if (pendingKey == null) pendingKey = s;
                else { stack.Peek().Values[pendingKey] = s; pendingKey = null; }
                continue;
            }
            // 不带引号的记号
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"') i++;
            var tok = text[start..i];
            if (pendingKey == null) pendingKey = tok;
            else { stack.Peek().Values[pendingKey] = tok; pendingKey = null; }
        }
        return root;
    }
}
