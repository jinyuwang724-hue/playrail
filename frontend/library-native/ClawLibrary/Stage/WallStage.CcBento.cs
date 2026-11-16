// 控制中心第三种样式「拼盘」（11-16 jinyu 选定，替换掉色带）。
// 六个功能一屏排开：左边一张大卡是游戏库，直接铺当前选中游戏的封面；右边性能一张宽卡，下面手柄 / 显示与声音 / 系统 / 软件设置四张小卡。
// 配色跟着封面走：封面的 6 个代表色（和动态背景同一份取色）压低饱和度和亮度后分给五张功能卡，每张是一块带颗粒的多色渐变。
// 每张卡：左上图标、右上实时读数、左下名称和一行状态。焦点卡放大一点、亮白边、底下一团同色的光，换焦点时一道高光扫过。
// 上下左右四向移动；最下一排再按 ↓ 去左下功能坞。Y 在 拍立得 → 卡片 → 拼盘 之间轮换，存在 controlcenter.ini。
using System.Numerics;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class CcTile
    {
        public required ContainerVisual El, Box;
        public required SpriteVisual Glow, Face, Cover, Over, Glare, Ring;
        public required CompositionRoundedRectangleGeometry ClipGeo;
        public required CompositionRadialGradientBrush GlowBrush;
        public string Drawn = "";
        public Vector2 Size;
        public LoadedImageSurface? CoverRef;
        public string? CoverPath;
        public Color Tone = Gfx.Rgba(0x4a, 0x44, 0x40);
    }

    /// <summary>拼盘样式开着（和 _ccCards 互斥）。</summary>
    private bool _ccBento = LoadCcBento();
    private ContainerVisual? _ccBentoRoot;
    private bool _ccBentoGoing;
    private readonly List<CcTile> _ccTiles = new();
    private Vector4[] _ccBentoColors = Array.Empty<Vector4>();
    private string? _ccBentoColorsFor;
    private int _ccBentoShown = -1, _ccBentoRight = 1;

    // 设计 px（× _ccK 到 DIP）
    private const float BtEdge = 40, BtTop = 104, BtBottomGap = 112, BtGap = 16, BtRadius = 14, BtLibShare = 430f / 1200f;
    private static readonly Spring BtSpring = new(320, 28, 1);

    private static bool LoadCcBento()
    {
        try { return File.Exists(CcPrefsPath) && File.ReadAllLines(CcPrefsPath).Contains("bento=true"); }
        catch { return false; }
    }

    private void SaveCcStyle()
    {
        try { Directory.CreateDirectory(App.CacheDir); File.WriteAllText(CcPrefsPath, $"cards={(_ccCards ? "true" : "false")}\nbento={(_ccBento ? "true" : "false")}"); }
        catch (Exception ex) { Log.Warn($"control center prefs: {ex.Message}"); }
    }

    /// <summary>Y：拍立得 → 卡片 → 拼盘 → 拍立得。</summary>
    private void CcCycleStyle()
    {
        if (_ccBento) { _ccBento = false; SetCcCardStyle(false); }
        else if (_ccCards) { _ccBento = true; SetCcCardStyle(false); }
        else SetCcCardStyle(true);
        SaveCcStyle();
        CcBentoApply();
        Toast($"控制中心样式：{(_ccBento ? "拼盘" : _ccCards ? "卡片" : "拍立得")}");
    }

    // ---------------- 建立 / 切换 ----------------

    private void CcEnsureBento()
    {
        if (_cc == null || _ccBentoRoot != null) return;
        _ccBentoRoot = G.Container();
        _cc.Children.InsertAbove(_ccBentoRoot, _ccRow!);
        for (int i = 0; i < _ccBlocks.Count; i++)
        {
            var el = G.C.CreateContainerVisual();
            var glowBrush = G.C.CreateRadialGradientBrush();
            glowBrush.ColorStops.Add(G.C.CreateColorGradientStop(0, Gfx.Rgba(255, 255, 255, 0.5)));
            glowBrush.ColorStops.Add(G.C.CreateColorGradientStop(1, Gfx.Rgba(255, 255, 255, 0)));
            var glow = G.Sprite(glowBrush, relative: false);
            glow.Opacity = 0;
            var face = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            var box = G.C.CreateContainerVisual();
            var geo = G.C.CreateRoundedRectangleGeometry();
            geo.CornerRadius = new Vector2(BtRadius);
            box.Clip = G.C.CreateGeometricClip(geo);
            var cover = G.Sprite(null, relative: false);
            var over = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            var glare = G.Sprite(G.LinearGradient(115, 200, 600, (0, Gfx.Rgba(255, 255, 255, 0)), (0.5, Gfx.Rgba(255, 255, 255, 0.22)), (1, Gfx.Rgba(255, 255, 255, 0))), relative: false);
            glare.Opacity = 0;
            box.Children.InsertAtTop(cover);
            box.Children.InsertAtTop(over);
            box.Children.InsertAtTop(glare);
            var ring = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            ring.Opacity = 0;
            el.Children.InsertAtTop(glow);
            el.Children.InsertAtTop(face);
            el.Children.InsertAtTop(box);
            el.Children.InsertAtTop(ring);
            _ccBentoRoot.Children.InsertAtTop(el);
            _ccTiles.Add(new CcTile { El = el, Box = box, Glow = glow, Face = face, Cover = cover, Over = over, Glare = glare, Ring = ring, ClipGeo = geo, GlowBrush = glowBrush });
        }
    }

    /// <summary>按当前样式显示拼盘或卡片排（换样式、打开时调）。</summary>
    private void CcBentoApply()
    {
        if (_cc == null) return;
        if (_ccBento) CcEnsureBento();
        if (_ccRow != null) _ccRow.IsVisible = !_ccBento;
        if (_ccLabel != null) _ccLabel.IsVisible = !_ccBento;
        if (_ccBentoRoot == null) return;
        _ccBentoRoot.IsVisible = _ccBento;
        if (!_ccBento) return;
        _ccBentoRoot.Scale = new Vector3(_ccK, _ccK, 1);
        _ccBentoShown = -1;
        CcBentoPaint();
        CcBentoLayout(animate: false);
    }

    private float BtW => (float)View.Width / _ccK;
    private float BtH => (float)View.Height / _ccK;

    /// <summary>第 i 张卡的矩形（设计 px）：0 游戏库整列，1 性能宽卡，2–5 四张小卡。</summary>
    private (float X, float Y, float W, float H) CcTileRect(int i)
    {
        float W = BtW - 2 * BtEdge, H = BtH - BtTop - BtBottomGap;
        float c1 = MathF.Round(W * BtLibShare), cw = (W - c1 - 2 * BtGap) / 2, rh = (H - 2 * BtGap) / 3;
        float x1 = BtEdge + c1 + BtGap, x2 = x1 + cw + BtGap;
        return i switch
        {
            0 => (BtEdge, BtTop, c1, H),
            1 => (x1, BtTop, cw * 2 + BtGap, rh),
            2 => (x1, BtTop + rh + BtGap, cw, rh),
            3 => (x2, BtTop + rh + BtGap, cw, rh),
            4 => (x1, BtTop + 2 * (rh + BtGap), cw, rh),
            _ => (x2, BtTop + 2 * (rh + BtGap), cw, rh),
        };
    }

    /// <summary>卡就位；焦点卡放大、亮边、底光，其余稍暗。焦点在功能坞上时没有焦点卡。</summary>
    private void CcBentoLayout(bool animate)
    {
        if (_ccBentoRoot == null || !_ccBento) return;
        int focus = _ccZone == CcZone.Row ? _ccFocus : -1;
        bool changed = focus != _ccBentoShown;
        _ccBentoShown = focus;
        for (int i = 0; i < _ccTiles.Count; i++)
        {
            var t = _ccTiles[i];
            var r = CcTileRect(i);
            bool on = i == focus;
            t.El.Offset = new Vector3(r.X, r.Y, 0);
            t.El.Size = new Vector2(r.W, r.H);
            t.El.CenterPoint = new Vector3(r.W / 2, r.H / 2, 0);
            float s = on ? 1 + 14f / MathF.Max(r.W, r.H) * 2 : 1;
            if (!animate || Reduced)
            {
                t.El.StopAnimation("Scale"); t.El.Scale = new Vector3(s, s, 1);
                t.El.StopAnimation("Opacity"); t.El.Opacity = on || focus < 0 ? 1 : 0.84f;
                t.Ring.StopAnimation("Opacity"); t.Ring.Opacity = on ? 1 : 0;
                t.Glow.StopAnimation("Opacity"); t.Glow.Opacity = on ? 1 : 0;
            }
            else
            {
                t.El.StartAnimation("Scale", G.SpringV3(new Vector3(s, s, 1), BtSpring));
                t.El.StartAnimation("Opacity", G.Scalar(t.El.Opacity, on || focus < 0 ? 1 : 0.84f, 200, EaseOutCss));
                t.Ring.StartAnimation("Opacity", G.Scalar(t.Ring.Opacity, on ? 1 : 0, on ? 180 : 120, EaseOutCss));
                t.Glow.StartAnimation("Opacity", G.Scalar(t.Glow.Opacity, on ? 1 : 0, on ? 320 : 160, EaseOutCss));
                if (on && changed)
                {
                    // 高光从左扫到右
                    t.Glare.Size = new Vector2(r.W * 0.5f, r.H);
                    var a = G.C.CreateVector3KeyFrameAnimation();
                    a.InsertKeyFrame(0, new Vector3(-r.W * 0.5f, 0, 0), G.Linear);
                    a.InsertKeyFrame(1, new Vector3(r.W, 0, 0), G.Ease(EaseOutCss));
                    a.Duration = Gfx.Ms(620);
                    t.Glare.StartAnimation("Offset", a);
                    var o = G.C.CreateScalarKeyFrameAnimation();
                    o.InsertKeyFrame(0, 1, G.Linear); o.InsertKeyFrame(0.8f, 1, G.Linear); o.InsertKeyFrame(1, 0, G.Linear);
                    o.Duration = Gfx.Ms(620);
                    t.Glare.StartAnimation("Opacity", o);
                }
            }
        }
        if (focus >= 0 && changed)
        {
            var el = _ccTiles[focus].El;
            _ccBentoRoot.Children.Remove(el);
            _ccBentoRoot.Children.InsertAtTop(el);
        }
    }

    // ---------------- 配色 ----------------

    private static (double h, double s, double l) BtHsl(Vector4 c)
    {
        double r = c.X, g = c.Y, b = c.Z, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-5) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    /// <summary>第 i 张功能卡的四个颜色：底、左上亮团、右下第二色、底边一点高光。都取自封面代表色，压低饱和度和亮度。</summary>
    private (Color Base, Color A, Color B, Color Hi) CcTileColors(int i)
    {
        var p = _ccBentoColors;
        Vector4 c1 = p.Length > 0 ? p[(i - 1) % p.Length] : new Vector4(0.36f, 0.33f, 0.31f, 1);
        Vector4 c2 = p.Length > 0 ? p[i % p.Length] : new Vector4(0.30f, 0.31f, 0.34f, 1);
        var (h1, s1, _) = BtHsl(c1);
        var (h2, s2, _) = BtHsl(c2);
        s1 = Math.Min(s1, 0.42) * 0.9; s2 = Math.Min(s2, 0.42) * 0.9;
        return (Gfx.Hsl(h1, s1 * 90, 17), Gfx.Hsl(h1, s1 * 100, 40), Gfx.Hsl(h2, s2 * 100, 29), Gfx.Hsl(h1, s1 * 70, 62));
    }

    private void CcBentoFetchColors()
    {
        string? path = _ccGameCover;
        if (path == _ccBentoColorsFor) return;
        _ccBentoColorsFor = path;
        if (path == null) { _ccBentoColors = Array.Empty<Vector4>(); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var colors = await Backdrop.CoverColors(path);
                _dq.TryEnqueue(() => { if (_ccBentoColorsFor != path) return; _ccBentoColors = colors; CcBentoPaint(); });
            }
            catch (Exception ex) { Log.Warn($"bento colors {path}: {ex.Message}"); }
        });
    }

    // ---------------- 画 ----------------

    /// <summary>内容变了才重画（每秒的读数刷新也走这里）。</summary>
    private void CcBentoPaint()
    {
        if (_ccBentoRoot == null || !_ccBento) return;
        CcBentoFetchColors();
        float raster = _ccK * Chrome.Dpi * 1.06f;
        string pal = string.Join(",", _ccBentoColors.Select(c => $"{c.X:0.00}{c.Y:0.00}{c.Z:0.00}"));
        for (int i = 0; i < _ccTiles.Count; i++)
        {
            var b = _ccBlocks[i];
            var t = _ccTiles[i];
            var r = CcTileRect(i);
            float w = r.W, h = r.H;
            string? cover = i == 0 ? _ccGameCover : null;
            string title = i == 0 ? SelectedGame()?.Title ?? "" : "";
            string key = $"{b.Name}|{b.Reading}|{b.Summary}|{b.Second}|{title}|{cover}|{pal}|{w}x{h}|{raster}";
            if (key == t.Drawn) continue;
            t.Drawn = key;
            t.Size = new Vector2(w, h);
            t.Face.Size = t.Over.Size = t.Cover.Size = t.Box.Size = t.ClipGeo.Size = t.Size;
            var col = i == 0 ? (Gfx.Rgba(0x27, 0x25, 0x24), Gfx.Rgba(0x4a, 0x44, 0x40), Gfx.Rgba(0x3a, 0x38, 0x3a), Gfx.Rgba(0x8a, 0x84, 0x80)) : CcTileColors(i);
            t.Tone = i == 0 ? Gfx.Mix(b.Accent, 0.5, Gfx.Rgba(0x60, 0x5a, 0x56)) : col.Item2;
            int idx = i;
            CcSet(t.Face, CcDraw(w, h, raster, ds => CcPaintTileFace(ds, col, w, h, idx)));
            CcSet(t.Over, CcDraw(w, h, raster, ds => CcPaintTileOver(ds, b, w, h, idx, cover != null, title)));
            // 焦点白边：比卡大一圈
            const float m = 6;
            t.Ring.Size = new Vector2(w + 2 * m, h + 2 * m);
            t.Ring.Offset = new Vector3(-m, -m, 0);
            CcSet(t.Ring, CcDraw(w + 2 * m, h + 2 * m, raster, ds =>
            {
                ds.DrawRoundedRectangle(m - 3, m - 3, w + 6, h + 6, BtRadius + 3, BtRadius + 3, Gfx.Rgba(255, 255, 255, 0.16), 4);
                ds.DrawRoundedRectangle(m - 1, m - 1, w + 2, h + 2, BtRadius + 1, BtRadius + 1, Gfx.Rgba(255, 255, 255, 0.92), 2);
            }));
            // 底光：卡下面一团同色的光
            t.Glow.Size = new Vector2(w * 1.3f, h * 1.3f);
            t.Glow.Offset = new Vector3(-w * 0.15f, -h * 0.15f + 18, 0);
            t.GlowBrush.ColorStops[0].Color = Gfx.WithAlpha(Gfx.Mix(t.Tone, 0.25, Colors.White), 0.5);
            t.GlowBrush.ColorStops[1].Color = Gfx.WithAlpha(t.Tone, 0);
            if (cover != t.CoverPath)
            {
                if (t.CoverRef != null) { t.Cover.Brush = null; Images.Release(t.CoverRef); t.CoverRef = null; }
                t.CoverPath = cover;
            }
            if (cover != null && t.CoverRef == null)
            {
                int px = (int)Math.Ceiling(Math.Max(w, h) * raster);
                var s = Images.AcquireReuse(cover, px, px);
                if (s != null)
                {
                    t.CoverRef = s;
                    var brush = CoverBrush(s);
                    brush.VerticalAlignmentRatio = 0.3f;
                    t.Cover.Brush = brush;
                }
            }
        }
    }

    /// <summary>卡面：暗底上两团颜色（左上亮、右下第二色）、底边一点高光、细颗粒。</summary>
    private static void CcPaintTileFace(CanvasDrawingSession ds, (Color Base, Color A, Color B, Color Hi) c, float w, float h, int seed)
    {
        using var clip = CanvasGeometry.CreateRoundedRectangle(ds, 0, 0, w, h, BtRadius, BtRadius);
        using var layer = ds.CreateLayer(1, clip);
        ds.FillRectangle(0, 0, w, h, c.Base);
        float m = MathF.Max(w, h);
        void Blob(Color col, float a, float cx, float cy, float rx, float ry)
        {
            using var g = new CanvasRadialGradientBrush(ds, Gfx.WithAlpha(col, a), Gfx.WithAlpha(col, 0)) { Center = new Vector2(cx, cy), RadiusX = rx, RadiusY = ry };
            ds.FillRectangle(0, 0, w, h, g);
        }
        Blob(c.A, 0.95f, w * 0.12f, -h * 0.1f, m * 0.72f, m * 0.62f);
        Blob(c.B, 0.9f, w * 1.02f, h * 1.1f, m * 0.66f, m * 0.6f);
        Blob(c.Hi, 0.22f, w * 0.3f, h * 1.2f, m * 0.34f, m * 0.3f);
        var rnd = new Random(977 + seed);
        int n = (int)(w * h / 70);
        for (int i = 0; i < n; i++)
            ds.FillRectangle((float)rnd.NextDouble() * w, (float)rnd.NextDouble() * h, 1, 1, rnd.Next(2) == 0 ? Gfx.WithAlpha(Colors.White, 0.05f) : Gfx.WithAlpha(Colors.Black, 0.08f));
    }

    /// <summary>盖在卡面（或封面）上的一层：上下压暗、内沿细边、图标、读数、名称、状态。</summary>
    private void CcPaintTileOver(CanvasDrawingSession ds, CcBlock b, float w, float h, int i, bool cover, string title)
    {
        var ink = Gfx.Rgba(255, 255, 255);
        const float pad = 22;
        using (var clip = CanvasGeometry.CreateRoundedRectangle(ds, 0, 0, w, h, BtRadius, BtRadius))
        using (ds.CreateLayer(1, clip))
        {
            using (var g = new CanvasLinearGradientBrush(ds, Gfx.Rgba(0, 0, 0, 0), Gfx.Rgba(0, 0, 0, cover ? 0.86 : 0.36)) { StartPoint = new Vector2(0, h * (cover ? 0.46f : 0.4f)), EndPoint = new Vector2(0, h) })
                ds.FillRectangle(0, 0, w, h, g);
            if (cover)
                using (var g = new CanvasLinearGradientBrush(ds, Gfx.Rgba(0, 0, 0, 0), Gfx.Rgba(0, 0, 0, 0.7)) { StartPoint = new Vector2(0, h * 0.72f), EndPoint = new Vector2(0, h) })
                    ds.FillRectangle(0, 0, w, h, g);
            if (cover)
                using (var g = new CanvasLinearGradientBrush(ds, Gfx.Rgba(0, 0, 0, 0.34), Gfx.Rgba(0, 0, 0, 0)) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, h * 0.22f) })
                    ds.FillRectangle(0, 0, w, h, g);
        }
        // 内沿：一圈很淡的边，上沿亮一点
        ds.DrawRoundedRectangle(0.5f, 0.5f, w - 1, h - 1, BtRadius, BtRadius, Gfx.Rgba(255, 255, 255, 0.12), 1);
        using (var g = new CanvasLinearGradientBrush(ds, Gfx.Rgba(255, 255, 255, 0.34), Gfx.Rgba(255, 255, 255, 0)) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, MathF.Min(h * 0.3f, 60)) })
            ds.DrawRoundedRectangle(0.5f, 0.5f, w - 1, h - 1, BtRadius, BtRadius, g, 1);

        LineIcons.Draw(ds, b.Icon, pad + 15, pad + 15, 30, Gfx.WithAlpha(ink, 0.95f));
        if (b.HasReading && b.Reading.Length > 0 && b.Reading != "—")
        {
            using var rd = TextKit.Layout(ds, b.Reading, TextKit.Ui(300), 30, w - pad * 2 - 44, CanvasHorizontalAlignment.Right);
            ds.DrawTextLayout(rd, pad + 44, pad + 15 - (float)(rd.LayoutBounds.Y + rd.LayoutBounds.Height / 2), ink);
        }
        string small = i == 0 ? (title.Length > 0 ? $"{title} · {b.Second}" : b.Second) : b.Summary.Length > 0 ? b.Summary : b.Second;
        if (i != 0 && b.HasReading && b.Reading.Length > 0 && small.EndsWith(b.Reading.Trim())) small = b.Second;
        float bottom = h - pad;
        if (small.Length > 0)
        {
            using var s = TextKit.Layout(ds, small, TextKit.Ui(400), 13, w - pad * 2, CanvasHorizontalAlignment.Left);
            s.WordWrapping = CanvasWordWrapping.NoWrap;
            s.TrimmingGranularity = CanvasTextTrimmingGranularity.Character;
            s.TrimmingSign = CanvasTrimmingSign.Ellipsis;
            bottom -= (float)s.LayoutBounds.Height;
            ds.DrawTextLayout(s, pad, bottom - (float)s.LayoutBounds.Y, Gfx.WithAlpha(ink, 0.78f));
            bottom -= 6;
        }
        using var name = TextKit.Layout(ds, b.Name, TextKit.Ui(700), i == 0 ? 30 : 22, w - pad * 2, CanvasHorizontalAlignment.Left);
        bottom -= (float)name.LayoutBounds.Height;
        ds.DrawTextLayout(name, pad, bottom - (float)name.LayoutBounds.Y, ink);
    }

    // ---------------- 输入 ----------------

    /// <summary>四向移动。返回 false = 没处理（最下一排按 ↓，交给功能坞）。</summary>
    private bool CcBentoMove(Direction dir)
    {
        int f = _ccFocus, next = f;
        switch (dir)
        {
            case Direction.Left: next = f is 1 or 2 or 4 ? 0 : f == 3 ? 2 : f == 5 ? 4 : f; break;
            case Direction.Right: next = f == 0 ? _ccBentoRight : f == 2 ? 3 : f == 4 ? 5 : f; break;
            case Direction.Up: next = f is 2 or 3 ? 1 : f == 4 ? 2 : f == 5 ? 3 : f; break;
            case Direction.Down:
                if (f is 0 or 4 or 5) return false;
                next = f == 1 ? 2 : f == 2 ? 4 : 5;
                break;
        }
        if (next == f) { Cue(Sfx.Bump); return true; }
        if (next == 0) _ccBentoRight = f;
        CcFocus(next);
        return true;
    }

    /// <summary>A：不做额外的过场，照常进页面。</summary>
    private bool CcBentoActivate(Action open) => false;

    /// <summary>点在卡上：点焦点卡 = 进入，点别的 = 换焦点。返回 true = 命中。</summary>
    private bool CcBentoClick(double sx, double sy)
    {
        if (!_ccBento || _ccBentoRoot == null) return false;
        float x = (float)(sx / _ccK), y = (float)(sy / _ccK);
        for (int i = 0; i < _ccTiles.Count; i++)
        {
            var r = CcTileRect(i);
            if (x < r.X || x > r.X + r.W || y < r.Y || y > r.Y + r.H) continue;
            if (_ccZone != CcZone.Row) { _ccZone = CcZone.Row; CcChromeFocus(); }
            if (i == _ccFocus) CcActivate(); else { Cue(Sfx.Nav); CcFocus(i); }
            return true;
        }
        return false;
    }
}
