// 设置页里只属于游戏库自己的选项（标题样式、开机落在哪、集合、外观、音效……），存在 ui.ini。
// 以前标题样式、动态背景、降低动态效果重启后都回到默认；从这里开始都记下来。
// 写法和 background.ini 一样：key=value，一行一项，NativeAOT 下不用反射。
namespace ClawLibrary.Stage;

public enum BootTarget { Library, ControlCenter, Perf, Gamepad, Display, System, Settings }
public enum AfterLaunch { Keep, Hide }

public sealed record UiPrefs
{
    public bool StyleB { get; init; }
    public bool Backdrop { get; init; } = true;
    public bool Reduced { get; init; }
    public BootTarget Boot { get; init; }
    public string StartTab { get; init; } = "all";
    /// <summary>集合（竖条页签）的顺序；没列到的排在后面。</summary>
    public string[] TabOrder { get; init; } = Array.Empty<string>();
    public string[] TabsHidden { get; init; } = Array.Empty<string>();
    public bool ShowHiddenGames { get; init; }
    public bool SourceSteam { get; init; } = true;
    public bool SourceShortcuts { get; init; } = true;
    public AfterLaunch AfterLaunch { get; init; }
    /// <summary>强调色：-1 = 跟随封面，0… = 固定色（UiPrefs.Accents）。</summary>
    public int Accent { get; init; } = -1;
    public bool Sounds { get; init; }
    public int SoundVolume { get; init; } = 40;
    public int SoundTheme { get; init; }
    public bool MusicEnabled { get; init; }
    public int MusicVolume { get; init; } = 20;
    public string MusicTrack { get; init; } = Data.MusicLibrary.DefaultId;
    public bool MusicLoop { get; init; } = true;
    /// <summary>手动加的游戏文件夹（每个子文件夹一款游戏）。</summary>
    public string[] Folders { get; init; } = Array.Empty<string>();
    /// <summary>全屏体验主页选了「Steam 大屏幕」：游戏库被当主页打开时转去 Steam 大屏幕（替代 AnyFSE）。</summary>
    public bool FseSteam { get; init; }
    /// <summary>游戏库里的导航震动（和 Steam 大屏幕同款）。</summary>
    public bool NavHaptics { get; init; } = true;
    /// <summary>控制中心的游戏库卡显示当前游戏封面；关掉时用图标，底色是当前选中游戏的主题色。</summary>
    public bool LibraryCover { get; init; }
    /// <summary>截图页的排版：0 日历、1 放映、2 冲印。</summary>
    public int ShotsStyle { get; init; }
    /// <summary>添加游戏 › 匹配弹窗的搜索栏样子：0 胶囊 / 1 大字 / 2 顶栏。</summary>

    public static readonly (string Name, uint Rgb)[] Accents =
    {
        ("橙", 0xfd8a5a), ("黄", 0xf4e04d), ("青", 0x2dd4bf), ("蓝", 0x60a5fa), ("紫", 0xa78bfa), ("粉", 0xf472b6),
    };

    private static string FilePath => Path.Combine(App.CacheDir, "ui.ini");

    public static UiPrefs Load()
    {
        var p = new UiPrefs();
        try
        {
            if (!File.Exists(FilePath)) return p;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                string v = kv[1];
                string[] List() => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                p = kv[0] switch
                {
                    "styleB" when bool.TryParse(v, out var b) => p with { StyleB = b },
                    "backdrop" when bool.TryParse(v, out var b) => p with { Backdrop = b },
                    "reduced" when bool.TryParse(v, out var b) => p with { Reduced = b },
                    "boot" when Enum.TryParse<BootTarget>(v, out var e) && Enum.IsDefined(e) => p with { Boot = e },
                    "startTab" => p with { StartTab = v },
                    "tabOrder" => p with { TabOrder = List() },
                    "tabsHidden" => p with { TabsHidden = List() },
                    "showHidden" when bool.TryParse(v, out var b) => p with { ShowHiddenGames = b },
                    "sourceSteam" when bool.TryParse(v, out var b) => p with { SourceSteam = b },
                    "sourceShortcuts" when bool.TryParse(v, out var b) => p with { SourceShortcuts = b },
                    "afterLaunch" when Enum.TryParse<AfterLaunch>(v, out var e) && Enum.IsDefined(e) => p with { AfterLaunch = e },
                    "accent" when int.TryParse(v, out var i) => p with { Accent = Math.Clamp(i, -1, Accents.Length - 1) },
                    "sounds" when bool.TryParse(v, out var b) => p with { Sounds = b },
                    "soundVolume" when int.TryParse(v, out var i) => p with { SoundVolume = Math.Clamp(i, 0, 100) },
                    "soundTheme" when int.TryParse(v, out var i) => p with { SoundTheme = Math.Clamp(i, 0, 2) },
                    "musicEnabled" when bool.TryParse(v, out var b) => p with { MusicEnabled = b },
                    "musicVolume" when int.TryParse(v, out var i) => p with { MusicVolume = Math.Clamp(i, 0, 100) },
                    "musicTrack" when v.Length > 0 => p with { MusicTrack = v },
                    "musicLoop" when bool.TryParse(v, out var b) => p with { MusicLoop = b },
                    "fseSteam" when bool.TryParse(v, out var b) => p with { FseSteam = b },
                    "navHaptics" when bool.TryParse(v, out var b) => p with { NavHaptics = b },
                    "libraryCover" when bool.TryParse(v, out var b) => p with { LibraryCover = b },
                    "shotsStyle" when int.TryParse(v, out var i) => p with { ShotsStyle = Math.Clamp(i, 0, 2) },
                    "folders" => p with { Folders = v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
                    _ => p,
                };
            }
        }
        catch (Exception ex) { Log.Warn($"ui prefs load: {ex.Message}"); }
        return p;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.CacheDir);
            File.WriteAllLines(FilePath + ".tmp", new[]
            {
                $"styleB={StyleB}", $"backdrop={Backdrop}", $"reduced={Reduced}", $"boot={Boot}", $"startTab={StartTab}",
                $"tabOrder={string.Join(',', TabOrder)}", $"tabsHidden={string.Join(',', TabsHidden)}", $"showHidden={ShowHiddenGames}",
                $"sourceSteam={SourceSteam}", $"sourceShortcuts={SourceShortcuts}", $"afterLaunch={AfterLaunch}",
                $"accent={Accent}", $"sounds={Sounds}", $"soundVolume={SoundVolume}", $"folders={string.Join('|', Folders)}", $"fseSteam={FseSteam}", $"navHaptics={NavHaptics}", $"libraryCover={LibraryCover}", $"shotsStyle={ShotsStyle}",
                $"soundTheme={SoundTheme}", $"musicEnabled={MusicEnabled}", $"musicVolume={MusicVolume}", $"musicTrack={MusicTrack}", $"musicLoop={MusicLoop}",
            });
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception ex) { Log.Warn($"ui prefs save: {ex.Message}"); }
    }
}
