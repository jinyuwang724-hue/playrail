// PS5 式焦点接到各页面（见 FocusFx）：每个宿主视觉一套焦点框 + 白底，坐标就是宿主的设计坐标。
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>焦点白底上的字和图标色。</summary>
    private static readonly Color FocusInk = Gfx.Rgba(0x0c, 0x0c, 0x0e);
    // 宿主没了（游戏库换页重建）焦点框跟着回收
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ContainerVisual, FocusFx> _focusFx = new();

    private FocusFx FxFor(ContainerVisual host)
    {
        if (!_focusFx.TryGetValue(host, out var fx))
        {
            fx = new FocusFx(G);
            _focusFx.Add(host, fx);
        }
        // 每次都放回最上面：宿主之后插进来的东西（换游戏时新的大卡、重排的卡）不能盖住焦点框
        fx.Root.Parent?.Children.Remove(fx.Root);
        host.Children.InsertAtTop(fx.Root);
        return fx;
    }

    private void FxHide(ContainerVisual host, bool animate = true)
    {
        if (_focusFx.TryGetValue(host, out var fx)) fx.Hide(animate);
    }

    /// <summary>白底上的内容：把 paint 画在 rect 那一块（设计坐标）上，返回贴到 FocusFx 的画刷。</summary>
    private CompositionSurfaceBrush FxInk(float x, float y, float w, float h, float raster, Action<CanvasDrawingSession> paint)
    {
        var surface = CcDraw(w, h, raster, ds =>
        {
            ds.Transform = Matrix3x2.CreateTranslation(-x, -y) * ds.Transform;
            paint(ds);
        });
        var brush = G.C.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        return brush;
    }

    /// <summary>
    /// 设置列表（PaintRowList）的焦点：设置行没有图标，不铺白（11-19 jinyu），只有焦点框套住整行，行字本身画成粗体。
    /// 列表外面的裁剪框左右放宽，焦点框不被切掉。
    /// </summary>
    private void RowFocusFx(SpriteVisual list, List<PerfRow> rows, int focus, float listWidth, float raster, bool numbers = false, bool leadingNotes = true)
    {
        var fx = FxFor(list);
        if (focus < 0 || focus >= rows.Count || rows[focus].Section || rows[focus].Skip) { fx.Hide(); LtFocus(list, 0, 0, 0, 0, false); return; }
        float k = _ccK;
        if (list.Parent?.Clip is InsetClip ic) { ic.LeftInset = -28 * k; ic.RightInset = -28 * k; }
        float top = 0;
        for (int i = 0; i < focus; i++) top += rows[i].Section ? PerfSecH : PerfRowH;
        const float padL = 18, padR = 16, inset = 2;
        if (LtFocus(list, padL, top + inset, listWidth + padR - padL, PerfRowH - 2 * inset, true)) { fx.Hide(false); LtWidenClip(list); return; }
        fx.To(padL, top + inset, listWidth + padR - padL, PerfRowH - 2 * inset, 12, k, false);
    }
}
