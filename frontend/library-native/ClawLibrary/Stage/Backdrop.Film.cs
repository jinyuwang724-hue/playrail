using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Windows.Foundation;

namespace ClawLibrary.Stage;

public sealed partial class Backdrop
{
    private string[] _filmCoverPaths = Array.Empty<string>();
    private CanvasRenderTarget[]? _filmAtlases;
    private Action<Action>? _postFilm;
    private int _filmVersion;
    private int _filmPendingWidth;
    public string? FilmAccentCover => _coverPath;
    public double FilmTime { get { lock (_gate) return _imageTime; } }
    public void SetFilmTime(double seconds)
    {
        lock (_gate) { _imageTime = seconds; InvalidateImageFrame(); _dirty = true; }
        _wake.Set();
    }

    /// <summary>The source is Steam's 600×900 q.jpg, shared by the wall and control centre.</summary>
    public void SetFilmCovers(IEnumerable<string> paths, Action<Action> post)
    {
        var next = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _postFilm = post;
        if (_filmCoverPaths.SequenceEqual(next, StringComparer.OrdinalIgnoreCase)) return;
        _filmCoverPaths = next;
        if (_settings.Kind != BackgroundKind.Filmstrip) return;
        if (next.Length == 0) ReleaseFilm();
        else LoadFilmAtlases(force: true);
    }

    private void LoadFilmAtlases(bool force = false)
    {
        var post = _postFilm;
        var paths = _filmCoverPaths;
        int width = _cw;
        var layout = _settings.FilmLayout;
        if (post == null || width == 0 || paths.Length == 0) return;
        if (!force && (_filmAtlases != null || _filmPendingWidth == width)) return;
        int version = ++_filmVersion;
        _filmPendingWidth = width;
        _ = Task.Run(async () =>
        {
            CanvasRenderTarget[]? ready = null;
            try
            {
                ready = await BuildFilmAtlases(paths, width, layout);
                var complete = ready;
                post(() =>
                {
                    if (_disposed || version != _filmVersion) { DisposeFilm(complete); return; }
                    lock (_gate)
                    {
                        DisposeFilm(_filmAtlases);
                        _filmAtlases = complete;
                        _filmPendingWidth = 0;
                        InvalidateImageFrame();
                        _dirty = true;
                    }
                    _wake.Set();
                });
            }
            catch (Exception ex)
            {
                DisposeFilm(ready);
                post(() => { if (version == _filmVersion) _filmPendingWidth = 0; });
                Log.Warn($"film covers: {ex.Message}");
            }
        });
    }

    private async Task<CanvasRenderTarget[]> BuildFilmAtlases(string[] paths, int width, FilmLayout layout)
    {
        const int lanes = 4, framesPerLane = 12;
        bool vertical = layout == FilmLayout.Vertical;
        int coverW = Math.Clamp(width / 10, 120, 184);
        int coverH = coverW * 3 / 2;
        int edge = Math.Max(17, coverW / 9);
        int pitch = (vertical ? coverH : coverW) + 9;
        int band = (vertical ? coverW : coverH) + edge * 2;
        int atlasW = vertical ? band : pitch * framesPerLane;
        int atlasH = vertical ? pitch * framesPerLane : band;
        int count = Math.Min(lanes * framesPerLane, paths.Length);
        var selected = Enumerable.Range(0, count)
            .Select(i => paths[i * paths.Length / count]).ToArray();
        var atlases = new CanvasRenderTarget[lanes];
        try
        {
            for (int lane = 0; lane < lanes; lane++)
            {
                var atlas = new CanvasRenderTarget(_g.Device, atlasW, atlasH, 96);
                atlases[lane] = atlas;
                using (var ds = atlas.CreateDrawingSession())
                {
                    ds.Clear(Gfx.Rgba(0x0b, 0x0c, 0x11));
                    if (vertical)
                    {
                        ds.FillRectangle(0, 0, edge, atlasH, Gfx.Rgba(0x18, 0x1a, 0x20));
                        ds.FillRectangle(edge + coverW, 0, edge, atlasH, Gfx.Rgba(0x18, 0x1a, 0x20));
                        ds.DrawLine(edge - 1, 0, edge - 1, atlasH, Gfx.Rgba(0x4b, 0x49, 0x4d), 1);
                        ds.DrawLine(edge + coverW, 0, edge + coverW, atlasH, Gfx.Rgba(0x4b, 0x49, 0x4d), 1);
                        for (int y = 9; y < atlasH; y += 32)
                        {
                            ds.FillRoundedRectangle(new Rect(5, y, 8, 14), 2, 2, Gfx.Rgba(0x6e, 0x70, 0x77));
                            ds.FillRoundedRectangle(new Rect(band - 13, y, 8, 14), 2, 2, Gfx.Rgba(0x6e, 0x70, 0x77));
                        }
                    }
                    else
                    {
                        ds.FillRectangle(0, 0, atlasW, edge, Gfx.Rgba(0x18, 0x1a, 0x20));
                        ds.FillRectangle(0, edge + coverH, atlasW, edge, Gfx.Rgba(0x18, 0x1a, 0x20));
                        ds.DrawLine(0, edge - 1, atlasW, edge - 1, Gfx.Rgba(0x4b, 0x49, 0x4d), 1);
                        ds.DrawLine(0, edge + coverH, atlasW, edge + coverH, Gfx.Rgba(0x4b, 0x49, 0x4d), 1);
                        for (int x = 9; x < atlasW; x += 32)
                        {
                            ds.FillRoundedRectangle(new Rect(x, 5, 14, 8), 2, 2, Gfx.Rgba(0x6e, 0x70, 0x77));
                            ds.FillRoundedRectangle(new Rect(x, band - 13, 14, 8), 2, 2, Gfx.Rgba(0x6e, 0x70, 0x77));
                        }
                    }
                }
                for (int frame = 0; frame < framesPerLane; frame++)
                {
                    if (selected.Length == 0) break;
                    var path = selected[(lane * framesPerLane + frame) % selected.Length];
                    try
                    {
                        using var cover = await CanvasBitmap.LoadAsync(_g.Device, path, 96);
                        float x = vertical ? edge : frame * pitch + 4;
                        float y = vertical ? frame * pitch + 4 : edge;
                        using var ds = atlas.CreateDrawingSession();
                        ds.DrawImage(cover, new Rect(x, y, coverW, coverH), cover.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                        ds.DrawRectangle(new Rect(x + .5, y + .5, coverW - 1, coverH - 1), Gfx.Rgba(0x8a, 0x88, 0x84), 1);
                    }
                    catch (Exception ex) { Log.Warn($"film cover {Path.GetFileName(Path.GetDirectoryName(path))}: {ex.Message}"); }
                }
            }
            return atlases;
        }
        catch { DisposeFilm(atlases); throw; }
    }

    private void LoadFilmAccent(string path, Action<Action> post, int version)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                Vector4[] samples;
                lock (ColorCache) ColorCache.TryGetValue(path, out samples!);
                if (samples == null)
                {
                    samples = await RepresentativeColors(path, 6);
                    lock (ColorCache) ColorCache[path] = samples;
                }
                var colors = MonetColors.FromCover(samples);
                post(() =>
                {
                    if (_disposed || version != _imageVersion) return;
                    lock (_gate)
                    {
                        _previousMonetColors = _monetColors;
                        _monetColors = colors;
                        _imageFadeMs = InstantColors ? 800 : 0;
                        _dirty = true;
                    }
                    _wake.Set();
                });
            }
            catch (Exception ex) { Log.Warn($"film accent: {ex.Message}"); }
        });
    }

    private void DrawFilmWall(CanvasDrawingSession ds)
    {
        ds.Clear(Gfx.Rgba(0x08, 0x09, 0x0f));
        if (_filmAtlases is { Length: > 0 } atlases)
        {
            bool vertical = _settings.FilmLayout == FilmLayout.Vertical;
            float angle = _settings.FilmAngle * MathF.PI / 180 * (vertical ? 1 : -1);
            float sin = MathF.Abs(MathF.Sin(angle)), cos = MathF.Abs(MathF.Cos(angle));
            float period = vertical ? atlases[0].SizeInPixels.Height : atlases[0].SizeInPixels.Width;
            float band = vertical ? atlases[0].SizeInPixels.Width : atlases[0].SizeInPixels.Height;
            float lanePitch = band * .86f; // 邻带轻微重叠，保持胶片墙满屏
            float axisDim = vertical ? _ch : _cw, crossDim = vertical ? _cw : _ch;
            float axisExtent = vertical ? sin * _cw + cos * _ch : cos * _cw + sin * _ch;
            float crossExtent = vertical ? cos * _cw + sin * _ch : sin * _cw + cos * _ch;
            float crossStart = (crossDim - crossExtent) / 2 - band;
            float axisPad = (axisExtent - axisDim) / 2 + band;
            int rows = (int)Math.Ceiling((crossExtent + 2 * band) / lanePitch) + 1;
            float velocity = 28 * _settings.FilmSpeed / 100f;
            var transform = ds.Transform;
            ds.Transform = Matrix3x2.CreateRotation(angle, new Vector2(_cw / 2f, _ch / 2f));
            for (int row = 0; row < rows; row++)
            {
                float cross = crossStart + row * lanePitch;
                int direction = row % 2 == 0 ? 1 : -1;
                double travel = row * 287.0 + direction * _imageTime * velocity;
                float phase = (float)((travel % period + period) % period);
                var atlas = atlases[row % atlases.Length];
                for (float axis = -axisPad - period + phase; axis < axisDim + axisPad; axis += period)
                    ds.DrawImage(atlas, vertical ? cross : axis, vertical ? axis : cross);
            }
            ds.Transform = transform;
        }

        float fade = _imageFadeMs >= 800 ? 1 : MonetDrift.Fade((float)(_imageFadeMs / 800));
        var accent = Vector4.Lerp(_previousMonetColors.Accent, _monetColors.Accent, fade);
        float dark = _settings.FilmDarkness / 100f;
        var tint = Gfx.Rgba(Byte(accent.X * .28f), Byte(accent.Y * .28f), Byte(accent.Z * .28f));
        ds.FillRectangle(0, 0, _cw, _ch, Gfx.WithAlpha(tint, dark * .82f));
        using var shade = new CanvasLinearGradientBrush(_g.Device, new[] {
            Stop(0, Gfx.Rgba(0, 0, 0), dark * .11f),
            Stop(.6f, Gfx.Rgba(0, 0, 0), dark * .37f),
            Stop(1, Gfx.Rgba(0, 0, 0), dark * .75f),
        }) { StartPoint = Vector2.Zero, EndPoint = new Vector2(_cw, _ch) };
        ds.FillRectangle(0, 0, _cw, _ch, shade);
    }

    private static void DisposeFilm(CanvasRenderTarget[]? atlases)
    {
        if (atlases == null) return;
        foreach (var atlas in atlases) atlas?.Dispose();
    }

    private void ReleaseFilm()
    {
        ++_filmVersion;
        _filmPendingWidth = 0;
        lock (_gate)
        {
            DisposeFilm(_filmAtlases);
            _filmAtlases = null;
            InvalidateImageFrame();
            _dirty = true;
        }
        _wake.Set();
    }
}
