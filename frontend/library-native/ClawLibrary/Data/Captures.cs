// 截图来源：Xbox Game Bar 的「捕获」文件夹（默认 视频\Captures，可以在资源管理器里把它挪走，位置记在 User Shell Folders 里），
// 和 Steam 自己的截图（userdata\<账号>\760\remote\<appid>\screenshots）。只收图片（png / jpg），录像先不管。
// Game Bar 不记录游戏：后台（claw_captures.py）看着捕获文件夹，每出现一张就记下当时是哪个窗口截的（进程路径 + 标题），
// 写在 %LOCALAPPDATA%\ClawToolbox\data\captures.jsonl；这里按进程路径对游戏库（Steam 安装目录、非 Steam 快捷方式的程序 /
// 起始目录、手动加的游戏文件夹），最准。没记录的（后台没开时截的、以前的）再看文件名「窗口标题 年_月_日 时_分_秒.png」，
// 按窗口标题归到游戏库里的游戏（名字对得上就算），对不上的（桌面、浏览器、别的软件）归到「其他」。Steam 截图的文件夹名就是 appid，直接对应。
// 连拍：同一款游戏、前后相隔不到 3 秒的几张叠成一摞。
// 读不到就返回空，绝不抛出。
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ClawLibrary.Data;

public sealed class Capture
{
    public required string Path;
    /// <summary>归到的游戏：游戏库里的 Id；对不上为 null（算「其他」）。</summary>
    public string? GameId;
    /// <summary>显示用的名字：游戏库里的标题，对不上就用文件名里的窗口标题。</summary>
    public required string Game;
    public DateTime Time;
    public long Bytes;
    public string Source = "Game Bar";
    /// <summary>连拍组号：同一摞的几张一样；不是连拍为 -1。</summary>
    public int Burst = -1;
}

public static partial class Captures
{
    private const string CapturesFolderId = "{EDC0FE71-98D8-4F4A-B920-C8DC133CB165}";

    /// <summary>Game Bar 的捕获文件夹（没挪过就是 视频\Captures）。</summary>
    public static string GameBarFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue(CapturesFolderId, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw && raw.Length > 0)
            {
                var p = Environment.ExpandEnvironmentVariables(raw);
                if (Directory.Exists(p)) return p;
            }
        }
        catch (Exception ex) { Log.Warn($"captures folder: {ex.Message}"); }
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
    }

    [GeneratedRegex(@"^(?<g>.*?)\s+(?<y>\d{4})_(?<mo>\d{1,2})_(?<d>\d{1,2})\s+(?<h>\d{1,2})_(?<mi>\d{2})_(?<s>\d{2})(?:\s*\(\d+\))?$")]
    private static partial Regex GameBarName();

    [GeneratedRegex(@"^(?<y>\d{4})(?<mo>\d{2})(?<d>\d{2})(?<h>\d{2})(?<mi>\d{2})(?<s>\d{2})_\d+$")]
    private static partial Regex SteamName();

    private static bool IsImage(string p) =>
        p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>比名字用：小写，去掉空白和常见符号（™ ® : · - _ 等）。</summary>
    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>扫一遍，按时间从新到旧。</summary>
    public static List<Capture> Scan(Library lib)
    {
        var list = new List<Capture>();
        var names = GameNames(lib);
        var sidecar = LoadSidecar();
        var owners = sidecar.Count > 0 ? ExeOwners(lib) : new();
        try
        {
            var dir = GameBarFolder();
            if (Directory.Exists(dir))
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                {
                    if (!IsImage(f.Name)) continue;
                    string stem = System.IO.Path.GetFileNameWithoutExtension(f.Name);
                    var m = GameBarName().Match(stem);
                    string window = m.Success ? m.Groups["g"].Value.Trim() : "";
                    DateTime time = m.Success && TryTime(m, out var t) ? t : f.LastWriteTime;
                    var game = sidecar.TryGetValue(f.Name, out var exe) ? MatchExe(exe, owners) : null;
                    game ??= Match(window, names);
                    list.Add(new Capture
                    {
                        Path = f.FullName, Time = time, Bytes = f.Length,
                        GameId = game?.Id, Game = game?.Title ?? (window.Length > 0 ? window : "其他"),
                    });
                }
        }
        catch (Exception ex) { Log.Warn($"captures scan: {ex.Message}"); }

        try
        {
            if (Steam.Root() is string root && Steam.UserDir(root) is string user)
            {
                var remote = System.IO.Path.Combine(user, "760", "remote");
                if (Directory.Exists(remote))
                    foreach (var appDir in Directory.GetDirectories(remote))
                    {
                        var shots = System.IO.Path.Combine(appDir, "screenshots");
                        if (!Directory.Exists(shots)) continue;
                        string appid = System.IO.Path.GetFileName(appDir);
                        lib.ById.TryGetValue(appid, out var game);
                        foreach (var f in new DirectoryInfo(shots).EnumerateFiles())
                        {
                            if (!IsImage(f.Name)) continue;
                            var m = SteamName().Match(System.IO.Path.GetFileNameWithoutExtension(f.Name));
                            list.Add(new Capture
                            {
                                Path = f.FullName, Time = m.Success && TryTime(m, out var t) ? t : f.LastWriteTime, Bytes = f.Length,
                                GameId = game?.Id, Game = game?.Title ?? $"Steam {appid}", Source = "Steam",
                            });
                        }
                    }
            }
        }
        catch (Exception ex) { Log.Warn($"steam screenshots scan: {ex.Message}"); }

        list.Sort((a, b) => b.Time.CompareTo(a.Time));
        // 连拍：同一款游戏、和上一张（更新的那张）相隔不到 3 秒
        int group = 0;
        for (int i = 1; i < list.Count; i++)
        {
            var a = list[i - 1]; var b = list[i];
            if (a.Game == b.Game && (a.Time - b.Time).TotalSeconds <= 3)
            {
                if (a.Burst < 0) a.Burst = ++group;
                b.Burst = a.Burst;
            }
        }
        return list;
    }

    private static bool TryTime(Match m, out DateTime t)
    {
        try
        {
            t = new DateTime(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture));
            return true;
        }
        catch { t = default; return false; }
    }

    /// <summary>游戏库里每款游戏能拿来比的名字：标题、排版主标题、小字原名、主 + 副标题、exe 名。</summary>
    private static List<(string Norm, LibraryItem Item)> GameNames(Library lib)
    {
        var list = new List<(string, LibraryItem)>();
        foreach (var g in lib.Games)
        {
            if (g.Kind != ItemKind.Game) continue;
            foreach (var n in new[] { g.Title, g.Main, g.Kicker, (g.Main ?? "") + (g.Tail ?? ""), g.ExePath == null ? null : System.IO.Path.GetFileNameWithoutExtension(g.ExePath) })
                if (!string.IsNullOrWhiteSpace(n) && Norm(n) is { Length: >= 2 } k) list.Add((k, g));
        }
        return list;
    }

    private static string SidecarPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawToolbox", "data", "captures.jsonl");

    /// <summary>后台记下的「文件名 → 截图时的进程路径」；同一个文件记了几次以最后一次为准。</summary>
    private static Dictionary<string, string> LoadSidecar()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(SidecarPath)) return map;
            using var fs = new FileStream(SidecarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            while (reader.ReadLine() is string line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("file", out var f) && root.TryGetProperty("exe", out var e)
                        && f.GetString() is { Length: > 0 } file && e.GetString() is { Length: > 0 } exe)
                        map[file] = exe;
                }
                catch (JsonException) { }   // 写到一半的最后一行
            }
        }
        catch (Exception ex) { Log.Warn($"captures sidecar: {ex.Message}"); }
        return map;
    }

    /// <summary>
    /// 游戏库里每款游戏「自己的」程序和目录：Steam 游戏的安装目录；非 Steam 快捷方式的程序和起始目录（启动器那种程序不在游戏目录里，
    /// 起始目录还是游戏的）；手动加的游戏文件夹的程序。
    /// </summary>
    private static List<(string? Exe, string Dir, LibraryItem Item)> ExeOwners(Library lib)
    {
        var list = new List<(string?, string, LibraryItem)>();
        void Add(string? exe, string? dir, LibraryItem item)
        {
            dir = Folder(dir);
            if (dir != null) list.Add((exe == null ? null : Folder(exe), dir, item));
        }
        foreach (var g in lib.Games)
            if (g.Kind == ItemKind.Game && g.ExePath is string exe) Add(exe, System.IO.Path.GetDirectoryName(exe), g);
        if (Steam.Root() is string root)
        {
            foreach (var (id, dir) in Steam.InstallDirs(root))
                if (lib.ById.TryGetValue(id, out var g) && g.Kind == ItemKind.Game) Add(null, dir, g);
            foreach (var (id, exe, start) in Steam.ShortcutTargets(Steam.UserDir(root)))
                if (lib.ById.TryGetValue(id, out var g) && g.Kind == ItemKind.Game)
                {
                    if (exe.Length > 0) Add(exe, System.IO.Path.GetDirectoryName(exe), g);
                    if (start.Length > 0) Add(null, start, g);
                }
        }
        return list;
    }

    /// <summary>完整路径、去掉结尾的 \；盘符根目录（C:\）太宽，不要。</summary>
    private static string? Folder(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try
        {
            var full = System.IO.Path.GetFullPath(p.Trim().Trim('"')).TrimEnd('\\', '/');
            return full.Length <= 3 ? null : full;
        }
        catch { return null; }
    }

    /// <summary>截图时的进程对游戏：程序一模一样最好；否则进程在哪个游戏目录里（取最深的目录）。</summary>
    private static LibraryItem? MatchExe(string exe, List<(string? Exe, string Dir, LibraryItem Item)> owners)
    {
        if (Folder(exe) is not string path) return null;
        foreach (var (e, _, item) in owners) if (e != null && e.Equals(path, StringComparison.OrdinalIgnoreCase)) return item;
        LibraryItem? best = null; int bestLen = 0;
        foreach (var (_, dir, item) in owners)
            if (dir.Length > bestLen && path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) { best = item; bestLen = dir.Length; }
        return best;
    }

    /// <summary>窗口标题对游戏名：一样最好；否则一个包含另一个（取最长的那个名字，免得「鸣潮」对上别的短名）。</summary>
    private static LibraryItem? Match(string window, List<(string Norm, LibraryItem Item)> names)
    {
        string w = Norm(window);
        if (w.Length < 2) return null;
        foreach (var (n, item) in names) if (n == w) return item;
        LibraryItem? best = null; int bestLen = 0;
        foreach (var (n, item) in names)
            if ((w.Contains(n) || n.Contains(w)) && n.Length > bestLen) { best = item; bestLen = n.Length; }
        return best;
    }

    /// <summary>图片的像素尺寸：只读文件头（PNG 的 IHDR、JPEG 的 SOF），读不出来返回 null。</summary>
    public static (int W, int H)? ImageSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[Math.Min(fs.Length, 256 * 1024)];
            int n = fs.Read(buf, 0, buf.Length);
            if (n >= 24 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47)
                return ((buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19], (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23]);
            if (n >= 4 && buf[0] == 0xFF && buf[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < n)
                {
                    if (buf[i] != 0xFF) { i++; continue; }
                    byte mk = buf[i + 1];
                    if (mk == 0xFF) { i++; continue; }
                    int len = (buf[i + 2] << 8) | buf[i + 3];
                    if (mk is >= 0xC0 and <= 0xCF && mk is not (0xC4 or 0xC8 or 0xCC))
                        return ((buf[i + 7] << 8) | buf[i + 8], (buf[i + 5] << 8) | buf[i + 6]);
                    i += 2 + len;
                }
            }
        }
        catch { }
        return null;
    }

    // ---------------- 删除：进回收站，不直接删 ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int Aborted;
        public IntPtr NameMappings;
        public IntPtr Title;
    }

    [DllImport("shell32.dll")]
    private static extern int SHFileOperationW(ref ShFileOp op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    public static bool Recycle(string path)
    {
        IntPtr from = Marshal.StringToHGlobalUni(path + "\0");   // 列表以两个 \0 结尾（StringToHGlobalUni 自己再补一个）
        try
        {
            var op = new ShFileOp { Func = FO_DELETE, From = from, Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI };
            int rc = SHFileOperationW(ref op);
            if (rc != 0 || op.Aborted != 0) { Log.Warn($"recycle {path}: rc={rc} aborted={op.Aborted}"); return false; }
            return !File.Exists(path);
        }
        finally { Marshal.FreeHGlobal(from); }
    }

    // ---------------- 收藏：存在 shots.ini，一行一个完整路径 ----------------

    private static string FavPath => System.IO.Path.Combine(App.CacheDir, "shots.ini");

    public static HashSet<string> LoadFavorites()
    {
        try { if (File.Exists(FavPath)) return new HashSet<string>(File.ReadAllLines(FavPath).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) { Log.Warn($"shots favorites load: {ex.Message}"); }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveFavorites(HashSet<string> favs)
    {
        try
        {
            Directory.CreateDirectory(App.CacheDir);
            File.WriteAllLines(FavPath + ".tmp", favs.Where(File.Exists));
            File.Move(FavPath + ".tmp", FavPath, true);
        }
        catch (Exception ex) { Log.Warn($"shots favorites save: {ex.Message}"); }
    }
}
