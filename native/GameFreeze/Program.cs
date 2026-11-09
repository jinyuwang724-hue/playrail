using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClawSleepAnimation;

internal static class Program
{
    internal static void Log(string kind, object data) => Console.Error.WriteLine($"{kind}: {data}");

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length != 1 && args.Length != 3) throw new ArgumentException("游戏冻结参数无效");
            var directory = args[0];
            Directory.CreateDirectory(directory);
            using var lease = new FileStream(Path.Combine(directory, "game-freeze.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string journal = Path.Combine(directory, "game-freeze.json");
            FrozenGame[] Read() => File.Exists(journal)
                ? JsonSerializer.Deserialize<FrozenGame[]>(File.ReadAllBytes(journal)) ?? throw new InvalidDataException("游戏恢复记录损坏") : [];
            void Write(FrozenGame[] games)
            {
                if (games.Length == 0) { File.Delete(journal); return; }
                string temporary = journal + ".tmp";
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { JsonSerializer.Serialize(file, games); file.Flush(true); }
                File.Move(temporary, journal, true);
            }
            void Recover()
            {
                var pending = Read();
                if (pending.Length > 128 || pending.Any(p => p.Pid <= 4 || p.Started <= 0 || string.IsNullOrWhiteSpace(p.Image)))
                    throw new InvalidDataException("游戏恢复记录无效");
                var failures = new List<Exception>();
                foreach (var game in pending.Reverse())
                {
                    try { GameSleep.Resume(game); pending = pending.Where(p => p != game).ToArray(); Write(pending); }
                    catch (Exception error) { failures.Add(error); }
                }
                if (failures.Count != 0) throw new AggregateException("游戏恢复尚未完成", failures);
            }
            Recover();
            if (args.Length == 1) return 0;
            var target = GameSleep.Validate(int.Parse(args[1]));
            if (target.Started != DateTime.FromFileTimeUtc(long.Parse(args[2])).Ticks)
                throw new InvalidOperationException("游戏进程已变化，请重新选择");
            var owner = Task.Run(Console.ReadLine);
            using var gameProcess = Process.GetProcessById(target.Pid);
            using var watch = new CancellationTokenSource();
            var exited = gameProcess.WaitForExitAsync(watch.Token);
            try
            {
                GameSleep.Suspend(target, Read, Write, () => owner.IsCompleted);
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, processes = Read().Length }));
                Task.WhenAny(owner, exited).GetAwaiter().GetResult().GetAwaiter().GetResult();
            }
            finally { watch.Cancel(); Recover(); }
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error.Message }));
            return 1;
        }
    }
}
