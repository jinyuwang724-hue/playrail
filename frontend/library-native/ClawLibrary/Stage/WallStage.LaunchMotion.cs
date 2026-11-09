// 启动动画「封面接力」：按 A 启动后，聚焦卡的竖封面飞到左下，背后铺这款游戏的横版大图，等游戏窗口真的到前台再退场。
// 清晰度：Steam 横图多是 1920×620、竖封面多是 300×450 / 600×900，铺满全屏一定糊，所以
//   横图只按屏幕宽度铺一条（1920 宽屏上正好 1:1），上下用同一张图的重度模糊垫底（模糊看不出分辨率）；
//   竖封面最多放到原图像素大小；标题优先用 Steam 的透明 logo（PNG，自带清晰度），没有才写字。
// 退场：游戏窗口拿到前台（游戏库失去焦点、前台不是 Steam / 启动器）就收起；Steam 先弹窗时只退到它后面接着等。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Windows.UI;
using static ClawLibrary.Engine.Geometry;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class LaunchMotion
    {
        public required LibraryItem Item;
        public required ContainerVisual Root;
        public required SpriteVisual Scrim, Back, Poster, Copy, Sweep;
        public Rect From, To;
        public long Started;
        public bool Slow, Leaving, Yielded;
        public CanvasBitmap? Hero, Logo, Portrait;
        /// <summary>logo 里不透明部分的范围（裁掉四周留白）。</summary>
        public Windows.Foundation.Rect? LogoInk;
        /// <summary>正在游戏面板三个按钮在文字层里的横向位置（点按、悬停用）；纵向固定在底部 50 dip。</summary>
        public readonly List<(float X, float W)> Buttons = new();
        public int Version;
        public LmMode Mode;
        /// <summary>正在游戏面板：聚焦的按钮（0 继续游戏 / 1 结束游戏 / 2 游戏库 / 3 窗口）。</summary>
        public int Btn;
        public string? Note;
    }

    private enum LmMode { Launching, Playing, Ended }

    private LaunchMotion? _lm;
    private int _lmVersion;
    private const double LmSlowMs = 45_000, LmGiveUpMs = 90_000;

    /// <summary>
    /// 启动画面 / 正在游戏面板开着：手柄 / 键盘输入先给它。启动中只认 B（收起）；正在游戏面板左右换按钮、A 按下、B 去游戏库。
    /// </summary>
    private bool LaunchMotionIntercept(bool back, Direction? dir = null, bool activate = false)
    {
        if (_lm == null || _lm.Leaving) return false;
        if (_lm.Mode == LmMode.Playing)
        {
            if (back) { BrowseFromNowPlaying(); return true; }
            if (dir is Direction.Left or Direction.Right)
            {
                int next = Math.Clamp(_lm.Btn + (dir == Direction.Left ? -1 : 1), 0, 3);
                if (next != _lm.Btn) { _lm.Btn = next; PaintLaunchCopy(_lm); } else Cue(Sfx.Bump);
            }
            if (activate) NowPlayingPress(_lm.Btn);
            return true;
        }
        if (back) EndLaunchMotion("back");
        return true;
    }

    private void StartLaunchMotion(LibraryItem item, Tile? tile, LmMode mode = LmMode.Launching)
    {
        if (_lm != null) EndLaunchMotion("replaced", animate: false);
        bool fly = mode == LmMode.Launching;
        double W = View.Width, H = View.Height, d = Chrome.Dpi;
        var cell = tile != null ? ScreensaverScreenRect(tile) : _lmFromRect is { } fr ? new Rect(fr.X, fr.Y, fr.Width, fr.Height) : new Rect(W / 2 - 150, H / 2 - 225, 300, 450);
        // 竖封面按卡片高度、2:3 放在卡片正中起飞：等比缩放，不压扁（展开的卡多是方的）
        double fh = Math.Min(cell.Height, cell.Width * 1.5), fw = fh * 2 / 3;
        var from = new Rect(cell.X + (cell.Width - fw) / 2, cell.Y + (cell.Height - fh) / 2, fw, fh);
        // 竖封面：原图 600×900（套件 q），按屏幕高度的 56 % 放，但不超过原图像素
        double ph = Math.Min(H * 0.56, 900 / d), pw = ph * 2 / 3;
        var to = new Rect(W * 0.065, H * 0.9 - ph, pw, ph);
        var root = G.Container();
        var scrim = G.Sprite(G.Solid(Microsoft.UI.Colors.Black));
        scrim.Opacity = 0;
        var back = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        back.Size = new Vector2((float)W, (float)H); back.Opacity = 0;
        var poster = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        poster.Size = new Vector2((float)to.Width, (float)to.Height);
        poster.Offset = new Vector3((float)from.X, (float)from.Y, 0);
        poster.Scale = new Vector3((float)(from.Height / to.Height), (float)(from.Height / to.Height), 1);
        poster.Opacity = 0;
        var copy = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        copy.Opacity = 0;
        var sweep = G.Sprite(G.Solid(Gfx.Rgba(255, 255, 255, 0.9)), relative: false);
        sweep.Opacity = 0;
        root.Children.InsertAtTop(scrim); root.Children.InsertAtTop(back);
        root.Children.InsertAtTop(copy); root.Children.InsertAtTop(sweep); root.Children.InsertAtTop(poster);
        _top.Children.InsertAtTop(root);
        var lm = _lm = new LaunchMotion
        {
            Item = item, Root = root, Scrim = scrim, Back = back, Poster = poster, Copy = copy, Sweep = sweep,
            From = from, To = to, Started = NowMs, Version = ++_lmVersion, Mode = mode,
        };
        _windowFocusWatch ??= WatchLaunchFocus();
        Log.Info($"launch motion start {item.Id} from={from.X:F0},{from.Y:F0} {from.Width:F0}x{from.Height:F0} poster={pw:F0}x{ph:F0}dip");

        double ms(double v) => Reduced ? 1 : v;
        if (!fly)
        {
            // 正在游戏 / 游戏刚结束：画面直接淡入，封面已经在左下
            poster.Offset = new Vector3((float)to.X, (float)to.Y, 0);
            poster.Scale = Vector3.One;
            scrim.StartAnimation("Opacity", G.Scalar(0, 0.62, ms(200), Bezier.EaseOut));
            poster.StartAnimation("Opacity", G.Scalar(0, 1, ms(260), Bezier.EaseOut));
            PaintLaunchPoster(lm, item.Art is { } pa && pa.Has('q') ? pa.File('q') : item.Art is { } pb && pb.Has('p') ? pb.File('p') : null);
            _ = LoadLaunchArtAsync(lm);
            return;
        }
        // 1) 按下：卡片轻压，墙变暗（0–150 ms）
        scrim.StartAnimation("Opacity", G.Scalar(0, 0.62, ms(300), Bezier.EaseOut));
        // 2) 封面飞到左下并放大到原图大小（150–650 ms）
        var ease = G.C.CreateCubicBezierEasingFunction(new Vector2(.2f, .9f), new Vector2(.25f, 1));
        var off = G.C.CreateVector3KeyFrameAnimation();
        off.Duration = Gfx.Ms(ms(520)); off.DelayTime = Gfx.Ms(ms(120));
        off.InsertKeyFrame(1, new Vector3((float)to.X, (float)to.Y, 0), ease);
        var scale = G.C.CreateVector3KeyFrameAnimation();
        scale.Duration = off.Duration; scale.DelayTime = off.DelayTime;
        scale.InsertKeyFrame(0.18f, new Vector3((float)(from.Height / to.Height * 0.97), (float)(from.Height / to.Height * 0.97), 1));
        scale.InsertKeyFrame(1, Vector3.One, ease);
        poster.StartAnimation("Offset", off); poster.StartAnimation("Scale", scale);
        poster.StartAnimation("Opacity", G.Scalar(0, 1, ms(140), Bezier.EaseOut, ms(60)));
        PaintLaunchPoster(lm, item.Art is { } art && art.Has('q') ? art.File('q') : item.Art is { } a2 && a2.Has('p') ? a2.File('p') : null);
        _ = LoadLaunchArtAsync(lm);
        After(LmSlowMs, () => { if (_lm == lm && !lm.Leaving) { lm.Slow = true; PaintLaunchCopy(lm); } });
        After(LmGiveUpMs, () => { if (_lm == lm && !lm.Leaving) EndLaunchMotion("timeout"); });
    }

    /// <summary>横图、logo、竖封面原图：后台解码，好了就画背景和文字。</summary>
    private async Task LoadLaunchArtAsync(LaunchMotion lm)
    {
        var (hero, logo, portrait) = LaunchArtFiles(lm.Item);
        async Task<CanvasBitmap?> Load(string? path)
        {
            if (path == null) return null;
            try { return await CanvasBitmap.LoadAsync(G.Device, path, 96); }
            catch (Exception ex) { Log.Warn($"launch art {path}: {ex.Message}"); return null; }
        }
        lm.Hero = await Load(hero);
        lm.Logo = await Load(logo);
        if (lm.Logo != null && LogoTooDark(lm.Logo)) { Log.Info("launch logo too dark; using title"); lm.Logo.Dispose(); lm.Logo = null; }
        if (lm.Logo != null) lm.LogoInk = LogoInkBounds(lm.Logo);
        lm.Portrait = lm.Hero == null ? await Load(portrait) : null;
        if (_lm != lm || lm.Leaving) return;
        Log.Info($"launch art hero={(lm.Hero is { } h ? $"{h.SizeInPixels.Width}x{h.SizeInPixels.Height}" : "none")} logo={(lm.Logo is { } l ? $"{l.SizeInPixels.Width}x{l.SizeInPixels.Height}" : "none")}");
        PaintLaunchBack(lm);
        PaintLaunchCopy(lm);
        double ms(double v) => Reduced ? 1 : v;
        long age = NowMs - lm.Started;
        lm.Back.StartAnimation("Opacity", G.Scalar(0, 1, ms(520), Bezier.EaseOut, Math.Max(0, 160 - age)));
        lm.Copy.StartAnimation("Opacity", G.Scalar(0, 1, ms(420), Bezier.EaseOut, Math.Max(0, 420 - age)));
        var rise = G.C.CreateVector3KeyFrameAnimation();
        rise.Duration = Gfx.Ms(ms(520)); rise.DelayTime = Gfx.Ms(Math.Max(0, 420 - age));
        rise.InsertKeyFrame(0, lm.Copy.Offset + new Vector3(0, 14, 0));
        rise.InsertKeyFrame(1, lm.Copy.Offset, G.C.CreateCubicBezierEasingFunction(new Vector2(.2f, .9f), new Vector2(.25f, 1)));
        lm.Copy.StartAnimation("Offset", rise);
        if (lm.Mode == LmMode.Launching) StartLaunchSweep(lm, Math.Max(0, 700 - age));
    }

    private (string? Hero, string? Logo, string? Portrait) LaunchArtFiles(LibraryItem item)
    {
        string? hero = null, logo = null, portrait = null;
        if (_steamRoot != null && uint.TryParse(item.Id, out _))
        {
            (portrait, hero) = Steam.ArtSources(_steamRoot, _steamUser, item.Id);
            logo = LocalGames.ArtFile(item.Id, "_logo");
            try
            {
                if (logo == null && _steamUser != null)
                    logo = new[] { ".png", ".jpg" }.Select(e => Path.Combine(_steamUser, "config", "grid", item.Id + "_logo" + e)).FirstOrDefault(File.Exists);
                var cache = Path.Combine(_steamRoot, "appcache", "librarycache", item.Id);
                if (logo == null && Directory.Exists(cache))
                    logo = Directory.GetFiles(cache, "*.png", SearchOption.AllDirectories)
                        .Where(f => Path.GetFileName(f).StartsWith("logo", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("library_logo", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                logo ??= new[] { $"{item.Id}_logo.png" }.Select(n => Path.Combine(_steamRoot, "appcache", "librarycache", n)).FirstOrDefault(File.Exists);
            }
            catch (Exception ex) { Log.Warn($"launch logo {item.Id}: {ex.Message}"); }
        }
        if (item.Art is { } art)
        {
            hero ??= art.Has('h') ? art.File('h') : null;
            portrait ??= art.Has('q') ? art.File('q') : art.Has('p') ? art.File('p') : null;
        }
        return (hero, logo, portrait);
    }

    private void PaintLaunchPoster(LaunchMotion lm, string? path)
    {
        if (path == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var bmp = await CanvasBitmap.LoadAsync(G.Device, path, 96);
                _dq.TryEnqueue(() =>
                {
                    if (_lm != lm) { bmp.Dispose(); return; }
                    float w = (float)lm.To.Width, h = (float)lm.To.Height;
                    // 不超过原图像素：raster 取屏幕 dpi，但面的像素数不会比原图多
                    float raster = (float)Math.Min(Chrome.Dpi, bmp.SizeInPixels.Height / h);
                    CcSet(lm.Poster, CcDraw(w, h, raster, ds =>
                    {
                        using var clip = CanvasGeometry.CreateRoundedRectangle(ds, 0, 0, w, h, 12, 12);
                        using (ds.CreateLayer(1, clip))
                            ds.DrawImage(bmp, new Windows.Foundation.Rect(0, 0, w, h), bmp.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                        ds.DrawRoundedRectangle(0.5f, 0.5f, w - 1, h - 1, 12, 12, Gfx.Rgba(255, 255, 255, 0.14), 1);
                    }));
                    bmp.Dispose();
                });
            }
            catch (Exception ex) { Log.Warn($"launch poster {path}: {ex.Message}"); }
        });
    }

    /// <summary>背景：模糊的横图铺满 + 原宽的清晰横图一条（底边渐隐）+ 左下压暗给文字。</summary>
    private void PaintLaunchBack(LaunchMotion lm)
    {
        float W = (float)View.Width, H = (float)View.Height;
        var src = lm.Hero ?? lm.Portrait;
        CcSet(lm.Back, CcDraw(W, H, Chrome.Dpi, ds =>
        {
            ds.Clear(Gfx.Rgba(9, 9, 11));
            if (src == null) return;
            var b = src.Bounds;
            // 模糊垫底：先缩小再模糊，等于把细节全抹掉，分辨率看不出来
            double cover = Math.Max(W / b.Width, H / b.Height) * 1.08;
            var dest = new Windows.Foundation.Rect((W - b.Width * cover) / 2, (H - b.Height * cover) / 2, b.Width * cover, b.Height * cover);
            using (var blur = new GaussianBlurEffect { Source = src, BlurAmount = 28, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Balanced })
                ds.DrawImage(blur, dest, b);
            ds.FillRectangle(0, 0, W, H, Gfx.Rgba(9, 9, 11, 0.5));
            if (lm.Hero is { } hero)
            {
                // 清晰的一条：按屏幕宽度，1920 宽的图在 1920 物理像素宽的屏上正好 1:1
                float bh = (float)(W * hero.Bounds.Height / hero.Bounds.Width);
                var band = new CanvasCommandList(ds);
                using (var bs = band.CreateDrawingSession())
                    bs.DrawImage(hero, new Windows.Foundation.Rect(0, 0, W, bh), hero.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                using var fade = new Microsoft.Graphics.Canvas.Brushes.CanvasLinearGradientBrush(ds, new[]
                {
                    new Microsoft.Graphics.Canvas.Brushes.CanvasGradientStop { Position = 0, Color = Gfx.Rgba(0, 0, 0, 1) },
                    new Microsoft.Graphics.Canvas.Brushes.CanvasGradientStop { Position = 0.62f, Color = Gfx.Rgba(0, 0, 0, 1) },
                    new Microsoft.Graphics.Canvas.Brushes.CanvasGradientStop { Position = 1, Color = Gfx.Rgba(0, 0, 0, 0) },
                }) { StartPoint = new Vector2(0, 0), EndPoint = new Vector2(0, bh) };
                using var masked = new AlphaMaskEffect { Source = band, AlphaMask = MaskOf(ds, fade, W, bh) };
                ds.DrawImage(masked);
                band.Dispose();
            }
            // 左下压暗：文字和封面落在这里
            using var shade = new Microsoft.Graphics.Canvas.Brushes.CanvasLinearGradientBrush(ds, Gfx.Rgba(9, 9, 11, 0), Gfx.Rgba(9, 9, 11, 0.86))
            { StartPoint = new Vector2(0, H * 0.35f), EndPoint = new Vector2(0, H) };
            ds.FillRectangle(0, 0, W, H, shade);
        }));
    }

    private static CanvasCommandList MaskOf(ICanvasResourceCreator rc, Microsoft.Graphics.Canvas.Brushes.ICanvasBrush brush, float w, float h)
    {
        var list = new CanvasCommandList(rc);
        using var s = list.CreateDrawingSession();
        s.FillRectangle(0, 0, w, h, brush);
        return list;
    }

    /// <summary>封面右边：logo（或标题）+ 状态一行 + 进度细线的轨道。</summary>
    private void PaintLaunchCopy(LaunchMotion lm)
    {
        float W = (float)View.Width, H = (float)View.Height;
        float x0 = (float)(lm.To.X + lm.To.Width + W * 0.035), bottom = (float)(lm.To.Y + lm.To.Height), cw = W - x0 - W * 0.065f;
        const float ch = 340;
        lm.Copy.Size = new Vector2(cw, ch);
        lm.Copy.Offset = new Vector3(x0, bottom - ch, 0);
        string status = lm.Mode switch
        {
            LmMode.Playing => lm.Note ?? $"正在游戏 · {PlayedText(_game)}",
            LmMode.Ended => lm.Note ?? "已退出",
            _ => lm.Slow ? "还在启动…" : lm.Yielded ? "正在等待 Steam" : "正在启动",
        };
        bool buttons = lm.Mode == LmMode.Playing;
        CcSet(lm.Copy, CcDraw(cw, ch, Chrome.Dpi, ds =>
        {
            float y = buttons ? ch - 110 : ch - 64;   // 状态行基线之上是标题区
            if (buttons)
            {
                string[] labels = { "继续游戏", "", "游戏库", "窗口" };
                float bx = 0; lm.Buttons.Clear();
                for (int i = 0; i < labels.Length; i++)
                {
                    using var t = i == 1 ? null : TextKit.Layout(ds, labels[i], TextKit.Ui(600), 20, 400, CanvasHorizontalAlignment.Left);
                    float bw = t == null ? 50 : (float)t.LayoutBounds.Width + 56, by = ch - 58;
                    bool f = lm.Btn == i;
                    ds.FillRoundedRectangle(bx, by, bw, 50, 25, 25, f ? Gfx.Rgba(255, 255, 255) : Gfx.Rgba(255, 255, 255, 0.14));
                    var ink = f ? Gfx.Rgba(12, 12, 14) : TextKit.Ink(0.92);
                    if (t == null) LineIcons.Draw(ds, "qam.quit", bx + 25, by + 25, 24, ink);
                    else ds.DrawTextLayout(t, bx + 28, by + 25 - (float)t.LayoutBounds.Height / 2, ink);
                    lm.Buttons.Add((bx, bw));
                    bx += bw + 14;
                }
            }
            if (lm.Logo is { } logo)
            {
                // 只量 logo 的不透明部分（PNG 四周常有大片留白）；框 = 文字区宽 × 标题区高，最多 520×180 dip，且不超过原图像素
                var src = lm.LogoInk ?? logo.Bounds;
                float boxW = Math.Min(520, cw), boxH = Math.Min(180, y - 24 - 12);
                double k = Math.Min(Math.Min(boxW / src.Width, boxH / src.Height), logo.SizeInPixels.Width / Chrome.Dpi / logo.Bounds.Width);
                float lw = (float)(src.Width * k), lh = (float)(src.Height * k);
                ds.DrawImage(logo, new Windows.Foundation.Rect(0, y - 24 - lh, lw, lh), src, 1, CanvasImageInterpolation.HighQualityCubic);
            }
            else
            {
                using var l = TextKit.Layout(ds, lm.Item.Title, TextKit.Ui(700), 52, cw, CanvasHorizontalAlignment.Left, maxLines: 2);
                ds.DrawTextLayout(l, 0, y - 24 - (float)l.LayoutBounds.Height, TextKit.Ink());
            }
            using (var s = TextKit.Layout(ds, status, TextKit.Ui(500), 20, cw, CanvasHorizontalAlignment.Left))
                ds.DrawTextLayout(s, 0, y, TextKit.Ink(0.78));
            if (lm.Mode == LmMode.Launching) ds.FillRoundedRectangle(0, ch - 12, 280, 3, 1.5f, 1.5f, Gfx.Rgba(255, 255, 255, 0.16));
            if (lm.Slow)
                using (var hint = TextKit.Layout(ds, "B 返回游戏库，游戏会继续启动", TextKit.Ui(400), 16, cw, CanvasHorizontalAlignment.Left))
                    ds.DrawTextLayout(hint, 300, ch - 22, TextKit.Ink(0.5));
        }));
    }

    /// <summary>进度细线上来回走的一小段亮光（不知道确切进度，只表示「还在动」）。</summary>
    private void StartLaunchSweep(LaunchMotion lm, double delay)
    {
        var o = lm.Copy.Offset;
        lm.Sweep.Size = new Vector2(70, 3);
        lm.Sweep.Offset = new Vector3(o.X, o.Y + 340 - 12, 0);
        lm.Sweep.Opacity = 0;
        if (Reduced) return;
        lm.Sweep.StartAnimation("Opacity", G.Scalar(0, 0.85, 400, Bezier.EaseOut, delay));
        var run = G.C.CreateScalarKeyFrameAnimation();
        run.Duration = TimeSpan.FromMilliseconds(1600);
        run.DelayTime = Gfx.Ms(delay);
        run.InsertKeyFrame(0, o.X);
        run.InsertKeyFrame(1, o.X + 280 - 70, G.C.CreateCubicBezierEasingFunction(new Vector2(.45f, 0), new Vector2(.55f, 1)));
        run.IterationBehavior = AnimationIterationBehavior.Forever;
        run.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
        lm.Sweep.StartAnimation("Offset.X", run);
    }

    /// <summary>收起：游戏到前台（handoff）时整层淡出；B / 超时时封面飞回卡片再淡出。</summary>
    private void EndLaunchMotion(string reason, bool animate = true)
    {
        var lm = _lm;
        if (lm == null || lm.Leaving) return;
        lm.Leaving = true;
        Log.Info($"launch motion end {lm.Item.Id} reason={reason} after={NowMs - lm.Started}ms");
        CsLaunchEnded(reason);
        void Drop()
        {
            if (_lm == lm) _lm = null;
            _top.Children.Remove(lm.Root);
            lm.Hero?.Dispose(); lm.Logo?.Dispose(); lm.Portrait?.Dispose();
            lm.Root.Dispose();
        }
        bool playing = _game is { } pg && pg.Item.Id == lm.Item.Id;
        // 交接给正在跑的游戏：画面原地变成「正在游戏」，留在游戏后面（看不见、不耗什么）。
        // 游戏中切回游戏库，第一帧就是它，不会先播墙面的进场动画再换过来。
        if (reason == "handoff" && playing)
        {
            lm.Sweep.StopAnimation("Opacity"); lm.Sweep.StopAnimation("Offset.X"); lm.Sweep.Opacity = 0;
            lm.Mode = LmMode.Playing; lm.Leaving = false; lm.Note = null; lm.Btn = 0; lm.Slow = lm.Yielded = false;
            PaintLaunchCopy(lm);
            return;
        }
        // 正在游戏面板上选了「游戏库」：飞回卡片后藏起来留着，下次切回来直接亮出
        bool park = reason == "browse" && playing;
        if (!animate || Reduced) { if (park) ParkLaunchMotion(lm); else Drop(); return; }
        double ms = 420;
        if (reason != "handoff")
        {
            var ease = G.C.CreateCubicBezierEasingFunction(new Vector2(.4f, 0), new Vector2(.2f, 1));
            var off = G.C.CreateVector3KeyFrameAnimation(); off.Duration = Gfx.Ms(ms);
            off.InsertKeyFrame(1, new Vector3((float)lm.From.X, (float)lm.From.Y, 0), ease);
            var sc = G.C.CreateVector3KeyFrameAnimation(); sc.Duration = off.Duration;
            sc.InsertKeyFrame(1, new Vector3((float)(lm.From.Height / lm.To.Height), (float)(lm.From.Height / lm.To.Height), 1), ease);
            lm.Poster.StartAnimation("Offset", off); lm.Poster.StartAnimation("Scale", sc);
            foreach (var v in new Visual[] { lm.Back, lm.Copy, lm.Sweep, lm.Scrim })
                v.StartAnimation("Opacity", FadeOut(ms * 0.8));
            lm.Poster.StartAnimation("Opacity", G.Scalar(1, 0, 160, Bezier.EaseOut, ms - 120));
        }
        else lm.Root.StartAnimation("Opacity", FadeOut(ms));
        After(ms + 40, () => { if (park && _game?.Item.Id == lm.Item.Id) ParkLaunchMotion(lm); else Drop(); });
    }

    private ScalarKeyFrameAnimation FadeOut(double ms)
    {
        var a = G.C.CreateScalarKeyFrameAnimation();
        a.Duration = Gfx.Ms(ms);
        a.InsertExpressionKeyFrame(0, "this.StartingValue");
        a.InsertKeyFrame(1, 0, G.C.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(.58f, 1)));
        return a;
    }

    // ---------------- 交接：前台换成了谁 ----------------

    private object? _windowFocusWatch;

    private object WatchLaunchFocus()
    {
        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated) { MaybeShowNowPlaying(); return; }
            // Restoring the library can briefly dispatch deactivation before it takes foreground.
            if ((_window as MainWindow)?.WindowSwitchInFlight != true) _nowPlayingDismissed = false;
            HideNowPlayingBar();
            // 前台可能还在切换途中：稍等再看是谁
            After(120, LaunchForegroundChanged);
        };
        return new object();
    }

    private static readonly HashSet<string> LaunchHelpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamerrorreporter", "steamservice", "gameoverlayui", "gameoverlayui64",
        "EADesktop", "EALauncher", "Origin", "upc", "UbisoftConnect", "EpicGamesLauncher", "Battle.net",
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost",
        "GameBar", "XboxGameBarWidgets", "ClawWidget", "ClawLibrary", "ApplicationFrameHost", "XboxPcApp", "XboxApp", "Magpie",
    };

    private void LaunchForegroundChanged()
    {
        var lm = _lm;
        if (lm == null || lm.Leaving) return;
        nint fg = LibraryWindow.Foreground;
        if (fg == 0 || fg == _windowHandle) return;
        string name = "";
        try { GetWindowThreadProcessId(fg, out uint pid); using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; }
        catch { }
        Log.Info($"launch motion foreground -> {name}");
        // 正在游戏 / 已退出面板：切走只是收起面板。只有真正的启动画面才把新的前台当成游戏（否则切到别的软件会被当成在玩这款）
        if (lm.Mode != LmMode.Launching) { EndLaunchMotion("handoff"); return; }
        if (LaunchHelpers.Contains(name) || LaunchNonGames.Contains(name)) { if (!lm.Yielded) { lm.Yielded = true; PaintLaunchCopy(lm); } return; }
        if (LaunchHelpers.Contains(name) || name.Contains("launcher", StringComparison.OrdinalIgnoreCase))
        {
            if (!lm.Yielded) { lm.Yielded = true; PaintLaunchCopy(lm); }
            return;
        }
        BeginGameSession(lm.Item, fg);
        EndLaunchMotion("handoff");
        if (_prefs.AfterLaunch == AfterLaunch.Hide) HideAfterLaunch();
    }

    /// <summary>明显不是游戏的常用软件（与后台 claw_game_session 的 NON_GAMES 一致）：启动等待时切到它们不算交接。</summary>
    private static readonly HashSet<string> LaunchNonGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "msedgewebview2", "codex", "claude", "chatgpt", "Taskmgr", "WindowsTerminal",
        "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "notepad", "Code", "LockApp", "MotionAssistant", "RTSS",
        "clash-verge", "sunshine", "vlc", "mpv", "PotPlayerMini64", "ClawPerformanceOverlay", "SystemSettings",
    };

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}

public sealed partial class WallStage
{
    /// <summary>录屏用：展开聚焦的卡后，只放启动画面，不真的启动游戏。</summary>
    internal bool DemoLaunchStart()
    {
        if (FocusKey is not { } key || SlotByKey(key) is not { } slot || ItemFor(slot) is not { } id || Lookup(id) is not { } item) return false;
        if (_expandedKey != key) Expand(slot);
        Cue(Sfx.Launch);
        StartLaunchMotion(item, _tiles.GetValueOrDefault(key));
        return true;
    }

    internal void DemoLaunchSlow() { if (_lm is { } lm) { lm.Slow = true; PaintLaunchCopy(lm); } }
    internal void DemoLaunchHandoff() => EndLaunchMotion("handoff");
    internal string? DemoLaunchTitle => FocusKey is { } k && SlotByKey(k) is { } s && ItemFor(s) is { } id ? Lookup(id)?.Title : null;
}

public sealed partial class WallStage
{
    /// <summary>logo 不透明部分的平均亮度太低（黑字 logo）：压在暗底上看不清，改用文字标题。</summary>
    private static bool LogoTooDark(CanvasBitmap logo)
    {
        try
        {
            var px = logo.GetPixelColors();
            double sum = 0, weight = 0;
            for (int i = 0; i < px.Length; i += 7)
            {
                var c = px[i];
                if (c.A < 64) continue;
                double a = c.A / 255.0;
                sum += a * (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
                weight += a;
            }
            return weight > 0 && sum / weight < 0.45;
        }
        catch (Exception ex) { Log.Warn($"logo luminance: {ex.Message}"); return false; }
    }
}

public sealed partial class WallStage
{
    private LaunchMotion? _lmParked;

    private void ParkLaunchMotion(LaunchMotion lm)
    {
        if (_lm == lm) _lm = null;
        if (_lmParked != null && _lmParked != lm) DropParkedMotion();
        lm.Root.StopAnimation("Opacity"); lm.Root.Opacity = 1; lm.Root.IsVisible = false;
        lm.Sweep.StopAnimation("Opacity"); lm.Sweep.StopAnimation("Offset.X"); lm.Sweep.Opacity = 0;
        lm.Mode = LmMode.Playing; lm.Leaving = false; lm.Note = null; lm.Btn = 0; lm.Slow = lm.Yielded = false;
        _lmParked = lm;
    }

    private void DropParkedMotion()
    {
        if (_lmParked is not { } lm) return;
        _lmParked = null;
        _top.Children.Remove(lm.Root);
        lm.Hero?.Dispose(); lm.Logo?.Dispose(); lm.Portrait?.Dispose();
        lm.Root.Dispose();
    }

    /// <summary>藏着的「正在游戏」画面原样亮出来（同步，第一帧就是它）。</summary>
    private bool UnparkNowPlaying(GameSession g)
    {
        if (_lmParked is not { } lm || lm.Item.Id != g.Item.Id) { DropParkedMotion(); return false; }
        if (_lm != null && !_lm.Leaving) return false;
        _lmParked = null;
        foreach (var v in new Microsoft.UI.Composition.Visual[] { lm.Back, lm.Copy, lm.Poster, lm.Scrim }) { v.StopAnimation("Opacity"); v.Opacity = 1; }
        lm.Scrim.Opacity = 0.62f;
        lm.Poster.StopAnimation("Offset"); lm.Poster.StopAnimation("Scale");
        lm.Poster.Offset = new System.Numerics.Vector3((float)lm.To.X, (float)lm.To.Y, 0); lm.Poster.Scale = System.Numerics.Vector3.One;
        lm.Started = NowMs; lm.Version = ++_lmVersion;
        _top.Children.Remove(lm.Root); _top.Children.InsertAtTop(lm.Root);
        lm.Root.IsVisible = true;
        _lm = lm;
        PaintLaunchCopy(lm);
        return true;
    }

    /// <summary>窗口要被唤出来之前（MainWindow.ActivateRoute）就把正在游戏画面准备好，避免先看到墙面的进场动画。</summary>
    internal void NowPlayingBeforeShow() => MaybeShowNowPlaying();
}

public sealed partial class WallStage
{
    /// <summary>logo 不透明像素的外框（alpha &gt; 16）；整张都透明或读不出来返回 null。</summary>
    private static Windows.Foundation.Rect? LogoInkBounds(CanvasBitmap logo)
    {
        try
        {
            var px = logo.GetPixelColors();
            int w = (int)logo.SizeInPixels.Width, h = (int)logo.SizeInPixels.Height;
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[y * w + x].A > 16) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            if (x1 < 0) return null;
            double s = logo.Bounds.Width / w;   // 像素 → dip（按 96 dpi 载入，等于 1）
            return new Windows.Foundation.Rect(x0 * s, y0 * s, (x1 - x0 + 1) * s, (y1 - y0 + 1) * s);
        }
        catch (Exception ex) { Log.Warn($"logo bounds: {ex.Message}"); return null; }
    }
}

public sealed partial class WallStage
{
    private int LaunchButtonAt(double sx, double sy)
    {
        if (_lm is not { Mode: LmMode.Playing } lm) return -1;
        var o = lm.Copy.Offset; var size = lm.Copy.Size;
        double lx = sx - o.X, ly = sy - o.Y;
        if (ly < size.Y - 58 || ly > size.Y - 8) return -1;
        for (int i = 0; i < lm.Buttons.Count; i++)
            if (lx >= lm.Buttons[i].X && lx <= lm.Buttons[i].X + lm.Buttons[i].W) return i;
        return -1;
    }

    /// <summary>点在启动 / 正在游戏画面上：正在游戏的按钮照按，别处什么都不做（不点穿到墙）。</summary>
    private void LaunchMotionClick(double sx, double sy)
    {
        int i = LaunchButtonAt(sx, sy);
        if (i < 0 || _lm is not { } lm) return;
        lm.Btn = i; PaintLaunchCopy(lm);
        Cue(Sfx.Activate);
        NowPlayingPress(i);
    }

    private void LaunchMotionHover(double sx, double sy)
    {
        int i = LaunchButtonAt(sx, sy);
        if (i < 0 || _lm is not { } lm || lm.Btn == i) return;
        lm.Btn = i; PaintLaunchCopy(lm);
    }

    /// <summary>别的页面要出来（Claw 键唤控制中心）：正在游戏画面藏起来，下次回游戏库再亮出。</summary>
    internal void NowPlayingYield()
    {
        if (_lm is { Leaving: false, Mode: LmMode.Playing } lm) ParkLaunchMotion(lm);
        else if (_lm is { Leaving: false, Mode: LmMode.Ended }) EndLaunchMotion("back", animate: false);
    }
}
