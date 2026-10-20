// 控制中心「拍立得」自己的一套设计语言，和「卡片」样式完全分开：
// - 相纸：暖白色的纸框，带很细的纸纹；字写在下沿，用衬线字（Noto Serif SC）和深墨色，像在相纸上手写的标注。
// - 相片：胶片色（每块一种偏灰、偏暖的颜色，像过期胶卷洗出来的），一角有一道暖色漏光，细颗粒，四周暗角；
//   图标是细线条、白色、偏小，像拍下来的一个物件。相片上不放文字。
// - 游戏库：相片色取当前选中游戏的主题色（降饱和）；外观里打开「游戏库显示封面」时这里是游戏封面。
// 卡片样式（CcPaintArt）用的是另一套更鲜亮的两色渐变，见 CcCardColors。
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private static readonly Color PaperColor = Gfx.Rgba(0xec, 0xe8, 0xe1), InkColor = Gfx.Rgba(0x2a, 0x27, 0x24);

    /// <summary>拍立得调色板：胶片色，每块一种。</summary>
    private Color CcFilmColor(CcBlock b) => _ccBlocks.IndexOf(b) switch
    {
        0 => Gfx.Mix(Gfx.Mix(b.Accent, 0.45, Gfx.Rgba(0x80, 0x78, 0x70)), 0.25, Colors.Black),
        1 => Gfx.Rgba(0xb0, 0x56, 0x3e),   // 性能：赤陶
        2 => Gfx.Rgba(0x2f, 0x66, 0x80),   // 手柄：石油蓝
        3 => Gfx.Rgba(0x72, 0x52, 0x7c),   // 显示与声音：灰紫
        4 => Gfx.Rgba(0x4a, 0x76, 0x4e),   // 系统：苔绿
        _ => Gfx.Rgba(0x5c, 0x62, 0x6b),   // 设置：石墨
    };

    private void CcPaintPolaroid(CanvasDrawingSession ds, CcBlock b, float img, float textW)
    {
        float W = CcCardW, H = _ccH;
        var rnd = new Random(b.Name.GetHashCode());
        // 相纸
        ds.FillRoundedRectangle(0, 0, W, H, 5, 5, PaperColor);
        for (int i = 0; i < 900; i++)
            ds.FillRectangle((float)rnd.NextDouble() * W, (float)rnd.NextDouble() * H, 1, 1, Gfx.WithAlpha(Colors.Black, 0.035f));
        // 相片框里一圈很淡的压痕
        ds.FillRectangle(CcPad - 1, CcPad - 1, img + 2, img + 2, Gfx.WithAlpha(Colors.Black, 0.10f));
        if (b.CoverPath == null) CcPaintFilm(ds, b, CcPad, CcPad, img, img, rnd);

        // 下沿的标注：衬线字、深墨色
        float y = CcPad + img + CcPad + CcTextTop - 2;
        using var title = TextKit.Layout(ds, b.Name, TextKit.TitleCjk, 18, textW, CanvasHorizontalAlignment.Left, 0, CcTitleLh + 2);
        ds.DrawTextLayout(title, CcPad + 2, y, InkColor);
        y += CcTitleLh + 6;
        string line = string.IsNullOrEmpty(b.Summary) ? b.Second : b.Summary;
        using var second = TextKit.Layout(ds, line, TextKit.UiB(400), 12, textW, CanvasHorizontalAlignment.Left, 0, CcSecondLh, maxLines: 1);
        ds.DrawTextLayout(second, CcPad + 2, y, Gfx.WithAlpha(InkColor, 0.55f));
    }

    private void CcPaintFilm(CanvasDrawingSession ds, CcBlock b, float x, float y, float w, float h, Random rnd)
    {
        using var clip = CanvasGeometry.CreateRectangle(ds, x, y, w, h);
        using var layer = ds.CreateLayer(1, clip);
        var tone = CcFilmColor(b);
        // 胶片底色：上亮下暗一点点
        using (var grad = new CanvasLinearGradientBrush(ds, Gfx.Mix(tone, 0.12, Colors.White), Gfx.Mix(tone, 0.25, Colors.Black))
               { StartPoint = new Vector2(x, y), EndPoint = new Vector2(x, y + h) })
            ds.FillRectangle(x, y, w, h, grad);
        // 漏光：右上角一道暖色
        using (var leak = new CanvasRadialGradientBrush(ds, Gfx.WithAlpha(Gfx.Rgba(0xff, 0xa8, 0x5c), 0.42f), Gfx.WithAlpha(Gfx.Rgba(0xff, 0x6a, 0x3d), 0))
               { Center = new Vector2(x + w * 1.02f, y - h * 0.05f), RadiusX = w * 0.55f, RadiusY = h * 0.45f })
            ds.FillRectangle(x, y, w, h, leak);
        // 颗粒
        for (int i = 0; i < 1800; i++)
        {
            float gx = x + (float)rnd.NextDouble() * w, gy = y + (float)rnd.NextDouble() * h;
            ds.FillRectangle(gx, gy, 1, 1, rnd.Next(2) == 0 ? Gfx.WithAlpha(Colors.White, 0.06f) : Gfx.WithAlpha(Colors.Black, 0.09f));
        }
        // 暗角
        using (var vignette = new CanvasRadialGradientBrush(ds, Gfx.WithAlpha(Colors.Black, 0), Gfx.WithAlpha(Colors.Black, 0.38f))
               { Center = new Vector2(x + w / 2, y + h / 2), RadiusX = w * 0.74f, RadiusY = h * 0.74f })
            ds.FillRectangle(x, y, w, h, vignette);
        // 细线白色图标，偏小
        using var icon = TextKit.Layout(ds, b.Icon, new FontSpec(CcIconFont, 300), 52, 300, CanvasHorizontalAlignment.Left);
        var ib = icon.DrawBounds;
        ds.DrawTextLayout(icon, x + w / 2 - (float)(ib.X + ib.Width / 2), y + h / 2 - (float)(ib.Y + ib.Height / 2), Gfx.WithAlpha(Colors.White, 0.88f));
    }
}
