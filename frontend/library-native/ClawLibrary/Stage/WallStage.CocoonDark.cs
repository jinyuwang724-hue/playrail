// Cocoon 暗色模式（11-28 jinyu：外观与主题里一个开关；不能是纯黑）。
//   各页写死的浅色中性色统一经 Ck() 换算：浅底（页面、卡片、分隔线）→ 深灰（页面约 #1F2024、卡片约 #27282D，留一点原来的冷调），
//   深字 → 浅字，中灰（图标、键位章）落在中间。带颜色的（主题色、红色、封面）不动；主题色里的墨色和选中底色由 CocoonPalette 另算。
//   用合成画刷（页面底色渐变、纯色精灵）的地方走 CkSolid / CkStop 登记，切换时 CkRetint() 原地改色，不用重建页面。
using System;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static Color Ck(Color light) => CocoonPalette.Ck(light);

    // 强引用：合成对象的托管包装可能先被回收（原生对象还在），弱引用会把还在用的色标丢掉。页面都是缓存复用的，数量有限。
    private static readonly List<(CompositionColorBrush Brush, Color Light)> _ckBrushes = new();
    private static readonly List<(CompositionColorGradientStop Stop, Color Light)> _ckStops = new();

    /// <summary>浅色中性纯色画刷：按当前明暗取色，并登记以便切换时改色。</summary>
    private CompositionColorBrush CkSolid(Color light)
    {
        var b = G.Solid(Ck(light));
        if (CocoonPalette.IsNeutral(light)) _ckBrushes.Add((b, light));
        return b;
    }

    /// <summary>渐变色标（页面底色、顶部渐隐）：同上。</summary>
    private CompositionColorGradientStop CkStop(float pos, Color light)
    {
        var s = G.C.CreateColorGradientStop(pos, Ck(light));
        if (CocoonPalette.IsNeutral(light)) _ckStops.Add((s, light));
        return s;
    }

    /// <summary>明暗切换后：登记过的画刷和色标原地换色（已释放的顺手清掉）。</summary>
    private static void CkRetint()
    {
        _ckBrushes.RemoveAll(e => { try { e.Brush.Color = Ck(e.Light); return false; } catch (ObjectDisposedException) { return true; } });
        _ckStops.RemoveAll(e => { try { e.Stop.Color = Ck(e.Light); return false; } catch (ObjectDisposedException) { return true; } });
    }

    /// <summary>录屏自测用：设成 on（null 不改），返回原来的值。</summary>
    internal bool ProbeDark(bool? on)
    {
        bool was = _prefs.CocoonDark;
        if (on is bool v) SetCocoonDark(v);
        return was;
    }

    private void SetCocoonDark(bool on)
    {
        if (_prefs.CocoonDark == on) return;
        SaveUi(_prefs with { CocoonDark = on });
        CkRetint();
        RefreshCocoonThemeVisuals();
    }
}
