// 文件日志：<缓存目录>\logs\library.log，超过 2 MB 换新文件。任何地方出错都只记一笔，不让程序崩。
using System.Diagnostics;
using System.Text;

namespace ClawLibrary;

public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    public static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Init(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "library.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > 2_000_000)
                File.Move(_path, Path.Combine(dir, "library.old.log"), true);
        }
        catch { _path = null; }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        Debug.Write(line);
        if (_path == null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_path, line, Encoding.UTF8); } catch { }
        }
    }
}
