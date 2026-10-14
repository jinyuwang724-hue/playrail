// 读本机 Steam：安装目录、当前用户、上次游玩 / 时长（localconfig.vdf、shortcuts.vdf），以及封面原图在哪。
// 读不到就返回空，绝不抛出。
using System.Text;
using Microsoft.Win32;

namespace ClawLibrary.Data;

public sealed record PlayStats(long? Last, int? Minutes);

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
