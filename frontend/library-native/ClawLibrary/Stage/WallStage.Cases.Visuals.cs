// 卡盒版游戏库的零件：卡盒（封面 + 卡盒质感）、散放的卡带、文字面、主色和焦点。
//
// 卡盒质感移植自 "Finally 3D Game Cases"（https://github.com/otisabiburada/finally-3d-game-cases-plugin，src/engine.ts）
// 的封面绘制：顶部主色条、112° 斜向光泽、书脊折痕、左缘阴影、三层内描边。
//
// BSD 3-Clause License
//
// Copyright (c) 2026, SANSO
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//
// 1. Redistributions of source code must retain the above copyright notice, this
//    list of conditions and the following disclaimer.
//
// 2. Redistributions in binary form must reproduce the above copyright notice,
//    this list of conditions and the following disclaimer in the documentation
//    and/or other materials provided with the distribution.
//
// 3. Neither the name of the copyright holder nor the names of its
//    contributors may be used to endorse or promote products derived from
//    this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
using System.Numerics;
using System.Text.Json;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    /// <summary>一个卡盒：外层定位（Offset / Size 动画），里面是投影、主色光晕、圆角裁切的封面层和焦点描边。</summary>
    private sealed class CsCase
    {
        public required ContainerVisual Root;
        public required SpriteVisual Shadow, Glow, Art, Shine, Dim;
        public required ShapeVisual Ring;
        public LoadedImageSurface? Surface;
        public LibraryItem? Item;   // null = 行尾的「全部 N」
        public int Index;
        public bool Selected;
        public Vector3 Offset;
        public Vector2 Size;
        public SpriteVisual? Label;   // 展架：分组标签
        public SpriteVisual? Caption;   // 网格：标题和上次游玩
    }

    /// <summary>散放的卡带（分类页「最近」）。</summary>
    private sealed class CsCart
    {
        public required ContainerVisual Root, Body;
        public required SpriteVisual Glow, Ring, Label, Stripe, Caption, Shine;
        public LoadedImageSurface? Surface;
        public required LibraryItem Item;
        public int Index;
    }

    private CompositionSurfaceBrush? _csOverlay, _csCartShell, _csCartBand, _csCartEdge, _csCartRing, _csAllFace, _csLabelOver;
    private CompositionNineGridBrush? _csSoft;
    private CompositionBrush? _csShadowBrush;
    private CompositionLinearGradientBrush? _csShineBrush;
    private int _csAllCount = -1;
    private readonly Dictionary<string, (CompositionColorBrush A, CompositionColorBrush B, CompositionMaskBrush Glow)> _csTints = new();
    private readonly Dictionary<string, (Color C, double Fx)> _csLook = new();
    private readonly HashSet<string> _csLookPending = new();

    private const float CsRadius = 6, CsSoftPad = 70;

    // ---------------- 共用的面 ----------------

    private CompositionDrawingSurface CsDraw(float w, float h, Action<CanvasDrawingSession> paint) => CcDraw(w, h, CsRaster, paint);

    private SpriteVisual CsSprite(float w, float h, Action<CanvasDrawingSession> paint)
    {
        var s = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        s.Size = new Vector2(w, h);
        CcSet(s, CsDraw(w, h, paint));
        return s;
    }

    private void CsRepaint(SpriteVisual s, float w, float h, Action<CanvasDrawingSession> paint)
    {
        s.Size = new Vector2(w, h);
        CcSet(s, CsDraw(w, h, paint));
    }

    /// <summary>CSS linear-gradient 的角度换成 Win2D 画刷的起止点（盒子 w×h）。</summary>
    private static CanvasLinearGradientBrush CssGradient(ICanvasResourceCreator rc, double deg, float x, float y, float w, float h, params (float At, Color C)[] stops)
    {
        double a = deg * Math.PI / 180, dx = Math.Sin(a), dy = -Math.Cos(a);
        double len = Math.Abs(w * dx) + Math.Abs(h * dy);
        var c = new Vector2(x + w / 2, y + h / 2);
        var half = new Vector2((float)(dx * len / 2), (float)(dy * len / 2));
        var b = new CanvasLinearGradientBrush(rc, stops.Select(s => new CanvasGradientStop { Position = s.At, Color = s.C }).ToArray())
        { StartPoint = c - half, EndPoint = c + half };
        return b;
    }

    private void CsEnsureShared()
    {
        if (_csOverlay != null) return;
        // 卡盒质感（Finally 3D Game Cases 的 .case::after）：按 400×600 画一张，所有卡盒拉伸共用
        const float W = 400, H = 600;
        _csOverlay = G.C.CreateSurfaceBrush(CcDraw(W, H, 1.5f, ds =>
        {
            float band = H * 0.046f;
            ds.FillRectangle(0, band, W, 1.5f, Gfx.Rgba(0, 0, 0, 0.35));
            using (var gloss = CssGradient(ds, 112, 0, 0, W, H, (0, Gfx.Rgba(255, 255, 255, 0.32)), (0.15f, Gfx.Rgba(255, 255, 255, 0.08)), (0.32f, Gfx.Rgba(255, 255, 255, 0)),
                (0.60f, Gfx.Rgba(255, 255, 255, 0)), (0.72f, Gfx.Rgba(255, 255, 255, 0.10)), (0.735f, Gfx.Rgba(255, 255, 255, 0))))
                ds.FillRectangle(0, 0, W, H, gloss);
            using (var spine = CssGradient(ds, 90, 0, 0, W, H, (0.032f, Gfx.Rgba(255, 255, 255, 0)), (0.0321f, Gfx.Rgba(255, 255, 255, 0.30)), (0.039f, Gfx.Rgba(255, 255, 255, 0.30)),
                (0.0391f, Gfx.Rgba(0, 0, 0, 0.28)), (0.05f, Gfx.Rgba(0, 0, 0, 0.28)), (0.0501f, Gfx.Rgba(255, 255, 255, 0.05)), (0.09f, Gfx.Rgba(255, 255, 255, 0))))
                ds.FillRectangle(0, 0, W, H, spine);
            using (var edge = CssGradient(ds, 90, 0, 0, W, H, (0, Gfx.Rgba(0, 0, 0, 0.42)), (0.015f, Gfx.Rgba(255, 255, 255, 0.20)), (0.032f, Gfx.Rgba(0, 0, 0, 0.16)), (0.075f, Gfx.Rgba(0, 0, 0, 0))))
                ds.FillRectangle(0, 0, W, H, edge);
            // inset 0 0 0 1px 白 .22、3px 黑 .18、4px 白 .08（后写的在下面）
            ds.DrawRoundedRectangle(3.5f, 3.5f, W - 7, H - 7, CsRadius, CsRadius, Gfx.Rgba(255, 255, 255, 0.08), 1);
            ds.DrawRoundedRectangle(2, 2, W - 4, H - 4, CsRadius, CsRadius, Gfx.Rgba(0, 0, 0, 0.18), 2);
            ds.DrawRoundedRectangle(0.5f, 0.5f, W - 1, H - 1, CsRadius, CsRadius, Gfx.Rgba(255, 255, 255, 0.22), 1);
        }));
        _csOverlay.Stretch = CompositionStretch.Fill;

        // 柔光：一块模糊的圆角方块，九宫格拉到任意大小；投影（黑）和主色光晕（遮罩上色）共用
        float S = CsSoftPad * 2 + 40;
        var soft = G.C.CreateSurfaceBrush(CcDraw(S, S, 1, ds =>
        {
            using var cl = new CanvasCommandList(ds);
            using (var c = cl.CreateDrawingSession()) c.FillRoundedRectangle(CsSoftPad, CsSoftPad, 40, 40, 8, 8, Colors.White);
            using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 22, BorderMode = EffectBorderMode.Soft };
            ds.DrawImage(blur);
        }));
        _csSoft = G.C.CreateNineGridBrush();
        _csSoft.Source = soft;
        _csSoft.SetInsets(CsSoftPad + 18);
        var shadow = G.C.CreateMaskBrush();
        shadow.Source = G.Solid(Colors.Black);
        shadow.Mask = _csSoft;
        _csShadowBrush = shadow;

        _csShineBrush = G.LinearGradient(105, 2.6, 1, (0.38, Gfx.Rgba(255, 255, 255, 0)), (0.5, Gfx.Rgba(255, 255, 255, 0.5)), (0.62, Gfx.Rgba(255, 255, 255, 0)));

        // 卡带（11-05 换成用户 demo 里的「蓝钢插槽卡」，外形参考 TIC-80 的卡带模板 build/cart.png，MIT）：
        // 400×255（1.57 : 1），圆角 6.8；外壳按每款游戏的主色上色（155° 浅 → 主色 72 % → 深），底下露 6 px 深色厚度；
        // 上面一个大凹框放横幅标签（标签 304×142，= Steam 横幅 460:215），下面一个窄凹框带一个朝下的三角，底边一排金色触点。
        // 凹框：内阴影 + 下沿 1 px 高光。选中特效是我们自己的（上移 10、转 −1.2°、3 px 白色内描边、投影 + 主色光晕、扫光），没照搬 demo。
        _csCartShape = G.C.CreateSurfaceBrush(CcDraw(CartW, CartH, 1.5f, ds =>
        {
            using var shape = CartShape(ds);
            ds.FillGeometry(shape, Colors.White);
        }));
        _csCartRecess = G.C.CreateSurfaceBrush(CcDraw(CartW, CartH, 1.5f, ds =>
        {
            using var r = CartRecess(ds);
            ds.FillGeometry(r, Colors.White);
        }));
        _csCartTri = G.C.CreateSurfaceBrush(CcDraw(CartW, CartH, 1.5f, ds =>
        {
            using var tri = CanvasGeometry.CreatePolygon(ds, new[] { new Vector2(336, 188.7f), new Vector2(368, 188.7f), new Vector2(352, 209.1f) });
            ds.FillGeometry(tri, Colors.White);
        }));
        // 外壳质感（中性色，叠在主色外壳上、凹框下）：上沿内高光、下沿内阴影、凹框下沿外高光
        _csCartShell = G.C.CreateSurfaceBrush(CcDraw(CartW, CartH, 1.5f, ds =>
        {
            using var shape = CartShape(ds);
            using (ds.CreateLayer(1, shape))
            {
                using (var top = CssGradient(ds, 180, 0, 0, CartW, 6, (0, Gfx.Rgba(255, 255, 255, 0.5)), (1, Gfx.Rgba(255, 255, 255, 0))))
                    ds.FillRectangle(0, 0, CartW, 6, top);
                using (var bot = CssGradient(ds, 0, 0, CartH - 8, CartW, 8, (0, Gfx.Rgba(0, 0, 0, 0.4)), (1, Gfx.Rgba(0, 0, 0, 0))))
                    ds.FillRectangle(0, CartH - 8, CartW, 8, bot);
                using var r = CartRecess(ds);
                ds.FillGeometry(r, 0, 2, Gfx.Rgba(255, 255, 255, 0.39));
            }
        }));
        // 凹框的内阴影（左上深）+ 触点
        _csCartBand = G.C.CreateSurfaceBrush(CcDraw(CartW, CartH, 1.5f, ds =>
        {
            using var r = CartRecess(ds);
            using (var cl = new CanvasCommandList(ds))
            {
                using (var c = cl.CreateDrawingSession())
                using (var ring = CanvasGeometry.CreateRectangle(c, -20, -20, CartW + 40, CartH + 40).CombineWith(r, Matrix3x2.CreateTranslation(2, 2), CanvasGeometryCombine.Exclude))
                    c.FillGeometry(ring, Gfx.Rgba(8, 10, 10, 0.8));
                using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 2 };
                using (ds.CreateLayer(1, r)) ds.DrawImage(blur);
            }
            // 触点：每 4 % 一组（暗铜 1 %、金 2 %、亮铜 0.5 %、缝 0.5 %）
            float px = 15.2f, py = 238.4f, pw = 369.6f, ph = 12.75f, per = pw * 0.04f;
            using var pins = CanvasGeometry.CreateRoundedRectangle(ds, px, py, pw, ph, 1, 1);
            using (ds.CreateLayer(1, pins))
            {
                ds.FillRectangle(px, py, pw, ph, Gfx.Rgba(0x1c, 0x1f, 0x1f));
                for (float x = px; x < px + pw; x += per)
                {
                    ds.FillRectangle(x, py, per * 0.25f, ph, Gfx.Rgba(0x80, 0x62, 0x43));
                    ds.FillRectangle(x + per * 0.25f, py, per * 0.5f, ph, Gfx.Rgba(0xed, 0xc8, 0x76));
                    ds.FillRectangle(x + per * 0.75f, py, per * 0.125f, ph, Gfx.Rgba(0xb2, 0x89, 0x49));
                }
            }
        }));
        _csCartEdge = G.C.CreateSurfaceBrush(CcDraw(CartW, CartH, 1.5f, ds =>
        {
            using var shape = CartShape(ds);
            ds.DrawGeometry(shape, Gfx.Rgba(0, 0, 0, 0.25), 1);
        }));
        // 选中描边：外壳内侧 3 px 白
        _csCartRing = G.C.CreateSurfaceBrush(CcDraw(CartW, CartH, 1.5f, ds =>
        {
            using var shape = CartShape(ds);
            using (ds.CreateLayer(1, shape)) ds.DrawGeometry(shape, Colors.White, 6);
        }));
    }

    private const float CartW = 400, CartH = 255, CartDepth = 6, LabX = 48, LabY = 17.85f, LabW = 304, LabH = 142.1f;
    private CompositionSurfaceBrush? _csCartShape, _csCartRecess, _csCartTri;

    private static CanvasGeometry CartShape(ICanvasResourceCreator rc) => CanvasGeometry.CreateRoundedRectangle(rc, 0, 0, CartW, CartH, 6.8f, 6.8f);

    /// <summary>两个凹框：上面放标签（inset 4% 4% 34%），下面一条窄的（70 % – 85 %）。</summary>
    private static CanvasGeometry CartRecess(ICanvasResourceCreator rc)
    {
        using var a = CanvasGeometry.CreateRoundedRectangle(rc, 16, 10.2f, 368, 158.1f, 6.4f, 6.4f);
        using var b = CanvasGeometry.CreateRoundedRectangle(rc, 16, 178.5f, 368, 38.25f, 4, 4);
        return a.CombineWith(b, Matrix3x2.Identity, CanvasGeometryCombine.Union);
    }

    /// <summary>标签：圆角 4（Steam 横幅比例）。</summary>
    private static CanvasGeometry LabelShape(ICanvasResourceCreator rc) => CanvasGeometry.CreateRoundedRectangle(rc, 0, 0, LabW, LabH, 4, 4);

    /// <summary>外壳主色：取游戏主色的色相，饱和度夹在 30–52 %，亮度 42 %（demo 里几款的范围）。</summary>
    private static Color CartSlotColor(Color a)
    {
        double r = a.R / 255.0, g = a.G / 255.0, b = a.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, l = (max + min) / 2;
        double h = 0, s = 0;
        if (d > 1e-6)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return Gfx.Hsl(h, Math.Clamp(s * 100, s < 0.08 ? 8 : 30, 52), 42);
    }

    // ---------------- 主色和焦点 ----------------

    /// <summary>主色（规格里的 a）和横幅图焦点。封面缓存里有就用；没有先用种子色，后台从封面算出来写进 cases.json。</summary>
    private (Color C, double Fx) CsLookOf(LibraryItem g)
    {
        if (_csLook.TryGetValue(g.Id, out var l)) return l;
        var art = g.Art;
        if (art != null && File.Exists(Path.Combine(art.Dir, "cases.json")))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(art.Dir, "cases.json")));
                var r = doc.RootElement;
                if (r.TryGetProperty("v", out var v) && v.GetInt32() == CsLookVersion)
                {
                    var hit = (Gfx.Hex(r.GetProperty("c").GetString(), CsFallback(g)), r.GetProperty("fx").GetDouble());
                    _csLook[g.Id] = hit;
                    return hit;
                }
            }
            catch (Exception ex) { Log.Warn($"cases.json {g.Id}: {ex.Message}"); }
        }
        var guess = (CsFallback(g), 0.5);
        if (art != null && _csLookPending.Add(g.Id)) _ = CsComputeLookAsync(g, art);
        return guess;
    }

    private const int CsLookVersion = 1;

    private static Color CsFallback(LibraryItem g)
    {
        var seed = Gfx.Hex(g.SeedColor ?? g.Art?.C, Gfx.Hsl(g.Hue, 41, 42));
        return CsNormalize(seed);
    }

    /// <summary>原型里的主色都是同一明度和饱和度（hsl(h, 41%, 42%)）：只保留色相，换哪张封面都一样稳。</summary>
    private static Color CsNormalize(Color c)
    {
        double r = c.R / 255.0, gg = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(gg, b)), min = Math.Min(r, Math.Min(gg, b)), d = max - min;
        double h = d < 1e-6 ? 215 : max == r ? 60 * (((gg - b) / d) % 6) : max == gg ? 60 * ((b - r) / d + 2) : 60 * ((r - gg) / d + 4);
        return Gfx.Hsl(h, 41, 42);
    }

    private async Task CsComputeLookAsync(LibraryItem g, ArtKit art)
    {
        string? hero = art.Has('h') ? art.File('h') : null, cover = art.Has('p') ? art.File('p') : art.Has('v') ? art.File('v') : null;
        if (hero == null && cover == null) return;
        try
        {
            var dev = G.Device;
            var result = await Task.Run(async () =>
            {
                Color main = CsFallback(g);
                double fx = 0.5;
                if ((cover ?? hero) is { } cp) using (var bmp = await CanvasBitmap.LoadAsync(dev, cp, 96)) main = DominantHue(dev, bmp);
                if (hero != null) using (var bmp = await CanvasBitmap.LoadAsync(dev, hero, 96)) fx = FocalX(dev, bmp);
                return (main, fx);
            });
            _csLook[g.Id] = result;
            try
            {
                File.WriteAllText(Path.Combine(art.Dir, "cases.json"),
                    $"{{\"v\":{CsLookVersion},\"c\":\"#{result.main.R:x2}{result.main.G:x2}{result.main.B:x2}\",\"fx\":{result.fx.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}");
            }
            catch (Exception ex) { Log.Warn($"cases.json write {g.Id}: {ex.Message}"); }
            _dq.TryEnqueue(() => CsLookChanged(g));
        }
        catch (Exception ex) { Log.Warn($"cases look {g.Id}: {ex.Message}"); }
    }

    private static Color[] Shrink(CanvasDevice dev, CanvasBitmap bmp, int w, int h)
    {
        using var rt = new CanvasRenderTarget(dev, w, h, 96);
        using (var ds = rt.CreateDrawingSession()) ds.DrawImage(bmp, new Windows.Foundation.Rect(0, 0, w, h), bmp.Bounds, 1, CanvasImageInterpolation.Linear);
        return rt.GetPixelColors();
    }

    /// <summary>主色：按饱和度 × 亮度加权的色相圆周平均（灰白黑不算）。</summary>
    private static Color DominantHue(CanvasDevice dev, CanvasBitmap bmp)
    {
        var px = Shrink(dev, bmp, 32, 48);
        double sx = 0, sy = 0;
        foreach (var c in px)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            if (d < 0.08 || max < 0.15) continue;
            double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
            double wgt = d * max;
            sx += Math.Cos(h * Math.PI / 180) * wgt; sy += Math.Sin(h * Math.PI / 180) * wgt;
        }
        double hue = Math.Abs(sx) + Math.Abs(sy) < 1e-6 ? 215 : Math.Atan2(sy, sx) * 180 / Math.PI;
        return Gfx.Hsl(hue, 41, 42);
    }

    /// <summary>横幅图焦点：每列的细节（相邻像素差）× 饱和度，取加权重心，夹在 0.15–0.85。</summary>
    private static double FocalX(CanvasDevice dev, CanvasBitmap bmp)
    {
        const int W = 64, H = 24;
        var px = Shrink(dev, bmp, W, H);
        var col = new double[W];
        for (int y = 0; y < H; y++)
            for (int x = 1; x < W - 1; x++)
            {
                var c = px[y * W + x]; var l = px[y * W + x - 1]; var r = px[y * W + x + 1];
                double detail = (Math.Abs(r.R - l.R) + Math.Abs(r.G - l.G) + Math.Abs(r.B - l.B)) / 765.0;
                double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
                col[x] += detail * (0.35 + (max - min));
            }
        double sum = 0, acc = 0;
        for (int x = 0; x < W; x++) { double v = col[x] * col[x]; sum += v; acc += v * (x + 0.5) / W; }
        return sum <= 0 ? 0.5 : Math.Clamp(acc / sum, 0.15, 0.85);
    }

    private (CompositionColorBrush A, CompositionColorBrush B, CompositionMaskBrush Glow) CsTint(LibraryItem? g)
    {
        string key = g?.Id ?? "";
        if (_csTints.TryGetValue(key, out var t)) return t;
        var a = g != null ? CsLookOf(g).C : Gfx.Rgba(0x55, 0x55, 0x58);
        var ca = G.Solid(a);
        var cb = G.Solid(Gfx.Mix(a, 0.45, Colors.Black));
        var glow = G.C.CreateMaskBrush();
        glow.Source = ca;
        glow.Mask = _csSoft;
        t = (ca, cb, glow);
        _csTints[key] = t;
        return t;
    }

    /// <summary>后台算好了主色和焦点：共用的画刷换色（所有用到它的卡盒、卡带一起变），背景按新焦点取景。</summary>
    private void CsLookChanged(LibraryItem g)
    {
        if (_csTints.TryGetValue(g.Id, out var t))
        {
            var a = _csLook[g.Id].C;
            t.A.Color = a;
            t.B.Color = Gfx.Mix(a, 0.45, Colors.Black);
        }
        if (_cs != null && CsSpotItem()?.Id == g.Id) CsSetBackground(g, force: true);
    }

    // ---------------- 卡盒 ----------------

    private CsCase CsNewCase(LibraryItem? g, int index, Vector2 size, bool hiRes = false)
    {
        CsEnsureShared();
        var tint = CsTint(g);
        var root = G.Container(relative: false);
        root.Size = size;
        var shadow = G.Sprite(_csShadowBrush);
        shadow.Size = new Vector2(CsSoftPad * 2 - 30, CsSoftPad * 2 - 30);
        shadow.Offset = new Vector3(-CsSoftPad + 15, -CsSoftPad + 15 + 10, 0);
        shadow.Opacity = 0.45f;
        var glow = G.Sprite(tint.Glow);
        glow.Size = new Vector2(CsSoftPad * 2 - 20, CsSoftPad * 2 - 20);
        glow.Offset = new Vector3(-CsSoftPad + 10, -CsSoftPad + 10 + 18, 0);
        glow.Opacity = 0;
        var face = G.Container();
        var clipGeo = G.C.CreateRoundedRectangleGeometry();
        clipGeo.CornerRadius = new Vector2(CsRadius, CsRadius);
        var sizeExpr = G.C.CreateExpressionAnimation("v.Size");
        sizeExpr.SetReferenceParameter("v", root);
        clipGeo.StartAnimation("Size", sizeExpr);
        face.Clip = G.C.CreateGeometricClip(clipGeo);
        var back = G.Sprite(g != null ? tint.B : G.Solid(Gfx.Rgba(0x23, 0x23, 0x26)));
        var artBrush = G.C.CreateSurfaceBrush();
        artBrush.Stretch = CompositionStretch.UniformToFill;
        var art = G.Sprite(artBrush);
        face.Children.InsertAtTop(back);
        face.Children.InsertAtTop(art);
        var c = new CsCase
        {
            Root = root, Shadow = shadow, Glow = glow, Art = art, Item = g, Index = index, Size = size,
            Shine = G.Sprite(_csShineBrush), Dim = G.Sprite(G.Solid(Colors.Black)),
            Ring = G.C.CreateShapeVisual(),
        };
        if (g != null)
        {
            var band = G.Sprite(tint.A);
            band.RelativeSizeAdjustment = new Vector2(1, 0.046f);
            face.Children.InsertAtTop(band);
            var over = G.Sprite(_csOverlay);
            face.Children.InsertAtTop(over);
            string? path = g.Art is { } k ? (hiRes && k.Has('q') ? k.File('q') : k.Has('p') ? k.File('p') : k.Has('v') ? k.File('v') : null) : null;
            if (path != null)
            {
                int px = (int)Math.Ceiling(size.X * CsRaster * (hiRes ? 1 : 1.36f));
                c.Surface = Images.AcquireReuse(path, px, px * 3 / 2);
                artBrush.Surface = c.Surface;
            }
        }
        else
        {
            var all = G.Sprite(CsAllFace());
            all.Brush = CsAllFace();
            face.Children.InsertAtTop(all);
        }
        c.Shine.RelativeSizeAdjustment = new Vector2(2.6f, 1);
        c.Shine.RelativeOffsetAdjustment = new Vector3(-2.56f, 0, 0);
        face.Children.InsertAtTop(c.Shine);
        c.Dim.Opacity = 0;
        face.Children.InsertAtTop(c.Dim);
        // 焦点描边：圆角外面 3 px 白（box-shadow 0 0 0 3px #fff）
        c.Ring.RelativeSizeAdjustment = Vector2.One;
        c.Ring.Size = new Vector2(12, 12);
        c.Ring.Offset = new Vector3(-6, -6, 0);
        var ringGeo = G.C.CreateRoundedRectangleGeometry();
        ringGeo.CornerRadius = new Vector2(CsRadius + 1.5f, CsRadius + 1.5f);
        ringGeo.Offset = new Vector2(4.5f, 4.5f);
        var ringExpr = G.C.CreateExpressionAnimation("Vector2(v.Size.X + 3, v.Size.Y + 3)");
        ringExpr.SetReferenceParameter("v", root);
        ringGeo.StartAnimation("Size", ringExpr);
        var ring = G.C.CreateSpriteShape(ringGeo);
        ring.StrokeBrush = G.Solid(Colors.White);
        ring.StrokeThickness = 3;
        c.Ring.Shapes.Add(ring);
        c.Ring.Opacity = 0;
        root.Children.InsertAtTop(shadow);
        root.Children.InsertAtTop(glow);
        root.Children.InsertAtTop(face);
        root.Children.InsertAtTop(c.Ring);
        return c;
    }

    private CompositionSurfaceBrush CsAllFace()
    {
        int n = CsList(1).Count;
        if (_csAllFace != null && _csAllCount == n) return _csAllFace;
        _csAllCount = n;
        var surface = CcDraw(400, 600, 1.2f, ds =>
        {
            ds.FillRectangle(0, 0, 400, 600, Gfx.Rgba(0x23, 0x23, 0x26));
            ds.DrawRoundedRectangle(0.5f, 0.5f, 399, 599, CsRadius, CsRadius, Gfx.Rgba(255, 255, 255, 0.12), 1);
            // 网格图标 + 数量
            float s = 60, x0 = 200 - s / 2, y0 = 300 - s - 14;
            for (int i = 0; i < 4; i++)
                ds.DrawRoundedRectangle(x0 + (i % 2) * s * 0.56f, y0 + (i / 2) * s * 0.56f, s * 0.44f, s * 0.44f, 5, 5, Gfx.Rgba(242, 242, 242, 0.75), 2.6f);
            using var l = TextKit.Layout(ds, n.ToString(), TextKit.Num(600), 48, 400, CanvasHorizontalAlignment.Center);
            ds.DrawTextLayout(l, 0, 300 + 14, Gfx.Rgba(242, 242, 242, 0.75));
        });
        if (_csAllFace == null) { _csAllFace = G.C.CreateSurfaceBrush(surface); _csAllFace.Stretch = CompositionStretch.Fill; }
        else { (_csAllFace.Surface as IDisposable)?.Dispose(); _csAllFace.Surface = surface; }
        return _csAllFace;
    }

    private void CsDisposeCase(CsCase c)
    {
        Images.Release(c.Surface);
        c.Surface = null;
        c.Root.Dispose();
        c.Label?.Dispose();
        c.Caption?.Dispose();
    }

    /// <summary>选中 / 取消选中：描边、光晕、投影和压暗按 400 ms 过渡；新选中的扫一道高光。</summary>
    private void CsSelect(CsCase c, bool on, bool shine, double delay = 200, bool dimOthers = true)
    {
        if (c.Selected == on && !shine) return;
        c.Selected = on;
        double ms = CsMs(400);
        c.Ring.StartAnimation("Opacity", CsS(on ? 1 : 0, ms, CsQuart));
        c.Glow.StartAnimation("Opacity", CsS(on ? 1 : 0, CsMs(550), CsQuart));
        c.Shadow.StartAnimation("Opacity", CsS(on ? 0.7f : 0.45f, ms, CsQuart));
        c.Shadow.StartAnimation("Offset", CsV3(new Vector3(-CsSoftPad + 15, -CsSoftPad + 15 + (on ? 22 : 10), 0), ms, CsQuart));
        if (dimOthers) c.Dim.StartAnimation("Opacity", CsS(on ? 0 : ShDim, ms, CsQuart));
        if (on && shine && !Reduced)
        {
            var a = G.C.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0, new Vector3(-2.56f, 0, 0), G.Linear);
            a.InsertKeyFrame(1, new Vector3(0.96f, 0, 0), G.Ease(Bezier.EaseOut));
            a.Duration = Gfx.Ms(1000);
            a.DelayTime = Gfx.Ms(delay);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            c.Shine.StartAnimation("RelativeOffsetAdjustment", a);
        }
    }

    // ---------------- 卡带 ----------------

    private CsCart CsNewCart(LibraryItem g, int index)
    {
        CsEnsureShared();
        var tint = CsTint(g);
        var (lookA, fx) = CsLookOf(g);
        var slot = CartSlotColor(lookA);
        var light = Gfx.Mix(slot, 0.76, Gfx.Rgba(0xe4, 0xe6, 0xe2));
        var dark = Gfx.Mix(slot, 0.84, Gfx.Rgba(0x18, 0x19, 0x18));
        var edgeC = Gfx.Mix(slot, 0.52, Gfx.Rgba(0x15, 0x16, 0x16));
        var recessC = Gfx.Mix(slot, 0.32, Gfx.Rgba(0x14, 0x17, 0x17));
        SpriteVisual Masked(CompositionBrush src, CompositionSurfaceBrush mask)
        {
            var m = G.C.CreateMaskBrush();
            m.Source = src;
            m.Mask = mask;
            return G.Sprite(m);
        }
        var root = G.Container(relative: false);
        root.Size = new Vector2(CartW, CartH + 12 + CapH);
        var body = G.Container(relative: false);
        body.Size = new Vector2(CartW, CartH);
        body.CenterPoint = new Vector3(CartW / 2, CartH / 2, 0);
        var shadow = G.Sprite(_csShadowBrush);
        shadow.Size = new Vector2(CsSoftPad * 2 - 34, CsSoftPad * 2 - 34);
        shadow.Offset = new Vector3(-CsSoftPad + 17, -CsSoftPad + 17 + 14, 0);
        shadow.Opacity = 0.55f;
        var glow = G.Sprite(tint.Glow);
        glow.Size = new Vector2(CsSoftPad * 2 - 30, CsSoftPad * 2 - 30);
        glow.Offset = new Vector3(-CsSoftPad + 15, -CsSoftPad + 15 + 10, 0);
        glow.Opacity = 0;
        var depth = Masked(G.Solid(edgeC), _csCartShape!);
        depth.Offset = new Vector3(0, CartDepth, 0);
        var shell = Masked(G.LinearGradient(155, CartW, CartH, (0, light), (0.72, slot), (1, dark)), _csCartShape!);
        var shellFx = G.Sprite(_csCartShell);
        var recess = Masked(G.Solid(recessC), _csCartRecess!);
        var recessFx = G.Sprite(_csCartBand);
        var tri = Masked(G.Solid(light), _csCartTri!);
        var labelBrush = G.C.CreateSurfaceBrush();
        labelBrush.Stretch = CompositionStretch.UniformToFill;
        labelBrush.HorizontalAlignmentRatio = (float)fx;
        labelBrush.VerticalAlignmentRatio = 0.3f;
        using var labGeo = LabelShape(G.Device);
        CompositionGeometricClip LabClip() => G.C.CreateGeometricClip(G.C.CreatePathGeometry(new CompositionPath(labGeo)));
        // 标签外一圈 1 px 黑 60 %（box-shadow 0 0 0 1px #0009）
        var labelRim = G.Sprite(G.Solid(Gfx.Rgba(0, 0, 0, 0.6)), relative: false);
        labelRim.Offset = new Vector3(LabX - 1, LabY - 1, 0); labelRim.Size = new Vector2(LabW + 2, LabH + 2);
        labelRim.Clip = G.C.CreateGeometricClip(G.C.CreateRoundedRectangleGeometry());
        ((CompositionRoundedRectangleGeometry)((CompositionGeometricClip)labelRim.Clip).Geometry).Size = labelRim.Size;
        ((CompositionRoundedRectangleGeometry)((CompositionGeometricClip)labelRim.Clip).Geometry).CornerRadius = new Vector2(5, 5);
        var labelBack = G.Sprite(tint.B, relative: false);
        labelBack.Offset = new Vector3(LabX, LabY, 0); labelBack.Size = new Vector2(LabW, LabH); labelBack.Clip = LabClip();
        var label = G.Sprite(labelBrush, relative: false);
        label.Offset = labelBack.Offset; label.Size = labelBack.Size; label.Clip = LabClip();
        // 标签光泽（117°：左上亮、右下一点反光）+ 1 px 白 20 % 内描边
        _csLabelOver ??= G.C.CreateSurfaceBrush(CcDraw(LabW, LabH, 1.5f, ds =>
        {
            using var shapeL = LabelShape(ds);
            using (ds.CreateLayer(1, shapeL))
            {
                using (var gloss = CssGradient(ds, 117, 0, 0, LabW, LabH, (0, Gfx.Rgba(255, 255, 255, 0.13)), (0.33f, Gfx.Rgba(255, 255, 255, 0)), (0.80f, Gfx.Rgba(255, 255, 255, 0)), (1, Gfx.Rgba(255, 255, 255, 0.07))))
                    ds.FillRectangle(0, 0, LabW, LabH, gloss);
                ds.DrawGeometry(shapeL, Gfx.Rgba(255, 255, 255, 0.2), 2);
            }
        }));
        var over = G.Sprite(_csLabelOver, relative: false);
        over.Offset = labelBack.Offset; over.Size = labelBack.Size;
        var edge = G.Sprite(_csCartEdge);
        // 扫光：和卡盒同一支（105° 白 50 %），裁在外壳里
        var shineHost = G.Container(relative: false);
        shineHost.Size = new Vector2(CartW, CartH);
        using (var sg = CartShape(G.Device)) shineHost.Clip = G.C.CreateGeometricClip(G.C.CreatePathGeometry(new CompositionPath(sg)));
        var shine = G.Sprite(_csShineBrush);
        shine.RelativeSizeAdjustment = new Vector2(2.6f, 1);
        shine.RelativeOffsetAdjustment = new Vector3(-2.56f, 0, 0);
        shineHost.Children.InsertAtTop(shine);
        var ring = G.Sprite(_csCartRing);
        ring.Opacity = 0;
        foreach (var v in new Visual[] { shadow, glow, depth, shell, shellFx, recess, recessFx, tri, labelRim, labelBack, label, over, edge, shineHost, ring }) body.Children.InsertAtTop(v);
        root.Children.InsertAtTop(body);
        var cart = new CsCart { Root = root, Body = body, Glow = glow, Ring = ring, Label = label, Stripe = shell, Shine = shine, Item = g, Index = index, Caption = CsCaption(g, 400, true) };
        cart.Caption.Offset = new Vector3(0, CartH + 12, 0);
        root.Children.InsertAtTop(cart.Caption);
        if (g.Art is { } k && (k.Has('h') ? k.File('h') : k.Has('x') ? k.File('x') : null) is { } path)
        {
            cart.Surface = Images.AcquireReuse(path, (int)Math.Ceiling(LabW * CsRaster * 1.3f), (int)Math.Ceiling(LabH * CsRaster * 1.3f));
            labelBrush.Surface = cart.Surface;
        }
        return cart;
    }

    /// <summary>选中：上移 10、转 −1.2°、3 px 白色内描边、投影加深 + 主色光晕、扫一道高光（和卡盒同一套）。</summary>
    private void CsFocusCart(CsCart c, bool on)
    {
        double ms = CsMs(450);
        c.Body.StartAnimation("Offset", CsV3(new Vector3(0, on ? -10 : 0, 0), ms, CsQuart));
        c.Body.StartAnimation("RotationAngleInDegrees", CsS(on ? -1.2f : 0, ms, CsQuart));
        c.Ring.StartAnimation("Opacity", CsS(on ? 1 : 0, ms, CsQuart));
        c.Glow.StartAnimation("Opacity", CsS(on ? 1 : 0, ms, CsQuart));
        if (on && !Reduced)
        {
            var a = G.C.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0, new Vector3(-2.56f, 0, 0), G.Linear);
            a.InsertKeyFrame(1, new Vector3(0.96f, 0, 0), G.Ease(Bezier.EaseOut));
            a.Duration = Gfx.Ms(1000);
            a.DelayTime = Gfx.Ms(120);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            c.Shine.StartAnimation("RelativeOffsetAdjustment", a);
        }
    }
    private void CsDisposeCart(CsCart c)
    {
        Images.Release(c.Surface);
        c.Surface = null;
        c.Root.Dispose();
    }

    // ---------------- 文字 ----------------

    private static string CsRel(LibraryItem g)
    {
        if (g.LastPlayedDays is not int d) return "未玩过";
        if (d == 0) return "今天";
        if (d == 1) return "昨天";
        if (d < 7) return $"{d} 天前";
        var t = DateTimeOffset.FromUnixTimeSeconds(g.LastPlayed ?? 0).LocalDateTime;
        return t.Year == DateTime.Now.Year ? $"{t.Month}月{t.Day}日" : $"{t.Year}年{t.Month}月";
    }

    private static string CsPlaytime(LibraryItem g) => g.PlaytimeH is double h && h >= 1 ? $"{h:0} 小时" : "不到 1 小时";

    /// <summary>开发商 · 年份；本地条目的副标题只是来源名，不重复写。</summary>
    private static string CsMaker(LibraryItem g) => g.Subtitle is "本地" or "非 Steam" ? "" : g.Subtitle;

    private static string CsBucket(LibraryItem g)
    {
        if (g.LastPlayedDays is not int d) return "未玩过";
        if (d == 0) return "今天";
        if (d == 1) return "昨天";
        if (d < 7) return "本周";
        if (d < 31) return "本月";
        return DateTimeOffset.FromUnixTimeSeconds(g.LastPlayed ?? 0).LocalDateTime.Year.ToString();
    }

    private static string CsMetaFull(LibraryItem g)
    {
        var parts = new List<string>();
        if (g.LastPlayedDays != null) { parts.Add(CsRel(g)); parts.Add("已玩 " + CsPlaytime(g)); }
        if (CsMaker(g) is { Length: > 0 } m) parts.Add(m);
        return string.Join(" · ", parts);
    }

    private static string CsMetaShort(LibraryItem g) => g.LastPlayedDays == null ? CsMaker(g) : $"{CsRel(g)} · {CsPlaytime(g)}";

    /// <summary>卡下面的两行：标题（17 px）和上次游玩 / 开发商（15 px，60% 白）。</summary>
    private SpriteVisual CsCaption(LibraryItem g, float w, bool bold)
    {
        return CsSprite(w, CapH, ds =>
        {
            using var t = TextKit.Layout(ds, g.Title, TextKit.Ui(bold ? (ushort)600 : (ushort)500), 24, w, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(t, 0, 0, Gfx.Rgba(242, 242, 242));
            var meta = CsMetaShort(g);
            if (meta.Length == 0) return;
            using var m = TextKit.Layout(ds, meta, TextKit.Ui(400), 20, w, CanvasHorizontalAlignment.Left);
            ds.DrawTextLayout(m, 0, 38, Gfx.Rgba(242, 242, 242, 0.6));
        });
    }
}
