// 从左侧栏进的全屏页（性能 / 显示与声音 / 系统 / 设置）换成 Cocoon 浅色样式（11-24 jinyu）：
// 浅灰竖向渐变底（盖住控制中心的动态背景）、左右两栏各一张白色圆角卡、墨色字、紫色强调；
// 焦点是首页同一套四角括号（CurSet），不再是白色描边框。排版和交互不动，只换皮。
// 其他入口（别的首页样式按 ≡ 进的旧控制中心）照旧深色。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>控制中心此刻用浅色皮（从左侧栏进的页面）。</summary>
    private bool _ccLight;
    private static readonly Color LtInk = Color.FromArgb(255, 0x1E, 0x1E, 0x24), LtAccent = Color.FromArgb(255, 0x73, 0x40, 0xFF);
    private SpriteVisual? _ccLightBg;
    private readonly Dictionary<(ContainerVisual Page, int Slot), SpriteVisual> _ltCards = new();
    private readonly Dictionary<ContainerVisual, (ContainerVisual Host, CurSet Cur)> _ltCur = new();

    /// <summary>浅色底：盖在动态背景上面、页面下面。</summary>
    private void LtApplyBg()
    {
        if (_cc == null) return;
        if (_ccLightBg == null)
        {
            var g = G.C.CreateLinearGradientBrush();
            g.StartPoint = new Vector2(0, 0); g.EndPoint = new Vector2(0, 1);
            g.ColorStops.Add(G.C.CreateColorGradientStop(0, Color.FromArgb(255, 0xF6, 0xF6, 0xF9)));
            g.ColorStops.Add(G.C.CreateColorGradientStop(1, Color.FromArgb(255, 0xE8, 0xE8, 0xEE)));
            _ccLightBg = G.Sprite(g);
            _cc.Children.InsertAbove(_ccLightBg, _ccDim);
        }
        _ccLightBg.IsVisible = _ccLight;
        foreach (var c in _ltCards.Values) c.IsVisible = _ccLight;
    }

    /// <summary>页面里一张白色圆角卡（设计坐标，× _ccK），放在页面容器最底下；只在浅色皮时可见。</summary>
    private void LtCard(ContainerVisual page, int slot, float x, float y, float w, float h)
    {
        float k = _ccK;
        if (!_ltCards.TryGetValue((page, slot), out var card))
        {
            card = G.Sprite(G.Solid(Color.FromArgb(255, 0xFF, 0xFF, 0xFF)), relative: false);
            var geo = G.C.CreateRoundedRectangleGeometry();
            card.Clip = G.C.CreateGeometricClip(geo);
            page.Children.InsertAtBottom(card);
            _ltCards[(page, slot)] = card;
        }
        card.IsVisible = _ccLight;
        card.Offset = new Vector3(x * k, y * k, 0);
        card.Size = new Vector2(w * k, h * k);
        var rr = (CompositionRoundedRectangleGeometry)((CompositionGeometricClip)card.Clip).Geometry;
        rr.Size = card.Size;
        rr.CornerRadius = new Vector2(18 * k);
    }

    /// <summary>
    /// 页面里的焦点：浅色皮用四角括号（坐标换到首页的 1920 设计单位里画，粗细和首页一样），深色皮照旧 FocusFx。
    /// host 是放焦点的那张面（列表会滚，括号跟着它走）。返回 true 表示已经用括号画了。
    /// </summary>
    private bool LtFocus(ContainerVisual host, float x, float y, float w, float h, bool show)
    {
        if (!_ltCur.TryGetValue(host, out var e))
        {
            if (!_ccLight) return false;
            var box = G.Container(relative: false);
            host.Children.InsertAtTop(box);
            e = (box, CurNew(box));
            _ltCur[host] = e;
        }
        e.Host.Parent?.Children.Remove(e.Host);
        host.Children.InsertAtTop(e.Host);
        if (!_ccLight || !show) { if (e.Cur.Shown || e.Cur.Glow.Opacity > 0) CurShow(e.Cur, false, 1); return _ccLight; }
        // 页面设计单位 1280 宽，首页 1920 宽：1 页面单位 = 1.5 首页单位
        float s = _ccK / 1.5f;
        e.Host.Scale = new Vector3(s, s, 1);
        bool first = !e.Cur.Shown;
        // 括号圆角小一点：行很扁，圆角大了四个角就只剩一小段弧，没有直腿
        CurTo(e.Cur, x * 1.5f, y * 1.5f, w * 1.5f, h * 1.5f, 8, 16, move: !first);
        if (first) CurShow(e.Cur, true, 120);
        return true;
    }

    /// <summary>列表外面的裁剪框左右放宽（括号和光晕比白框宽）。</summary>
    private void LtWidenClip(ContainerVisual list)
    {
        if (_ccLight && list.Parent?.Clip is InsetClip ic) { ic.LeftInset = -70 * _ccK; ic.RightInset = -70 * _ccK; }
    }
}
