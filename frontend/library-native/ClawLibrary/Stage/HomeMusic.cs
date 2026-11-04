using ClawLibrary.Data;

namespace ClawLibrary.Stage;

/// <summary>与界面音效分组的音乐通道（同一个 AudioEngine）；前台才播放，离开或启动游戏时淡出并暂停。</summary>
public sealed class HomeMusic : IDisposable
{
    private const int FadeInMs = 900, FadeOutMs = 450;
    private string? _requested, _loaded;
    private bool _enabled, _loop = true, _open, _playing, _disposed, _broken;
    private int _volume;
    private float _target = -1;
    private long _holdUntil;
    public event Action<string>? Failed;

    public void Configure(bool enabled, int volume, string path, bool loop = true)
    {
        if (_disposed) return;
        if (enabled && !_enabled && _broken) { _loaded = null; _broken = false; }
        _enabled = enabled; _volume = Math.Clamp(volume, 0, 100); _requested = path;
        if (_loop != loop) { _loop = loop; AudioEngine.MusicLoop(loop); }
    }

    public void HoldForLaunch() => _holdUntil = Environment.TickCount64 + 6000;

    // Called on the UI dispatcher at 100 ms intervals, including while the window is inactive.
    // Fades run inside the engine; this only decides the target and opens / pauses tracks once silent.
    public void Tick(bool foreground)
    {
        if (_disposed) return;
        bool switching = _requested != _loaded || (_enabled && !_open && !_broken);
        bool allowed = HomeAudioPolicy.CanPlay(_enabled, _volume, foreground, Environment.TickCount64 < _holdUntil);
        float target = allowed && !switching && !_broken && _open ? _volume / 100f : 0;
        if (target != _target)
        {
            AudioEngine.FadeMusic(target, target > _target ? FadeInMs : FadeOutMs);
            _target = target;
            if (target > 0 && !_playing) { AudioEngine.PlayMusic(); _playing = true; }
        }
        // 不循环的曲子放完后不再被混音，淡出停在原处：按已静音处理
        if (target > 0 || (AudioEngine.MusicPlaying && AudioEngine.MusicLevel > .001f)) return;
        if (_playing) { AudioEngine.PauseMusic(); _playing = false; }
        if (!switching) return;
        AudioEngine.CloseMusic();
        _open = false; _loaded = _requested; _broken = false; _target = -1;
        if (!_enabled) return;
        try
        {
            if (_loaded == null || !File.Exists(_loaded)) throw new IOException("音乐文件已不存在，请重新选择");
            AudioEngine.OpenMusic(_loaded, _loop);
            _open = true;
        }
        catch (Exception ex) { _broken = true; Failed?.Invoke(ex.Message); }
    }

    /// <summary>导入时的解码校验（不碰播放设备）。</summary>
    public static Task ValidateAsync(string path) => Task.Run(() => { AudioEngine.Probe(path); });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AudioEngine.CloseMusic();
    }
}
