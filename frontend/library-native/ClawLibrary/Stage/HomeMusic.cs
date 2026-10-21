using ClawLibrary.Data;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ClawLibrary.Stage;

/// <summary>与界面音效独立的音乐通道；前台才播放，离开或启动游戏时淡出并暂停。</summary>
public sealed class HomeMusic : IDisposable
{
    private readonly MediaPlayer _player = new() { AutoPlay = false, IsLoopingEnabled = true, Volume = 0, AudioCategory = MediaPlayerAudioCategory.GameMedia };
    private MediaSource? _source;
    private string? _requested, _loaded;
    private bool _enabled, _playing, _disposed;
    private volatile bool _broken;
    private int _volume;
    private double _gain;
    private long _holdUntil;
    public event Action<string>? Failed;

    public HomeMusic()
    {
        _player.CommandManager.IsEnabled = false;
        _player.MediaFailed += (_, e) => { _broken = true; Failed?.Invoke(e.ErrorMessage); };
    }

    public void Configure(bool enabled, int volume, string path, bool loop = true)
    {
        if (_disposed) return;
        if (enabled && !_enabled && _broken) { _loaded = null; _broken = false; }
        _enabled = enabled; _volume = Math.Clamp(volume, 0, 100); _requested = path;
        _player.IsLoopingEnabled = loop;
    }

    public void HoldForLaunch() => _holdUntil = Environment.TickCount64 + 6000;

    // Called on the UI dispatcher at 100 ms intervals, including while the window is inactive.
    public void Tick(bool foreground)
    {
        if (_disposed) return;
        bool switching = _requested != _loaded || (_enabled && _source == null && !_broken);
        bool allowed = HomeAudioPolicy.CanPlay(_enabled, _volume, foreground, Environment.TickCount64 < _holdUntil);
        double target = allowed && !switching && !_broken ? _volume / 100.0 : 0;
        _gain += (target - _gain) * .38;
        if (Math.Abs(_gain - target) < .001) _gain = target;
        _player.Volume = Math.Clamp(_gain, 0, 1);
        if (_gain == 0)
        {
            if (_playing) { _player.Pause(); _playing = false; }
            if (switching)
            {
                _player.Source = null;
                _source?.Dispose(); _source = null;
                _loaded = _requested; _broken = false;
                if (!_enabled) return;
                try
                {
                    if (_loaded == null || !File.Exists(_loaded)) throw new IOException("音乐文件已不存在，请重新选择");
                    _source = MediaSource.CreateFromUri(new Uri(_loaded));
                    _player.Source = _source;
                }
                catch (Exception ex) { _broken = true; Failed?.Invoke(ex.Message); }
            }
        }
        else if (target > 0 && !_playing && _source != null)
        {
            _player.Play(); _playing = true;
        }
    }

    public static async Task ValidateAsync(string path)
    {
        using var player = new MediaPlayer { AutoPlay = false, Volume = 0 };
        player.CommandManager.IsEnabled = false;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => done.TrySetResult(true);
        player.MediaFailed += (_, e) => done.TrySetException(new IOException("无法解码这个音频：" + e.ErrorMessage));
        using var source = MediaSource.CreateFromUri(new Uri(path));
        player.Source = source;
        await done.Task.WaitAsync(TimeSpan.FromSeconds(12));
        player.Source = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.Pause(); _player.Source = null; _source?.Dispose(); _player.Dispose();
    }
}
