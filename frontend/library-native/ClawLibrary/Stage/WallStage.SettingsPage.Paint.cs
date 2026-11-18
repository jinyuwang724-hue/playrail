// 设置页左栏的画法：和性能、显示与声音页同一套字号和颜色，不加底色块和卡片。
// 对齐规则：
// - 横向：分类名的左边和页标题「设置」、其他页左栏读数的左边对齐（PerfLeftX）；焦点三角画在左边的页边距里（StGutter）。
// - 纵向：顶上一行小字节头「分类」和右栏第一个节头同一条基线；每个分类和右栏的行一样高（PerfRowH），
//   第一个分类和右栏第一行对齐。
// 右栏直接用各页共用的 PaintRowList。
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const float StGutter = 22;

    /// <summary>ink：只画焦点分类，黑色粗体（叠在 FocusFx 的白底上）；平时一律细体，焦点不再画 ▸。</summary>
    private void PaintStNav(CanvasDrawingSession ds, float h, bool ink = false)
    {
        float x = StGutter;
        if (ink) { PText(ds, StCats[_stCat], TextKit.Ui(600), 17, FocusInk, x, PerfSecH + _stCat * StCatH + StCatH / 2, StLeftW - 10); return; }
        PText(ds, "分类", TextKit.Ui(500), 13, Gfx.WithAlpha(CcText, 0.42), x, PerfSecH - 10, 200);
        for (int i = 0; i < StCats.Length; i++)
        {
            float cy = PerfSecH + i * StCatH + StCatH / 2;
            bool cur = i == _stCat;
            PText(ds, StCats[i], TextKit.Ui(cur && _stZone == 0 ? (ushort)600 : (ushort)300), 17, Gfx.WithAlpha(CcText, cur ? 1f : 0.45f), x, cy, StLeftW - 10);
        }
        if (_stAgentDown) PText(ds, "连不上工具箱后台", TextKit.Ui(400), 13, Gfx.WithAlpha(CcText, 0.5), x, h - 12, StLeftW);
    }
}
