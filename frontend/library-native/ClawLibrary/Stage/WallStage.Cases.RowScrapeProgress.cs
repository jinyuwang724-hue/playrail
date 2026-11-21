// 刮削进度页（11-21 jinyu：照 Cocoon「scraper game process」视频，做我们自己的版本；刮完自己退回上一页）。
// Cocoon：顶上「B ←」、进度文字 + 细进度条；大横幅（背景图，右边渐暗，放游戏图标）、资料卡（紫色标题、平台·开发商、简介）、
// 底部一排刮到的图。我们：
//   标题栏「正在刮削」，右边是当前步骤文字和一条进度条（不放 Y 胶囊；进行中 B 不退）；
//   横幅：刮到的背景图铺满，右侧渐暗，上面放竖版封面和标志；还没取到时是浅灰底；
//   资料卡：标题（紫色）、开发商 · 发行商 · 发行日期 · 类型、简介（最多三行）；
//   图片条：封面 / 横封面 / 背景 / 标志四格，取到的显示缩略图，写进游戏库的那几张右上角打绿勾；
//   刮完：进度条满、文字变结果，停 1.6 秒自己退回刮削设置页，弹一条结果提示。
using System.Numerics;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const int ScSteps = 6;
    private static readonly Color ScPurple = Color.FromArgb(255, 0x7A, 0x4C, 0xFF);

    private RsPage ScProgressPage(ScState s, LibraryItem g) => new()
    {
        Title = "正在刮削",
        TitleIcon = "scrape",
        NoY = true,
        Build = () => new(),
        Lock = () => s.Busy,
        Move = _ => false,
        Activate = () => Cue(Sfx.Bump),
        PaintHeaderExtra = ds => ScPaintProgressBar(ds, s),
        Paint = ds => ScPaintProgress(ds, s, g),
    };

    /// <summary>标题栏右边：当前步骤（结束后是结果）和一条进度条。</summary>
    private void ScPaintProgressBar(CanvasDrawingSession ds, ScState s)
    {
        float x1 = 1784, w = 560, x0 = x1 - w;
        string text = s.Status.Length > 0 ? s.Status : "正在准备";
        using (var tf = new CanvasTextFormat { FontFamily = RmFont(text), FontSize = 32, FontWeight = RsWt(text, 500), HorizontalAlignment = CanvasHorizontalAlignment.Right, WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
            ds.DrawText(text, new Windows.Foundation.Rect(x0 - 200, 100, w + 200, 46), s.Done ? ScPurple : RmInk, tf);
        float y = 160, h = 10;
        ds.FillRoundedRectangle(x0, y, w, h, h / 2, h / 2, Color.FromArgb(255, 0xE2, 0xE3, 0xE7));
        float f = Math.Clamp(s.Step / (float)ScSteps, 0.04f, 1f);
        using var br = new CanvasLinearGradientBrush(ds.Device, RwC1, RwC2) { StartPoint = new Vector2(x0, y), EndPoint = new Vector2(x0 + w, y) };
        ds.FillRoundedRectangle(x0, y, w * f, h, h / 2, h / 2, br);
    }

    private void ScPaintProgress(CanvasDrawingSession ds, ScState s, LibraryItem g)
    {
        var dev = ds.Device;
        float x = 136, w = 1648;
        // ---- 横幅 ----
        float by = 214, bh = 380, br = 44;
        using (var banner = CanvasGeometry.CreateRoundedRectangle(dev, x, by, w, bh, br, br))
        {
            RsSoftShadow(ds, x, by + 8, w, bh, br, 14, 0.18f);
            using (var ph = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xEC, 0xED, 0xF1), Color.FromArgb(255, 0xD6, 0xD7, 0xDE)) { StartPoint = new Vector2(x, by), EndPoint = new Vector2(x + w, by + bh) })
                ds.FillGeometry(banner, ph);
            using (ds.CreateLayer(1, banner))
            {
                if (AgBitmapOf(s.ArtUrl.GetValueOrDefault("hero")) is { } hero)
                {
                    ScDrawImage(ds, hero, x, by, w, bh, contain: false);
                    using var dark = new CanvasLinearGradientBrush(dev, Color.FromArgb(0, 0x18, 0x1E, 0x28), Color.FromArgb(235, 0x18, 0x1E, 0x28)) { StartPoint = new Vector2(x + w * 0.42f, 0), EndPoint = new Vector2(x + w, 0) };
                    ds.FillRectangle(x, by, w, bh, dark);
                }
                if (AgBitmapOf(s.ArtUrl.GetValueOrDefault("logo")) is { } logo)
                    ScDrawImage(ds, logo, x + 60, by + bh - 60 - 170, 560, 170, contain: true, alignLeft: true);
            }
            // 竖版封面：刮到的，没有就用游戏库现在的
            float cw = 200, ch = 300, cx = x + w - 100 - cw, cy = by + (bh - ch) / 2;
            var cover = AgBitmapOf(s.ArtUrl.GetValueOrDefault("p")) ?? AgBitmapOf(RqArt(g));
            RsSoftShadow(ds, cx, cy + 8, cw, ch, cw * RwRound, 12, 0.35f);
            using var cr = CanvasGeometry.CreateRoundedRectangle(dev, cx, cy, cw, ch, cw * RwRound, cw * RwRound);
            ds.FillGeometry(cr, Color.FromArgb(255, 0xC8, 0xC9, 0xD0));
            if (cover != null) using (ds.CreateLayer(1, cr)) ScDrawImage(ds, cover, cx, cy, cw, ch, contain: false);
        }
        // ---- 资料卡 ----
        float iy = 620, ih = 300;
        ScCard(ds, x, iy, w, ih);
        string title = s.Got.GetValueOrDefault("title") is { Length: > 0 } t ? t : g.Title;
        using (var tf = new CanvasTextFormat { FontFamily = RmFont(title), FontSize = 52, FontWeight = RsWt(title), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
            ds.DrawText(title, new Windows.Foundation.Rect(x + 40, iy + 28, w - 80, 70), ScPurple, tf);
        string meta = string.Join(" · ", new[] { "developer", "publisher", "released", "genre" }.Select(k => s.Got.GetValueOrDefault(k)).Where(v => !string.IsNullOrWhiteSpace(v)));
        if (meta.Length == 0) meta = s.Done ? "没有刮到资料" : "正在找资料…";
        using (var mf = new CanvasTextFormat { FontFamily = RmFont(meta), FontSize = 32, FontWeight = RsWt(meta, 500), WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis })
            ds.DrawText(meta, new Windows.Foundation.Rect(x + 40, iy + 104, w - 80, 46), RsSub, mf);
        if (s.Got.GetValueOrDefault("description") is { Length: > 0 } desc)
        {
            using var df = new CanvasTextFormat { FontFamily = RmFont(desc), FontSize = 32, FontWeight = new Windows.UI.Text.FontWeight(400), WordWrapping = CanvasWordWrapping.Wrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis };
            using var lay = new CanvasTextLayout(dev, desc, df, w - 80, 136);
            ds.DrawTextLayout(lay, x + 40, iy + 160, RmInk);
        }
        // ---- 图片条 ----
        float sy = 944, sh = 160;
        ScCard(ds, x, sy, w, sh);
        float tx = x + 40, th = 104, ty = sy + 16;
        foreach (var (kind, name) in new[] { ("p", "封面"), ("header", "横封面"), ("hero", "背景"), ("logo", "标志") })
        {
            float tw = kind switch { "p" => 70, "header" => 222, _ => 250 };
            using var slot = CanvasGeometry.CreateRoundedRectangle(dev, tx, ty, tw, th, 16, 16);
            ds.FillGeometry(slot, Color.FromArgb(255, 0xE6, 0xE7, 0xEB));
            bool has = s.ArtUrl.TryGetValue(kind, out var url);
            if (has && AgBitmapOf(url) is { } bmp) using (ds.CreateLayer(1, slot)) ScDrawImage(ds, bmp, tx, ty, tw, th, contain: kind == "logo");
            string label = has ? name : s.Done ? $"{name} · 没找到" : name;
            using (var lf = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 24, FontWeight = new Windows.UI.Text.FontWeight(500), WordWrapping = CanvasWordWrapping.NoWrap })
                ds.DrawText(label, new Windows.Foundation.Rect(tx, ty + th + 4, tw + 120, 32), has ? RmInk : RsSub, lf);
            if (s.Wrote.Contains(kind))
            {
                float ccx = tx + tw - 4, ccy = ty + 4;
                ds.FillCircle(ccx, ccy, 17, Color.FromArgb(255, 0x12, 0xC9, 0xA8));
                ds.DrawCircle(ccx, ccy, 17, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 3);
                using var pb = new CanvasPathBuilder(dev);
                pb.BeginFigure(ccx - 7, ccy); pb.AddLine(ccx - 2, ccy + 5); pb.AddLine(ccx + 8, ccy - 5); pb.EndFigure(CanvasFigureLoop.Open);
                using var tick = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(tick, Color.FromArgb(255, 0xFA, 0xFA, 0xFA), 4, RwRoundStroke);
            }
            tx += Math.Max(tw, kind == "p" ? 120 : tw) + 48;
        }
    }

    /// <summary>浅色分组卡片（和列表的分组卡片同一套描边、投影）。</summary>
    private void ScCard(CanvasDrawingSession ds, float x, float y, float w, float h)
    {
        RsSoftShadow(ds, x, y + 5, w, h, 36, 6, 0.16f);
        ds.FillRoundedRectangle(x, y, w, h, 36, 36, RsGroup);
        using var edge = new CanvasLinearGradientBrush(ds.Device, Color.FromArgb(255, 0xE8, 0xE9, 0xEC), Color.FromArgb(255, 0xBC, 0xBD, 0xC1)) { StartPoint = new Vector2(0, y), EndPoint = new Vector2(0, y + h) };
        ds.DrawRoundedRectangle(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, 36, 36, edge, 1.5f);
    }

    /// <summary>图片铺满（裁边）或放进框里（留边，标志用）。</summary>
    private static void ScDrawImage(CanvasDrawingSession ds, CanvasBitmap bmp, float x, float y, float w, float h, bool contain, bool alignLeft = false)
    {
        double sw = bmp.Size.Width, sh = bmp.Size.Height;
        if (sw <= 0 || sh <= 0) return;
        double k = contain ? Math.Min(w / sw, h / sh) : Math.Max(w / sw, h / sh);
        float iw = (float)(sw * k), ih = (float)(sh * k);
        float ix = alignLeft ? x : x + (w - iw) / 2, iy = y + (h - ih) / 2;
        ds.DrawImage(bmp, new Windows.Foundation.Rect(ix, iy, iw, ih), bmp.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
    }
}
