// 首页样式「单行」（11-19 jinyu：照他安卓上 Cocoon 前端的样子，封面换成我们的竖封面）。
// 只用他的设置备份、截图实测和公开数值写的规格（cocoon-study\SPEC_单行首页.md），没有拿对方的代码或素材。
// - 背景 = Windows 桌面壁纸，铺满裁切，不压暗；
// - 底部一排竖封面 280×420（2:3），圆角 = 宽 × 0.174，卡距 50，左 62，底 46；焦点那张按中心放大 1.15；
// - 移动：临界阻尼弹簧（ζ = 1，刚度 600），可打断；
// - 焦点框：四角括号（圆弧 + 两条直腿），紫色光晕 + 沿周长流动的两色渐变（1.8 s 一圈、45 档）+ 40 % 白边光；
// - 顶部两块灰色磨砂面板：左 LB · 头像 · 账号，右 RB · Wi-Fi · 电量 · 时间；右上 ··· 圆按钮。面板像素按截图拟合（规格 §7）。
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.UI;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private const float RwW = 280, RwH = 420, RwPitch = 330, RwX = 62, RwBottom = 64, RwFocus = 1.15f, RwRound = 0.17391305f;
    private const float RwY = CsH - RwBottom - RwH;
    /// <summary>Cocoon 设备上 1 dp ≈ 1.75 px（截图反推）；规格里的 dp 数值都乘这个换成我们的设计单位。</summary>
    private const float RwDp = 1.75f;
    private const float RwThick = 0.6f, RwSw = 8 * RwDp * RwThick;
    private static readonly Color RwC1 = Color.FromArgb(255, 0x86, 0xA7, 0xFD), RwC2 = Color.FromArgb(255, 0x80, 0x37, 0xFF);
    private const double RwPeriodMs = 1800;
    private const int RwSteps = 45;
    // 随包字体只在打包运行时能按 ms-appx 加载；不打包的录屏版退回系统字体（只影响自测录屏的字形）
    private static readonly bool RwPackaged = RwIsPackaged();
    private static bool RwIsPackaged() { try { return Windows.ApplicationModel.Package.Current != null; } catch { return false; } }
    private static readonly string RwPoppins = RwPackaged ? "ms-appx:///Assets/Fonts/Poppins-SemiBold.ttf#Poppins" : "Segoe UI Variable Display";
    private static readonly string RwPoppinsXb = RwPackaged ? "ms-appx:///Assets/Fonts/Poppins-ExtraBold.ttf#Poppins" : "Segoe UI Variable Display";
    private static readonly string RwPoppinsB = RwPackaged ? "ms-appx:///Assets/Fonts/Poppins-Bold.ttf#Poppins" : "Segoe UI Variable Display";
    private static readonly string RwCjk = RwPackaged ? "ms-appx:///Assets/Fonts/PingFangSC-Medium.otf#PingFang SC" : "Microsoft YaHei UI";

    // 面板（设计单位，按截图实测）：x, y, w, h, 圆角, 种类（0 = 灰色磨砂状态面板，1 = 淡灰圆按钮）
    private static readonly (float X, float Y, float W, float H, float R, int Kind) RwPanelL = (38, 23, 281, 112, 36, 0);
    private static readonly (float X, float Y, float W, float H, float R, int Kind) RwMore = (1818, 187, 74, 74, 37, 1);
    private const float RwPanelRRight = 1882, RwClockW0 = 168;
    private const float RwPanelPad = 20;   // 面板贴图四周留给投影的边

    private sealed class RwChrome
    {
        public required ContainerVisual Root;
        public required SpriteVisual Wall, PanelL, PanelR, More, Glow, Line, Rim;
        public CanvasRenderTarget? Sharp, Soft;   // 壁纸原图 / 模糊后的像素（画面板用）
        public CompositionDrawingSurface? WallSurf, LSurf, RSurf, MoreSurf, GlowSurf, LineSurf, RimSurf;
        public DispatcherQueueTimer? Tick;
        public int Phase = -1;
        public string Drawn = "";
        public float Raster;
        public bool Disposed;
    }

    // ---------------- 页面 ----------------

    /// <summary>「单行」这一页的背景和顶部面板、焦点框（都在 page.Root 里，跟着页面一起淡入淡出、拆掉）。</summary>
    private ContainerVisual RwBuild(CsPage page, bool entry)
    {
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        var wall = G.Sprite(G.Solid(Gfx.Rgba(0x1b, 0x1b, 0x22)), relative: false);
        wall.Size = new Vector2(CsW, CsH);
        root.Children.InsertAtTop(wall);
        SpriteVisual Sp() { var s = G.Sprite(G.C.CreateSurfaceBrush(), relative: false); return s; }
        var rw = new RwChrome { Root = root, Wall = wall, PanelL = Sp(), PanelR = Sp(), More = Sp(), Glow = Sp(), Line = Sp(), Rim = Sp() };
        page.Rw = rw;
        RwLoadWallpaper(page, entry);
        return root;
    }

    /// <summary>焦点框三层和顶部面板放在内容层（卡片上面）。</summary>
    private void RwAttach(CsPage page)
    {
        if (page.Rw is not { } rw) return;
        foreach (var v in new Visual[] { rw.PanelL, rw.PanelR, rw.More }) page.Content.Children.InsertAtTop(v);
        foreach (var v in new Visual[] { rw.Glow, rw.Line, rw.Rim }) page.Content.Children.InsertAtTop(v);
        RwPaintCursorStatic(page);
        rw.Tick = _dq.CreateTimer();
        rw.Tick.Interval = TimeSpan.FromMilliseconds(20);
        rw.Tick.Tick += (_, _) => RwTick(page);
        rw.Tick.Start();
    }

    private void RwDispose(CsPage page)
    {
        if (page.Rw is not { } rw || rw.Disposed) return;
        rw.Disposed = true;
        rw.Tick?.Stop();
        rw.Sharp?.Dispose(); rw.Soft?.Dispose();
        foreach (var s in new[] { rw.WallSurf, rw.LSurf, rw.RSurf, rw.MoreSurf, rw.GlowSurf, rw.LineSurf, rw.RimSurf }) s?.Dispose();
    }

    /// <summary>每 20 ms：焦点框流光换一档（只在档位变了时重画），整分钟 / 电量 / 网络变了重画右面板。</summary>
    private void RwTick(CsPage page)
    {
        if (page.Rw is not { } rw || rw.Disposed) return;
        if (_cs == null || (_cs.Page != page && !_cs.Leaving.Contains(page))) { rw.Tick?.Stop(); return; }
        int phase = Reduced ? 0 : (int)(NowMs % (long)RwPeriodMs * RwSteps / (long)RwPeriodMs);
        if (phase != rw.Phase) { rw.Phase = phase; RwPaintCursorLine(page, phase / (float)RwSteps); }
        if (rw.Sharp != null && RwStatusKey() != rw.Drawn) RwPaintPanels(page);
    }

    // ---------------- 壁纸 ----------------

    private static string? RwWallpaperPath()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (k?.GetValue("WallPaper") is string p && p.Length > 0 && File.Exists(p)) return p;
        }
        catch (Exception ex) { Log.Warn($"row wallpaper reg: {ex.Message}"); }
        string t = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(t) ? t : null;
    }

    private void RwLoadWallpaper(CsPage page, bool entry)
    {
        var rw = page.Rw!;
        string? path = RwWallpaperPath();
        var dev = G.Device;
        float R = CsRaster;
        rw.Raster = R;
        _ = Task.Run(async () =>
        {
            CanvasBitmap? bmp = null;
            try
            {
                if (path != null)
                {
                    // 拷到临时文件再读：壁纸可能正被资源管理器占着
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    await fs.CopyToAsync(ms);
                    ms.Position = 0;
                    bmp = await CanvasBitmap.LoadAsync(dev, ms.AsRandomAccessStream(), 96);
                }
            }
            catch (Exception ex) { Log.Warn($"row wallpaper {path}: {ex.Message}"); }
            _dq.TryEnqueue(() =>
            {
                try
                {
                    if (rw.Disposed) { bmp?.Dispose(); return; }
                    int pw = (int)Math.Ceiling(CsW * R), ph = (int)Math.Ceiling(CsH * R);
                    rw.Sharp = new CanvasRenderTarget(dev, pw, ph, 96);
                    rw.Soft = new CanvasRenderTarget(dev, pw, ph, 96);
                    using (var ds = rw.Sharp.CreateDrawingSession())
                    {
                        ds.Clear(Gfx.Rgba(0x1b, 0x1b, 0x22));
                        if (bmp != null)
                        {
                            double cover = Math.Max(pw / bmp.Bounds.Width, ph / bmp.Bounds.Height);
                            var dest = new Windows.Foundation.Rect((pw - bmp.Bounds.Width * cover) / 2, (ph - bmp.Bounds.Height * cover) / 2, bmp.Bounds.Width * cover, bmp.Bounds.Height * cover);
                            ds.DrawImage(bmp, dest, bmp.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                        }
                    }
                    using (var ds = rw.Soft.CreateDrawingSession())
                    using (var blur = new GaussianBlurEffect { Source = rw.Sharp, BlurAmount = 3 * RwDp * R, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Quality })
                        ds.DrawImage(blur);
                    bmp?.Dispose();
                    rw.WallSurf = G.Surface(pw, ph);
                    using (var ds = CanvasComposition.CreateDrawingSession(rw.WallSurf)) ds.DrawImage(rw.Sharp);
                    rw.Wall.Brush = G.C.CreateSurfaceBrush(rw.WallSurf);
                    RwPaintPanels(page);
                }
                catch (Exception ex) { Log.Error("row wallpaper paint", ex); }
                CsBgReady(page, entry);
            });
        });
    }

    // ---------------- 顶部面板 ----------------

    private string RwStatusKey()
    {
        var b = SystemInfo.Battery();
        var n = SystemInfo.Network();
        var now = DateTime.Now;
        return $"{now:hh:mm tt}|{b.Percent}|{b.Charging}|{n.Online}|{n.Wlan}|{_railModel.Avatar != null}|{CsRaster}";
    }

    private static string RwClock(DateTime t) => $"{(t.Hour % 12 == 0 ? 12 : t.Hour % 12)}:{t.Minute:00}";

    private void RwPaintPanels(CsPage page)
    {
        if (page.Rw is not { } rw || rw.Sharp == null || rw.Soft == null) return;
        rw.Drawn = RwStatusKey();
        var dev = G.Device;
        float R = rw.Raster;
        var now = DateTime.Now;
        string clock = RwClock(now), ampm = now.Hour < 12 ? "上午" : "下午";
        // 时间变宽 / 变窄时右面板向左长（右边缘固定），里面的东西跟着整体平移
        float clockW = RwClockWidth(dev, clock, ampm);
        float dx = MathF.Round(clockW - RwClockW0);
        var panelR = (X: 1428 - dx, Y: 23f, W: 454 + dx, H: 112f, R: 36f, Kind: 0);
        RwPanel(rw, ref rw.LSurf, rw.PanelL, RwPanelL, R, ds => RwDrawLeft(ds));
        RwPanel(rw, ref rw.RSurf, rw.PanelR, panelR, R, ds => RwDrawRight(ds, -dx, clock, ampm));
        RwPanel(rw, ref rw.MoreSurf, rw.More, RwMore, R, ds => RwDrawMore(ds));
    }

    private float RwClockWidth(ICanvasResourceCreator dev, string clock, string ampm)
    {
        using var f1 = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 39, FontWeight = new Windows.UI.Text.FontWeight(600) };
        using var f2 = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 37.5f };
        using var l1 = new CanvasTextLayout(dev, clock, f1, 1000, 100);
        using var l2 = new CanvasTextLayout(dev, ampm, f2, 1000, 100);
        return (float)(l1.DrawBounds.Width + 16.5 + l2.DrawBounds.Width);
    }

    /// <summary>
    /// 一块面板：从壁纸像素算出磨砂（模糊 → 去饱和 40 % → 叠 20 % 深灰）、上重下轻的内光带、只在下方的投影、
    /// 按覆盖率抗锯齿的边；再把图标和文字画上去。坐标都是设计单位，像素 = 设计单位 × R。
    /// </summary>
    private void RwPanel(RwChrome rw, ref CompositionDrawingSurface? surf, SpriteVisual sprite,
        (float X, float Y, float W, float H, float R, int Kind) p, float R, Action<CanvasDrawingSession> content)
    {
        var dev = G.Device;
        float pad = RwPanelPad;
        int x0 = Math.Max(0, (int)Math.Floor((p.X - pad) * R)), y0 = Math.Max(0, (int)Math.Floor((p.Y - pad) * R));
        int x1 = Math.Min((int)rw.Sharp!.SizeInPixels.Width, (int)Math.Ceiling((p.X + p.W + pad) * R));
        int y1 = Math.Min((int)rw.Sharp.SizeInPixels.Height, (int)Math.Ceiling((p.Y + p.H + pad) * R));
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return;
        byte[] sharp = rw.Sharp.GetPixelBytes(x0, y0, w, h), soft = rw.Soft!.GetPixelBytes(x0, y0, w, h);
        var outPx = new byte[w * h * 4];
        Vector2 hs = new(p.W / 2, p.H / 2), c = new(p.X + hs.X, p.Y + hs.Y);
        float rad = p.R;
        static float Sd(Vector2 q, Vector2 hh, float r) { var a = Vector2.Abs(q) - (hh - new Vector2(r)); return Vector2.Max(a, Vector2.Zero).Length() - r + MathF.Min(MathF.Max(a.X, a.Y), 0); }
        static float Sst(float e0, float e1, float x) { float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }
        for (int j = 0; j < h; j++)
        for (int i = 0; i < w; i++)
        {
            int k = (j * w + i) * 4;
            var pt = new Vector2((x0 + i + 0.5f) / R, (y0 + j + 0.5f) / R);
            float best = Sd(pt - c, hs, rad);
            // BGRA
            float b = sharp[k] / 255f, g = sharp[k + 1] / 255f, r = sharp[k + 2] / 255f;
            if (p.Kind == 0 && best > -1)
            {
                float s = Sd(pt - c - new Vector2(0, 6), hs - new Vector2(3), MathF.Max(rad - 3, 0));
                float sh = 1 - 0.22f * MathF.Pow(1 - Sst(-2, 12, s), 2);
                sh *= 1 - 0.03f * MathF.Exp(-MathF.Max(best, 0) * 1.2f);
                r *= sh; g *= sh; b *= sh;
            }
            if (best < 0.5f / R + 0.5f)
            {
                float cover = Math.Clamp(0.5f - best * R, 0, 1);
                float depth = MathF.Max(-best, 0);
                float gb = soft[k] / 255f, gg = soft[k + 1] / 255f, gr = soft[k + 2] / 255f;
                if (p.Kind == 0)
                {
                    float l = 0.299f * gr + 0.587f * gg + 0.114f * gb;
                    gr += (l - gr) * 0.4f; gg += (l - gg) * 0.4f; gb += (l - gb) * 0.4f;
                    gr += (0.353f - gr) * 0.2f; gg += (0.353f - gg) * 0.2f; gb += (0.37f - gb) * 0.2f;
                    // 法线（数值梯度）：光从上方来，上边宽而亮，左右窄，底边一条细线
                    const float e = 0.5f;
                    var n = new Vector2(Sd(pt - c + new Vector2(e, 0), hs, rad) - Sd(pt - c - new Vector2(e, 0), hs, rad),
                                        Sd(pt - c + new Vector2(0, e), hs, rad) - Sd(pt - c - new Vector2(0, e), hs, rad));
                    n = n.LengthSquared() > 1e-8f ? Vector2.Normalize(n) : new Vector2(0, -1);
                    float up = MathF.Pow(MathF.Max(0, -n.Y), 2), side = n.X * n.X, down = MathF.Pow(MathF.Max(0, n.Y), 2);
                    float add = 0.035f * up * MathF.Exp(-depth * 1.4f);
                    float amp = 0.24f * up + 0.14f * side + 0.13f * down, wid = MathF.Max(12 * up + 5 * side + 3 * down, 0.5f);
                    add += amp * MathF.Pow(Math.Clamp(1 - depth / wid, 0, 1), 1.35f);
                    gr += add; gg += add; gb += add;
                }
                else
                {
                    gr += (0.43f - gr) * 0.2f; gg += (0.43f - gg) * 0.2f; gb += (0.49f - gb) * 0.2f;
                }
                r += (gr - r) * cover; g += (gg - g) * cover; b += (gb - b) * cover;
            }
            outPx[k] = (byte)Math.Clamp(b * 255 + 0.5f, 0, 255);
            outPx[k + 1] = (byte)Math.Clamp(g * 255 + 0.5f, 0, 255);
            outPx[k + 2] = (byte)Math.Clamp(r * 255 + 0.5f, 0, 255);
            outPx[k + 3] = 255;
        }
        using var bmp = CanvasBitmap.CreateFromBytes(dev, outPx, w, h, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        if (surf == null || (int)surf.Size.Width != w || (int)surf.Size.Height != h) { surf?.Dispose(); surf = G.Surface(w, h); }
        using (var ds = CanvasComposition.CreateDrawingSession(surf))
        {
            ds.Clear(Colors.Transparent);
            ds.DrawImage(bmp);
            ds.Transform = Matrix3x2.CreateTranslation(-x0 / R, -y0 / R) * Matrix3x2.CreateScale(R);
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            content(ds);
        }
        ((CompositionSurfaceBrush)sprite.Brush).Surface = surf;
        sprite.Size = new Vector2(w / R, h / R);
        sprite.Offset = new Vector3(x0 / R, y0 / R, 0);
    }

    private static readonly Color RwWhite = Color.FromArgb(255, 0xfa, 0xfa, 0xfa), RwInk = Color.FromArgb(255, 0x21, 0x21, 0x21);

    /// <summary>LB / RB：上圆下方的小墓碑（33×38，上圆角 16.5、下圆角 7），深灰半透明底 + 浅灰粗体字。</summary>
    private static void RwShoulder(CanvasDrawingSession ds, float x, float y, float w, float h, string label)
    {
        float rt = w / 2, rb = 7;
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(x, y + rt);
        pb.AddArc(new Vector2(x + w, y + rt), rt, rt, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        pb.AddLine(x + w, y + h - rb);
        pb.AddArc(new Vector2(x + w - rb, y + h), rb, rb, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        pb.AddLine(x + rb, y + h);
        pb.AddArc(new Vector2(x, y + h - rb), rb, rb, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var geo = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(geo, Color.FromArgb(133, 62, 64, 76));
        using var f = new CanvasTextFormat { FontFamily = RwPoppinsB, FontSize = 19, FontWeight = new Windows.UI.Text.FontWeight(700), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        ds.DrawText(label, new Windows.Foundation.Rect(x - 4, y + 2, w + 8, h), Color.FromArgb(255, 0xA9, 0xAD, 0xBA), f);
    }

    /// <summary>贴纸：同一组图形先描 10 px 白边（圆角接头）带一点投影，再填色。</summary>
    private static void RwSticker(CanvasDrawingSession ds, CanvasGeometry outline, float strokeW)
    {
        var dev = ds.Device;
        using var cl = new CanvasCommandList(dev);
        using (var s = cl.CreateDrawingSession())
        {
            using var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round, StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
            s.FillGeometry(outline, RwWhite);
            s.DrawGeometry(outline, RwWhite, strokeW, style);
        }
        var t = ds.Transform;
        float k = t.M11;
        using (var sh = new ShadowEffect { Source = cl, BlurAmount = 1.8f * k, ShadowColor = Color.FromArgb(77, 0x3c, 0x3c, 0x5a) })
        {
            ds.DrawImage(sh, 0, 1.6f);
        }
        ds.DrawImage(cl);
    }

    private void RwDrawLeft(CanvasDrawingSession ds)
    {
        var dev = ds.Device;
        RwShoulder(ds, 77, 60, 33, 38, "LB");
        // 头像：直径 75，白圈 7.5
        float ax = 141, ay = 42.5f, ad = 75;
        using (var sh = new CanvasCommandList(dev))
        {
            using (var s = sh.CreateDrawingSession()) s.FillCircle(ax + ad / 2, ay + ad / 2, ad / 2, Colors.Black);
            using var se = new ShadowEffect { Source = sh, BlurAmount = 1.5f * ds.Transform.M11, ShadowColor = Color.FromArgb(56, 40, 40, 60) };
            ds.DrawImage(se, 0, 1.5f);
        }
        ds.FillCircle(ax + ad / 2, ay + ad / 2, ad / 2, RwWhite);
        float ir = ad / 2 - 7.5f;
        if (_railModel.Avatar is { } av)
        {
            using var clip = CanvasGeometry.CreateCircle(dev, ax + ad / 2, ay + ad / 2, ir);
            using (ds.CreateLayer(1, clip))
            {
                double side = Math.Min(av.Bounds.Width, av.Bounds.Height);
                var src = new Windows.Foundation.Rect((av.Bounds.Width - side) / 2, (av.Bounds.Height - side) / 2, side, side);
                ds.DrawImage(av, new Windows.Foundation.Rect(ax + ad / 2 - ir, ay + ad / 2 - ir, ir * 2, ir * 2), src, 1, CanvasImageInterpolation.HighQualityCubic);
            }
        }
        else
        {
            using var gb = new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(0, ay), EndPoint = new Vector2(0, ay + ad) };
            ds.FillCircle(ax + ad / 2, ay + ad / 2, ir, gb);
        }
        // 账号：头 r13.8 @ (263.5, 72)，肩 243–284 × 86–100；主题色渐变
        using var head = CanvasGeometry.CreateCircle(dev, 263.5f, 72, 13.8f);
        using var pb = new CanvasPathBuilder(dev);
        pb.BeginFigure(243.2f, 98.2f);
        pb.AddQuadraticBezier(new Vector2(244.6f, 86.3f), new Vector2(257, 86));
        pb.AddLine(270, 86);
        pb.AddQuadraticBezier(new Vector2(282.4f, 86.3f), new Vector2(283.8f, 98.2f));
        pb.AddQuadraticBezier(new Vector2(284, 100), new Vector2(282, 100));
        pb.AddLine(245, 100);
        pb.AddQuadraticBezier(new Vector2(243, 100), new Vector2(243.2f, 98.2f));
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var body = CanvasGeometry.CreatePath(pb);
        using var both = body.CombineWith(head, Matrix3x2.Identity, CanvasGeometryCombine.Union);
        RwSticker(ds, both, 10);
        using var acc = new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(0, 58), EndPoint = new Vector2(0, 100) };
        ds.FillGeometry(body, acc);
        ds.FillCircle(263.5f, 72, 16, RwWhite);
        ds.FillGeometry(head, acc);
    }

    private void RwDrawRight(CanvasDrawingSession ds, float shift, string clock, string ampm)
    {
        var dev = ds.Device;
        var t0 = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(shift, 0) * t0;
        RwShoulder(ds, 1467, 61, 34, 37, "RB");
        // Wi-Fi：右下一个点 + 三段四分之一圆弧（圆心 = 点，半径 11 / 20 / 29.5，线宽 5.2）
        var net = SystemInfo.Network();
        bool on = net.Online || net.Wlan || net.Wired;
        var wc = new Vector2(1573.2f, 93.8f);
        using (var pb = new CanvasPathBuilder(dev))
        {
            foreach (float r in new[] { 11f, 20f, 29.5f })
            {
                pb.BeginFigure(wc.X - r, wc.Y);
                pb.AddArc(new Vector2(wc.X, wc.Y - r), r, r, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
                pb.EndFigure(CanvasFigureLoop.Open);
            }
            using var arcs = CanvasGeometry.CreatePath(pb);
            using var thick = arcs.Stroke(5.2f, new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round });
            using var dot = CanvasGeometry.CreateCircle(dev, wc, 4.7f);
            using var all = thick.CombineWith(dot, Matrix3x2.Identity, CanvasGeometryCombine.Union);
            RwSticker(ds, all, 10);
            using var wb = on ? new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0x8C, 0xC9, 0xF9), Color.FromArgb(255, 0x5B, 0xA3, 0xFF)) { StartPoint = new Vector2(0, 62), EndPoint = new Vector2(0, 98) }
                              : new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xC6, 0xC8, 0xD0), Color.FromArgb(255, 0xA4, 0xA7, 0xB2)) { StartPoint = new Vector2(0, 62), EndPoint = new Vector2(0, 98) };
            ds.FillGeometry(all, wb);
        }
        // 电量：橙色电池（右侧小正极）+ 前面一块白牌写百分比，充电时加小闪电
        var bat = SystemInfo.Battery();
        using (var body = CanvasGeometry.CreateRoundedRectangle(dev, 1611, 65, 44.5f, 30, 7, 7))
        using (var nub = CanvasGeometry.CreateRoundedRectangle(dev, 1652, 71.5f, 6, 7, 2, 2))
        using (var badge = CanvasGeometry.CreateRoundedRectangle(dev, 1613.5f, 74, 44.5f, 27, 13.5f, 13.5f))
        using (var cell = body.CombineWith(nub, Matrix3x2.Identity, CanvasGeometryCombine.Union))
        using (var all = cell.CombineWith(badge, Matrix3x2.Identity, CanvasGeometryCombine.Union))
        {
            RwSticker(ds, all, 9);
            using var ob = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xFF, 0xC0, 0x9C), Color.FromArgb(255, 0xFF, 0x8A, 0x28)) { StartPoint = new Vector2(0, 64), EndPoint = new Vector2(0, 100) };
            ds.FillGeometry(cell, ob);
            ds.FillGeometry(badge, RwWhite);
            string pct = bat.HasBattery ? Math.Clamp(bat.Percent, 0, 100).ToString() : "AC";
            using var nf = new CanvasTextFormat { FontFamily = RwPoppinsXb, FontSize = pct.Length > 2 ? 18 : 24, FontWeight = new Windows.UI.Text.FontWeight(800), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
            using var nb = new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xFF, 0xBE, 0x95), Color.FromArgb(255, 0xFF, 0x8F, 0x30)) { StartPoint = new Vector2(0, 82), EndPoint = new Vector2(0, 100) };
            ds.DrawText(pct, new Windows.Foundation.Rect(1614, 73, bat.Charging ? 38 : 44, 31), nb, nf);
            if (bat.Charging)
            {
                using var pb = new CanvasPathBuilder(dev);
                pb.BeginFigure(1655.6f, 86); pb.AddLine(1650.2f, 92.4f); pb.AddLine(1653.6f, 92.4f); pb.AddLine(1651.8f, 96.8f); pb.AddLine(1657.4f, 90.2f); pb.AddLine(1654, 90.2f);
                pb.EndFigure(CanvasFigureLoop.Closed);
                using var bolt = CanvasGeometry.CreatePath(pb);
                ds.FillGeometry(bolt, Color.FromArgb(255, 0xFF, 0xC2, 0xA2));
            }
        }
        // 时间：Poppins SemiBold 39（数字顶 66、底 93），后面「上午 / 下午」PingFang 37.5，间距 11
        using var f1 = new CanvasTextFormat { FontFamily = RwPoppins, FontSize = 39, FontWeight = new Windows.UI.Text.FontWeight(600) };
        using var f2 = new CanvasTextFormat { FontFamily = RwCjk, FontSize = 37.5f };
        using var l1 = new CanvasTextLayout(dev, clock, f1, 1000, 100);
        using var l2 = new CanvasTextLayout(dev, ampm, f2, 1000, 100);
        // 按墨迹定位（截图实测：数字底 93，「上午」底 96）；LineMetrics 在裁剪发布里取不到
        var d1 = l1.DrawBounds; var d2 = l2.DrawBounds;
        ds.DrawTextLayout(l1, 1683.5f - (float)d1.X, 93 - (float)(d1.Y + d1.Height), RwInk);
        ds.DrawTextLayout(l2, 1683.5f + (float)d1.Width + 16.5f - (float)d2.X, 96 - (float)(d2.Y + d2.Height), RwInk);
        ds.Transform = t0;
    }

    private static void RwDrawMore(CanvasDrawingSession ds)
    {
        for (int i = 0; i < 3; i++) ds.FillRoundedRectangle(1832 + i * 16.3f, 217, 14, 14, 4.5f, 4.5f, RwInk);
    }

    // ---------------- 焦点框：四角括号 ----------------

    /// <summary>焦点卡的大小固定（1.15 倍的卡），所以括号画一次；移动时只挪精灵。</summary>
    private static (float W, float H, float Out, float R, float Leg) RwBracketGeom()
    {
        float w = RwW * RwFocus, h = RwH * RwFocus;
        float o = Math.Clamp(0.12f * w, 5 * RwDp, 24 * RwDp) * RwThick;
        float W = w + o, H = h + o;
        float r = MathF.Min(w * RwRound + o / 2, MathF.Min(W, H) / 2);
        float sc = Math.Clamp(MathF.Min(W, H) / MathF.Min(w, h), 0.35f, 1.75f);
        float gap = MathF.Min(14 * RwDp * sc, 0.36f * MathF.Min(W, H));
        float leg = MathF.Max(0, MathF.Min(18 * RwDp * sc, (MathF.Min(W, H) - 2 * r - gap) / 2));
        return (W, H, o, r, leg);
    }

    private const float RwCurPad = 44;   // 精灵四周给光晕留的边

    /// <summary>四个括号的采样点：(x, y, 周长位置 s)，s 从上边中点顺时针。</summary>
    private static (List<List<Vector3>> Segs, float P) RwBracketPoints()
    {
        var (W, H, _, r, leg) = RwBracketGeom();
        float x0 = RwCurPad, y0 = RwCurPad;
        float sx = W - 2 * r, sy = H - 2 * r, q = MathF.PI * r / 2, P = 2 * sx + 2 * sy + 4 * q;
        List<Vector3> Line(float ax, float ay, float bx, float by, float s0, float len)
        {
            int n = Math.Max(2, (int)MathF.Ceiling(len / 3)); var a = new List<Vector3>();
            for (int i = 0; i <= n; i++) { float t = i / (float)n; a.Add(new Vector3(ax + (bx - ax) * t, ay + (by - ay) * t, s0 + len * t)); }
            return a;
        }
        List<Vector3> Arc(float cx, float cy, float a0, float s0)
        {
            var a = new List<Vector3>();
            for (int i = 0; i <= 24; i++) { float t = i / 24f, an = a0 + t * MathF.PI / 2; a.Add(new Vector3(cx + r * MathF.Cos(an), cy + r * MathF.Sin(an), s0 + q * t)); }
            return a;
        }
        float top = sx / 2;
        var segs = new List<List<Vector3>>();
        float s = top - leg;
        segs.Add([.. Line(x0 + W - r - leg, y0, x0 + W - r, y0, s, leg), .. Arc(x0 + W - r, y0 + r, -MathF.PI / 2, top), .. Line(x0 + W, y0 + r, x0 + W, y0 + r + leg, top + q, leg)]);
        s = top + q + sy - leg;
        segs.Add([.. Line(x0 + W, y0 + H - r - leg, x0 + W, y0 + H - r, s, leg), .. Arc(x0 + W - r, y0 + H - r, 0, s + leg), .. Line(x0 + W - r, y0 + H, x0 + W - r - leg, y0 + H, s + leg + q, leg)]);
        s = top + 2 * q + sy + sx - leg;
        segs.Add([.. Line(x0 + r + leg, y0 + H, x0 + r, y0 + H, s, leg), .. Arc(x0 + r, y0 + H - r, MathF.PI / 2, s + leg), .. Line(x0, y0 + H - r, x0, y0 + H - r - leg, s + leg + q, leg)]);
        s = top + 3 * q + 2 * sy + sx - leg;
        segs.Add([.. Line(x0, y0 + r + leg, x0, y0 + r, s, leg), .. Arc(x0 + r, y0 + r, MathF.PI, s + leg), .. Line(x0 + r, y0, x0 + r + leg, y0, (s + leg + q) % P, leg)]);
        return (segs, P);
    }

    private static CanvasGeometry RwBracketPath(ICanvasResourceCreator dev, List<List<Vector3>> segs)
    {
        using var pb = new CanvasPathBuilder(dev);
        foreach (var sg in segs)
        {
            pb.BeginFigure(sg[0].X, sg[0].Y);
            for (int i = 1; i < sg.Count; i++) pb.AddLine(sg[i].X, sg[i].Y);
            pb.EndFigure(CanvasFigureLoop.Open);
        }
        return CanvasGeometry.CreatePath(pb);
    }

    private static readonly CanvasStrokeStyle RwRoundStroke = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

    /// <summary>光晕（2 倍线宽、第二色、模糊 7 dp）和边光（沿线的外轮廓再描 2 dp，#FAFAFA 40 %）：不随时间变，画一次。</summary>
    private void RwPaintCursorStatic(CsPage page)
    {
        var rw = page.Rw!;
        var dev = G.Device;
        float R = CsRaster;
        var (W, H, _, _, _) = RwBracketGeom();
        float sw = W + RwCurPad * 2, shh = H + RwCurPad * 2;
        int pw = (int)Math.Ceiling(sw * R), ph = (int)Math.Ceiling(shh * R);
        var (segs, _) = RwBracketPoints();
        using var path = RwBracketPath(dev, segs);
        rw.GlowSurf ??= G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rw.GlowSurf))
        {
            ds.Clear(Colors.Transparent);
            using var cl = new CanvasCommandList(ds);
            using (var s = cl.CreateDrawingSession()) { s.Transform = Matrix3x2.CreateScale(R); s.DrawGeometry(path, RwC2, RwSw * 2, RwRoundStroke); }
            using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 7 * RwDp * R / 2, BorderMode = EffectBorderMode.Soft };
            ds.DrawImage(blur);
        }
        rw.RimSurf ??= G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rw.RimSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            using var outer = path.Stroke(RwSw + 2 * RwDp, RwRoundStroke);
            using var inner = path.Stroke(MathF.Max(0.1f, RwSw - 2 * RwDp), RwRoundStroke);
            using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            ds.FillGeometry(ring, Color.FromArgb(102, 0xfa, 0xfa, 0xfa));
        }
        rw.LineSurf ??= G.Surface(pw, ph);
        foreach (var (v, s) in new[] { (rw.Glow, rw.GlowSurf), (rw.Line, rw.LineSurf), (rw.Rim, rw.RimSurf) })
        {
            ((CompositionSurfaceBrush)v.Brush).Surface = s;
            v.Size = new Vector2(sw, shh);
        }
        RwPaintCursorLine(page, 0);
    }

    /// <summary>主线：沿周长的两色渐变，颜色 = mix(色1, 色2, 0.5 − 0.5 cos 2π(s/P − 相位))。</summary>
    private void RwPaintCursorLine(CsPage page, float phase)
    {
        var rw = page.Rw!;
        if (rw.LineSurf == null) return;
        float R = CsRaster;
        var (segs, P) = RwBracketPoints();
        using var ds = CanvasComposition.CreateDrawingSession(rw.LineSurf);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateScale(R);
        foreach (var sg in segs)
            for (int i = 1; i < sg.Count; i++)
            {
                var a = sg[i - 1]; var b = sg[i];
                float t = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * ((a.Z + b.Z) / 2 / P - phase));
                var col = Color.FromArgb(255, (byte)(RwC1.R + (RwC2.R - RwC1.R) * t), (byte)(RwC1.G + (RwC2.G - RwC1.G) * t), (byte)(RwC1.B + (RwC2.B - RwC1.B) * t));
                ds.DrawLine(a.X, a.Y, b.X, b.Y, col, RwSw, RwRoundStroke);
            }
    }

    // ---------------- 排 ----------------

    private int RwScrollIndex(int s, int n)
    {
        int visible = (int)Math.Floor((CsW - RwX) / RwPitch);   // 一屏放得下 5 张，第 6 张露一半
        return Math.Clamp(s - 2, 0, Math.Max(0, n - visible));
    }

    private static float RwSlotX(int i, int scroll) => RwX + (i - scroll) * RwPitch;

    private SpringVector3NaturalMotionAnimation RwSpring(Vector3 to)
    {
        var a = G.C.CreateSpringVector3Animation();
        a.DampingRatio = 1f;
        a.Period = TimeSpan.FromSeconds(2 * Math.PI / Math.Sqrt(600));
        a.FinalValue = to;
        return a;
    }

    private void CsLayoutRow(CsPage p, bool animate, bool entry = false)
    {
        int n = CsCount(p), s = Math.Clamp(p.Sel, 0, n - 1);
        int scroll = RwScrollIndex(s, n);
        p.RailX = scroll;
        int lo = Math.Max(0, scroll - 2), hi = Math.Min(n - 1, scroll + 7);
        foreach (var gone in p.Cases.Keys.Where(i => i < lo || i > hi).ToList())
            if (p.Cases.Remove(gone, out var gc)) { p.Row!.Children.Remove(gc.Root); CsDisposeCase(gc); }
        bool move = animate && !Reduced;
        int order = 0;
        for (int i = lo; i <= hi; i++)
        {
            var off = new Vector3(RwSlotX(i, scroll), RwY, 0);
            if (!p.Cases.TryGetValue(i, out var c))
            {
                c = CsNewCase(i < p.Items.Count ? p.Items[i] : null, i, new Vector2(RwW, RwH), radiusK: RwRound);
                c.Root.CenterPoint = new Vector3(RwW / 2, RwH / 2, 0);
                c.Ring.Opacity = 0; c.Dim.Opacity = 0; c.Glow.Opacity = 0;
                // 新出现的卡从上一轮的位置开始（滑进来），不是凭空出现在终点
                c.Root.Offset = move ? new Vector3(RwSlotX(i, RwScrollIndex(Math.Clamp(p.Prev, 0, n - 1), n)), RwY, 0) : off;
                p.Cases[i] = c;
                p.Row!.Children.InsertAtTop(c.Root);
            }
            c.Offset = off; c.Size = new Vector2(RwW, RwH);
            var scale = new Vector3(i == s ? RwFocus : 1, i == s ? RwFocus : 1, 1);
            if (move) { c.Root.StartAnimation("Offset", RwSpring(off)); c.Root.StartAnimation("Scale", RwSpring(scale)); }
            else
            {
                c.Root.StopAnimation("Offset"); c.Root.StopAnimation("Scale");
                c.Root.Scale = scale;
                if (entry && !Reduced && off.X < CsW)
                {
                    double delay = 100 + order++ * 35;
                    c.Root.Offset = off + new Vector3(0, 60, 0);
                    c.Root.Opacity = 0;
                    c.Root.StartAnimation("Offset", CsV3(off, 600, CsQuart, delay));
                    c.Root.StartAnimation("Opacity", CsS(1, 420, CsQuart, delay));
                }
                else c.Root.Offset = off;
            }
        }
        // 焦点那张放在最上层（放大后盖住左右邻居）
        if (p.Cases.TryGetValue(s, out var sel)) { p.Row!.Children.Remove(sel.Root); p.Row.Children.InsertAtTop(sel.Root); }
        // 焦点框：中心对准焦点卡中心
        if (p.Rw is { } rw)
        {
            var (W, H, _, _, _) = RwBracketGeom();
            var to = new Vector3(RwSlotX(s, scroll) + RwW / 2 - W / 2 - RwCurPad, RwY + RwH / 2 - H / 2 - RwCurPad, 0);
            foreach (var v in new Visual[] { rw.Glow, rw.Line, rw.Rim })
            {
                if (move) v.StartAnimation("Offset", RwSpring(to));
                else { v.StopAnimation("Offset"); v.Offset = to; }
                if (entry && !Reduced && !move) { v.Opacity = 0; v.StartAnimation("Opacity", CsS(1, 420, CsQuart, 300)); }
            }
        }
        p.Prev = s;
    }

    /// <summary>焦点卡在屏幕上的矩形（放大后），给启动动画用。</summary>
    private static (float X, float Y, float W, float H) RwFocusRect(CsCase c) =>
        (c.Offset.X - RwW * (RwFocus - 1) / 2, c.Offset.Y - RwH * (RwFocus - 1) / 2, RwW * RwFocus, RwH * RwFocus);
}
