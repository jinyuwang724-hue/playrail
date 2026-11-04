// 界面音效：一套签名音色（tools/Compose-UiSounds.mjs 合成，48 kHz / 24 位立体声），按 Steam 大屏幕的事件划分。
// 声音只用三种材料：触（移动）、光（状态变化，上滑 = 进 / 开、下滑 = 退 / 关）、气（空间变化）；提示 / 成功用玻璃铃、出错用闷嗡。
// 播放走 AudioEngine（miniaudio）：音效整段解码进内存，可叠、可变调、可摆声相（导航跟着焦点在屏幕上的位置）。
namespace ClawLibrary.Stage;

public enum Sfx
{
    None, Nav, Bump, Activate, Back, DetailIn, DetailOut, Launch, Tab, MenuIn, MenuOut, ModalShow, ModalHide,
    ToggleOn, ToggleOff, SliderUp, SliderDown, Toast, Positive, Error, Typing, Startup, Shutter,
}

public static class SoundFx
{
    /// <summary>Sfx 顺序对应的文件名（Sfx.None 不对应文件）。</summary>
    private static readonly string[] Files =
    {
        "", "nav", "bump", "activate", "back", "detail_in", "detail_out", "launch", "tab", "menu_in", "menu_out", "modal_show", "modal_hide",
        "toggle_on", "toggle_off", "slider_up", "slider_down", "toast", "positive", "error", "typing", "startup", "shutter",
    };

    private static readonly bool[] _loaded = new bool[Files.Length];
    private static int _volume = -1;
    private static volatile bool _active = true;
    private static long _lastNav;
    private static bool _navFlip;
    public static bool Enabled;
    /// <summary>放出去的次数（测试用：确认引擎真的在出声）。</summary>
    public static long Plays_;
    private static readonly Random _rng = new();

    public static void Configure(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        // 音量是响度刻度（平方），40 % 听起来是一半左右，0 静音；总线软限幅兜住叠音
        AudioEngine.SfxVolume((float)Math.Pow(volume / 100.0, 2) * 1.6f);
        if (_volume < 0)
            for (int i = 1; i < Files.Length; i++)
                _loaded[i] = AudioEngine.LoadSfx(i, Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "Sfx", "claw", Files[i] + ".wav"));
        _volume = volume;
    }

    /// <summary>游戏库不在前台：正在响的声音立刻停掉，之后不再出声。</summary>
    public static void SetActive(bool active)
    {
        if (_active && !active) AudioEngine.StopSfx();
        _active = active;
    }

    /// <param name="pitch">变调（1 = 原调；播放速度跟着变）。</param>
    /// <param name="pan">声相 −1（左）..1（右）。</param>
    public static void Play(Sfx kind, bool preview = false, float pitch = 1, float pan = 0)
    {
        if (kind == Sfx.None || (!Enabled && !preview) || !_active || _volume <= 0 || !_loaded[(int)kind]) return;
        if (kind == Sfx.Nav)
        {
            // 连按 / 摇杆连发：30 ms 内只响一次；相邻两下音高错开一点点，听着不像机关枪
            long now = Environment.TickCount64;
            if (now - _lastNav < 30) return;
            _lastNav = now;
            _navFlip = !_navFlip;
            pitch *= _navFlip ? 1f : 1.03f;
        }
        if (kind == Sfx.Typing) pitch *= 0.94f + (float)_rng.NextDouble() * 0.12f;
        AudioEngine.PlaySfx((int)kind, pitch, pan);
        Plays_++;
    }

    public static void Dispose()
    {
        Enabled = false;
        AudioEngine.StopSfx();
        Array.Clear(_loaded);
        _volume = -1;
    }
}
