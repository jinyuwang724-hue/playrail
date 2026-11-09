// 声音引擎：clawaudio.dll（miniaudio，见 Native\ClawAudio）。界面音效和背景音乐共用一个 WASAPI 设备，分两组调音量；
// 没有声音在响一秒后设备自己停掉。打不开设备（没有声卡、被独占）时所有调用都静默不出声。
using System.Runtime.InteropServices;
using System.Text;

namespace ClawLibrary.Stage;

internal static partial class AudioEngine
{
    private const string Lib = "clawaudio";
    private static int _state;   // 0 未开，1 已开，-1 打不开

    [LibraryImport(Lib)] private static partial int ca_open();
    [LibraryImport(Lib)] private static partial void ca_close();
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] private static partial int ca_sfx_load(int id, string path);
    [LibraryImport(Lib)] private static partial int ca_sfx_play(int id, float pitch, float gain, float pan);
    [LibraryImport(Lib)] private static partial void ca_sfx_stop_all();
    [LibraryImport(Lib)] private static partial void ca_sfx_volume(float volume);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] private static partial int ca_music_open(string path, int loop);
    [LibraryImport(Lib)] private static partial void ca_music_close();
    [LibraryImport(Lib)] private static partial void ca_music_loop(int loop);
    [LibraryImport(Lib)] private static partial void ca_music_fade(float target, int ms);
    [LibraryImport(Lib)] private static partial float ca_music_level();
    [LibraryImport(Lib)] private static partial void ca_music_play();
    [LibraryImport(Lib)] private static partial void ca_music_pause();
    [LibraryImport(Lib)] private static partial int ca_music_playing();
    [LibraryImport(Lib)] private static partial int ca_idle(int ms);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)] private static partial int ca_probe(string path, out ulong lengthMs);
    [LibraryImport(Lib)] private static unsafe partial int ca_info(byte* buffer, int capacity);

    /// <summary>设备从停着变成在跑（有声音要出）时触发，用来叫醒定时空闲检查。</summary>
    public static event Action? Woke;

    private static bool Ready
    {
        get
        {
            if (_state == 0)
            {
                try
                {
                    int r = ca_open();
                    _state = r == 0 ? 1 : -1;
                    if (r == 0) Log.Info("audio engine: " + Info());
                    else Log.Warn($"audio engine: open failed ({r})");
                }
                catch (Exception ex) { _state = -1; Log.Warn("audio engine: " + ex.Message); }
            }
            return _state == 1;
        }
    }

    public static bool LoadSfx(int id, string path)
    {
        if (!Ready) return false;
        int r = ca_sfx_load(id, path);
        if (r != 0) Log.Warn($"sound load {Path.GetFileName(path)}: {r}");
        return r == 0;
    }

    public static void PlaySfx(int id, float pitch, float pan)
    {
        if (!Ready) return;
        ca_sfx_play(id, pitch, 1, Math.Clamp(pan, -1, 1));
        Woke?.Invoke();
    }

    public static void StopSfx() { if (_state == 1) ca_sfx_stop_all(); }
    public static void SfxVolume(float volume) { if (Ready) ca_sfx_volume(volume); }

    /// <summary>打开一首背景音乐（暂停、音量 0）；失败抛 IOException。</summary>
    public static void OpenMusic(string path, bool loop)
    {
        if (!Ready) throw new IOException("没有可用的音频设备");
        int r = ca_music_open(path, loop ? 1 : 0);
        if (r != 0) throw new IOException($"无法解码这个音频（{r}）");
    }

    public static void CloseMusic() { if (_state == 1) ca_music_close(); }
    public static void MusicLoop(bool loop) { if (_state == 1) ca_music_loop(loop ? 1 : 0); }
    public static void FadeMusic(float target, int ms) { if (_state == 1) ca_music_fade(target, ms); }
    public static float MusicLevel => _state == 1 ? ca_music_level() : 0;
    public static bool MusicPlaying => _state == 1 && ca_music_playing() != 0;
    public static void PlayMusic() { if (_state != 1) return; ca_music_play(); Woke?.Invoke(); }
    public static void PauseMusic() { if (_state == 1) ca_music_pause(); }

    /// <summary>空闲 ms 毫秒就停设备；返回设备是否还在跑。</summary>
    public static bool Idle(int ms) => _state == 1 && ca_idle(ms) != 0;

    /// <summary>试解码：不需要打开设备。</summary>
    public static TimeSpan Probe(string path)
    {
        int r = ca_probe(path, out ulong ms);
        if (r != 0) throw new IOException($"无法解码这个音频（{r}）");
        return TimeSpan.FromMilliseconds(ms);
    }

    public static unsafe string Info()
    {
        if (_state != 1) return _state == 0 ? "closed" : "unavailable";
        byte* buffer = stackalloc byte[256];
        int n = ca_info(buffer, 256);
        return Encoding.UTF8.GetString(buffer, Math.Clamp(n, 0, 255));
    }

    public static void Close()
    {
        if (_state == 1) ca_close();
        _state = 0;
    }
}
