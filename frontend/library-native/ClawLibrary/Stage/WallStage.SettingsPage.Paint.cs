// 设置页左栏的画法：和性能、显示与声音页同一套字号和颜色，不加底色块和卡片。
// 一行一个分类名；当前分类亮，其余灰；焦点在左栏时，当前分类前面是和右栏列表一样的 ▶ 焦点标记。
// 右栏直接用各页共用的 PaintRowList。
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private void PaintStNav(CanvasDrawingSession ds, float h)
    {
        for (int i = 0; i < StCats.Length; i++)
        {
            float cy = PerfSecH + i * StCatH + StCatH / 2;   // 和右栏第一行对齐（右栏先有一个节头）
            bool cur = i == _stCat;
            if (cur && _stZone == 0)
            {
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure(1, cy - 6); pb.AddLine(9, cy); pb.AddLine(1, cy + 6); pb.EndFigure(CanvasFigureLoop.Closed);
                using var g = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(g, CcText);
            }
            PText(ds, StCats[i], TextKit.Ui(400), 17, Gfx.WithAlpha(CcText, cur ? 1f : 0.45f), 36, cy, PerfLeftW - 40);
        }
        if (_stAgentDown) PText(ds, "连不上工具箱后台", TextKit.Ui(400), 13, Gfx.WithAlpha(CcText, 0.5), 0, h - 12, PerfLeftW);
    }
}
