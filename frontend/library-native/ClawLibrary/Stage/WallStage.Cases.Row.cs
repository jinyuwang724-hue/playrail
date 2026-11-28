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
using ClawLibrary.Engine;
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
    private static Color RwC1 => CocoonPalette.Current.Start;
    private static Color RwC2 => CocoonPalette.Current.End;
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
    private static readonly (float X, float Y, float W, float H, float R, int Kind) RwPanelL = (38, 23, 264, 112, 36, 0);   // 不放 LB 提示（11-20 jinyu）：截图里的面板去掉肩键那一格，头像往左挪 64
    private static readonly (float X, float Y, float W, float H, float R, int Kind) RwMore = (1818, 187, 74, 74, 37, 1);
    private const float RwPanelRRight = 1882, RwClockW0 = 168;
    private const float RwPanelPad = 20;   // 面板贴图四周留给投影的边

    private sealed class RwChrome
    {
        public required ContainerVisual Root;
        public required SpriteVisual Wall, PanelL, PanelR, More, Glow, Line, Rim, RGlow, RLine, RRim;
        /// <summary>背景层里壁纸上面的组件层（顶部面板、图标焦点）：开菜单时和内容层一起模糊；壁纸只压暗不糊（照 Cocoon 视频）。</summary>
        public LayerVisual Ui = null!;
        /// <summary>开菜单时组件层的整层模糊（壁纸不糊）。</summary>
        public CompositionEffectBrush? UiFx, ContentFx;
        public CompositionDrawingSurface? RGlowSurf, RLineSurf, RRimSurf;
        public CanvasRenderTarget? Sharp, Soft;   // 壁纸原图 / 模糊后的像素（画面板用）
        public CompositionDrawingSurface? WallSurf, LSurf, RSurf, MoreSurf, GlowSurf, LineSurf, RimSurf;
        public DispatcherQueueTimer? Tick;
        public int Phase = -1;
        /// <summary>焦点在左上面板上时是第几个页签图标；-1 = 焦点在封面上。</summary>
        public int PanelFocus = -1;
        /// <summary>焦点在正在玩的卡右上角的 ✕ 上（↑ 从卡上过去，11-28 jinyu）；它自己的圆形焦点框。</summary>
        public bool XFocus;
        public CurSet? XCur;
        /// <summary>这一页卡片的尺寸（首页 280×420，Steam / 本地网格更小）：焦点框按它画。</summary>
        public float CardW = RwW, CardH = RwH;
        public BatteryState Bat;
        public SystemInfo.NetState Net;
        public bool StatusBusy;
        public CompositionLinearGradientBrush? Flow;
        public CompositionPropertySet? FlowProps;
        public string Drawn = "";
        public float Raster;
        public bool Disposed;
    }

    // 壁纸像素和最近一次读到的电量 / 网络在页面之间共用：切到 Steam / 本地页不用重新解码壁纸，面板一出来就是对的电量
    private string _rwWallKey = "";
    private BatteryState _rwBat;
    private SystemInfo.NetState _rwNet;
    /// <summary>从面板切页签：新页面一建好焦点就停在面板上（不跳回封面）。</summary>
    private bool _rwPanelNext;

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
        var rw = new RwChrome { Root = root, Wall = wall, PanelL = Sp(), PanelR = Sp(), More = Sp(), Glow = Sp(), Line = Sp(), Rim = Sp(), RGlow = Sp(), RLine = Sp(), RRim = Sp() };
        rw.Bat = _rwBat; rw.Net = _rwNet;
        rw.Ui = G.C.CreateLayerVisual();
        rw.Ui.Size = new Vector2(CsW, CsH);
        root.Children.InsertAtTop(rw.Ui);
        page.Rw = rw;
        RwLoadWallpaper(page, entry);
        return root;
    }

    /// <summary>焦点框三层和顶部面板放在内容层（卡片上面）。</summary>
    private void RwAttach(CsPage page)
    {
        if (page.Rw is not { } rw) return;
        // 面板和图标焦点放在背景层：切页时页面内容会左右滑，背景层只淡入淡出，面板位置纹丝不动
        foreach (var v in new Visual[] { rw.PanelL, rw.PanelR, rw.RGlow, rw.RLine, rw.RRim }) rw.Ui.Children.InsertAtTop(v);
        RwPaintRing(rw);
        foreach (var v in new Visual[] { rw.Glow, rw.Line, rw.Rim }) page.Content.Children.InsertAtTop(v);
        if (_rwPanelNext)
        {
            _rwPanelNext = false;
            rw.PanelFocus = Math.Max(0, Array.IndexOf(RwTabs, page.Tab));
            // 圆环从上一页按下时所在的图标滑到新页签的图标
            if (_rwRingFrom is { } from) foreach (var v in new Visual[] { rw.RGlow, rw.RLine, rw.RRim }) v.Offset = new Vector3(from.X - RwRingSize / 2, from.Y - RwRingSize / 2, 0);
            RwRingTo(rw, rw.PanelFocus, show: true, move: _rwRingFrom != null);
            _rwRingFrom = null;
        }
        RwPaintCursorStatic(page);
        if (rw.PanelFocus >= 0) foreach (var v in new Visual[] { rw.Glow, rw.Line, rw.Rim }) v.Opacity = 0;
        rw.Tick = _dq.CreateTimer();
        rw.Tick.Interval = TimeSpan.FromMilliseconds(1000);
        rw.Tick.Tick += (_, _) => RwTick(page);
        rw.Tick.Start();
        RwTick(page);
        After(1500, () => SpPrewarm(page));   // 右侧栏先在后台搭好（按 R1 时不用现画）
    }

    private void RwDispose(CsPage page)
    {
        if (page.Rw is not { } rw || rw.Disposed) return;
        rw.Disposed = true;
        rw.Tick?.Stop();
        // 壁纸像素是共用缓存（_rwSharp / _rwSoft / _rwWallSurf），不在这里释放
        foreach (var s in new[] { rw.LSurf, rw.RSurf, rw.MoreSurf, rw.GlowSurf, rw.LineSurf, rw.RimSurf, rw.RGlowSurf, rw.RLineSurf, rw.RRimSurf }) s?.Dispose();
    }

    /// <summary>
    /// 每秒一次：电量、网络在后台线程读（读网络状态要枚举连接，放在界面线程上会卡住翻页和动画），
    /// 回来后只在整分钟 / 电量 / 网络变了时重画右面板。焦点框流光由合成器自己转（RwFlow），不占界面线程。
    /// </summary>
    private void RwTick(CsPage page)
    {
        if (page.Rw is not { } rw || rw.Disposed || rw.StatusBusy) return;
        if (_cs == null || (_cs.Page != page && !_cs.Leaving.Contains(page))) { rw.Tick?.Stop(); return; }
        rw.StatusBusy = true;
        _ = Task.Run(() =>
        {
            BatteryState b = default; SystemInfo.NetState n = default;
            try { b = SystemInfo.Battery(); n = SystemInfo.Network(); } catch { }
            _dq.TryEnqueue(() =>
            {
                rw.StatusBusy = false;
                if (rw.Disposed) return;
                rw.Bat = b; rw.Net = n; _rwBat = b; _rwNet = n;
                if (rw.Sharp != null && RwStatusKey(rw) != rw.Drawn) RwPaintPanels(page);
            });
        });
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

    /// <summary>
    /// 壁纸两种处理，按页面用：0 = 原样（首页）；1 = 网格页（Steam / 本地）用的「退后」版：大半径模糊 + 压暗 + 略去饱和，
    /// 让封面网格从背景里跳出来（11-20 jinyu：网格页太满、看不过来）。都按壁纸路径 + 修改时间 + 缩放缓存，跨页面共用。
    /// </summary>
    private readonly Dictionary<int, (CanvasRenderTarget Sharp, CanvasRenderTarget Soft, CompositionDrawingSurface Surf)> _rwWall = new();

    private void RwApplyWall(CsPage page, bool entry, int mode)
    {
        var rw = page.Rw!;
        var w = _rwWall[mode];
        rw.Sharp = w.Sharp; rw.Soft = w.Soft; rw.WallSurf = w.Surf;
        rw.Wall.Brush = G.C.CreateSurfaceBrush(w.Surf);
        try { if (!rw.Disposed) RwPaintPanels(page); } catch (Exception ex) { Log.Error("row panels", ex); }
        CsBgReady(page, entry);
    }

    /// <summary>从原样那一份算出网格页的退后版：模糊 18、亮度 × 0.6、饱和 × 0.75。</summary>
    private void RwDeriveDim()
    {
        var dev = G.Device;
        var src = _rwWall[0].Sharp;
        int pw = (int)src.SizeInPixels.Width, ph = (int)src.SizeInPixels.Height;
        float R = pw / CsW;
        var sharp = new CanvasRenderTarget(dev, pw, ph, 96);
        using (var ds = sharp.CreateDrawingSession())
        using (var blur = new GaussianBlurEffect { Source = src, BlurAmount = 18 * R, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Quality })
        using (var sat = new SaturationEffect { Source = blur, Saturation = 0.75f })
        using (var dim = new ColorMatrixEffect { Source = sat, ColorMatrix = new Matrix5x4 { M11 = 0.6f, M22 = 0.6f, M33 = 0.6f, M44 = 1 } })
            ds.DrawImage(dim);
        var soft = new CanvasRenderTarget(dev, pw, ph, 96);
        using (var ds = soft.CreateDrawingSession())
        using (var blur = new GaussianBlurEffect { Source = sharp, BlurAmount = 3 * RwDp * R, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Quality })
            ds.DrawImage(blur);
        var surf = G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(surf)) ds.DrawImage(sharp);
        _rwWall[1] = (sharp, soft, surf);
    }

    private void RwLoadWallpaper(CsPage page, bool entry)
    {
        var rw = page.Rw!;
        int mode = page.Tab == 0 ? 0 : 1;
        string? path = RwWallpaperPath();
        var dev = G.Device;
        float R = CsRaster;
        rw.Raster = R;
        string key = $"{path}|{(path != null ? File.GetLastWriteTimeUtc(path).Ticks : 0)}|{R}";
        if (key != _rwWallKey && _rwWall.Count > 0)
        {
            // 换了壁纸 / 缩放：旧的像素等离场页面淡完再释放
            var old = _rwWall.Values.ToList();
            _rwWall.Clear();
            After(3000, () => { foreach (var o in old) { o.Sharp.Dispose(); o.Soft.Dispose(); o.Surf.Dispose(); } });
        }
        if (key == _rwWallKey && _rwWall.ContainsKey(0))
        {
            _dq.TryEnqueue(() =>
            {
                if (rw.Disposed) return;
                try { if (!_rwWall.ContainsKey(mode)) RwDeriveDim(); RwApplyWall(page, entry, mode); }
                catch (Exception ex) { Log.Error("row wallpaper", ex); CsBgReady(page, entry); }
            });
            return;
        }
        _ = Task.Run(async () =>
        {
            CanvasBitmap? bmp = null;
            try
            {
                if (path != null)
                {
                    // 读进内存再解码：壁纸文件可能正被资源管理器占着
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
                    if (!_rwWall.ContainsKey(0))
                    {
                        int pw = (int)Math.Ceiling(CsW * R), ph = (int)Math.Ceiling(CsH * R);
                        var sharp = new CanvasRenderTarget(dev, pw, ph, 96);
                        var soft = new CanvasRenderTarget(dev, pw, ph, 96);
                        using (var ds = sharp.CreateDrawingSession())
                        {
                            ds.Clear(Gfx.Rgba(0x1b, 0x1b, 0x22));
                            if (bmp != null)
                            {
                                double cover = Math.Max(pw / bmp.Bounds.Width, ph / bmp.Bounds.Height);
                                var dest = new Windows.Foundation.Rect((pw - bmp.Bounds.Width * cover) / 2, (ph - bmp.Bounds.Height * cover) / 2, bmp.Bounds.Width * cover, bmp.Bounds.Height * cover);
                                ds.DrawImage(bmp, dest, bmp.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                            }
                        }
                        using (var ds = soft.CreateDrawingSession())
                        using (var blur = new GaussianBlurEffect { Source = sharp, BlurAmount = 3 * RwDp * R, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Quality })
                            ds.DrawImage(blur);
                        var surf = G.Surface(pw, ph);
                        using (var ds = CanvasComposition.CreateDrawingSession(surf)) ds.DrawImage(sharp);
                        _rwWall[0] = (sharp, soft, surf);
                        _rwWallKey = key;
                    }
                    bmp?.Dispose();
                    if (!_rwWall.ContainsKey(mode)) RwDeriveDim();
                    RwApplyWall(page, entry, mode);
                }
                catch (Exception ex) { Log.Error("row wallpaper paint", ex); CsBgReady(page, entry); }
            });
        });
    }

    // ---------------- 顶部面板 ----------------

    private string RwStatusKey(RwChrome rw)
    {
        var b = rw.Bat; var n = rw.Net;
        var now = DateTime.Now;
        return $"{_cs?.Tab}|{now:hh:mm tt}|{b.Percent}|{b.Charging}|{n.Online}|{n.Wlan}|{_railModel.Avatar != null}|{CsRaster}";
    }

    private static string RwClock(DateTime t) => $"{(t.Hour % 12 == 0 ? 12 : t.Hour % 12)}:{t.Minute:00}";

    private void RwPaintPanels(CsPage page)
    {
        if (page.Rw is not { } rw || rw.Sharp == null || rw.Soft == null) return;
        rw.Drawn = RwStatusKey(rw);
        var dev = G.Device;
        float R = rw.Raster;
        var now = DateTime.Now;
        string clock = RwClock(now), ampm = now.Hour < 12 ? "上午" : "下午";
        // 时间变宽 / 变窄时右面板向左长（右边缘固定），里面的东西跟着整体平移
        float clockW = RwClockWidth(dev, clock, ampm);
        float dx = MathF.Round(clockW - RwClockW0);
        var panelR = (X: 1496 - dx, Y: 23f, W: 386 + dx, H: 112f, R: 36f, Kind: 0);
        RwPanel(rw, ref rw.LSurf, rw.PanelL, RwPanelL, R, ds => RwDrawLeft(ds, rw));
        RwPanel(rw, ref rw.RSurf, rw.PanelR, panelR, R, ds => RwDrawRight(ds, rw, -dx, clock, ampm));
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
        byte[] soft = rw.Soft!.GetPixelBytes(x0, y0, w, h);
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
            // 输出是预乘 BGRA：面板外面只有投影（黑色半透明，压暗下面真实的背景，不烤背景像素进来——
            // 否则切页时这圈边会带着上一页的背景，显出一块矩形）；面板里面是不透明的磨砂。
            float shadeA = 0;
            if (p.Kind == 0)
            {
                float s = Sd(pt - c - new Vector2(0, 6), hs - new Vector2(3), MathF.Max(rad - 3, 0));
                float m = 1 - 0.22f * MathF.Pow(1 - Sst(-2, 12, s), 2);
                m *= 1 - 0.03f * MathF.Exp(-MathF.Max(best, 0) * 1.2f);
                shadeA = 1 - m;
            }
            float r = 0, g = 0, b = 0, a = shadeA;
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
                r = gr * cover; g = gg * cover; b = gb * cover;
                a = cover + (1 - cover) * shadeA;
            }
            outPx[k] = (byte)Math.Clamp(b * 255 + 0.5f, 0, 255);
            outPx[k + 1] = (byte)Math.Clamp(g * 255 + 0.5f, 0, 255);
            outPx[k + 2] = (byte)Math.Clamp(r * 255 + 0.5f, 0, 255);
            outPx[k + 3] = (byte)Math.Clamp(a * 255 + 0.5f, 0, 255);
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

    /// <summary>左上面板的页签（11-20 jinyu：主页 / Steam / 本地，用图标不写字）。tab 编号沿用卡盒版：0 首页、2 Steam、3 本地。</summary>
    private static readonly int[] RwTabs = { 0, 2, 3 };
    private const float RwTabX0 = 92, RwTabPitch = 78, RwTabY = 79;

    /// <summary>贴纸风格的三个页签图标：当前页签填主题色渐变，其余灰；焦点在面板上时，焦点那个外面套一圈小四角括号。</summary>
    private void RwDrawLeft(CanvasDrawingSession ds, RwChrome rw)
    {
        var dev = ds.Device;
        int cur = _cs?.Tab ?? 0;
        for (int i = 0; i < RwTabs.Length; i++)
        {
            float cx = RwTabX0 + i * RwTabPitch, cy = RwTabY;
            bool on = RwTabs[i] == cur;
            using var fill = on ? new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(0, cy - 22), EndPoint = new Vector2(0, cy + 22) }
                                : new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xC9, 0xCB, 0xD3), Color.FromArgb(255, 0xA0, 0xA3, 0xAE)) { StartPoint = new Vector2(0, cy - 22), EndPoint = new Vector2(0, cy + 22) };
            switch (RwTabs[i])
            {
                case 0:
                {
                    // 房子：圆角屋顶 + 屋身，白色小门
                    using var pb = new CanvasPathBuilder(dev);
                    pb.BeginFigure(cx - 20, cy - 1); pb.AddLine(cx, cy - 19); pb.AddLine(cx + 20, cy - 1); pb.AddLine(cx + 15.5f, cy - 1);
                    pb.AddLine(cx + 15.5f, cy + 18); pb.AddLine(cx - 15.5f, cy + 18); pb.AddLine(cx - 15.5f, cy - 1);
                    pb.EndFigure(CanvasFigureLoop.Closed);
                    using var raw = CanvasGeometry.CreatePath(pb);
                    using var house = raw.Stroke(5, RwRoundStroke).CombineWith(raw, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                    RwSticker(ds, house, 10);
                    ds.FillGeometry(house, fill);
                    ds.FillRoundedRectangle(cx - 5, cy + 5, 10, 15.5f, 3, 3, RwWhite);
                    break;
                }
                case 2:
                {
                    // Steam：渐变圆底上一枚白色 Steam 标志
                    using var disc = CanvasGeometry.CreateCircle(dev, cx, cy, 21);
                    RwSticker(ds, disc, 10);
                    ds.FillGeometry(disc, fill);
                    _rwSteamSvg ??= Microsoft.Graphics.Canvas.Svg.CanvasSvgDocument.LoadFromXml(dev, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Steam.svg")));
                    _rwSteamSvg.Root.SetColorAttribute("fill", RwWhite);
                    ds.DrawSvg(_rwSteamSvg, new Windows.Foundation.Size(30, 30), cx - 15, cy - 15);
                    break;
                }
                default:
                {
                    // 本地：文件夹（左上一个小签）
                    using var body = CanvasGeometry.CreateRoundedRectangle(dev, cx - 22, cy - 11, 44, 29, 6, 6);
                    using var tab = CanvasGeometry.CreateRoundedRectangle(dev, cx - 22, cy - 18, 20, 12, 4, 4);
                    using var folder = body.CombineWith(tab, Matrix3x2.Identity, CanvasGeometryCombine.Union);
                    RwSticker(ds, folder, 10);
                    ds.FillGeometry(folder, fill);
                    ds.FillRoundedRectangle(cx - 22, cy - 5, 44, 2.5f, 1.2f, 1.2f, Color.FromArgb(70, 255, 255, 255));
                    break;
                }
            }
        }
    }

    private Microsoft.Graphics.Canvas.Svg.CanvasSvgDocument? _rwSteamSvg;

    /// <summary>面板里的焦点：圆环（11-20 jinyu：图标是圆的，圆环更合适）。主题两色斜向渐变 + 第二色光晕，线宽同封面焦点框。</summary>
    private static void RwFocusCircle(CanvasDrawingSession ds, float cx, float cy, float r)
    {
        var dev = ds.Device;
        using var glow = new CanvasCommandList(dev);
        using (var g = glow.CreateDrawingSession()) g.DrawCircle(cx, cy, r, RwC2, RwSw * 1.6f);
        using (var blur = new GaussianBlurEffect { Source = glow, BlurAmount = 4 * ds.Transform.M11 }) ds.DrawImage(blur);
        using var br = new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(cx - r, cy - r), EndPoint = new Vector2(cx + r, cy + r) };
        ds.DrawCircle(cx, cy, r, br, RwSw * 0.85f);
        ds.DrawCircle(cx, cy, r + RwSw * 0.425f, Color.FromArgb(70, 0xfa, 0xfa, 0xfa), 1.2f);
    }

    /// <summary>（旧）面板里的焦点：和封面焦点框同一种四角括号，缩小到图标大小（主题两色斜向渐变）。</summary>
    private static void RwMiniBrackets(CanvasDrawingSession ds, float cx, float cy, float size)
    {
        var dev = ds.Device;
        float h = size / 2, r = 18, leg = 7;
        using var pb = new CanvasPathBuilder(dev);
        void Corner(float sx, float sy)
        {
            // 从竖腿起，经圆弧到横腿
            var a = new Vector2(cx + sx * h, cy + sy * (h - r - leg));
            pb.BeginFigure(a);
            pb.AddLine(cx + sx * h, cy + sy * (h - r));
            pb.AddArc(new Vector2(cx + sx * (h - r), cy + sy * h), r, r, 0, sx * sy > 0 ? CanvasSweepDirection.Clockwise : CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
            pb.AddLine(cx + sx * (h - r - leg), cy + sy * h);
            pb.EndFigure(CanvasFigureLoop.Open);
        }
        Corner(-1, -1); Corner(1, -1); Corner(1, 1); Corner(-1, 1);
        using var geo = CanvasGeometry.CreatePath(pb);
        using var glow = new CanvasCommandList(dev);
        using (var g = glow.CreateDrawingSession()) g.DrawGeometry(geo, RwC2, 7, RwRoundStroke);
        using (var blur = new GaussianBlurEffect { Source = glow, BlurAmount = 4 * ds.Transform.M11 }) ds.DrawImage(blur);
        using var br = new CanvasLinearGradientBrush(dev, RwC1, RwC2) { StartPoint = new Vector2(cx - h, cy - h), EndPoint = new Vector2(cx + h, cy + h) };
        ds.DrawGeometry(geo, br, 3.5f, RwRoundStroke);
    }

    private void RwDrawRight(CanvasDrawingSession ds, RwChrome rw, float shift, string clock, string ampm, Color? ink = null)
    {
        var dev = ds.Device;
        var t0 = ds.Transform;
        ds.Transform = Matrix3x2.CreateTranslation(shift, 0) * t0;
        // Wi-Fi：右下一个点 + 三段四分之一圆弧（圆心 = 点，半径 11 / 20 / 29.5，线宽 5.2）
        var net = rw.Net;
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
        // 电量（两套，照 jinyu 的两张截图）：
        // 充电中 = 橙色电池（右侧橙色小正极）+ 左下贴一块白牌写百分比 + 小闪电；
        // 没充电 = 绿色电池（灰色小正极）+ 右下一块更窄更高的白牌写百分比，没有闪电。
        var bat = rw.Bat;
        bool chg = bat.Charging;
        string pct = bat.HasBattery ? Math.Clamp(bat.Percent, 0, 100).ToString() : "AC";
        var (bx, by, bw, bh, br) = chg ? (1611f, 65f, 44.5f, 30f, 7f) : (1614f, 64f, 42.5f, 28.5f, 5f);
        var (nx, ny, nw, nh) = chg ? (1652f, 71.5f, 6f, 7f) : (1654.5f, 72f, 5.5f, 8f);
        var (gx, gy, gw, gh, gr) = chg ? (1613.5f, 74f, 44.5f, 27f, 13.5f) : (1626.5f, 74f, 38f, 32f, 14f);
        Color top = chg ? Color.FromArgb(255, 0xFF, 0xC0, 0x9C) : Color.FromArgb(255, 0x7C, 0xD9, 0x8A);
        Color bot = chg ? Color.FromArgb(255, 0xFF, 0x8A, 0x28) : Color.FromArgb(255, 0x0F, 0xD0, 0xB5);
        using (var body = CanvasGeometry.CreateRoundedRectangle(dev, bx, by, bw, bh, br, br))
        using (var nub = CanvasGeometry.CreateRoundedRectangle(dev, nx, ny, nw, nh, 2, 2))
        using (var badge = CanvasGeometry.CreateRoundedRectangle(dev, gx, gy, gw, gh, gr, gr))
        using (var cell = body.CombineWith(nub, Matrix3x2.Identity, CanvasGeometryCombine.Union))
        using (var all = cell.CombineWith(badge, Matrix3x2.Identity, CanvasGeometryCombine.Union))
        {
            RwSticker(ds, all, 9);
            using var ob = new CanvasLinearGradientBrush(dev, top, bot) { StartPoint = new Vector2(0, by), EndPoint = new Vector2(0, by + bh) };
            if (chg) ds.FillGeometry(cell, ob);
            else { ds.FillGeometry(nub, Color.FromArgb(255, 0xDC, 0xDC, 0xE0)); ds.FillGeometry(body, ob); }
            ds.FillGeometry(badge, RwWhite);
            using var nf = new CanvasTextFormat { FontFamily = RwPoppinsXb, FontSize = pct.Length > 2 ? 18 : 24, FontWeight = new Windows.UI.Text.FontWeight(800), HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
            using var nb = chg ? new CanvasLinearGradientBrush(dev, Color.FromArgb(255, 0xFF, 0xBE, 0x95), Color.FromArgb(255, 0xFF, 0x8F, 0x30)) { StartPoint = new Vector2(0, 82), EndPoint = new Vector2(0, 100) }
                               : new CanvasLinearGradientBrush(dev, top, bot) { StartPoint = new Vector2(0, 81), EndPoint = new Vector2(0, 98) };
            var textBox = chg ? new Windows.Foundation.Rect(1614, 73, 38, 31) : new Windows.Foundation.Rect(gx, gy - 1, gw, gh);
            ds.DrawText(pct, textBox, nb, nf);
            if (chg)
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
        ds.DrawTextLayout(l1, 1683.5f - (float)d1.X, 93 - (float)(d1.Y + d1.Height), ink ?? RwInk);
        ds.DrawTextLayout(l2, 1683.5f + (float)d1.Width + 16.5f - (float)d2.X, 96 - (float)(d2.Y + d2.Height), ink ?? RwInk);
        ds.Transform = t0;
    }

    private static void RwDrawMore(CanvasDrawingSession ds)
    {
        for (int i = 0; i < 3; i++) ds.FillRoundedRectangle(1832 + i * 16.3f, 217, 14, 14, 4.5f, 4.5f, RwInk);
    }

    // ---------------- 焦点框：四角括号 ----------------

    /// <summary>焦点卡的大小固定（1.15 倍的卡），所以括号画一次；移动时只挪精灵。</summary>
    private static (float W, float H, float Out, float R, float Leg) RwBracketGeom(float cw, float ch)
    {
        float w = cw * RwFocus, h = ch * RwFocus;
        float o = Math.Clamp(0.12f * w, 5 * RwDp, 24 * RwDp) * RwThick;
        float W = w + o, H = h + o;
        float r = MathF.Min(w * RwRound + o / 2, MathF.Min(W, H) / 2);
        float sc = Math.Clamp(MathF.Min(W, H) / MathF.Min(w, h), 0.35f, 1.75f);
        float gap = MathF.Min(14 * RwDp * sc, 0.36f * MathF.Min(W, H));
        float leg = MathF.Max(0, MathF.Min(18 * RwDp * sc, (MathF.Min(W, H) - 2 * r - gap) / 2));
        return (W, H, o, r, leg);
    }

    private const float RwCurPad = 44;   // 精灵四周给光晕留的边

    private static readonly CanvasStrokeStyle RwRoundStroke = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

    /// <summary>光晕（2 倍线宽、第二色、模糊 7 dp）和边光（沿线的外轮廓再描 2 dp，#FAFAFA 40 %）：不随时间变，画一次。</summary>
    private void RwPaintCursorStatic(CsPage page)
    {
        var rw = page.Rw!;
        var dev = G.Device;
        float R = CsRaster;
        var (W, H, _, _, _) = RwBracketGeom(rw.CardW, rw.CardH);
        float sw = W + RwCurPad * 2, shh = H + RwCurPad * 2;
        int pw = (int)Math.Ceiling(sw * R), ph = (int)Math.Ceiling(shh * R);
        var (bw, bh, _, br, _) = RwBracketGeom(rw.CardW, rw.CardH);
        using var path = CocoonBracketPath(dev, bw, bh, br, RwCurPad);   // 全局同一份括号几何
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
        // 主线：白色线条只画一次当遮罩，颜色是一块会转的线性渐变（合成器按显示刷新率转，界面线程不参与，不再一档一档地跳）
        rw.LineSurf ??= G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(rw.LineSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            ds.DrawGeometry(path, Colors.White, RwSw, RwRoundStroke);
        }
        foreach (var (v, s) in new[] { (rw.Glow, rw.GlowSurf), (rw.Rim, rw.RimSurf) })
        {
            ((CompositionSurfaceBrush)v.Brush).Surface = s;
            v.Size = new Vector2(sw, shh);
        }
        rw.Line.Size = new Vector2(sw, shh);
        rw.Line.Brush = RwFlowBrush(rw, rw.LineSurf);
    }

    /// <summary>
    /// 流光：色2 亮带在一侧、色1 在对侧，中间按 0.5 − 0.5 cos 过渡（和规格里沿周长的公式同一条曲线），
    /// 渐变方向绕中心顺时针转，1.8 s 一圈，连续不分档。
    /// </summary>
    private CompositionMaskBrush RwFlowBrush(RwChrome rw, CompositionDrawingSurface maskSurf)
    {
        var grad = G.C.CreateLinearGradientBrush();
        Color Mix(float t) => CocoonPalette.MixFlow(t);
        for (int i = 0; i <= 8; i++)
        {
            float x = i / 8f, t = 0.5f + 0.5f * MathF.Cos(MathF.PI * x);
            grad.ColorStops.Add(G.C.CreateColorGradientStop(x, Mix(t)));
        }
        rw.Flow = grad;
        var props = G.C.CreatePropertySet();
        props.InsertScalar("a", 0);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var start = G.C.CreateExpressionAnimation("Vector2(0.5 + 0.5 * Cos(p.a), 0.5 + 0.5 * Sin(p.a))");
        start.SetReferenceParameter("p", props);
        var end = G.C.CreateExpressionAnimation("Vector2(0.5 - 0.5 * Cos(p.a), 0.5 - 0.5 * Sin(p.a))");
        end.SetReferenceParameter("p", props);
        grad.StartAnimation("StartPoint", start);
        grad.StartAnimation("EndPoint", end);
        if (!Reduced)
        {
            var spin = G.C.CreateScalarKeyFrameAnimation();
            spin.InsertKeyFrame(0, -MathF.PI / 2, G.Linear);
            spin.InsertKeyFrame(1, -MathF.PI / 2 + 2 * MathF.PI, G.Linear);
            spin.Duration = TimeSpan.FromMilliseconds(RwPeriodMs);
            spin.IterationBehavior = AnimationIterationBehavior.Forever;
            props.StartAnimation("a", spin);
        }
        else props.InsertScalar("a", -MathF.PI / 2);
        rw.FlowProps = props;
        var mask = G.C.CreateMaskBrush();
        mask.Source = grad;
        mask.Mask = G.C.CreateSurfaceBrush(maskSurf);
        return mask;
    }

    // ---------------- 排 ----------------

    private int RwScrollIndex(int s, int n)
    {
        int visible = (int)Math.Floor((CsW - RwX) / RwPitch);   // 一屏放得下 5 张，第 6 张露一半
        return Math.Clamp(s - 2, 0, Math.Max(0, n - visible));
    }

    private static float RwSlotX(int i, int scroll) => RwX + (i - scroll) * RwPitch;

    /// <summary>临界阻尼弹簧。Cocoon：焦点框刚度 600（「默认」档），整排滚动用安卓默认的滚动弹簧（刚度 1500，更快）。</summary>
    private SpringVector3NaturalMotionAnimation RwSpring(Vector3 to, float stiffness = 600)
    {
        var a = G.C.CreateSpringVector3Animation();
        a.DampingRatio = 1f;
        // 合成器的弹簧 Period 实测按 ω = 1 / Period 算（录屏量出来的），不是 2π / Period
        a.Period = TimeSpan.FromSeconds(1 / Math.Sqrt(stiffness));
        a.FinalValue = to;
        return a;
    }

    private void CsLayoutRow(CsPage p, bool animate, bool entry = false)
    {
        int n = Math.Max(1, p.Items.Count), s = Math.Clamp(p.Sel, 0, n - 1);   // 单行不放行尾的「全部 N」（没有「全部」页签）
        int scroll = RwScrollIndex(s, n);
        p.RailX = scroll;
        int lo = Math.Max(0, scroll - 4), hi = Math.Min(n - 1, scroll + 10);   // 两边多留几张：连按时不在屏幕边上现建现解码
        foreach (var gone in p.Cases.Keys.Where(i => i < lo || i > hi).ToList())
            if (p.Cases.Remove(gone, out var gc)) { p.Row!.Children.Remove(gc.Root); CsDisposeCase(gc); }
        bool move = animate && !Reduced;
        int order = 0;
        for (int i = lo; i <= hi; i++)
        {
            var off = new Vector3(RwSlotX(i, scroll), RwY, 0);
            if (!p.Cases.TryGetValue(i, out var c))
            {
                c = CsNewCase(i < p.Items.Count ? p.Items[i] : null, i, new Vector2(RwW, RwH), radiusK: RwRound, circle: true);
                c.Root.CenterPoint = new Vector3(RwW / 2, RwH / 2, 0);
                c.Ring.Opacity = 0; c.Dim.Opacity = 0; c.Glow.Opacity = 0;
                // 新出现的卡从上一轮的位置开始（滑进来），不是凭空出现在终点
                c.Root.Offset = move ? new Vector3(RwSlotX(i, RwScrollIndex(Math.Clamp(p.Prev, 0, n - 1), n)), RwY, 0) : off;
                p.Cases[i] = c;
                p.Row!.Children.InsertAtTop(c.Root);
            }
            c.Offset = off; c.Size = new Vector2(RwW, RwH);
            bool big = i == s && (p.Rw?.PanelFocus ?? -1) < 0;   // 焦点在左上面板时卡片不放大
            var scale = new Vector3(big ? RwFocus : 1, big ? RwFocus : 1, 1);
            if (move) { c.Root.StartAnimation("Offset", RwSpring(off, 1500)); c.Root.StartAnimation("Scale", RwSpring(scale, 1500)); c.Root.StopAnimation("Opacity"); c.Root.Opacity = 1; }
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
        RwMarkRunning(p);
        // 不调整叠放顺序：放大 1.15 后每边只多出 21，卡距 50 碰不到邻居；把卡从树上摘下再挂回会打断它的动画（连按时卡片停在半路或消失）
        // 焦点框：中心对准焦点卡中心
        if (p.Rw is { } rw)
        {
            var (W, H, _, _, _) = RwBracketGeom(RwW, RwH);
            var to = new Vector3(RwSlotX(s, scroll) + RwW / 2 - W / 2 - RwCurPad, RwY + RwH / 2 - H / 2 - RwCurPad, 0);
            foreach (var v in new Visual[] { rw.Glow, rw.Line, rw.Rim })
            {
                if (move) v.StartAnimation("Offset", RwSpring(to));
                else { v.StopAnimation("Offset"); v.Offset = to; }
                if (entry && !Reduced && !move && rw.PanelFocus < 0) { v.Opacity = 0; v.StartAnimation("Opacity", CsS(1, 420, CsQuart, 300)); }
            }
        }
        p.Prev = s;
    }

    /// <summary>焦点卡在屏幕上的矩形（放大后），给启动动画用。</summary>
    private static (float X, float Y, float W, float H) RwFocusRect(CsCase c) =>
        (c.Offset.X - c.Size.X * (RwFocus - 1) / 2, c.Offset.Y - c.Size.Y * (RwFocus - 1) / 2, c.Size.X * RwFocus, c.Size.Y * RwFocus);

    // ---------------- 页签切换（左上面板） ----------------

    /// <summary>当前样式下显示的页签：主页一定有，Steam / 本地没有游戏时跳过。</summary>
    private List<int> RwTabsShown() => RwTabs.Where(t => t == 0 || CsList(t).Count > 0).ToList();

    private void RwPanelEnter(CsPage p)
    {
        if (p.Rw is not { } rw) return;
        if (rw.XFocus) RwXFocus(p, false, quiet: true);
        rw.PanelFocus = Math.Max(0, Array.IndexOf(RwTabs, p.Tab));
        CuePan(-0.6f);
        RwCursorShow(p, false);
        RwRingTo(rw, rw.PanelFocus, show: true, move: false);
        if (p.Cases.TryGetValue(p.Sel, out var c)) c.Root.StartAnimation("Scale", RwSpring(Vector3.One, 1500));
    }

    private void RwPanelLeave(CsPage p)
    {
        if (p.Rw is not { } rw || rw.PanelFocus < 0) return;
        RwRingTo(rw, rw.PanelFocus, show: false, move: false);
        rw.PanelFocus = -1;
        Cue(Sfx.Back);
        RwCursorShow(p, true);
        if (p.Cases.TryGetValue(p.Sel, out var c)) c.Root.StartAnimation("Scale", RwSpring(new Vector3(RwFocus, RwFocus, 1), 1500));
    }

    private void RwCursorShow(CsPage p, bool on)
    {
        if (p.Rw is not { } rw) return;
        foreach (var v in new Visual[] { rw.Glow, rw.Line, rw.Rim }) { v.StopAnimation("Opacity"); v.StartAnimation("Opacity", CsS(on ? 1 : 0, CsMs(160), CsQuart)); }
    }

    private void RwMove(CsPage p, Direction dir)
    {
        if (p.Rw is not { } rw) return;
        int n = p.Items.Count;
        if (rw.PanelFocus >= 0)
        {
            if (dir is Direction.Left or Direction.Right)
            {
                var shown = RwTabsShown();
                int at = shown.IndexOf(RwTabs[rw.PanelFocus]) + (dir == Direction.Right ? 1 : -1);
                if (at < 0 || at >= shown.Count) { Cue(Sfx.Bump, pan: dir == Direction.Left ? -0.6f : 0.6f); return; }
                rw.PanelFocus = Array.IndexOf(RwTabs, shown[at]);
                CuePan(dir == Direction.Left ? -0.4f : 0.4f);
                RwRingTo(rw, rw.PanelFocus, show: true, move: true);
                return;
            }
            if (dir == Direction.Down && n > 0) { RwPanelLeave(p); return; }
            Cue(Sfx.Bump);
            return;
        }
        int s = p.Sel, j = s;
        if (rw.XFocus)
        {
            RwXFocus(p, false);
            if (dir == Direction.Down) { Cue(Sfx.Back); return; }
            if (dir == Direction.Up) { RwPanelEnter(p); return; }
        }
        else if (dir == Direction.Up && s < n && RunningGame(p.Items[s].Id) != null && p.Cases.ContainsKey(s)) { CuePan(0.4f); RwXFocus(p, true); return; }
        if (p.Tab == 0)
        {
            if (dir == Direction.Up) { RwPanelEnter(p); return; }
            if (dir == Direction.Left) j = s - 1; else if (dir == Direction.Right) j = s + 1;
        }
        else
        {
            int row = s / RgCols, rows = (n + RgCols - 1) / RgCols;
            if (dir == Direction.Up) { if (row == 0) { RwPanelEnter(p); return; } j = s - RgCols; }
            else if (dir == Direction.Down) j = s + RgCols < n ? s + RgCols : row < rows - 1 ? n - 1 : s;
            else if (dir == Direction.Left) j = s % RgCols == 0 ? s : s - 1;
            else j = s % RgCols == RgCols - 1 ? s : s + 1;
        }
        if (j < 0 || j >= n || j == s) { Cue(Sfx.Bump, pan: dir == Direction.Left ? -0.6f : dir == Direction.Right ? 0.6f : 0); return; }
        CuePan(dir == Direction.Left ? -0.3f : dir == Direction.Right ? 0.3f : 0);
        p.Prev = s;
        p.Sel = j;
        p.Ai = j; p.InRecent = false;
        if (p.Tab == 0) CsLayoutRow(p, animate: true); else RwLayoutGrid(p, animate: true);
        CsNoteSpot();
    }

    private void RwActivate(CsPage p)
    {
        if (p.Rw is not { } rw) return;
        if (rw.PanelFocus >= 0)
        {
            int tab = RwTabs[rw.PanelFocus];
            if (tab == p.Tab) { RwPanelLeave(p); return; }
            _rwPanelNext = true;
            _rwRingFrom = new Vector2(RwTabX0 + rw.PanelFocus * RwTabPitch, RwTabY);
            CsGoTab(tab);
            return;
        }
        if (rw.XFocus) { if (!RwQuitFocused()) Cue(Sfx.Bump); return; }   // ✕：退出游戏小窗
        if (p.Sel >= p.Items.Count) { Cue(Sfx.Bump); return; }
        var g = p.Items[p.Sel];
        if (RunningGame(g.Id) is { } running) { ResumeGame(running); return; }
        if (p.Cases.TryGetValue(p.Sel, out var c)) CsLaunch(g, c.Root, RwFocusRect(c));
    }

    // ---------------- Steam / 本地页：竖封面网格 ----------------

    // 6 列 264×396，列距 52、行距 48；左右边缘对齐左上 / 右上面板的外边（x 38 – 1882，11-20 jinyu）；一屏两整行加第三行露头；焦点行保持在第二行
    private const int RgCols = 6;
    private const float RgW = 264, RgH = 396, RgGapX = 52, RgGapY = 48, RgY0 = 196;
    private static float RgX0 => (CsW - (RgCols * RgW + (RgCols - 1) * RgGapX)) / 2;
    private const float RgPitchY = RgH + RgGapY;

    private CsPage RwBuildCat(int tab, string? spot, bool entry)
    {
        var items = CsList(tab);
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        ContainerVisual content = G.C.CreateLayerVisual();   // 组件层：开菜单时整层模糊
        content.Size = new Vector2(CsW, CsH);
        var page = new CsPage { Root = root, Bg = null!, Content = content, Tab = tab, Style = HomeStyle.Row, Items = items, Built = NowMs };
        page.Bg = RwBuild(page, entry);
        page.Bg.Opacity = 0;
        page.Rw!.CardW = RgW; page.Rw.CardH = RgH;
        root.Children.InsertAtTop(page.Bg);
        root.Children.InsertAtTop(content);
        var grid = G.Container(relative: false);
        grid.Size = new Vector2(CsW, CsH);
        content.Children.InsertAtTop(grid);
        grid.Clip = G.C.CreateInsetClip(0, 150, 0, 0);   // 往上滚走的卡从面板下面那条线消失，不压在面板上
        page.Row = grid;
        int k = spot == null ? -1 : items.FindIndex(g => g.Id == spot);
        page.Sel = page.Prev = page.Ai = Math.Max(0, k);
        var prev = _cs!.Page;
        _cs.Page = page;
        RwAttach(page);
        RwLayoutGrid(page, animate: false, entry: entry);
        _cs.Page = prev;
        return page;
    }

    private static int RgScrollRow(int s, int n)
    {
        int rows = (n + RgCols - 1) / RgCols;
        return Math.Clamp(s / RgCols - 1, 0, Math.Max(0, rows - 2));
    }

    private void RwLayoutGrid(CsPage p, bool animate, bool entry = false)
    {
        try { RwLayoutGridCore(p, animate, entry); } finally { RwMarkRunning(p); }
    }

    private void RwLayoutGridCore(CsPage p, bool animate, bool entry)
    {
        int n = p.Items.Count;
        if (n == 0) { RwCursorShow(p, false); return; }
        int s = Math.Clamp(p.Sel, 0, n - 1);
        int sr = RgScrollRow(s, n), psr = RgScrollRow(Math.Clamp(p.Prev, 0, n - 1), n);
        int lo = Math.Max(0, (sr - 2) * RgCols), hi = Math.Min(n - 1, (sr + 5) * RgCols - 1);
        foreach (var gone in p.Cases.Keys.Where(i => i < lo || i > hi).ToList())
            if (p.Cases.Remove(gone, out var gc)) { p.Row!.Children.Remove(gc.Root); CsDisposeCase(gc); }
        bool move = animate && !Reduced;
        Vector3 Slot(int i, int scrollRow) => new(RgX0 + i % RgCols * (RgW + RgGapX), RgY0 + (i / RgCols - scrollRow) * RgPitchY, 0);
        for (int i = lo; i <= hi; i++)
        {
            var off = Slot(i, sr);
            if (!p.Cases.TryGetValue(i, out var c))
            {
                c = CsNewCase(p.Items[i], i, new Vector2(RgW, RgH), radiusK: RwRound, circle: true);
                c.Root.CenterPoint = new Vector3(RgW / 2, RgH / 2, 0);
                c.Ring.Opacity = 0; c.Dim.Opacity = 0; c.Glow.Opacity = 0;
                c.Root.Offset = move ? Slot(i, psr) : off;
                p.Cases[i] = c;
                p.Row!.Children.InsertAtTop(c.Root);
            }
            c.Offset = off; c.Size = new Vector2(RgW, RgH);
            bool big = i == s && (p.Rw?.PanelFocus ?? -1) < 0;   // 焦点在左上面板时卡片不放大
            var scale = new Vector3(big ? RwFocus : 1, big ? RwFocus : 1, 1);
            if (move) { c.Root.StartAnimation("Offset", RwSpring(off, 1500)); c.Root.StartAnimation("Scale", RwSpring(scale, 1500)); c.Root.StopAnimation("Opacity"); c.Root.Opacity = 1; }
            else
            {
                c.Root.StopAnimation("Offset"); c.Root.StopAnimation("Scale");
                c.Root.Scale = scale;
                if (entry && !Reduced && off.Y < CsH)
                {
                    double delay = 80 + (i - lo) % (RgCols * 3) * 18;
                    c.Root.Offset = off + new Vector3(0, 40, 0);
                    c.Root.Opacity = 0;
                    c.Root.StartAnimation("Offset", CsV3(off, 560, CsQuart, delay));
                    c.Root.StartAnimation("Opacity", CsS(1, 400, CsQuart, delay));
                }
                else c.Root.Offset = off;
            }
        }
        if (p.Rw is { } rw)
        {
            var (W, H, _, _, _) = RwBracketGeom(RgW, RgH);
            var o = Slot(s, sr);
            var to = new Vector3(o.X + RgW / 2 - W / 2 - RwCurPad, o.Y + RgH / 2 - H / 2 - RwCurPad, 0);
            foreach (var v in new Visual[] { rw.Glow, rw.Line, rw.Rim })
            {
                if (move) v.StartAnimation("Offset", RwSpring(to));
                else { v.StopAnimation("Offset"); v.Offset = to; }
                if (entry && !Reduced && !move && rw.PanelFocus < 0) { v.Opacity = 0; v.StartAnimation("Opacity", CsS(1, 420, CsQuart, 300)); }
            }
        }
        p.Prev = s;
    }

    // ---------------- 左上图标的焦点：断开的圆环 ----------------

    // Cocoon 在圆按钮上的焦点：同一套四角括号变成圆，被上下左右四个缺口分成四段弧（jinyu 11-20 截图）。
    // 线宽、光晕、边光、流光都和封面焦点框一样；只是画在图标外面一圈，焦点左右移动时弹簧滑过去。
    private const float RwRingR = 36, RwRingPad = 30, RwRingSpan = 52;   // 每段弧 52°，缺口 38°
    private static float RwRingSize => (RwRingR + RwRingPad) * 2;
    private Vector2? _rwRingFrom;

    private static Vector3 RwRingOffset(int i) => new(RwTabX0 + i * RwTabPitch - RwRingSize / 2, RwTabY - RwRingSize / 2, 0);

    private void RwPaintRing(RwChrome rw)
    {
        var dev = G.Device;
        float R = CsRaster, S = RwRingSize, c = S / 2;
        int px = (int)Math.Ceiling(S * R);
        // 面板图标的圆环：圆形容器，走全局括号几何（正圆 = 四段对称的弧）
        using var path = CocoonBracketPath(dev, 2 * RwRingR, 2 * RwRingR, RwRingR, c - RwRingR);
        rw.RGlowSurf ??= G.Surface(px, px);
        using (var ds = CanvasComposition.CreateDrawingSession(rw.RGlowSurf))
        {
            ds.Clear(Colors.Transparent);
            using var cl = new CanvasCommandList(ds);
            using (var s = cl.CreateDrawingSession()) { s.Transform = Matrix3x2.CreateScale(R); s.DrawGeometry(path, RwC2, RwSw * 2, RwRoundStroke); }
            using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 7 * RwDp * R / 2, BorderMode = EffectBorderMode.Soft };
            ds.DrawImage(blur);
        }
        rw.RRimSurf ??= G.Surface(px, px);
        using (var ds = CanvasComposition.CreateDrawingSession(rw.RRimSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            using var outer = path.Stroke(RwSw + 2 * RwDp, RwRoundStroke);
            using var inner = path.Stroke(MathF.Max(0.1f, RwSw - 2 * RwDp), RwRoundStroke);
            using var ring = outer.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            ds.FillGeometry(ring, Color.FromArgb(102, 0xfa, 0xfa, 0xfa));
        }
        rw.RLineSurf ??= G.Surface(px, px);
        using (var ds = CanvasComposition.CreateDrawingSession(rw.RLineSurf))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(R);
            ds.DrawGeometry(path, Colors.White, RwSw, RwRoundStroke);
        }
        ((CompositionSurfaceBrush)rw.RGlow.Brush).Surface = rw.RGlowSurf;
        ((CompositionSurfaceBrush)rw.RRim.Brush).Surface = rw.RRimSurf;
        rw.RLine.Brush = RwFlowBrush(rw, rw.RLineSurf);
        foreach (var v in new Visual[] { rw.RGlow, rw.RLine, rw.RRim }) { v.Size = new Vector2(S, S); v.Opacity = 0; }
    }

    /// <summary>
    /// 直接跳到位：用一段 1 ms 的关键帧动画盖掉正在跑的弹簧（StopAnimation 之后马上赋值，有时会被还没停下的弹簧盖回去——
    /// 压页时焦点框停在上一页那一行就是这个原因）。
    /// </summary>
    private void RwJump(Visual v, Vector3 to)
    {
        v.StopAnimation("Offset");
        v.Offset = to;
        v.StartAnimation("Offset", CsV3(to, 1, CsQuart));
    }

    /// <summary>圆环显示 / 隐藏、移到第 i 个图标（move = 弹簧滑过去，否则直接到位）。</summary>
    private void RwRingTo(RwChrome rw, int i, bool show, bool move)
    {
        var to = RwRingOffset(Math.Max(0, i));
        foreach (var v in new Visual[] { rw.RGlow, rw.RLine, rw.RRim })
        {
            if (move && !Reduced) v.StartAnimation("Offset", RwSpring(to)); else RwJump(v, to);
            v.StopAnimation("Opacity");
            v.StartAnimation("Opacity", CsS(show ? 1 : 0, CsMs(160), CsQuart));
        }
    }
}
