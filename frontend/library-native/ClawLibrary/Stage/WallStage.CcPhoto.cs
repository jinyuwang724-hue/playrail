// 控制中心「拍立得」自己的设计语言，和「卡片」样式分开：
// - 卡片：整张铺满，板块色斜向渐变 + 右上柔光 + 白色大图标（亮、像海报）。
// - 拍立得：相纸框里的一张相片，相片是哑光的胶片底色（板块色压暗、去掉一半饱和度，平涂不做渐变），
//   上面一层很细的颗粒，四周轻微暗角；图标是同一色系里提亮的一档，线条细、居中偏小，像压在相片上的印记。
//   相片上不放文字，块名和一行摘要写在相纸下沿。
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
    private void CcPaintFilm(CanvasDrawingSession ds, CcBlock b, float x, float y, float w, float h, float r)
    {
        using var clip = CanvasGeometry.CreateRoundedRectangle(ds, x, y, w, h, r, r);
        using var layer = ds.CreateLayer(1, clip);
        var accent = b.Accent.A == 0 ? Gfx.Rgba(0x71, 0x71, 0x7a) : b.Accent;
        var muted = Gfx.Mix(accent, 0.5, Gfx.Rgba(0x80, 0x80, 0x80));   // 去掉一半饱和度
        var tone = Gfx.Mix(Theme.Bg, 0.42, muted);                       // 哑光底色
        ds.FillRectangle(x, y, w, h, tone);

        // 胶片颗粒：固定种子，每次画出来一样（不会闪）
        var rnd = new Random(b.Name.GetHashCode());
        for (int i = 0; i < 1600; i++)
        {
            float gx = x + (float)rnd.NextDouble() * w, gy = y + (float)rnd.NextDouble() * h;
            ds.FillRectangle(gx, gy, 1, 1, rnd.Next(2) == 0 ? Gfx.WithAlpha(Colors.White, 0.05f) : Gfx.WithAlpha(Colors.Black, 0.08f));
        }
        using (var vignette = new CanvasRadialGradientBrush(ds, Gfx.WithAlpha(Colors.Black, 0), Gfx.WithAlpha(Colors.Black, 0.32f))
               { Center = new Vector2(x + w / 2, y + h / 2), RadiusX = w * 0.72f, RadiusY = h * 0.72f })
            ds.FillRectangle(x, y, w, h, vignette);

        // 同色系提亮一档的图标
        var ink = Gfx.WithAlpha(Gfx.Mix(muted, 0.55, Colors.White), 0.85f);
        using var icon = TextKit.Layout(ds, b.Icon, new FontSpec(CcIconFont, 400), 56, 300, CanvasHorizontalAlignment.Left);
        var ib = icon.DrawBounds;
        ds.DrawTextLayout(icon, x + w / 2 - (float)(ib.X + ib.Width / 2), y + h / 2 - (float)(ib.Y + ib.Height / 2), ink);
    }
}
