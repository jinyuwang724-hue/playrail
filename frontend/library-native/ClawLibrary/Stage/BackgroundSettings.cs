namespace ClawLibrary.Stage;

public enum BackgroundKind { Latent, Monet, Nomand, Filmstrip }
public enum LatentLayers { Mixed, Pixels, Fluid }
public enum NomandEffect { Lens, Paper }
public enum FilmLayout { Horizontal, Vertical }

/// <summary>Shared by the wall and control centre. Small explicit format also works in NativeAOT.</summary>
public sealed record BackgroundSettings
{
    public BackgroundKind Kind { get; init; }
    public LatentLayers Latent { get; init; }
    public NomandEffect Nomand { get; init; }
    public bool MonetDrift { get; init; } = true;
    public bool MonetStreaks { get; init; } = true;
    public FilmLayout FilmLayout { get; init; } = FilmLayout.Horizontal;
    public int FilmStripCount { get; init; } // 0 keeps the original screen-sized layout.
    public int FilmSpeed { get; init; } = 100;
    public int FilmAngle { get; init; } = 15;
    public int FilmDarkness { get; init; } = 70;
    public string Name => Kind switch { BackgroundKind.Monet => "莫奈", BackgroundKind.Nomand => "漫游", BackgroundKind.Filmstrip => "胶片封面墙", _ => "隐现" };
    public string Variant => Kind switch
    {
        BackgroundKind.Nomand => Nomand == NomandEffect.Lens ? "镜头畸变" : "纸张纹理",
        BackgroundKind.Filmstrip => FilmLayout == FilmLayout.Horizontal ? "横向交错滑动" : "竖向交错滑动",
        BackgroundKind.Latent => Latent switch { LatentLayers.Pixels => "像素", LatentLayers.Fluid => "流体", _ => "混合" },
        _ => "全屏叠色",
    };
    private static string FilePath => Path.Combine(App.CacheDir, "background.ini");
    public static BackgroundSettings Load()
    {
        var result = new BackgroundSettings();
        try
        {
            if (!File.Exists(FilePath)) return result;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var p = line.Split('=', 2); if (p.Length != 2) continue;
                switch (p[0])
                {
                    case "kind" when Enum.TryParse<BackgroundKind>(p[1], out var k) && Enum.IsDefined(k): result = result with { Kind = k }; break;
                    case "latent" when Enum.TryParse<LatentLayers>(p[1], out var l) && Enum.IsDefined(l): result = result with { Latent = l }; break;
                    case "nomand" when Enum.TryParse<NomandEffect>(p[1], out var n) && Enum.IsDefined(n): result = result with { Nomand = n }; break;
                    case "drift" when bool.TryParse(p[1], out var d): result = result with { MonetDrift = d }; break;
                    case "streaks" when bool.TryParse(p[1], out var s): result = result with { MonetStreaks = s }; break;
                    case "filmLayout" when Enum.TryParse<FilmLayout>(p[1], out var fl) && Enum.IsDefined(fl): result = result with { FilmLayout = fl }; break;
                    case "filmStrips" when int.TryParse(p[1], out var fc): result = result with { FilmStripCount = fc == 0 ? 0 : Math.Clamp(fc, 3, 16) }; break;
                    case "filmSpeed" when int.TryParse(p[1], out var fs): result = result with { FilmSpeed = Math.Clamp(fs, 25, 200) }; break;
                    case "filmAngle" when int.TryParse(p[1], out var fa): result = result with { FilmAngle = Math.Clamp(fa, 0, 45) }; break;
                    case "filmDarkness" when int.TryParse(p[1], out var fd): result = result with { FilmDarkness = Math.Clamp(fd, 0, 90) }; break;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"background settings load: {ex.Message}"); }
        return result;
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.CacheDir);
            File.WriteAllLines(FilePath + ".tmp", new[] { $"kind={Kind}", $"latent={Latent}", $"nomand={Nomand}", $"drift={MonetDrift}", $"streaks={MonetStreaks}", $"filmLayout={FilmLayout}", $"filmStrips={FilmStripCount}", $"filmSpeed={FilmSpeed}", $"filmAngle={FilmAngle}", $"filmDarkness={FilmDarkness}" });
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception ex) { Log.Warn($"background settings save: {ex.Message}"); }
    }
}
