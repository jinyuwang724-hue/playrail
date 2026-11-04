// 界面音效怎么配到操作上（照 Steam 大屏幕）：每次输入（方向、A、B、LB/RB、≡）先给一个默认声音，
// 具体的页面在处理时可以换成更贴切的那个（到头、进出详情、开关开 / 关、滑块上 / 下、启动游戏……），处理完只响一次。
// 不是输入引起的（后台结果、提示条）直接响。
namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private Sfx _cue = Sfx.None;
    private float _cuePitch = 1;
    private int _cueDepth;

    /// <summary>这次输入改响这个声音（Sfx.None = 不出声）；不在输入里就直接响。</summary>
    internal void Cue(Sfx s, float pitch = 1)
    {
        if (_cueDepth > 0) { _cue = s; _cuePitch = pitch; }
        else SoundFx.Play(s, pitch: pitch);
    }

    /// <summary>一次输入：默认声音 def，处理过程中可以被 Cue 换掉，结束时响最后那个。</summary>
    private void WithCue(Sfx def, Action handle)
    {
        if (_cueDepth++ == 0) { _cue = def; _cuePitch = 1; }
        try { handle(); }
        finally
        {
            if (--_cueDepth == 0 && _cue != Sfx.None) SoundFx.Play(_cue, pitch: _cuePitch);
        }
    }

    /// <summary>箭头值需要先确认进入调整；平铺开关仍可直接切换。</summary>
    private void RowStep(PerfRow r, int d)
    {
        if (r.Disabled || r.Skip) return;
        if (r.Numeric && !SettingsRowEditing(r)) { Cue(Sfx.Bump); return; }
        if (r.Choices is { Length: 2 }) Cue(Math.Clamp(r.Choice + d, 0, 1) == r.Choice ? Sfx.Bump : r.Choice + d > 0 ? Sfx.ToggleOn : Sfx.ToggleOff);
        else Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown);
        r.Step?.Invoke(d);
    }

    /// <summary>箭头值的确认只进入 / 结束调整，不能同时执行旧的 Press 动作。</summary>
    private void RowPress(PerfRow r)
    {
        if (r.Disabled || r.Skip) return;
        if (r.Numeric)
        {
            _settingsEdit.Toggle(SettingsEditKey(r));
            SettingsPaint();
            return;
        }
        _settingsEdit.End();
        if (r.Choices is { Length: 2 }) Cue(r.Choice == 0 ? Sfx.ToggleOn : Sfx.ToggleOff);
        else if (r.Choices != null || r.Numeric) Cue(Sfx.SliderUp);
        r.Press?.Invoke();
    }

    /// <summary>Bench：把整套音效按顺序放一遍（不管设置开没开），结束时记下混音器写了多少块。</summary>
    internal void SoundTestState()
    {
        SoundFx.SetActive(true);
        PreviewSounds();
        After(10000, () => Log.Info($"sfx test: {SoundFx.Plays_} plays, engine {AudioEngine.Info()}"));
    }

    /// <summary>开机：墙第一次画出来时响一下（音效开着才响）。</summary>
    private void PlayStartupSound() => SoundFx.Play(Sfx.Startup);
}
