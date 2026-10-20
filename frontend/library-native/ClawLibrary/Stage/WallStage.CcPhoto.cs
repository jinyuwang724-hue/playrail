// 控制中心拍立得：没有自己封面的功能块，照片区放一张「相片」——最近玩过的游戏画面（每块一张，不重复），
// 去色后按板块颜色上一层单色调，四角压暗、整体略微褪色，像一张拍立得相片。照片上不放文字和图标，名字写在相片下面。
// 图按需异步读进来，读好之前照片区是板块色的暗底；读好后重画这张卡。
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using ClawLibrary.Data;
using Windows.Foundation;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private readonly Dictionary<string, CanvasBitmap?> _ccPhotos = new();

    /// <summary>这块用哪张游戏画面：按最近玩过排，跳过游戏库那张封面，第 i 块用第 i 款。</summary>
    private string? CcPhotoPath(CcBlock b)
    {
        int index = _ccBlocks.IndexOf(b);
        if (index <= 0) return null;
        var libGame = SelectedGame()?.Id;
        var recent = _lib.Games.Where(g => g.Kind == ItemKind.Game && g.Art != null && g.Id != libGame)
            .OrderByDescending(g => g.LastPlayed ?? 0).Take(12).ToList();
        if (recent.Count == 0) return null;
        var art = recent[(index - 1) % recent.Count].Art!;
        foreach (var k in new[] { 'h', 'q', 'p', 'v' })
            if (art.Has(k)) return art.File(k);
        return null;
    }

    private void CcPaintPhoto(CanvasDrawingSession ds, CcBlock b, float x, float y, float w, float h, float r)
    {
        using var clip = CanvasGeometry.CreateRoundedRectangle(ds, x, y, w, h, r, r);
        using var layer = ds.CreateLayer(1, clip);
        var accent = b.Accent.A == 0 ? Gfx.Rgba(0x71, 0x71, 0x7a) : b.Accent;
        ds.FillRectangle(x, y, w, h, Gfx.Mix(Theme.Bg, 0.25, accent));
        var path = CcPhotoPath(b);
        if (path == null) return;
        if (!_ccPhotos.TryGetValue(path, out var bmp))
        {
            _ccPhotos[path] = null;
            _ = CcLoadPhoto(path, b);
            return;
        }
        if (bmp == null) return;
        // 中间裁一块和照片区同比例的
        var size = bmp.Size;
        double ratio = w / h, sw = size.Width, sh = size.Height;
        if (sw / sh > ratio) sw = sh * ratio; else sh = sw / ratio;
        var src = new Rect((size.Width - sw) / 2, (size.Height - sh) / 2, sw, sh);
        using var gray = new GrayscaleEffect { Source = bmp };
        using var tint = new TintEffect { Source = gray, Color = Gfx.Mix(accent, 0.45, Colors.White) };
        ds.DrawImage(tint, new Rect(x, y, w, h), src, 1, CanvasImageInterpolation.HighQualityCubic);
        // 褪色：一层很淡的暖白；暗角：四周往里压暗
        ds.FillRectangle(x, y, w, h, Gfx.WithAlpha(Gfx.Rgba(0xf4, 0xef, 0xe6), 0.06f));
        using var vignette = new CanvasRadialGradientBrush(ds, Gfx.WithAlpha(Colors.Black, 0), Gfx.WithAlpha(Colors.Black, 0.45f))
        { Center = new System.Numerics.Vector2(x + w / 2, y + h / 2), RadiusX = w * 0.75f, RadiusY = h * 0.75f };
        ds.FillRectangle(x, y, w, h, vignette);
    }

    private async Task CcLoadPhoto(string path, CcBlock b)
    {
        try
        {
            var bmp = await CanvasBitmap.LoadAsync(G.Device, path);
            _ccPhotos[path] = bmp;
            foreach (var block in _ccBlocks)
                if (!block.Rounded && block.CoverPath == null && CcPhotoPath(block) == path) CcPaintCard(block, force: true);
        }
        catch (Exception ex) { Log.Warn($"cc photo {path}: {ex.Message}"); }
    }
}
