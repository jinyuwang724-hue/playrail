using System.Text.Json;

namespace ClawLibrary.Data;

/// <summary>库内显示名称按稳定游戏编号保存，刷新平台数据后仍保留。</summary>
public static class GameNames
{
    private static string FilePath => Path.Combine(App.CacheDir, "game-names.json");

    private static Dictionary<string, string> Read()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(FilePath)) return names;
        using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
        foreach (var item in document.RootElement.EnumerateObject())
            if (item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { Length: > 0 } name)
                names[item.Name] = name;
        return names;
    }

    public static void Save(string id, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("找不到这款游戏");
        if (name.Length == 0) throw new ArgumentException("游戏名称不能为空");
        if (name.Length > 200) throw new ArgumentException("游戏名称不能超过 200 个字符");
        if (name.Any(char.IsControl)) throw new ArgumentException("游戏名称不能包含换行或控制字符");
        var names = Read();
        names[id] = name;
        Directory.CreateDirectory(App.CacheDir);
        string temporary = FilePath + ".tmp";
        using (var stream = File.Create(temporary))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var pair in names) writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
        }
        File.Move(temporary, FilePath, overwrite: true);
    }

    public static void Apply(IEnumerable<LibraryItem> games)
    {
        Dictionary<string, string> names;
        try { names = Read(); }
        catch (Exception ex) { Log.Warn($"game names: {ex.Message}"); return; }
        foreach (var game in games)
            if (game.Kind == ItemKind.Game && names.TryGetValue(game.Id, out var name))
            {
                game.Title = game.Main = name;
                game.Kicker = game.Tail = null;
            }
    }
}
