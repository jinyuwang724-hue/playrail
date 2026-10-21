namespace ClawLibrary.Data;

public sealed record HomeTrack(string Id, string Name, string Path, bool Custom = false);

public sealed class MusicLibrary(string assets, string userFolder)
{
    public const string DefaultId = "builtin:daylight";
    public static readonly string[] Extensions = { ".mp3", ".wav", ".m4a", ".flac", ".aac", ".wma" };
    public static bool Supports(string path) => Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<HomeTrack> Tracks()
    {
        var tracks = new List<HomeTrack>
        {
            new(DefaultId, "浮光", System.IO.Path.Combine(assets, "daylight.wav")),
            new("builtin:starport", "星港", System.IO.Path.Combine(assets, "starport.wav")),
            new("builtin:night", "夜航", System.IO.Path.Combine(assets, "night.wav")),
        };
        if (Directory.Exists(userFolder))
            foreach (var file in Directory.EnumerateFiles(userFolder).Where(Supports).OrderBy(System.IO.Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(file);
                int split = name.IndexOf("--", StringComparison.Ordinal);
                if (split >= 0) name = name[(split + 2)..];
                tracks.Add(new("custom:" + System.IO.Path.GetFileName(file), name, file, true));
            }
        return tracks;
    }
    public HomeTrack Resolve(string id) => Tracks().FirstOrDefault(t => t.Id == id) ?? Tracks()[0];

    public HomeTrack Import(string source)
    {
        if (!Supports(source)) throw new IOException("请选择 MP3、WAV、M4A、FLAC、AAC 或 WMA 音频");
        var info = new FileInfo(source);
        if (!info.Exists || info.Length == 0) throw new IOException("这个音频文件为空或已不存在");
        if (info.Length > 250L * 1024 * 1024) throw new IOException("请选择小于 250 MB 的音频文件");
        Directory.CreateDirectory(userFolder);
        string title = System.IO.Path.GetFileNameWithoutExtension(source);
        if (title.Length > 80) title = title[..80];
        string name = Guid.NewGuid().ToString("N") + "--" + title + info.Extension.ToLowerInvariant();
        string destination = System.IO.Path.Combine(userFolder, name), temporary = destination + ".importing";
        try { File.Copy(source, temporary); File.Move(temporary, destination); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new("custom:" + name, title, destination, true);
    }

    public void Remove(HomeTrack track)
    {
        string expected = System.IO.Path.GetFullPath(userFolder) + System.IO.Path.DirectorySeparatorChar;
        string full = System.IO.Path.GetFullPath(track.Path);
        if (!track.Custom || !full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(System.IO.Path.GetDirectoryName(full), System.IO.Path.GetFullPath(userFolder), StringComparison.OrdinalIgnoreCase))
            throw new IOException("只能移除已导入的音乐副本");
        File.Delete(full);
    }
}

public static class HomeAudioPolicy
{
    public static bool CanPlay(bool enabled, int volume, bool foreground, bool launchHold) => enabled && volume > 0 && foreground && !launchHold;
}
