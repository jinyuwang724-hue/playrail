using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Windows.Foundation;
using Windows.UI;

namespace ClawLibrary.Stage;

public sealed partial class Backdrop
{
    private BackgroundSettings _settings = new();
    private CanvasBitmap? _cover, _noise;
    private CanvasRenderTarget? _imageFrame, _poster, _previousPoster, _readability, _previousReadability;
    private PixelShaderEffect? _lens, _paper;
    private Action<Action>? _postImage;
    /// <summary>外面最后要的封面（壁纸盖住它时也记着）。</summary>
    private string? _requestedCover;
    private int _imageVersion;
    private bool _disposed;
    private double _imageTime, _imageFadeMs = 800;
    private MonetColors _monetColors = MonetColors.Default, _previousMonetColors = MonetColors.Default;
    private bool Animated => _settings.Kind == BackgroundKind.Latent
        || (_settings.Kind == BackgroundKind.Monet && _settings.MonetDrift && _poster != null)
        || (_settings.Kind == BackgroundKind.Filmstrip && _filmAtlases != null);
    private bool ImageFading => _imageFadeMs < 800
        && ((_settings.Kind == BackgroundKind.Monet && _previousPoster != null) || _settings.Kind == BackgroundKind.Filmstrip);

    public void Configure(BackgroundSettings settings)
    {
        if (_settings == settings) return;
        var oldKind = _settings.Kind;
        bool filmGeometryChanged = oldKind == BackgroundKind.Filmstrip && settings.Kind == BackgroundKind.Filmstrip
            && (_settings.FilmLayout != settings.FilmLayout || _settings.FilmStripCount != settings.FilmStripCount);
        bool reload = oldKind != settings.Kind || _settings.MonetStreaks != settings.MonetStreaks;
        _settings = settings;
        if (oldKind == BackgroundKind.Filmstrip && (settings.Kind != BackgroundKind.Filmstrip || filmGeometryChanged)) ReleaseFilm();
        InvalidateImageFrame();
        _dirty = true;
        if (reload && _postImage != null) LoadCoverImage(_coverPath, _postImage);
        // 壁纸开关、换了一张壁纸、换了样式：该用的图变了就换过去
        if (_postImage != null && (_settings.WallpaperInUse ?? _requestedCover) != _coverPath) SetCover(_requestedCover, _postImage);
        Log.Info($"background: {settings.Name} / {settings.Variant}");
    }

    private void InvalidateImageFrame(bool keepReadability = false)
    {
        _imageFrame?.Dispose(); _imageFrame = null;
        if (!keepReadability) { _readability?.Dispose(); _readability = null; }
    }

    private void LoadCoverImage(string? path, Action<Action> post)
    {
        _postImage = post;
        int version = ++_imageVersion;
        if (path == null)
        {
            _cover?.Dispose(); _cover = null;
            _poster?.Dispose(); _poster = null;
            _previousPoster?.Dispose(); _previousPoster = null;
            _previousReadability?.Dispose(); _previousReadability = null;
            InvalidateImageFrame(); _dirty = true;
            if (_settings.Kind == BackgroundKind.Filmstrip)
            {
                _previousMonetColors = _monetColors;
                _monetColors = MonetColors.Default;
                _imageFadeMs = InstantColors ? 800 : 0;
            }
            return;
        }
        if (_settings.Kind == BackgroundKind.Latent) return;
        if (_settings.Kind == BackgroundKind.Filmstrip)
        {
            _cover?.Dispose(); _cover = null;
            _poster?.Dispose(); _poster = null;
            _previousPoster?.Dispose(); _previousPoster = null;
            _readability?.Dispose(); _readability = null;
            _previousReadability?.Dispose(); _previousReadability = null;
            LoadFilmAccent(path, post, version);
            return;
        }
        var settings = _settings;
        // Win2D's device is free-threaded. Decode and process away from input / composition callbacks.
        _ = Task.Run(async () =>
        {
            CanvasBitmap? cover = null;
            CanvasRenderTarget? poster = null;
            try
            {
                cover = await CanvasBitmap.LoadAsync(_g.Device, path, 96);
                var colors = _monetColors;
                if (settings.Kind == BackgroundKind.Monet)
                {
                    Vector4[] samples;
                    lock (ColorCache) ColorCache.TryGetValue(path, out samples!);
                    if (samples == null)
                    {
                        samples = await RepresentativeColors(path, 6);
                        lock (ColorCache) ColorCache[path] = samples;
                    }
                    colors = MonetColors.FromCover(samples);
                    poster = BuildMonetPoster(cover, settings.MonetStreaks, colors);
                }
                var readyCover = cover; var readyPoster = poster;
                post(() =>
                {
                    if (_disposed || version != _imageVersion) { readyCover.Dispose(); readyPoster?.Dispose(); return; }
                    _cover?.Dispose(); _cover = readyCover;
                    _previousPoster?.Dispose();
                    _previousReadability?.Dispose();
                    _previousPoster = _poster;
                    _previousReadability = _readability;
                    _readability = null;
                    _previousMonetColors = _monetColors;
                    _monetColors = colors;
                    _poster = readyPoster;
                    _imageFadeMs = InstantColors ? 800 : 0;
                    if (_previousPoster == null || _poster == null || InstantColors)
                    {
                        _imageFadeMs = 800;
                        _previousPoster?.Dispose(); _previousPoster = null;
                        _previousReadability?.Dispose(); _previousReadability = null;
                    }
                    InvalidateImageFrame(keepReadability: true); _dirty = true;
                });
            }
            catch (Exception ex)
            {
                cover?.Dispose(); poster?.Dispose();
                post(() =>
                {
                    if (_disposed || version != _imageVersion) return;
                    _cover?.Dispose(); _cover = null;
                    _poster?.Dispose(); _poster = null;
                    _previousPoster?.Dispose(); _previousPoster = null;
                    _previousReadability?.Dispose(); _previousReadability = null;
                    InvalidateImageFrame(); _dirty = true;
                });
                Log.Warn($"background cover: {ex.Message}");
            }
        });
    }

    // Folia's default Monet treatment: fixed 1920×1080 cover crop, 6 px blur,
    // saturation 1.05, theme luminance wash .34, overlay .74, bloom, veil and 18 streaks.
    private CanvasRenderTarget BuildMonetPoster(CanvasBitmap cover, bool streaks, MonetColors theme)
    {
        const int w = 1920, h = 1080;
        const float overlay = .74f;
        using var crop = new CanvasRenderTarget(_g.Device, w, h, 96);
        using (var ds = crop.CreateDrawingSession())
        {
            ds.Clear(theme.BackgroundColor);
            var size = cover.Size;
            float scale = Math.Max(w / (float)size.Width, h / (float)size.Height);
            ds.DrawImage(cover, new Rect((w-size.Width*scale)/2, (h-size.Height*scale)/2, size.Width*scale, size.Height*scale));
        }
        var result = new CanvasRenderTarget(_g.Device, w, h, 96);
        using (var blur = new GaussianBlurEffect { Source = crop, BlurAmount = 6, BorderMode = EffectBorderMode.Hard })
        using (var ds = result.CreateDrawingSession()) ds.DrawImage(blur);
        var pixels = result.GetPixelBytes(); // BGRA8
        var bg = theme.Background; var accent = theme.Accent; var primary = theme.Primary;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            var rgb = new Vector3(pixels[i+2], pixels[i+1], pixels[i]) / 255f;
            float lum = Vector3.Dot(rgb, new Vector3(.2126f, .7152f, .0722f));
            var tinted = lum <= .55f ? Vector4.Lerp(bg, accent, lum/.55f) : Vector4.Lerp(accent, primary, (lum-.55f)/.45f);
            rgb = Vector3.Lerp(new Vector3(lum)+(rgb-new Vector3(lum))*1.05f, new Vector3(tinted.X,tinted.Y,tinted.Z), .34f);
            pixels[i] = Byte(rgb.Z); pixels[i+1] = Byte(rgb.Y); pixels[i+2] = Byte(rgb.X);
        }
        result.SetPixelBytes(pixels);
        using (var ds = result.CreateDrawingSession())
        {
            using var gradient = new CanvasLinearGradientBrush(_g.Device, new[] {
                Stop(0, theme.AccentColor, .08f+overlay*.62f), Stop(.44f, theme.BackgroundColor, .16f+overlay*.58f), Stop(1, theme.PrimaryColor, .06f+overlay*.36f)
            }) { StartPoint = Vector2.Zero, EndPoint = new Vector2(w,h) };
            ds.FillRectangle(0,0,w,h,gradient);
            using var bloom = new CanvasRadialGradientBrush(_g.Device, new[] { Stop(0,theme.AccentColor,overlay*.42f), Stop(1,theme.BackgroundColor,0) })
                { Center = new Vector2(w*.18f,h*.34f), RadiusX = w*.55f, RadiusY = w*.55f };
            ds.FillRectangle(0,0,w,h,bloom);
            using var veil = new CanvasLinearGradientBrush(_g.Device, new[] { Stop(0,theme.BackgroundColor,0), Stop(1,theme.BackgroundColor,.18f+overlay*.38f) })
                { StartPoint = new Vector2(w*.45f,0), EndPoint = new Vector2(w,0) };
            ds.FillRectangle(0,0,w,h,veil);
            if (streaks) for (int i=0;i<18;i++) ds.FillRectangle(i*127%w, i*211%h-40, 1, h*.28f, Gfx.WithAlpha(theme.BackgroundColor,.1f+overlay*.08f));
        }
        return result;
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v*255),0,255);
    private static CanvasGradientStop Stop(float at, Color color, float alpha) => new() { Position = at, Color = Gfx.WithAlpha(color,alpha) };

    private readonly record struct MonetColors(Vector4 Background, Vector4 Accent, Vector4 Primary)
    {
        public static MonetColors Default => new(ThemeBg, ThemeAccent, ThemePrimary);
        public Color BackgroundColor => Gfx.Rgba(Byte(Background.X), Byte(Background.Y), Byte(Background.Z));
        public Color AccentColor => Gfx.Rgba(Byte(Accent.X), Byte(Accent.Y), Byte(Accent.Z));
        public Color PrimaryColor => Gfx.Rgba(Byte(Primary.X), Byte(Primary.Y), Byte(Primary.Z));

        public static MonetColors FromCover(IReadOnlyList<Vector4> samples)
        {
            if (samples.Count == 0) return Default;
            // Weighted median-cut samples are sorted by covered area. Use the first
            // visible color so a small, vivid logo cannot override a broad cover hue.
            // Skip dark/nearly gray areas when a colored part of the cover exists.
            var best = samples[0];
            foreach (var color in samples)
            {
                float max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
                float min = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
                if (max >= .25f && max - min >= .14f) { best = color; break; }
            }
            var accent = Vector4.Lerp(best, new Vector4(.5f,.5f,.5f,1), .12f);
            var bg = Vector4.Lerp(new Vector4(.065f,.07f,.08f,1), accent, .20f);
            var primary = Vector4.Lerp(accent, new Vector4(1,1,1,1), .7f);
            return new MonetColors(bg, accent, primary);
        }
    }

    private void EnsureImageFrame()
    {
        if (_settings.Kind == BackgroundKind.Filmstrip)
        {
            _imageFrame ??= new CanvasRenderTarget(_g.Device, _cw, _ch, 96);
            using var film = _imageFrame.CreateDrawingSession();
            DrawFilmWall(film);
            return;
        }
        if (_imageFrame != null) return;
        _imageFrame = new CanvasRenderTarget(_g.Device,_cw,_ch,96);
        using var ds = _imageFrame.CreateDrawingSession();
        ds.Clear(Theme.Bg);
        if (_cover == null) return;
        if (_settings.Kind != BackgroundKind.Nomand) return;
        bool paper = _settings.Nomand == NomandEffect.Paper;
        var shader = paper ? _paper ??= ImageShader("NomandPaper") : _lens ??= ImageShader("NomandLens");
        shader.Source1 = _cover;
        shader.Source1Interpolation = CanvasImageInterpolation.Linear;
        if (paper)
        {
            _noise ??= CanvasBitmap.LoadAsync(_g.Device,Path.Combine(AppContext.BaseDirectory,"Assets","PaperNoise.png"),96).AsTask().GetAwaiter().GetResult();
            shader.Source2 = _noise;
            shader.Source2Interpolation = CanvasImageInterpolation.Linear;
        }
        var p = shader.Properties;
        p["resolution"] = new Vector2(_cw,_ch);
        p["imageSize"] = new Vector2((float)_cover.Size.Width,(float)_cover.Size.Height);
        p["imageScale"] = paper ? 1f/(1f-2f*.02f*(2*.65f+1.5f*.35f+.2f*.2f+.75f*.42f+.1f*.3f))+.01f : LensOverscan();
        p["themeBg"] = ThemeBg;
        if (paper) { p["pixelRatio"] = _pixelRatio; p["themeAccent"] = ThemeAccent; }
        ds.DrawImage(shader,0,0,new Rect(0,0,_cw,_ch));
    }

    private PixelShaderEffect ImageShader(string name) => new(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets",name+".bin")));
    private static float LensOverscan()
    {
        double radius = .5*Math.Sqrt(1+16.0/9*16/9), bulge = .3*1.4;
        double reach = .7*Math.Pow(.45,1.3+2.7*.45);
        double room = Math.Max(.35,1-reach*.2/radius);
        double Push(double scale) { double rn = 2*radius/scale; return Math.Tan(rn*bulge)/Math.Tan(bulge)/rn; }
        double low = Math.Max(1,2*radius*bulge/1.45), high=1.8;
        if (low >= high || Push(high)>high*room) return 1.8f;
        for (int i=0;i<32;i++) { double mid=(low+high)/2; if(Push(mid)>mid*room) low=mid; else high=mid; }
        return (float)Math.Min(1.8,high+.01);
    }

    private void DrawImageRange(int x0, int width)
    {
        EnsureImageFrame();
        var disposable = new List<IDisposable>();
        try
        {
            ICanvasImage image = _imageFrame!;
            if (_settings.Kind == BackgroundKind.Monet && _poster != null)
            {
                var drift = _settings.MonetDrift ? MonetDrift.At(_imageTime) : new Vector3(0,0,1);
                float fit = Math.Max(_cw/1920f,_ch/1080f);
                var matrix = Matrix3x2.CreateTranslation(-960,-540) * Matrix3x2.CreateScale(fit*drift.Z) * Matrix3x2.CreateTranslation(_cw*(.5f+drift.X),_ch*(.5f+drift.Y));
                var current = new Transform2DEffect { Source=_poster,TransformMatrix=matrix,InterpolationMode=CanvasImageInterpolation.Linear };
                disposable.Add(current); image=current;
                if (ImageFading)
                {
                    var previous = new Transform2DEffect { Source=_previousPoster,TransformMatrix=matrix,InterpolationMode=CanvasImageInterpolation.Linear };
                    // CSS ease-in-out (0.42,0,0.58,1), 800 ms.
                    float a = MonetDrift.Fade((float)(_imageFadeMs/800));
                    var oldVeil = _previousReadability ??= Readability(_previousMonetColors);
                    var currentVeil = _readability ??= Readability(_monetColors);
                    var oldLayer = new CompositeEffect { Sources = { previous, oldVeil } };
                    var currentLayer = new CompositeEffect { Sources = { current, currentVeil } };
                    var mix = new ArithmeticCompositeEffect { Source1=oldLayer,Source2=currentLayer,Source1Amount=1-a,Source2Amount=a,MultiplyAmount=0,Offset=0 };
                    disposable.Add(previous); disposable.Add(oldLayer); disposable.Add(currentLayer); disposable.Add(mix); image=mix;
                }
                else
                {
                    if (_previousPoster != null) { _previousPoster.Dispose(); _previousPoster=null; }
                    if (_previousReadability != null) { _previousReadability.Dispose(); _previousReadability=null; }
                    _readability ??= Readability(_monetColors);
                    var composite = new CompositeEffect { Sources = { image, _readability } };
                    disposable.Add(composite); image=composite;
                }
            }
            if (BlurDip>0)
            {
                var blur = new GaussianBlurEffect { Source=image,BlurAmount=BlurDip*_pixelRatio,BorderMode=EffectBorderMode.Hard,Optimization=EffectOptimization.Speed };
                disposable.Add(blur); image=blur;
            }
            using(var ds=_strip!.CreateDrawingSession())
            {
                ds.Clear(Theme.Bg);
                ds.DrawImage(image,0,0,new Rect(x0,0,width,_ch));
            }
            using(var ds=CanvasComposition.CreateDrawingSession(_surface!,new Rect(x0,0,width,_ch)))
                ds.DrawImage(_strip,0,0,new Rect(0,0,width,_ch));
        }
        finally { for(int i=disposable.Count-1;i>=0;i--) disposable[i].Dispose(); }
    }

    private CanvasRenderTarget Readability(MonetColors theme)
    {
        var rt=new CanvasRenderTarget(_g.Device,_cw,_ch,96);
        using var ds=rt.CreateDrawingSession();
        ds.Clear(Microsoft.UI.Colors.Transparent);
        using var veil=new CanvasLinearGradientBrush(_g.Device,new[] { Stop(0,theme.BackgroundColor,.18f),Stop(.34f,theme.BackgroundColor,.32f),Stop(.7f,theme.BackgroundColor,.66f),Stop(1,theme.BackgroundColor,.82f) })
            { StartPoint=Vector2.Zero,EndPoint=new Vector2(_cw,0) };
        ds.FillRectangle(0,0,_cw,_ch,veil);
        return rt;
    }

    private void DisposeImages()
    {
        _disposed=true; ++_imageVersion;
        ReleaseFilm();
        _cover?.Dispose(); _noise?.Dispose(); _poster?.Dispose(); _previousPoster?.Dispose(); _previousReadability?.Dispose();
        _lens?.Dispose(); _paper?.Dispose(); InvalidateImageFrame();
    }
}

/// <summary>240 s loop, 156 linear samples of normalized two-octave periodic noise; Folia's .5 strength.</summary>
internal static class MonetDrift
{
    private static readonly Vector3[] Frames = Build();
    private static Vector3[] Build()
    {
        double Hash(double n,double seed) { double x=Math.Sin(n*127.1+seed*311.7)*43758.5453; return x-Math.Floor(x); }
        double Noise(double t,int period,double seed) { double n=Math.Floor(t),f=t-n; return Hash(n%period,seed)+(Hash((n+1)%period,seed)-Hash(n%period,seed))*f*f*(3-2*f); }
        double Fbm(double t,double seed) => (Noise(t,76,seed)*.68+Noise(t*2,152,seed+17.31)*.32)*2-1;
        var channels=new double[3][]; double[] seeds={12.9898,78.233,39.425};
        for(int c=0;c<3;c++) { channels[c]=Enumerable.Range(0,157).Select(i=>Fbm(76.0*i/156,seeds[c])).ToArray(); double peak=channels[c].Max(Math.Abs); for(int i=0;i<157;i++) channels[c][i]/=peak; }
        return Enumerable.Range(0,157).Select(i=>new Vector3((float)Math.Round(channels[0][i]*1.7,4)/100,(float)Math.Round(channels[1][i]*1.7,4)/100,(float)Math.Round(1.044+.0175*(channels[2][i]+1)/2,5))).ToArray();
    }
    public static Vector3 At(double seconds) { double f=seconds%240/240*156; int i=(int)f; return Vector3.Lerp(Frames[i],Frames[i+1],(float)(f-i)); }
    public static float Fade(float x)
    {
        x=Math.Clamp(x,0,1); float low=0,high=1,t=0;
        for(int i=0;i<16;i++) { t=(low+high)/2; float bx=3*(1-t)*(1-t)*t*.42f+3*(1-t)*t*t*.58f+t*t*t; if(bx<x)low=t; else high=t; }
        return t*t*(3-2*t);
    }
}
