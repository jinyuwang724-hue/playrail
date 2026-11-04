// 设置页里只属于游戏库自己的选项（标题样式、开机落在哪、集合、外观、音效……），存在 ui.ini。
// 以前标题样式、动态背景、降低动态效果重启后都回到默认；从这里开始都记下来。
// 写法和 background.ini 一样：key=value，一行一项，NativeAOT 下不用反射。
namespace ClawLibrary.Stage;

public enum BootTarget { Library, ControlCenter, Perf, Gamepad, Display, System, Settings }
public enum AfterLaunch { Keep, Hide }
/// <summary>游戏中回到游戏库：先给「正在游戏」面板，还是直接回墙面（顶上一条「正在游戏」）。</summary>
public enum NowPlayingStyle { Panel, Bar }
public enum SteamShortcutTarget { Library, BigPicture }

public sealed record UiPrefs
{
    public bool StyleB { get; init; }
    /// <summary>空值沿用旧版 styleB；以后按稳定名称保存标题样式。</summary>
    public string TitleStyleKey { get; init; } = "";
    public bool LibraryRail { get; init; } = true;
    /// <summary>竖条 Steam 按钮的目标；默认打开 Steam 库。</summary>
    public SteamShortcutTarget SteamShortcut { get; init; }
    public bool Screensaver { get; init; }
    public int ScreensaverIdleSeconds { get; init; } = 60;
    public int ScreensaverIntervalSeconds { get; init; } = 6;
    public bool Backdrop { get; init; } = true;
    public bool Reduced { get; init; }
    public BootTarget Boot { get; init; }
    public string StartTab { get; init; } = "all";
    /// <summary>集合（竖条页签）的顺序；没列到的排在后面。</summary>
    public string[] TabOrder { get; init; } = Array.Empty<string>();
    public string[] TabsHidden { get; init; } = Array.Empty<string>();
    public bool ShowHiddenGames { get; init; }
    public bool SourceSteam { get; init; } = true;
    /// <summary>Steam 游戏只显示已安装的；默认 false，沿用完整游戏库。</summary>
    public bool SteamInstalledOnly { get; init; }
    public bool SourceXbox { get; init; } = true;
    public bool PreloadSteam { get; init; } = true;
    /// <summary>游戏模式窗口管理（试验）：新窗口铺满、最小化或关掉就回游戏库，窗口变化即时处理。</summary>
    public bool WindowManager { get; init; }
    public bool BackgroundRun { get; init; } = true;
    public bool SourceShortcuts { get; init; } = true;
    public AfterLaunch AfterLaunch { get; init; }
    public NowPlayingStyle NowPlaying { get; init; }
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
    /// <summary>墙上的灯光（folia「海报灯光」）：关灯时除焦点 / 展开的卡外每张压 82 % 黑，竖条压 60 %。</summary>
    public bool LightsOn { get; init; } = true;
    /// <summary>海报叠色（folia latticePosterTint*）：开关、用自定义色、自定义色、强度 0–100 %。</summary>
    public bool PosterTint { get; init; } = true;
    public bool PosterTintCustom { get; init; }
    public string PosterTintColor { get; init; } = DefaultTintColor;
    public int PosterTintIntensity { get; init; } = 50;
    public const string DefaultTintColor = "#161419";

    public static readonly (string Name, uint Rgb)[] Accents =
    {
        ("橙", 0xfd8a5a), ("黄", 0xf4e04d), ("青", 0x2dd4bf), ("蓝", 0x60a5fa), ("紫", 0xa78bfa), ("粉", 0xf472b6),
    };

    private static string FilePath => Path.Combine(App.CacheDir, "ui.ini");
    /// <summary>做完的首次启动引导版本（WallStage.OobeVersion）。</summary>
    public int Oobe { get; init; }
    /// <summary>界面语言（zh-CN / en-US）；空 = 还没选过，跟系统。目前只有首次启动引导有英文。</summary>
    public string Lang { get; init; } = "";
    /// <summary>这次启动之前还没有 ui.ini：新装，首次启动引导自动出现。</summary>
    public static readonly bool FreshInstall = !File.Exists(FilePath);

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
                    "titleStyle" when v is "D" or "B" or "Folia" or "Quiet" or "Editorial" => p with { TitleStyleKey = v },
                    "libraryRail" when bool.TryParse(v, out var b) => p with { LibraryRail = b },
                    "steamShortcut" when Enum.TryParse<SteamShortcutTarget>(v, out var e) && Enum.IsDefined(e) => p with { SteamShortcut = e },
                    "screensaver" when bool.TryParse(v, out var b) => p with { Screensaver = b },
                    "screensaverIdleSeconds" when int.TryParse(v, out var i) => p with { ScreensaverIdleSeconds = Math.Clamp(i, 15, 900) },
                    "screensaverIntervalSeconds" when int.TryParse(v, out var i) => p with { ScreensaverIntervalSeconds = Math.Clamp(i, 3, 60) },
                    "backdrop" when bool.TryParse(v, out var b) => p with { Backdrop = b },
                    "reduced" when bool.TryParse(v, out var b) => p with { Reduced = b },
                    "boot" when Enum.TryParse<BootTarget>(v, out var e) && Enum.IsDefined(e) => p with { Boot = e },
                    "startTab" => p with { StartTab = v },
                    "tabOrder" => p with { TabOrder = List() },
                    "tabsHidden" => p with { TabsHidden = List() },
                    "showHidden" when bool.TryParse(v, out var b) => p with { ShowHiddenGames = b },
                    "sourceSteam" when bool.TryParse(v, out var b) => p with { SourceSteam = b },
                    "steamInstalledOnly" when bool.TryParse(v, out var b) => p with { SteamInstalledOnly = b },
                    "sourceXbox" when bool.TryParse(v, out var b) => p with { SourceXbox = b },
                    "preloadSteam" when bool.TryParse(v, out var b) => p with { PreloadSteam = b },
                    "windowManager" when bool.TryParse(v, out var b) => p with { WindowManager = b },
                    "backgroundRun" when bool.TryParse(v, out var b) => p with { BackgroundRun = b },
                    "sourceShortcuts" when bool.TryParse(v, out var b) => p with { SourceShortcuts = b },
                    "afterLaunch" when Enum.TryParse<AfterLaunch>(v, out var e) && Enum.IsDefined(e) => p with { AfterLaunch = e },
                    "nowPlaying" when Enum.TryParse<NowPlayingStyle>(v, out var np) && Enum.IsDefined(np) => p with { NowPlaying = np },
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
                    "lightsOn" when bool.TryParse(v, out var b) => p with { LightsOn = b },
                    "posterTint" when bool.TryParse(v, out var b) => p with { PosterTint = b },
                    "posterTintCustom" when bool.TryParse(v, out var b) => p with { PosterTintCustom = b },
                    "posterTintColor" when v.Length == 7 && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit) => p with { PosterTintColor = v.ToLowerInvariant() },
                    "posterTintIntensity" when int.TryParse(v, out var i) => p with { PosterTintIntensity = Math.Clamp(i, 0, 100) },
                    "oobe" when int.TryParse(v, out var i) => p with { Oobe = Math.Max(0, i) },
                    "lang" when v is "zh-CN" or "en-US" => p with { Lang = v },
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
                $"titleStyle={TitleStyleKey}", $"libraryRail={LibraryRail}", $"screensaver={Screensaver}",
                $"steamShortcut={SteamShortcut}",
                $"screensaverIdleSeconds={ScreensaverIdleSeconds}", $"screensaverIntervalSeconds={ScreensaverIntervalSeconds}",
                $"tabOrder={string.Join(',', TabOrder)}", $"tabsHidden={string.Join(',', TabsHidden)}", $"showHidden={ShowHiddenGames}",
                $"sourceSteam={SourceSteam}", $"steamInstalledOnly={SteamInstalledOnly}", $"sourceShortcuts={SourceShortcuts}", $"afterLaunch={AfterLaunch}", $"nowPlaying={NowPlaying}",
                $"sourceXbox={SourceXbox}", $"preloadSteam={PreloadSteam}", $"windowManager={WindowManager}", $"backgroundRun={BackgroundRun}",
                $"accent={Accent}", $"sounds={Sounds}", $"soundVolume={SoundVolume}", $"folders={string.Join('|', Folders)}", $"fseSteam={FseSteam}", $"navHaptics={NavHaptics}", $"libraryCover={LibraryCover}", $"shotsStyle={ShotsStyle}",
                $"soundTheme={SoundTheme}", $"musicEnabled={MusicEnabled}", $"musicVolume={MusicVolume}", $"musicTrack={MusicTrack}", $"musicLoop={MusicLoop}",
                $"lightsOn={LightsOn}", $"posterTint={PosterTint}", $"posterTintCustom={PosterTintCustom}", $"posterTintColor={PosterTintColor}", $"posterTintIntensity={PosterTintIntensity}", $"oobe={Oobe}", $"lang={Lang}",
            });
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception ex) { Log.Warn($"ui prefs save: {ex.Message}"); }
    }
}
