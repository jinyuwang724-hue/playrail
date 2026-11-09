using System.Text.Json.Nodes;
using ClawLibrary.Data;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private HomeMusic? _homeMusic;
    private MusicLibrary? _musicLibrary;
    private DispatcherQueueTimer? _audioTimer;
    private bool _stBrowseMusic, _musicImporting, _audioDisposed;
    private int _soundPreviewVersion;
    private string? _musicError;
    private MusicLibrary MusicFiles => _musicLibrary ??= new MusicLibrary(
        Path.Combine(AppContext.BaseDirectory, "Assets", "Audio"), Path.Combine(App.CacheDir, "music"));

    private void StartHomeAudio()
    {
        try
        {
            _homeMusic = new HomeMusic();
            _homeMusic.Failed += message => _dq.TryEnqueue(() =>
            {
                if (_audioDisposed) return;
                _musicError = "播放失败，请重新选择曲目";
                Log.Warn("home music: " + message);
                if (_stOpen && _gsItem == null && _stCat == 5) { Toast(_musicError); Repaint(); }
            });
            _audioTimer = _dq.CreateTimer();
            _audioTimer.Interval = TimeSpan.FromMilliseconds(100);
            _audioTimer.Tick += (_, _) =>
            {
                try
                {
                    bool active = IsForeground(); SoundFx.SetActive(active); _homeMusic.Tick(active);
                    // 一秒没声音就停设备；两路都关、设备也停了，定时器也歇着，下次出声时 Woke 再叫醒
                    if (!AudioEngine.Idle(1000) && !_prefs.Sounds && !_prefs.MusicEnabled) _audioTimer?.Stop();
                }
                catch (Exception ex) { Log.Warn("audio tick: " + ex.Message); _audioTimer?.Stop(); }
            };
            AudioEngine.Woke += () => _dq.TryEnqueue(() => { if (!_audioDisposed) _audioTimer?.Start(); });
            ApplyHomeAudio();
            _ = ConvertLegacyMusic();
            if (Program.Args.Contains("--audio-probe")) _ = ProbeHomeAudio();
        }
        catch (Exception ex) { _musicError = "音乐播放器暂不可用"; Log.Warn("home audio init: " + ex.Message); }
    }

    private void ApplyHomeAudio()
    {
        SoundFx.Enabled = _prefs.Sounds;
        SoundFx.Configure(_prefs.SoundVolume);
        SoundFx.SetActive(IsForeground());
        _homeMusic?.Configure(_prefs.MusicEnabled, _prefs.MusicVolume, MusicFiles.Resolve(_prefs.MusicTrack).Path, _prefs.MusicLoop);
        // The tick stops itself once both channels are off and the device has gone idle (after any fade-out).
        _audioTimer?.Start();
    }

    /// <summary>旧版导入的 M4A / AAC / WMA 副本转成引擎能解的 MP3，选中的曲目跟着换 id。</summary>
    private async Task ConvertLegacyMusic()
    {
        try
        {
            var moved = await MusicFiles.ConvertLegacyAsync();
            if (moved.Count == 0 || _audioDisposed) return;
            Log.Info($"music: converted {moved.Count} imported track(s) to MP3");
            if (moved.TryGetValue(_prefs.MusicTrack, out var id)) { _musicError = null; SaveUi(_prefs with { MusicTrack = id }); ApplyHomeAudio(); }
            if (_stOpen && _gsItem == null && _stCat == 5) Repaint();
        }
        catch (Exception ex) { Log.Warn("music convert: " + ex.Message); }
    }

    private void SaveAudio(UiPrefs next)
    {
        _musicError = null;
        SaveUi(next); ApplyHomeAudio(); Repaint();
    }

    private void PreviewSounds()
    {
        int version = ++_soundPreviewVersion;
        var sequence = new[] { Sfx.Nav, Sfx.Nav, Sfx.Bump, Sfx.Activate, Sfx.DetailIn, Sfx.DetailOut, Sfx.Back, Sfx.Tab, Sfx.ToggleOn, Sfx.ToggleOff, Sfx.SliderUp, Sfx.SliderDown, Sfx.MenuIn, Sfx.MenuOut, Sfx.ModalShow, Sfx.ModalHide, Sfx.Toast, Sfx.Positive, Sfx.Error, Sfx.Shutter, Sfx.Launch };
        for (int i = 0; i < sequence.Length; i++)
        {
            Sfx cue = sequence[i];
            After(i * 420, () => { if (!_audioDisposed && version == _soundPreviewVersion && IsForeground()) { SoundFx.SetActive(true); SoundFx.Play(cue, preview: true); } });
        }
    }

    private void BuildAudioRows()
    {
        _stRows.Add(new PerfRow { Section = true, Title = "界面音效" });
        _stRows.Add(StToggle("界面音效", _prefs.Sounds, on => { SaveAudio(_prefs with { Sounds = on }); }, false));
        var volume = StNumber("音效音量", _prefs.SoundVolume, 0, 100, 5, n => $"{n} %", n =>
        {
            SaveAudio(_prefs with { SoundVolume = n }); Cue(Sfx.None); SoundFx.Play(Sfx.Nav, preview: true);
        }, 40);
        _stRows.Add(volume);
        _stRows.Add(new PerfRow { Title = "试听音效", Value = "", Press = () => { Cue(Sfx.None); PreviewSounds(); } });

        _stRows.Add(new PerfRow { Section = true, Title = "背景音乐" });
        _stRows.Add(StToggle("背景音乐", _prefs.MusicEnabled, on => SaveAudio(_prefs with { MusicEnabled = on }), false));
        _stRows.Add(StNumber("音乐音量", _prefs.MusicVolume, 0, 100, 5, n => $"{n} %", n => SaveAudio(_prefs with { MusicVolume = n }), 20));
        var tracks = MusicFiles.Tracks();
        int index = Math.Max(0, tracks.ToList().FindIndex(t => t.Id == _prefs.MusicTrack));
        void Select(int n) { n = Math.Clamp(n, 0, tracks.Count - 1); SaveAudio(_prefs with { MusicTrack = tracks[n].Id }); }
        string name = tracks[index].Name;
        _stRows.Add(new PerfRow
        {
            Title = "音乐曲目", Value = name.Length > 20 ? name[..19] + "…" : name, Numeric = true,
            Changed = tracks[index].Id != MusicLibrary.DefaultId,
            Step = d => Select(index + d), Press = () => Select((index + 1) % tracks.Count), Reset = () => Select(0),
        });
        _stRows.Add(StToggle("循环播放", _prefs.MusicLoop, on => SaveAudio(_prefs with { MusicLoop = on }), true));
        _stRows.Add(new PerfRow
        {
            Title = "导入本地音乐", Value = _musicImporting ? "正在导入…" : "", Disabled = _musicImporting,
            Press = () => { _stBrowseExe = false; _stBrowseMusic = true; StBrowseTo(""); },
        });
        if (tracks[index].Custom)
        {
            var selected = tracks[index];
            _stRows.Add(StAction("移除所选音乐", "music-remove", "仅移除导入的副本", () =>
            {
                if (!StConfirm("music-remove")) return;
                SaveAudio(_prefs with { MusicTrack = MusicLibrary.DefaultId });
                After(1800, () =>
                {
                    try { MusicFiles.Remove(selected); Toast("已移除音乐副本"); }
                    catch (Exception ex) { Toast("没能移除：" + ex.Message); }
                    if (_stOpen) Repaint();
                });
            }));
        }
        if (_musicError != null) _stRows.Add(StInfo("音乐状态", _musicError));
        else _stRows.Add(StInfo("播放范围", "仅前台播放 · 游戏中淡出"));
    }

    private void BuildMusicBrowse(string path)
    {
        _stRows.Add(new PerfRow { Title = "‹ 返回上一级", Value = "B", Press = StBack });
        try
        {
            foreach (var folder in Directory.GetDirectories(path).Where(d => (new DirectoryInfo(d).Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).Take(400))
                _stRows.Add(new PerfRow { Title = Path.GetFileName(folder), Value = "›", Press = () => StBrowseTo(folder) });
            var files = Directory.GetFiles(path).Where(MusicLibrary.Supports).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).Take(400).ToArray();
            foreach (var file in files)
                _stRows.Add(new PerfRow { Title = Path.GetFileNameWithoutExtension(file), Value = Path.GetExtension(file).TrimStart('.').ToUpperInvariant(), Disabled = _musicImporting, Press = () => _ = ImportHomeMusic(file) });
            if (files.Length == 0) _stRows.Add(StInfo("没有可导入的音频", "可进入子文件夹继续查找"));
        }
        catch (Exception ex) { _stRows.Add(StInfo("打不开这个文件夹", ex.Message)); }
    }

    private async Task ImportHomeMusic(string source)
    {
        if (_musicImporting) return;
        _musicImporting = true; HomeTrack? imported = null;
        if (_stOpen) Repaint();
        try
        {
            imported = await MusicFiles.ImportAsync(source);
            await HomeMusic.ValidateAsync(imported.Path);
            if (_audioDisposed) return;
            SaveUi(_prefs with { MusicTrack = imported.Id });
            _musicError = null; ApplyHomeAudio();
            if (_stOpen && _stBrowseMusic) StBrowseTo(null);
            Toast($"已导入「{imported.Name}」"); SoundFx.Play(Sfx.Positive);
        }
        catch (Exception ex)
        {
            if (imported != null) try { MusicFiles.Remove(imported); } catch { }
            if (!_audioDisposed) { Toast("导入失败：" + (ex is TimeoutException ? "读取音频超时，请换一个文件" : ex.Message)); SoundFx.Play(Sfx.Error); }
        }
        finally { _musicImporting = false; if (!_audioDisposed && _stOpen) Repaint(); }
    }

    private async Task ProbeHomeAudio()
    {
        var report = new JsonObject();
        foreach (var track in MusicFiles.Tracks().Where(t => !t.Custom))
            try { await HomeMusic.ValidateAsync(track.Path); report[track.Id] = "decoded"; }
            catch (Exception ex) { report[track.Id] = ex.Message; }
        report["loopEnabled"] = _prefs.MusicLoop;
        report["foregroundPolicy"] = !HomeAudioPolicy.CanPlay(true, 20, false, false);
        report["engine"] = AudioEngine.Info();
        File.WriteAllText(Path.Combine(App.CacheDir, "audio-probe.json"), report.ToJsonString());
        Log.Info("audio probe: " + report.ToJsonString());
    }

    private void DisposeHomeAudio()
    {
        _audioDisposed = true; ++_soundPreviewVersion;
        _audioTimer?.Stop(); _homeMusic?.Dispose(); SoundFx.Dispose(); AudioEngine.Close();
    }
}
