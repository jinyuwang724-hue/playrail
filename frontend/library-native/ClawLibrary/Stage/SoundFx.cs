// A separate short-cue channel. Pin WAV buffers for the entire asynchronous WinMM playback lifetime.
using System.Runtime.InteropServices;
using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public enum Sfx { Move, Confirm, Back, Switch, Open, Toggle, Launch, Error }

public static class SoundFx
{
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
    private static extern bool PlaySound(nint sound, nint module, uint flags);
    private static readonly string[] Files = { "move", "confirm", "back", "switch", "open", "toggle", "launch", "error" };
    public static readonly string[] ThemeNames = { "轻触", "清透", "数码" };
    private static readonly string[] Themes = { "soft", "crystal", "digital" };
    private static GCHandle[] _clips = new GCHandle[8];
    private static int _volume = -1, _theme;
    private static bool _active = true;
    private static long _lastMove;
    public static bool Enabled;

    public static void SetVolume(int volume) => Configure(_theme, volume);
    public static void Configure(int theme, int volume)
    {
        theme = Math.Clamp(theme, 0, Themes.Length - 1); volume = Math.Clamp(volume, 0, 100);
        if (_theme == theme && _volume == volume) return;
        Release(); _theme = theme; _volume = volume;
        for (int i = 0; i < Files.Length; i++)
            try
            {
                var bytes = WavePcm.WithVolume(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "Sfx", Themes[theme], Files[i] + ".wav")), volume);
                _clips[i] = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            }
            catch (Exception ex) { Log.Warn($"sound load {Files[i]}: {ex.Message}"); }
    }

    public static void SetActive(bool active)
    {
        if (_active && !active) PlaySound(0, 0, 0);
        _active = active;
    }

    public static void Play(Sfx kind, bool preview = false)
    {
        if ((!Enabled && !preview) || !_active || _volume <= 0) return;
        if (kind == Sfx.Move && Environment.TickCount64 - _lastMove < 45) return;
        if (kind == Sfx.Move) _lastMove = Environment.TickCount64;
        if (_clips[(int)kind].IsAllocated)
            PlaySound(_clips[(int)kind].AddrOfPinnedObject(), 0, 0x1 | 0x2 | 0x4);
    }

    private static void Release()
    {
        PlaySound(0, 0, 0);
        for (int i = 0; i < _clips.Length; i++) if (_clips[i].IsAllocated) _clips[i].Free();
    }
    public static void Dispose() { Release(); _volume = -1; Enabled = false; }
}
