// 从左侧栏进的全屏页用浅色：控制中心字色变墨色（CcText），动态背景上盖一层浅色底；页面本身照 Cocoon 画（WallStage.CocoonPages.cs）。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>控制中心此刻用浅色皮（从左侧栏进的页面）。</summary>
    private bool _ccLight;
    private static Color LtInk => Ck(Color.FromArgb(255, 0x1E, 0x1E, 0x24));
    private static Color LtAccent => CocoonPalette.Current.Deep;
    private SpriteVisual? _ccLightBg;

    /// <summary>浅色底：盖在动态背景上面、页面下面。</summary>
    private void LtApplyBg()
    {
        if (_cc == null) return;
        if (_ccLightBg == null)
        {
            var g = G.C.CreateLinearGradientBrush();
            g.StartPoint = new Vector2(0, 0); g.EndPoint = new Vector2(0, 1);
            // 11-28：暗色模式进页面闪白——这层浅色底先露出来、Cocoon 页面再淡入盖住它；跟着明暗换色（CkStop 登记，切换时一起改）
            g.ColorStops.Add(CkStop(0, Color.FromArgb(255, 0xF6, 0xF6, 0xF9)));
            g.ColorStops.Add(CkStop(1, Color.FromArgb(255, 0xE8, 0xE8, 0xEE)));
            _ccLightBg = G.Sprite(g);
            _cc.Children.InsertAbove(_ccLightBg, _ccDim);
        }
        _ccLightBg.IsVisible = _ccLight;
    }
}
