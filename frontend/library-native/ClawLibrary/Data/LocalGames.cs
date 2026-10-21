// 设置 › 游戏库 › 添加游戏 加的本地游戏：清单和选好的封面都在工具箱后台的 data\local-games 里（后台写，这里只读）。
// 游戏的 id 就是它在 Steam 里的非 Steam 快捷方式 appid（添加时就定好），同步进 Steam 以后，
// 自动收新快捷方式那一步看到同一个 id，不会再出现第二份。还没同步的直接启动 exe。
using System.Text.Json;

namespace ClawLibrary.Data;

public sealed record LocalGame(string Id, string Name, string Exe, bool Steam, string? Collection, long ArtVersion);

public static class LocalGames
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawToolbox", "data", "local-games");
    private static string ArtDir => Path.Combine(Dir, "art");

    public static List<LocalGame> Read()
    {
        var list = new List<LocalGame>();
        try
        {
            var path = Path.Combine(Dir, "games.json");
            if (!File.Exists(path)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var g in doc.RootElement.EnumerateArray())
            {
                string S(string k) => g.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (S("id").Length == 0 || S("exe").Length == 0) continue;
                bool steam = !g.TryGetProperty("steam", out var st) || st.ValueKind != JsonValueKind.False;
                long ver = g.TryGetProperty("artVersion", out var av) && av.TryGetInt64(out var a) ? a : 0;
                list.Add(new LocalGame(S("id"), S("name"), S("exe"), steam, S("collection") is { Length: > 0 } c ? c : null, ver));
            }
        }
        catch (Exception ex) { Log.Warn($"local games: {ex.Message}"); }
        return list;
    }

    /// <summary>选好的封面原图：kind 是 Steam grid 的后缀（"p"、"_hero"、"_logo"、""）。</summary>
    public static string? ArtFile(string id, string suffix)
    {
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var p = Path.Combine(ArtDir, id + suffix + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
