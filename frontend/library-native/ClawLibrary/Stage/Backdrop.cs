// 动态背景：一比一复刻 folia 的「隐现」（Latent）背景——封面取色的流体渐变 + 像素抖动两层着色器 + 主题底色遮罩
// （着色器见 Shaders/Latent.hlsl）。参数全部照 folia 的默认值：流体速度 0.3、扭曲 0.8、旋涡 0.1、颗粒 0.01；
// 像素层 warp / 4x4、格子 2.5、速度 0.1、不透明度 0.55（soft-light）；遮罩 35 %；颜色来源「封面 + 主题」：
// 封面缩到 50×50 用按像素数加权的中位切分取 6 个代表色（folia utils/colorPalette 的算法）。
// 画布分辨率同 folia：不低于 1 倍、总像素不超过 1280×720，再拉伸到全屏。
// 出帧：合成器的帧上（UI 线程的 Rendering），每帧只算缝的开口那一条（离屏画好再拷进合成表面的那一块）。
// 画布按「缝在屏幕正中」来画，整块视觉跟着缝平移（WallStage 里的表达式），墙面平移时不用重画。
// 墙面在动（相机、让位、翻牌、波次、平移）的时候背景停一下、时钟也停，动完接着走：实测墙面动画和背景同时出帧
// 会让 DWM 隔帧合成；背景走得极慢（folia 在主页干脆让它静止），停这零点几秒看不出来。暂停时停在最后一帧。
// 与 folia 的差别：folia 在主页默认让它静止（省 GPU），这里按要求让它动，并在没操作 60 秒后停；
// 换焦点时颜色 800 ms 渐变过去（folia 换歌是直接换）。没有音频，音频驱动的那部分恒为 0。
using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class Backdrop : IDisposable
{
    // folia DEFAULT_LATENT_BACKGROUND_TUNING
    private const float MeshSpeed = 0.3f, MeshDistortion = 0.8f, MeshSwirl = 0.1f, GrainOverlay = 0.01f;
    private const float DitherSpeed = 0.1f, DitherSize = 2.5f, DitherOpacity = 0.55f, DitherScale = 0.6f;
    private const float OverlayOpacity = 0.35f;
    private const double ColorFadeMs = 800;

    // 主题（folia 午夜主题的底色 / 主色 / 强调色）
    private static readonly Vector4 ThemeBg = Rgb(Theme.Bg), ThemePrimary = Rgb(Gfx.Rgba(0xf7, 0xf4, 0xee)), ThemeAccent = Rgb(Gfx.Rgba(0xfd, 0x5c, 0x47));

    private readonly Gfx _g;
    public readonly ContainerVisual Root;
    private readonly CompositionSurfaceBrush _brush;
    private readonly byte[]? _shader;
    private PixelShaderEffect? _effect;
    private CompositionDrawingSurface? _surface;
    private CanvasRenderTarget? _strip;
    private int _cw, _ch;
    private float _pixelRatio = 1;
    private bool _running, _dirty = true;
    private double _meshTime, _ditherTime;
    private Vector4[] _from = DefaultColors(), _to = DefaultColors();
    /// <summary>换色渐变走了多少毫秒：按出帧的 dt 累加（不按挂钟），背景停帧时渐变也停，接着走时不会一下跳到新颜色。</summary>
    private double _fadeMs = ColorFadeMs + 1;
    private string? _coverPath;
    private int _lastX0 = -1, _lastX1 = -1;
    public float Dpi = 1;
    /// <summary>画完再整体高斯模糊（DIP，标准差；0 = 不模糊）。控制中心铺满全屏时用：folia 网格主页在背景上压 4 px 模糊。</summary>
    public float BlurDip;
    private static readonly Dictionary<string, Vector4[]> ColorCache = new();
    private GaussianBlurEffect? _blur;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private double _stripLeft, _stripWidth;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    public Backdrop(Gfx g)
    {
        _g = g;
        Root = g.Container();
        _brush = g.C.CreateSurfaceBrush();
        _brush.Stretch = CompositionStretch.Fill;
        Root.Children.InsertAtTop(g.Sprite(g.Solid(Theme.Bg)));
        Root.Children.InsertAtTop(g.Sprite(_brush));
        try { _shader = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Latent.bin")); }
        catch (Exception ex) { Log.Error("latent shader missing", ex); }
    }

    private static Vector4 Rgb(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1);

    private static Vector4[] DefaultColors() => Palette(Array.Empty<Vector4>());

    /// <summary>colorSource = cover-theme：[c0, c1|主色, c2|c0, c3|c1, 主题底色, 强调色]；抖动层前景 c0、背景主题底色。</summary>
    private static Vector4[] Palette(IReadOnlyList<Vector4> cover)
    {
        var primary = cover.Count > 0 ? cover[0] : ThemePrimary;
        var secondary = cover.Count > 1 ? cover[1] : ThemePrimary;
        var tertiary = cover.Count > 2 ? cover[2] : primary;
        var quaternary = cover.Count > 3 ? cover[3] : secondary;
        return new[] { primary, secondary, tertiary, quaternary, ThemeBg, ThemeAccent, /* 抖动前景 */ primary };
    }

    /// <summary>按视口建画布（folia：渲染倍率不低于 1，总像素不超过 1280×720）。</summary>
    public void Build(float w, float h)
    {
        if (_shader == null) return;
        double scale = Math.Max(Dpi, 1);
        double px = w * scale * h * scale;
        double maxPixels = _settings.Kind == BackgroundKind.Latent ? 1280 * 720 : 1920 * 1080;
        if (px > maxPixels) scale *= Math.Sqrt(maxPixels / px);
        int cw = Math.Max(1, (int)Math.Round(w * scale)), ch = Math.Max(1, (int)Math.Round(h * scale));
        if (cw == _cw && ch == _ch && _surface != null) return;
        lock (_gate)
        {
        _cw = cw; _ch = ch; _pixelRatio = (float)scale;
        InvalidateImageFrame();
        try
        {
            _effect ??= new PixelShaderEffect(_shader);
            _surface = _g.Surface(cw, ch);
            _brush.Surface = _surface;
            using (var ds = CanvasComposition.CreateDrawingSession(_surface)) ds.Clear(Theme.Bg);
            _strip?.Dispose();
            _strip = null;
            _lastX0 = _lastX1 = -1;
            _validX0 = _validX1 = 0;
            _dirty = true;
        }
        catch (Exception ex) { Log.Error("latent surface", ex); _surface = null; }
        }
        Log.Info($"latent backdrop canvas {cw}x{ch} (pixel ratio {scale:F3})");
    }

    public bool Visible { set => Root.IsVisible = value; }

    /// <summary>开 / 停（停的时候画面定在最后一帧）。</summary>
    public void Run(bool on)
    {
        lock (_gate) { if (on && !_running) _dirty = true; _running = on; }
        _wake.Set();
    }

    /// <summary>缝的开口在屏幕上的位置（DIP）；UI 线程在相机 / 缝变化时报过来（补间途中报起止两头的并集）。</summary>
    public void SetStrip(double left, double width)
    {
        lock (_gate)
        {
            if (Math.Abs(left - _stripLeft) < 0.5 && Math.Abs(width - _stripWidth) < 0.5) return;
            _stripLeft = left; _stripWidth = width;
            _stripChanged = true;   // 停着的时候开口挪了，也要把新露出来的那块补画上（只补新的那块，见 Frame）
        }
        _wake.Set();
    }

    private void EnsureThread()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "latent-backdrop", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Loop()
    {
        long last = 0;
        while (!_stop)
        {
            bool need;
            double left, width;
            lock (_gate) { need = NeedsFrames; left = _stripLeft; width = _stripWidth; }
            if (!need) { _wake.WaitOne(500); last = 0; continue; }
            DwmFlush();   // 等到这一帧合成完，下一帧开始画
            long now = Stopwatch.GetTimestamp();
            double dt = last == 0 ? 16.7 : Math.Min(100, (now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
            try { lock (_gate) Frame(dt, left, width); }
            catch (Exception ex) { Log.Error("latent loop", ex); Thread.Sleep(500); }
        }
    }

    /// <summary>还要不要逐帧画（在跑、或颜色还在渐变、或有待画的一帧）。</summary>
    public bool NeedsFrames => _surface != null && ((_running && Animated) || _dirty || _stripChanged || (_settings.Kind == BackgroundKind.Latent && _fadeMs < ColorFadeMs) || ImageFading);
    private bool _stripChanged;

    /// <summary>换封面：后台缩到 50×50 取代表色，颜色在 800 ms 里渐变过去。post 把结果送回 UI 线程。</summary>
    public void SetCover(string? imagePath, Action<Action> post)
    {
        if (imagePath == _coverPath) return;
        _coverPath = imagePath;
        LoadCoverImage(imagePath, post);
        if (imagePath == null) { SetPalette(Palette(Array.Empty<Vector4>())); return; }
        // 同一张封面取过色就直接用（两块背景、反复开关控制中心都不再解码）
        lock (ColorCache)
            if (ColorCache.TryGetValue(imagePath, out var known)) { SetPalette(Palette(known)); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var colors = await RepresentativeColors(imagePath, 6);
                lock (ColorCache) ColorCache[imagePath] = colors;
                post(() => { if (_coverPath == imagePath) SetPalette(Palette(colors)); });
            }
            catch (Exception ex) { Log.Warn($"latent colors {imagePath}: {ex.Message}"); }
        });
    }

    private static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>true：换色不做 800 ms 渐变，直接换（看不见的时候用：渐变期间每帧都要重画整张着色器）。</summary>
    public bool InstantColors;

    private void SetPalette(Vector4[] next)
    {
        lock (_gate)
        {
            _from = CurrentColors();
            _to = next;
            _fadeMs = 0;
            if (InstantColors) { _from = next; _fadeMs = ColorFadeMs + 1; }
            _dirty = true;
        }
        _wake.Set();
    }

    /// <summary>
    /// 背景此刻的主题色，给控制中心里的图表、读数当强调色：封面前四个代表色里挑最鲜艳的（按占比稍加权），
    /// 色相不变，饱和度和亮度拉到深色底上看得清的范围；封面全是灰的就用 folia 的强调色。
    /// </summary>
    public Color Accent
    {
        get
        {
            Vector4[] to;
            lock (_gate) to = _to;
            Vector4 best = ThemeAccent;
            double bestScore = 0.12;
            for (int i = 0; i < Math.Min(4, to.Length); i++)
            {
                var c = to[i];
                double max = Math.Max(c.X, Math.Max(c.Y, c.Z)), min = Math.Min(c.X, Math.Min(c.Y, c.Z));
                double score = (max - min) * (1 - i * 0.08);
                if (score > bestScore) { bestScore = score; best = c; }
            }
            var (h, s, _) = ToHsl(best);
            return FromHsl(h, Math.Clamp(s, 0.55, 0.9), 0.68);
        }
    }

    /// <summary>当前封面的前四个代表色（按占比从多到少），日志 / 调色用。</summary>
    public string CoverColorsText()
    {
        Vector4[] to;
        lock (_gate) to = _to;
        return string.Join(" ", to.Take(4).Select(c => $"#{(int)(c.X * 255):x2}{(int)(c.Y * 255):x2}{(int)(c.Z * 255):x2}"));
    }

    private static (double h, double s, double l) ToHsl(Vector4 c)
    {
        double r = c.X, g = c.Y, b = c.Z, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-6) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Ch(double t)
        {
            t = (t % 1 + 1) % 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        byte B(double x) => (byte)Math.Round(Math.Clamp(x, 0, 1) * 255);
        return Color.FromArgb(255, B(Ch(h + 1 / 3.0)), B(Ch(h)), B(Ch(h - 1 / 3.0)));
    }

    private Vector4[] CurrentColors()
    {
        double k = Math.Clamp(_fadeMs / ColorFadeMs, 0, 1);
        k = k * k * (3 - 2 * k);
        var r = new Vector4[_to.Length];
        for (int i = 0; i < r.Length; i++) r[i] = Vector4.Lerp(_from[i], _to[i], (float)k);
        return r;
    }

    /// <summary>画一帧（UI 线程，合成器的帧上）：只算 [left, left+width)（DIP）那一条。</summary>
    /// <summary>hold：墙面在动，背景自己的流动和换色先停（不推进时间），但开口变宽 / 挪了露出来的新区域照样补画，不留旧画面。</summary>
    public void FrameNow(double dtMs, bool hold = false) { double l, w; lock (_gate) { l = _stripLeft; w = _stripWidth; } Frame(hold ? 0 : dtMs, l, w, hold); }

    private void Frame(double dtMs, double left, double width, bool hold = false)
    {
        if (_surface == null || _effect == null) return;
        _stripChanged = false;
        if (_running && Animated)
        {
            // folia：currentFrame += dt × speed，u_time = currentFrame / 1000
            _meshTime += dtMs * MeshSpeed / 1000;
            _ditherTime += dtMs * DitherSpeed / 1000;
            _imageTime += dtMs / 1000;
        }
        bool fading = (_settings.Kind == BackgroundKind.Latent && _fadeMs < ColorFadeMs) || ImageFading;
        if (fading && !hold) _fadeMs += dtMs;
        if (ImageFading && !hold) _imageFadeMs += dtMs;
        int x0 = Math.Clamp((int)Math.Floor((left - 6) * _pixelRatio), 0, _cw);
        int x1 = Math.Clamp((int)Math.Ceiling((left + width + 6) * _pixelRatio), 0, _cw);
        if (x1 - x0 < 1) return;
        bool moved = x0 != _lastX0 || x1 != _lastX1;
        if (hold && !_dirty)
        {
            // 停着：画面上已经是同一时刻的那段（_validX0.._validX1）不用重画，只补开口新露出来的两头
            if (x0 >= _validX0 && x1 <= _validX1) return;
            _lastX0 = x0; _lastX1 = x1;
            if (x1 <= _validX0 || x0 >= _validX1 || _validX1 <= _validX0) { DrawRange(x0, x1); _validX0 = x0; _validX1 = x1; return; }
            if (x0 < _validX0) DrawRange(x0, _validX0);
            if (x1 > _validX1) DrawRange(_validX1, x1);
            _validX0 = Math.Min(_validX0, x0); _validX1 = Math.Max(_validX1, x1);
            return;
        }
        if (!hold && !(_running && Animated) && !_dirty && !moved && !fading) return;
        _dirty = false;
        _lastX0 = x0; _lastX1 = x1;
        _validX0 = x0; _validX1 = x1;
        DrawRange(x0, x1);
    }

    /// <summary>画布上已经画好、时间和颜色都是当前这一刻的那段（停帧时只补这段以外的）。</summary>
    private int _validX0, _validX1;

    private void DrawRange(int x0, int x1)
    {
        int sw = x1 - x0;
        if (sw < 1 || _surface == null || _effect == null) return;
        try
        {
            if (_strip == null || _strip.SizeInPixels.Width < sw)
            {
                _strip?.Dispose();
                int alloc = Math.Min(_cw, (sw + 63) / 64 * 64);
                _strip = new CanvasRenderTarget(_g.Device, alloc, _ch, 96);
            }
            if (_settings.Kind != BackgroundKind.Latent)
            {
                DrawImageRange(x0, sw);
                return;
            }
            var colors = CurrentColors();
            var p = _effect.Properties;
            p["resolution"] = new Vector2(_cw, _ch);
            p["origin"] = new Vector2(x0, 0);
            p["pixelRatio"] = _pixelRatio;
            p["meshTime"] = (float)_meshTime;
            p["ditherTime"] = (float)_ditherTime;
            p["distortion"] = MeshDistortion;
            p["swirl"] = MeshSwirl;
            p["grainOverlay"] = GrainOverlay;
            p["pxSize"] = DitherSize;
            p["ditherScale"] = DitherScale;
            p["ditherOpacity"] = DitherOpacity;
            p["overlayOpacity"] = OverlayOpacity;
            p["displayMode"] = (float)_settings.Latent;
            for (int i = 0; i < 6; i++) p[$"c{i}"] = colors[i];
            p["ditherBack"] = ThemeBg;
            p["ditherFront"] = colors[6];
            p["themeBg"] = ThemeBg;
            using (var ds = _strip.CreateDrawingSession())
            {
                ICanvasImage img = _effect;
                if (BlurDip > 0)
                {
                    _blur ??= new GaussianBlurEffect { BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Speed };
                    _blur.Source = _effect;
                    _blur.BlurAmount = BlurDip * _pixelRatio;
                    img = _blur;
                }
                ds.DrawImage(img, 0, 0, new Windows.Foundation.Rect(0, 0, sw, _ch));
            }
            using (var ds = CanvasComposition.CreateDrawingSession(_surface, new Windows.Foundation.Rect(x0, 0, sw, _ch)))
                ds.DrawImage(_strip, 0, 0, new Windows.Foundation.Rect(0, 0, sw, _ch));   // 更新区域里的坐标从这块的左上角算
        }
        catch (Exception ex) { Log.Error("latent frame", ex); _surface = null; }
    }

    // ---------------- 取色（folia utils/colorPalette：加权中位切分） ----------------

    private static async Task<Vector4[]> RepresentativeColors(string path, int count)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var dec = await BitmapDecoder.CreateAsync(stream);
        var t = new BitmapTransform { ScaledWidth = 50, ScaledHeight = 50, InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await dec.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, t, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var px = data.DetachPixelData();

        var hist = new Dictionary<int, (long r, long g, long b, int w)>();
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            if (px[i + 3] < 128) continue;
            int r = px[i], g = px[i + 1], b = px[i + 2];
            int key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
            hist.TryGetValue(key, out var e);
            hist[key] = (e.r + r, e.g + g, e.b + b, e.w + 1);
        }
        var colors = hist.Values.Select(e => new WColor((int)Math.Round(e.r / (double)e.w), (int)Math.Round(e.g / (double)e.w), (int)Math.Round(e.b / (double)e.w), e.w)).ToList();
        if (colors.Count == 0) return Array.Empty<Vector4>();
        var buckets = new List<List<WColor>> { colors };
        while (buckets.Count < Math.Min(count, colors.Count))
        {
            int best = -1; double bestScore = -1;
            for (int i = 0; i < buckets.Count; i++)
            {
                if (buckets[i].Count < 2) continue;
                double score = WidestRange(buckets[i]).range * Math.Sqrt(buckets[i].Sum(c => c.W));
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) break;
            var split = Split(buckets[best]);
            if (split == null) break;
            buckets.RemoveAt(best);
            buckets.InsertRange(best, new[] { split.Value.a, split.Value.b });
        }
        return buckets.Select(Average)
            .Select((c, i) => (c, i)).OrderByDescending(e => e.c.W).ThenBy(e => e.i)   // 与 Array.sort 一样是稳定排序
            .Select(e => new Vector4(e.c.R / 255f, e.c.G / 255f, e.c.B / 255f, 1)).ToArray();
    }

    private readonly record struct WColor(int R, int G, int B, int W);

    private static (char ch, int range) WidestRange(List<WColor> cs)
    {
        int Range(Func<WColor, int> f) => cs.Max(f) - cs.Min(f);
        int r = Range(c => c.R), g = Range(c => c.G), b = Range(c => c.B);
        var best = ('r', r);            // reduce 从 r 开始，只有严格更大才换
        if (g > best.Item2) best = ('g', g);
        if (b > best.Item2) best = ('b', b);
        return best;
    }

    private static (List<WColor> a, List<WColor> b)? Split(List<WColor> bucket)
    {
        if (bucket.Count < 2) return null;
        var (ch, _) = WidestRange(bucket);
        Func<WColor, int> key = ch == 'r' ? c => c.R : ch == 'g' ? c => c.G : c => c.B;
        var sorted = bucket.Select((c, i) => (c, i)).OrderBy(e => key(e.c)).ThenBy(e => e.i).Select(e => e.c).ToList();
        double mid = bucket.Sum(c => c.W) / 2.0, acc = 0;
        int splitIndex = 1;
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            acc += sorted[i].W;
            splitIndex = i + 1;
            if (acc >= mid) break;
        }
        return (sorted.Take(splitIndex).ToList(), sorted.Skip(splitIndex).ToList());
    }

    private static WColor Average(List<WColor> b)
    {
        double w = b.Sum(c => c.W);
        return new WColor((int)Math.Round(b.Sum(c => (double)c.R * c.W) / w), (int)Math.Round(b.Sum(c => (double)c.G * c.W) / w), (int)Math.Round(b.Sum(c => (double)c.B * c.W) / w), (int)w);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(500);
        _strip?.Dispose();
        _blur?.Dispose();
        _effect?.Dispose();
        DisposeImages();
    }
}
