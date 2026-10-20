// 设置页里手动加的游戏文件夹（比如 D:\gal）：每个子文件夹算一款游戏，名字就是文件夹名，
// 主程序是里面（最多往下两层）最大的那个 exe，安装、卸载、配置、崩溃上报这类程序不算。
// 子文件夹里没有 exe 的（原声、图片）跳过；已经加进 Steam 非 Steam 快捷方式的跳过，免得同一款出现两次。
// 上次启动时间记在 folder-played.ini，「最近」排序能用上。
using System.Security.Cryptography;
using System.Text;

namespace ClawLibrary.Data;

public static class FolderGames
{
    private static readonly string[] Skip =
    {
        "unins", "uninst", "setup", "install", "config", "crash", "update", "redist", "vcredist", "dxsetup", "dotnet",
        "unitycrashhandler", "ue4prereq", "7z", "patch", "register", "settings", "notification_helper", "cefsharp", "oalinst",
        "安装", "注意", "卸载", "设置",
    };

    private static string PlayedPath => Path.Combine(App.CacheDir, "folder-played.ini");

    public static string IdOf(string exe) =>
        "f-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(exe.ToUpperInvariant())))[..12].ToLowerInvariant();

    /// <summary>一个根文件夹里有多少款游戏（设置页显示用，不读封面）。</summary>
    public static int Count(string root) => Scan(root, new List<string>()).Count;

    public static List<(string Title, string Exe)> Scan(string root, List<string> shortcutPaths)
    {
        var found = new List<(string, string)>();
        try
        {
            if (!Directory.Exists(root)) return found;
            var known = shortcutPaths.Select(p => p.TrimEnd('\\').ToUpperInvariant()).ToList();
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.CurrentCulture))
            {
                string up = dir.TrimEnd('\\').ToUpperInvariant();
                if (known.Any(k => k == up || k.StartsWith(up + "\\"))) continue;
                var exe = MainExe(dir);
                if (exe != null) found.Add((Path.GetFileName(dir), exe));
            }
        }
        catch (Exception ex) { Log.Warn($"folder games {root}: {ex.Message}"); }
        return found;
    }

    private static string? MainExe(string dir)
    {
        string? best = null;
        long bestSize = -1;
        int bestDepth = int.MaxValue;
        void Look(string d, int depth)
        {
            if (depth > 2) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*.exe"))
                {
                    string name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (Skip.Any(s => name.Contains(s))) continue;
                    long size = new FileInfo(f).Length;
                    // 浅的优先，同一层取最大的
                    if (depth < bestDepth || (depth == bestDepth && size > bestSize)) { best = f; bestSize = size; bestDepth = depth; }
                }
                if (best == null || depth + 1 <= bestDepth)
                    foreach (var sub in Directory.EnumerateDirectories(d)) Look(sub, depth + 1);
            }
            catch { }
        }
        Look(dir, 0);
        return best;
    }

    public static Dictionary<string, long> ReadPlayed()
    {
        var map = new Dictionary<string, long>();
        try
        {
            if (File.Exists(PlayedPath))
                foreach (var line in File.ReadAllLines(PlayedPath))
                    if (line.Split('=', 2) is { Length: 2 } kv && long.TryParse(kv[1], out var t)) map[kv[0]] = t;
        }
        catch { }
        return map;
    }

    public static void MarkPlayed(string id)
    {
        try
        {
            var map = ReadPlayed();
            map[id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Directory.CreateDirectory(App.CacheDir);
            File.WriteAllLines(PlayedPath, map.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch (Exception ex) { Log.Warn($"folder played: {ex.Message}"); }
    }
}
