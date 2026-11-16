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
        public required CompositionPathGeometry ClipGeo;
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
    private const float BtEdge = 40, BtTop = 104, BtBottomGap = 112, BtGap = 16, BtRadius = 24, BtLibShare = 430f / 1200f;
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
            var geo = G.C.CreatePathGeometry();
            box.Clip = G.C.CreateGeometricClip(geo);
            var cover = G.Sprite(null, relative: false);
            var over = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            var glare = G.Sprite(G.LinearGradient(90, 200, 600, (0, Gfx.Rgba(255, 255, 255, 0)), (0.5, Gfx.Rgba(255, 255, 255, 0.5)), (1, Gfx.Rgba(255, 255, 255, 0))), relative: false);
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
        CcBentoWallLayout();
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
                    // 一条斜着的光带（向右倾 24°）从左下外面扫到右上外面
                    float band = MathF.Max(r.W, r.H) * 0.32f, tall = (r.W + r.H) * 1.4f, gy = (r.H - tall) / 2;
                    t.Glare.Size = new Vector2(band, tall);
                    t.Glare.CenterPoint = new Vector3(band / 2, tall / 2, 0);
                    t.Glare.RotationAngleInDegrees = 24;
                    var a = G.C.CreateVector3KeyFrameAnimation();
                    a.InsertKeyFrame(0, new Vector3(-band - r.H * 0.5f, gy, 0), G.Linear);
                    a.InsertKeyFrame(1, new Vector3(r.W + r.H * 0.5f, gy, 0), G.Ease(EaseOutCss));
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

    // ---------------- 这一页自己的动态背景 ----------------
    // 11-16 jinyu：做一张配这页的动态壁纸，颜色还是跟封面取。
    // 一层中间调的底色（主题色相，压到四成亮）上面浮着五团封面代表色的光，各自沿很慢的闭合路线漂（24–40 s 一圈，合成线程动画，不占 UI 线程）。
    // 明暗卡在中间：浅色卡片和封面浮得起来，左上标题、右上状态、左下功能坞的白字白图标也还看得清。
    private ContainerVisual? _ccWall;
    private SpriteVisual? _ccWallBase;
    private readonly List<(SpriteVisual V, CompositionRadialGradientBrush B)> _ccWallBlobs = new();
    private string _ccWallFor = "";
    private (float W, float H) _ccWallSize;

    private void CcBentoWallEnsure()
    {
        if (_ccBentoRoot == null || _ccWall != null) return;
        _ccWall = G.C.CreateContainerVisual();
        _ccWallBase = G.Sprite(G.Solid(Gfx.Rgba(0x4a, 0x44, 0x40)), relative: false);
        _ccWall.Children.InsertAtTop(_ccWallBase);
        for (int i = 0; i < 5; i++)
        {
            var b = G.C.CreateRadialGradientBrush();
            b.ColorStops.Add(G.C.CreateColorGradientStop(0, Gfx.Rgba(0x60, 0x5a, 0x56, 0.95)));
            b.ColorStops.Add(G.C.CreateColorGradientStop(0.55f, Gfx.Rgba(0x60, 0x5a, 0x56, 0.5)));
            b.ColorStops.Add(G.C.CreateColorGradientStop(1, Gfx.Rgba(0x60, 0x5a, 0x56, 0)));
            var v = G.Sprite(b, relative: false);
            _ccWall.Children.InsertAtTop(v);
            _ccWallBlobs.Add((v, b));
        }
        _ccBentoRoot.Children.InsertAtBottom(_ccWall);
    }

    /// <summary>铺满窗口，五团光各就各位并开始漂。窗口尺寸变了才重排。</summary>
    private void CcBentoWallLayout()
    {
        CcBentoWallEnsure();
        if (_ccWall == null) return;
        float w = BtW, h = BtH;
        if (_ccWallSize == (w, h)) return;
        _ccWallSize = (w, h);
        _ccWallBase!.Size = new Vector2(w, h);
        // 位置（中心，占窗口的比例）、直径（占宽）、漂移幅度、周期（秒）
        (float X, float Y, float D, float Dx, float Dy, double T)[] spec =
        {
            (0.16f, 0.22f, 0.95f, 0.10f, 0.12f, 34),
            (0.88f, 0.12f, 0.80f, -0.12f, 0.10f, 28),
            (0.70f, 0.92f, 1.00f, -0.10f, -0.12f, 40),
            (0.08f, 0.95f, 0.70f, 0.12f, -0.08f, 24),
            (0.50f, 0.48f, 0.62f, 0.14f, 0.06f, 31),
        };
        for (int i = 0; i < _ccWallBlobs.Count; i++)
        {
            var (v, _) = _ccWallBlobs[i];
            var p = spec[i];
            float d = w * p.D;
            v.Size = new Vector2(d, d);
            var home = new Vector3(w * p.X - d / 2, h * p.Y - d / 2, 0);
            v.StopAnimation("Offset");
            v.Offset = home;
            if (Reduced) continue;
            var a = G.C.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0, home, G.Linear);
            a.InsertKeyFrame(0.25f, home + new Vector3(w * p.Dx, 0, 0), G.Ease(new Bezier(0.37f, 0f, 0.63f, 1f)));
            a.InsertKeyFrame(0.5f, home + new Vector3(w * p.Dx, h * p.Dy, 0), G.Ease(new Bezier(0.37f, 0f, 0.63f, 1f)));
            a.InsertKeyFrame(0.75f, home + new Vector3(0, h * p.Dy, 0), G.Ease(new Bezier(0.37f, 0f, 0.63f, 1f)));
            a.InsertKeyFrame(1, home, G.Ease(new Bezier(0.37f, 0f, 0.63f, 1f)));
            a.Duration = TimeSpan.FromSeconds(p.T);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            v.StartAnimation("Offset", a);
        }
    }

    /// <summary>封面换了：底色和五团光在 0.7 s 里渐变到新封面的颜色。</summary>
    private void CcBentoWallColors()
    {
        if (_ccWall == null) return;
        var acc = _ccBlocks[0].Accent;
        string key = acc + "|" + string.Join(",", _ccBentoColors.Select(c => $"{c.X:0.00}{c.Y:0.00}{c.Z:0.00}"));
        if (key == _ccWallFor) return;
        bool first = _ccWallFor.Length == 0;
        _ccWallFor = key;
        var (th, ts, _) = BtHsl(new Vector4(acc.R / 255f, acc.G / 255f, acc.B / 255f, 1));
        void To(CompositionObject o, string prop, Color c)
        {
            if (first || Reduced) { o.StopAnimation(prop); if (o is CompositionColorBrush cb) cb.Color = c; else ((CompositionColorGradientStop)o).Color = c; return; }
            var a = G.C.CreateColorKeyFrameAnimation();
            a.InsertKeyFrame(1, c, G.Ease(EaseOutCss));
            a.Duration = Gfx.Ms(700);
            o.StartAnimation(prop, a);
        }
        bool dark = BtDark;
        Log.Info($"bento colors: {(dark ? "dark" : "light")} cover={_ccGameCover}");
        var (bh, _) = BtPick(dark ? 0.2 : 0.7);
        var (_, bc0) = BtPick(0.9);
        To((CompositionColorBrush)_ccWallBase!.Brush, "Color", dark ? Ok(0.2, Math.Min(bc0, 0.03), bh) : Ok(0.7, Math.Min(bc0 * 1.4, 0.05), bh));
        double[] light = dark ? new double[] { 0.36, 0.32, 0.28, 0.24, 0.40 } : new double[] { 0.80, 0.76, 0.72, 0.66, 0.84 };
        for (int i = 0; i < _ccWallBlobs.Count; i++)
        {
            var p = _ccBentoColors;
            var (_, pc, ph) = p.Length > 0 ? ToOklch(p[i % p.Length]) : (0.5, 0.0, bh);
            double L = light[i];
            // 暗处的黄棕不要：换成主色相的近中性；其余颜色保留色相，彩度封顶 0.09
            var c = BtMuddy(ph, L) ? Ok(L, 0.012, bh) : Ok(L, Math.Min(pc * 0.8, 0.09), ph);
            var stops = _ccWallBlobs[i].B.ColorStops;
            To(stops[0], "Color", Gfx.WithAlpha(c, 0.95));
            To(stops[1], "Color", Gfx.WithAlpha(c, 0.5));
            To(stops[2], "Color", Gfx.WithAlpha(c, 0));
        }
    }

    // ---------------- 配色 ----------------

    /// <summary>
    /// 11-17 jinyu：浅色卡只适合浅色封面，深色封面要出深色的一套。按封面代表色的亮度自动选：
    /// 6 个代表色按像素多少排好，越靠前权重越大，算加权相对亮度；低于 0.40 = 深色封面。没取到色时看主题色。
    /// </summary>
    private bool BtDark
    {
        get
        {
            static double Lum(Vector4 c)
            {
                static double Lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
                return 0.2126 * Lin(c.X) + 0.7152 * Lin(c.Y) + 0.0722 * Lin(c.Z);
            }
            var p = _ccBentoColors;
            if (p.Length == 0) { var a = _ccBlocks[0].Accent; return Lum(new Vector4(a.R / 255f, a.G / 255f, a.B / 255f, 1)) < 0.18; }
            double sum = 0, wsum = 0;
            for (int i = 0; i < p.Length; i++) { double w = p.Length - i; sum += Math.Sqrt(Lum(p[i])) * w; wsum += w; }   // √ 让中间调不被压得太暗
            return sum / wsum < 0.40;
        }
    }

    /// <summary>卡上字和图标的颜色：浅色卡用同色相的深墨色，深色卡用同色相的暖白。</summary>
    private Color BtInk
    {
        get { var (h, _) = BtPick(0.5); return BtDark ? Ok(0.96, 0.012, h) : Ok(0.27, 0.035, h); }
    }

    // ---------------- 取色：OKLCH ----------------
    // 11-17 jinyu 嫌艾尔登法环的「屎黄色」。依据：
    // - 色彩偏好研究（Palmer & Schloss 2010，生态效价理论）：人对颜色的好恶来自联想到的东西，蓝 / 青（天空、水）最受欢迎，
    //   暗黄、橄榄、土棕（粪便、腐烂食物）最不受欢迎；澳洲控烟研究挑出的「最丑颜色」Pantone 448C 也是一种暗橄榄棕。
    // - 黄色只在高亮度下才是「黄」：同一个色相一压暗就变成橄榄 / 土棕（亮度一低黄色的彩度上限就塌下来）。
    //   所以「同色相压暗」对红、蓝、紫、青没问题，对橙黄绿黄这一段一定出脏色。
    // - HSL 的亮度不是人眼的亮度（黄 50% 比蓝 50% 亮得多），压暗后黄色还会发灰发闷。换成 OKLCH：L 是感知亮度，C 是彩度，h 是色相。
    // 规则：
    // 1. 在封面 6 个代表色里挑色相：面积越大越优先，彩度太低（接近灰）的分数低；
    // 2. 目标亮度低（深色一套、动态背景）时，OKLCH 色相 50°–115°（橙黄 → 黄 → 橄榄）的颜色大幅降分，
    //    优先用封面里的蓝、青、紫、红；封面里实在只有黄棕，就把彩度压到接近 0，做成带一点暖意的中性深灰，而不是棕。
    // 3. 彩度封顶：卡面 0.04 以内（干净，不抢封面），动态背景 0.09 以内。

    private static double SrgbToLin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    private static double LinToSrgb(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    private static (double L, double C, double h) ToOklch(Vector4 c)
    {
        double r = SrgbToLin(c.X), g = SrgbToLin(c.Y), b = SrgbToLin(c.Z);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        double L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        double A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        double B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        double h = Math.Atan2(B, A) * 180 / Math.PI;
        return (L, Math.Sqrt(A * A + B * B), h < 0 ? h + 360 : h);
    }

    /// <summary>OKLCH → sRGB；超出色域就降彩度直到能显示（不裁通道，免得色相跑偏）。</summary>
    private static Color Ok(double L, double C, double h)
    {
        double hr = h * Math.PI / 180;
        for (int i = 0; i < 24; i++, C *= 0.85)
        {
            double a = C * Math.Cos(hr), b = C * Math.Sin(hr);
            double l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
            double m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
            double s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
            double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
            double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
            double bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
            if (r < -1e-4 || g < -1e-4 || bl < -1e-4 || r > 1.0001 || g > 1.0001 || bl > 1.0001) continue;
            int R(double v) => (int)Math.Round(Math.Clamp(LinToSrgb(Math.Clamp(v, 0, 1)), 0, 1) * 255);
            return Gfx.Rgba(R(r), R(g), R(bl));
        }
        int n = (int)Math.Round(Math.Clamp(LinToSrgb(L * L * L), 0, 1) * 255);
        return Gfx.Rgba(n, n, n);
    }

    /// <summary>暗的时候会变脏的色相段（OKLCH）：橙黄 → 黄 → 橄榄。</summary>
    private static bool BtMuddy(double h, double targetL) => targetL < 0.86 && h >= 50 && h <= 115;   // 0.86 以下的黄已经发芥末 / 卡其；卡面浅色是 0.915，不受影响

    /// <summary>为目标亮度挑一个色相和彩度（彩度是这个色相能用的上限，调用方再按需缩）。</summary>
    private (double h, double c) BtPick(double targetL)
    {
        var src = new List<Vector4>(_ccBentoColors);
        var acc = _ccBlocks[0].Accent;
        if (src.Count == 0) src.Add(new Vector4(acc.R / 255f, acc.G / 255f, acc.B / 255f, 1));
        double best = -1, bh = 0, bc = 0;
        for (int i = 0; i < src.Count; i++)
        {
            var (_, c, h) = ToOklch(src[i]);
            double score = (src.Count - i) * (0.25 + Math.Min(c / 0.10, 1)) * (BtMuddy(h, targetL) ? 0.12 : 1);
            if (score > best) { best = score; bh = h; bc = c; }
        }
        double cap = targetL < 0.7 ? 0.04 : 0.035;
        if (BtMuddy(bh, targetL) || bc < 0.02) return (bh, 0.008);   // 只剩黄棕 / 本来就灰：近中性
        return (bh, Math.Min(bc * 0.6, cap));
    }

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
        // 11-17 jinyu：艾尔登法环取出一片屎黄。原因：拿主题色（金棕 #96703e）在 HSL 里直接压暗，黄色一压暗就是橄榄 / 土棕，
        // 正好落在色彩偏好研究里最不受欢迎的那一带（Pantone 448C、暗黄、橄榄、土棕）。改成在 OKLCH 里挑色相、控彩度，见 BtPick。
        bool dark = BtDark;
        var (h, c) = BtPick(dark ? 0.26 : 0.92);
        if (dark) return (Ok(0.25, c * 0.9, h), Ok(0.32, c, h), Ok(0.19, c * 0.8, h), Ok(0.52, c * 0.7, h));
        return (Ok(0.915, c * 0.9, h), Ok(0.965, c * 0.7, h), Ok(0.855, c, h), Ok(0.985, c * 0.4, h));
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
        CcBentoWallColors();
        string pal = _ccBlocks[0].Accent + string.Join(",", _ccBentoColors.Select(c => $"{c.X:0.00}{c.Y:0.00}{c.Z:0.00}"));
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
            t.Face.Size = t.Over.Size = t.Cover.Size = t.Box.Size = t.Size;
            using (var shape = BtShape(G.Device, 0, 0, w, h, BtRadius)) t.ClipGeo.Path = new CompositionPath(shape);
            var col = i == 0 ? (Gfx.Rgba(0x27, 0x25, 0x24), Gfx.Rgba(0x4a, 0x44, 0x40), Gfx.Rgba(0x3a, 0x38, 0x3a), Gfx.Rgba(0x8a, 0x84, 0x80)) : CcTileColors(i);
            t.Tone = i == 0 ? Gfx.Mix(b.Accent, 0.5, Gfx.Rgba(0x60, 0x5a, 0x56)) : col.Item2;
            t.Face.Opacity = 0.94f;
            int idx = i;
            CcSet(t.Face, CcDraw(w, h, raster, ds => { if (cover == null) CcPaintTileFace(ds, col, w, h, idx); }));
            CcSet(t.Over, CcDraw(w, h, raster, ds => CcPaintTileOver(ds, b, w, h, idx, cover != null, title)));
            // 焦点白边：比卡大一圈
            const float m = 6;
            t.Ring.Size = new Vector2(w + 2 * m, h + 2 * m);
            t.Ring.Offset = new Vector3(-m, -m, 0);
            CcSet(t.Ring, CcDraw(w + 2 * m, h + 2 * m, raster, ds =>
            {
                using var halo = BtShape(ds, m - 3, m - 3, w + 6, h + 6, BtRadius + 3);
                ds.DrawGeometry(halo, Gfx.Rgba(255, 255, 255, 0.16), 4);
                using var line = BtShape(ds, m - 1, m - 1, w + 2, h + 2, BtRadius + 1);
                ds.DrawGeometry(line, Gfx.Rgba(255, 255, 255, 0.92), 2);
            }));
            // 底光：卡下面一团同色的光
            t.Glow.Size = new Vector2(w * 1.3f, h * 1.3f);
            t.Glow.Offset = new Vector3(-w * 0.15f, -h * 0.15f + 18, 0);
            t.GlowBrush.ColorStops[0].Color = Gfx.WithAlpha(Colors.White, 0.16);
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
        using var clip = BtShape(ds, 0, 0, w, h, BtRadius);
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
        bool dark = BtDark;
        var ink = BtInk;
        var paper = CcTileColors(1).Base;
        const float pad = 22;
        // 封面卡：底部渐变到和功能卡同一个浅色（字写在这块浅色上，深墨色），左上角图标后面一小片浅色。
        // 11-16 jinyu：之前是按深色页面做的压黑 + 白字，页面改成浅色调后不搭。
        // 不在这里裁圆角（外面的 Box 已经按同一个形状裁了），并且画出边界一圈，边缘不漏没盖住的封面。
        if (cover)
        {
            using (var g = new CanvasLinearGradientBrush(ds, Gfx.WithAlpha(paper, 0), Gfx.WithAlpha(paper, 0.97)) { StartPoint = new Vector2(0, h * 0.56f), EndPoint = new Vector2(0, h * 0.84f) })
                ds.FillRectangle(-2, -2, w + 4, h + 4, g);
            using (var g = new CanvasRadialGradientBrush(ds, Gfx.WithAlpha(paper, 0.85), Gfx.WithAlpha(paper, 0)) { Center = new Vector2(pad + 15, pad + 15), RadiusX = 70, RadiusY = 70 })
                ds.FillRectangle(-2, -2, w + 4, h + 4, g);
        }
        // 内沿：一圈很淡的边，上沿亮一点。封面卡不画：亮色封面上这圈会变成一道白边
        using var rim = BtShape(ds, 0.5f, 0.5f, w - 1, h - 1, BtRadius);
        if (!cover) ds.DrawGeometry(rim, dark ? Gfx.Rgba(255, 255, 255, 0.10) : Gfx.Rgba(0, 0, 0, 0.08), 1);
        if (!cover) using (var g = new CanvasLinearGradientBrush(ds, Gfx.Rgba(255, 255, 255, dark ? 0.28 : 0.9), Gfx.Rgba(255, 255, 255, 0)) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, MathF.Min(h * 0.3f, 60)) })
            ds.DrawGeometry(rim, g, 1);

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
            ds.DrawTextLayout(s, pad, bottom - (float)s.LayoutBounds.Y, Gfx.WithAlpha(ink, 0.74f));
            bottom -= 6;
        }
        using var name = TextKit.Layout(ds, b.Name, TextKit.Ui(700), i == 0 ? 30 : 22, w - pad * 2, CanvasHorizontalAlignment.Left);
        bottom -= (float)name.LayoutBounds.Height;
        ds.DrawTextLayout(name, pad, bottom - (float)name.LayoutBounds.Y, ink);
    }

    /// <summary>连续曲率圆角（苹果那种）：四个角是超椭圆弧（n = 4.6），占的边长比同半径的圆角长一截，和直边之间没有突变。</summary>
    private static CanvasGeometry BtShape(ICanvasResourceCreator rc, float x, float y, float w, float h, float r)
    {
        float a = MathF.Min(r * 1.52f, MathF.Min(w, h) / 2);
        const float n = 4.6f; const int steps = 18;
        using var pb = new CanvasPathBuilder(rc);
        // 一个角：k = 0 在 (a, 0)，k = steps 在 (0, a)（相对角点，朝里为正）
        Vector2 P(int k) { float t = k / (float)steps * MathF.PI / 2; return new Vector2(a * (1 - MathF.Pow(MathF.Max(0, MathF.Sin(t)), 2 / n)), a * (1 - MathF.Pow(MathF.Max(0, MathF.Cos(t)), 2 / n))); }
        pb.BeginFigure(x, y + a);
        for (int k = steps - 1; k >= 0; k--) { var p = P(k); pb.AddLine(x + p.X, y + p.Y); }                       // 左上
        pb.AddLine(x + w - a, y);
        for (int k = 1; k <= steps; k++) { var p = P(k); pb.AddLine(x + w - p.X, y + p.Y); }               // 右上
        pb.AddLine(x + w, y + h - a);
        for (int k = steps - 1; k >= 0; k--) { var p = P(k); pb.AddLine(x + w - p.X, y + h - p.Y); }               // 右下
        pb.AddLine(x + a, y + h);
        for (int k = 1; k <= steps; k++) { var p = P(k); pb.AddLine(x + p.X, y + h - p.Y); }               // 左下
        pb.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(pb);
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
