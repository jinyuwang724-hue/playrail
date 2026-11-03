// 墙面外观：folia 的「海报灯光」（开灯 / 关灯）与「海报叠色」（wall.css + bravaisAppearance.css，自己实现）。
// - 叠色：每张卡上一层主题渐变（或自定义纯色），强度默认 50 %；焦点 / 展开的那张不染；关灯时归零。
// - 关灯：每张卡上一层 82 % 黑（连标题、角标一起盖住），焦点 / 展开的那张不熄；集合的叠页边同样混进去。
//   背景另盖同强度的黑层，覆盖整屏空隙；位于封面下方，避免把高亮卡再次压暗。
//   我们的缝是「始终透明」的那种：不往上涂黑，只把里面的内容和边缘标签压到 0.4；焦点在缝里（含搜索、设置）时整条亮着。
// 开关在 设置 › 外观 › 墙面（海报灯光）；键盘 L。
namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private readonly CssTransition _seamLightsT;
    private readonly CssTransition _backgroundLightsT;

    internal WallLook Look => new(
        _prefs.LightsOn, _prefs.PosterTint, _prefs.PosterTintCustom,
        Gfx.Hex(_prefs.PosterTintColor, Gfx.Hex(UiPrefs.DefaultTintColor, Theme.Bg)), _prefs.PosterTintIntensity / 100.0);

    /// <summary>叠色可选的颜色（folia 是取色器；手柄上换成一排色板，第一个是 folia 的默认色）。</summary>
    internal static readonly (string Name, string Hex)[] TintSwatches =
    {
        ("墨", UiPrefs.DefaultTintColor), ("黑", "#000000"), ("深蓝", "#0f172a"), ("深绿", "#0b1f17"), ("酒红", "#2a0f17"), ("紫", "#1e1033"),
    };

    /// <summary>设置变了：所有卡、缝按新的外观过渡（260 ms）。</summary>
    private void ApplyWallLook()
    {
        foreach (var t in _tiles.Values) t.ApplyLook();
        var raisedKey = ScreensaverActive ? _screensaverLitKey : FocusKey;
        if (raisedKey is { } key && _tiles.TryGetValue(key, out var raised)) BringToFront(raised);
        UpdateSeamLights();
    }

    public void ToggleLights()
    {
        SaveUi(_prefs with { LightsOn = !_prefs.LightsOn });
        Cue(_prefs.LightsOn ? Sfx.ToggleOn : Sfx.ToggleOff);
        ApplyWallLook();
    }

    /// <summary>Bench：设置 › 外观，焦点放在「海报灯光」上（对齐检查用）。</summary>
    internal void LookTestState()
    {
        OpenSettingsFromLibrary();
        _stCat = 2;
        _stZone = 1;
        Repaint();
        _stFocus = Math.Max(0, _stRows.FindIndex(r => r.Title == "海报灯光"));
        Repaint();
    }

    /// <summary>缝的熄灯：焦点在缝里（竖条、搜索、设置）时豁免。</summary>
    private void UpdateSeamLights(bool animate = true)
    {
        bool exempt = _railFocused || _searchOpen || _railModel.SettingsOpen;
        _seamLightsT.To(!_prefs.LightsOn && !exempt ? (float)Engine.Motion.LightsOutSeamFade : 1, Engine.Motion.TintMs, Engine.Bezier.Ease_, NowMs, animate && !Reduced);
        _backgroundLightsT.To(ScreensaverActive || !_prefs.LightsOn ? (float)Engine.Motion.LightsOutOpacity : 0,
            Engine.Motion.TintMs, Engine.Bezier.Ease_, NowMs, animate && !Reduced);
    }
}
