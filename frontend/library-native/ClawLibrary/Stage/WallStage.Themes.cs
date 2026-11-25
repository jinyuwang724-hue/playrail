// 主题（11-25 jinyu）：三套界面各自定版一套固定的样子，不再在设置里零碎地挑首页样式、背景、控制中心样式；
// 换主题 = 整套界面一起换：
//   Folia     海报墙首页、隐现背景（从封面抽出颜色做的动态背景）、拍立得控制中心、截图页「放映」；
//   复古卡带   展台首页（第一张封面放大、其余正常大小）、拼盘控制中心、截图页「冲印」；
//   Cocoon    单行首页、左右侧栏、Cocoon 样式的功能页（以后可能加深色模式）。
// 设置 › 外观与主题只显示当前主题自己的可改项。
namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum UiTheme { Folia, Cases, Cocoon }
    private static readonly string[] UiThemeNames = { "Folia", "复古卡带", "Cocoon" };

    private UiTheme CurrentTheme => _prefs.Home switch
    {
        HomeStyle.Row => UiTheme.Cocoon,
        HomeStyle.Wall => UiTheme.Folia,
        _ => UiTheme.Cases,
    };

    /// <summary>换主题：首页样式、背景、控制中心样式、截图页排版一起换（控制中心开着时首页等回去再切）。</summary>
    private void ApplyTheme(UiTheme theme)
    {
        if (theme == CurrentTheme) return;
        Log.Info($"theme → {theme}");
        switch (theme)
        {
            case UiTheme.Folia:
                _backgroundSettings = _backgroundSettings with { Kind = BackgroundKind.Latent };
                ApplyBackgroundSettings();
                _ccBento = false; SetCcCardStyle(false); SaveCcStyle(); CcBentoApply();
                ShSetStyle(ShStyle.Projector);
                CsSetHomeStyle(HomeStyle.Wall, true);
                break;
            case UiTheme.Cases:
                _ccBento = true; SetCcCardStyle(false); SaveCcStyle(); CcBentoApply();
                ShSetStyle(ShStyle.Prints);
                CsSetHomeStyle(HomeStyle.Stand, true);
                break;
            default:
                CsSetHomeStyle(HomeStyle.Row, true);
                break;
        }
        Toast($"主题：{UiThemeNames[(int)theme]}");
    }
}
