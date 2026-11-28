// Cocoon 主题专属无框单图屏保：
//   - 全屏单张画面（优先玩家实机游戏截图，无截图时自动回退各游戏高清 Hero 横幅），无外框、无胶囊底板、无游戏文字；
//   - 仅保留「日历（月日 · 星期）+ 玻璃折射体圆角时钟」，冒号按 4 s 静息节律柔和呼吸；
//   - 后台对每张图做 48×30 像素级实时分析：
//     1) 自动识别 16:9 过场上下黑边（1.138× 满幅裁除）与掌机顶部 OSD 监控条（1.062× 微偏下净画取景）；
//     2) 计算四角亮度与边缘高频杂乱度（避开游戏血条、小地图、技能栏与强光），叠加「上一幕同角惩罚」实现无残影智能轮换；
//     3) 按落位角亮度自适应调节无边界椭圆形局部景深柔焦（Backdrop Blur）与微暗晕；
//   - 切图时文字先柔焦隐去，新图带微幅对焦拉清（Focus-pull）淡入，文字再在新留白角柔和浮现；交流电下附带亚像素慢速微漂移，电池省电态自动静止。
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private readonly record struct CcSaverSlide(string Path, LibraryItem? Game);
    private readonly record struct CcZoneStat(float MeanLum, float Clutter, float Cost);

    private sealed class CcSlideAnalysis
    {
        public bool HasLetterbox;
        public float Aspect = 1.6f;
        public required CcZoneStat[] Zones; // 0:左下, 1:右下, 2:左上, 3:右上
    }

    private sealed class CcSaverState
    {
        public required ContainerVisual Root, ClockBox;
        public required LayerVisual[] Layers;
        public required CompositionEffectBrush[] LayerBlurs;
        public required SpriteVisual[] Img;
        public required SpriteVisual BokehV, VeilV, ClockMainV, ClockColonV;
        public CompositionDrawingSurface? PeriphSurf, MaskSurf, VeilSurf;
        public LoadedImageSurface?[] Surf = new LoadedImageSurface?[2];
        public List<CcSaverSlide> Queue = new();
        public bool ShotsUpgraded;
        public int Round;
        public int PrevZone = -1;
        public int ActiveZone;
        public LibraryItem? Game;
        public string? ClockKey;
        public bool Disposed;
    }

    private CcSaverState? _ccSs;
    private readonly Dictionary<string, CcSlideAnalysis> _ccSsCache = new(StringComparer.OrdinalIgnoreCase);

    private const float CcClockW = 640, CcClockH = 236;
    private const float CcCornerMx = 88, CcCornerMy = 76;
    private const float CcBokehW = 980, CcBokehH = 640;
    private static readonly Bezier CcSaverEase = new(0.22f, 1f, 0.36f, 1f);

    private bool CcSaverEnter()
    {
        if (_cs == null || _ccSs != null || CurrentTheme != UiTheme.Cocoon) return false;
        var games = CsList(1);
        var fallbackQueue = new List<CcSaverSlide>();
        foreach (var g in games)
            if (BnPath(g, cat: false) is { Length: > 0 } p && File.Exists(p))
                fallbackQueue.Add(new CcSaverSlide(p, g));
        if (fallbackQueue.Count == 0) return false;

        float k = _cs.K;
        var root = G.Container(relative: false);
        root.Size = new Vector2(CsW, CsH);
        root.Scale = new Vector3(k, k, 1);
        root.Offset = _cs.Root.Offset;
        root.Opacity = 0;

        var floor = G.Sprite(G.Solid(Gfx.Rgba(0x12, 0x13, 0x1c)), relative: false);
        floor.Size = new Vector2(CsW, CsH);
        root.Children.InsertAtTop(floor);

        var layers = new LayerVisual[2];
        var blurs = new CompositionEffectBrush[2];
        var imgs = new SpriteVisual[2];
        for (int i = 0; i < 2; i++)
        {
            var lv = G.C.CreateLayerVisual();
            lv.Size = new Vector2(CsW, CsH);
            lv.Opacity = 0;
            var fx = RwBlurBrush();
            lv.Effect = fx;
            var b = G.C.CreateSurfaceBrush();
            b.Stretch = CompositionStretch.UniformToFill;
            b.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
            var sp = G.Sprite(b, relative: false);
            sp.Size = new Vector2(CsW, CsH);
            sp.CenterPoint = new Vector3(CsW / 2f, CsH / 2f, 0);
            lv.Children.InsertAtTop(sp);
            root.Children.InsertAtTop(lv);
            layers[i] = lv;
            blurs[i] = fx;
            imgs[i] = sp;
        }

        // 极轻的镜头暗角（中心 64% 完全通透，仅四角边缘微收 22%）
        var periphSurf = CcPaintPeripheryVignette();
        var periph = G.Sprite(G.C.CreateSurfaceBrush(periphSurf), relative: false);
        periph.Size = new Vector2(CsW, CsH);
        root.Children.InsertAtTop(periph);

        // 无边界椭圆形局部景深柔焦 + 自适应暗晕遮罩
        var maskSurf = CcPaintRadialEllipse(CcBokehW, CcBokehH, Colors.White);
        var veilSurf = CcPaintRadialEllipse(CcBokehW, CcBokehH, Color.FromArgb(255, 8, 10, 18));

        var bokehV = G.Sprite(CcCreateMaskedBackdropBlur(maskSurf), relative: false);
        bokehV.Size = new Vector2(CcBokehW, CcBokehH);
        bokehV.Opacity = 0;
        root.Children.InsertAtTop(bokehV);

        var veilBrush = G.C.CreateSurfaceBrush(veilSurf);
        veilBrush.Stretch = CompositionStretch.Fill;
        var veilV = G.Sprite(veilBrush, relative: false);
        veilV.Size = new Vector2(CcBokehW, CcBokehH);
        veilV.Opacity = 0;
        root.Children.InsertAtTop(veilV);

        // 纯无框日历 + 玻璃折射体时钟（主体与呼吸冒号分离，静止时 0 CPU 重绘）
        var clockBox = G.Container(relative: false);
        clockBox.Size = new Vector2(CcClockW, CcClockH);
        clockBox.Opacity = 0;
        var clockMain = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        var clockColon = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        clockBox.Children.InsertAtTop(clockMain);
        clockBox.Children.InsertAtTop(clockColon);
        root.Children.InsertAtTop(clockBox);

        _top.Children.InsertAtTop(root);
        var ss = new CcSaverState
        {
            Root = root,
            ClockBox = clockBox,
            Layers = layers,
            LayerBlurs = blurs,
            Img = imgs,
            BokehV = bokehV,
            VeilV = veilV,
            ClockMainV = clockMain,
            ClockColonV = clockColon,
            PeriphSurf = periphSurf,
            MaskSurf = maskSurf,
            VeilSurf = veilSurf,
            Queue = fallbackQueue,
        };
        _ccSs = ss;

        CcStartColonAndDriftAnimations(ss);
        root.StartAnimation("Opacity", CsS(1, CsMs(900), Bezier.EaseOut));
        if (_cs.Page?.Rw is { Disposed: false } rw)
            rw.Ui.StartAnimation("Opacity", CsS(0, CsMs(550), Bezier.EaseOut));
        _cs.Header.StartAnimation("Opacity", CsS(0, CsMs(550), Bezier.EaseOut));

        // 后台异步扫描玩家实机游戏截图：若有实机截图则无缝切入实机截图队列
        var lib = _lib;
        _ = Task.Run(() => CcScanGameCaptures(lib, games)).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result.Count == 0) return;
            _dq.TryEnqueue(() =>
            {
                if (_ccSs != ss || ss.Disposed) return;
                ss.Queue = t.Result;
                ss.ShotsUpgraded = true;
                ss.Round = -1;
                CcSaverShow(first: false);
            });
        }, TaskScheduler.Default);

        Log.Info($"cocoon frameless screensaver entered ({fallbackQueue.Count} initial hero slides)");
        return true;
    }

    private void CcSaverExit()
    {
        var ss = _ccSs;
        if (ss == null) return;
        _ccSs = null;
        ss.Disposed = true;
        if (_cs?.Page?.Rw is { Disposed: false } rw)
            rw.Ui.StartAnimation("Opacity", CsS(1, CsMs(400), CsQuart));
        if (_cs != null && _csInsert == null)
            _cs.Header.StartAnimation("Opacity", CsS(CsNoHeader ? 0 : 1, CsMs(400), CsQuart));
        _cs?.Pages.StartAnimation("Opacity", CsS(1, CsMs(400), CsQuart));
        ss.Root.StartAnimation("Opacity", CsS(0, CsMs(480), Bezier.EaseOut));
        After(CsMs(510), () =>
        {
            _top.Children.Remove(ss.Root);
            foreach (var s in ss.Surf) Images.Release(s);
            ss.PeriphSurf?.Dispose();
            ss.MaskSurf?.Dispose();
            ss.VeilSurf?.Dispose();
            ss.Root.Dispose();
        });
    }

    private bool CcSaverWakeWithGame()
    {
        var ss = _ccSs;
        if (ss == null) return false;
        var g = ss.Game;
        ConsumeScreensaverWake();
        if (g != null && _cs != null && _prefs.Home != HomeStyle.Wall)
        {
            _csSpot = g.Id;
            _csSpotForce = g.Id;
            _ccGameId = g.Id;
            CsShowTab(0, _cs.Tab == 0 ? 0 : -1, entry: true);
        }
        return true;
    }

    private void CcSaverPowerChanged()
    {
        if (_ccSs is not { Disposed: false } ss) return;
        _screensaverNextSwitch = NowMs + CsSaverHoldMs;
        if (_screensaverPowerWanted)
        {
            foreach (var sp in ss.Img)
            {
                sp.StopAnimation("Scale");
                sp.StopAnimation("Offset");
            }
            ss.ClockColonV.StopAnimation("Opacity");
            ss.ClockColonV.Opacity = 0.88f;
            ss.ClockMainV.StopAnimation("Offset");
            ss.ClockColonV.StopAnimation("Offset");
            ss.ClockMainV.Offset = Vector3.Zero;
            ss.ClockColonV.Offset = Vector3.Zero;
        }
        else
        {
            CcStartColonAndDriftAnimations(ss);
        }
    }

    private void CcSaverShow(bool first = false)
    {
        var ss = _ccSs;
        if (ss == null || ss.Disposed || ss.Queue.Count == 0) return;
        if (!first) ss.Round++;
        if (ss.Round < 0) ss.Round = 0;

        var slide = ss.Queue[ss.Round % ss.Queue.Count];
        ss.Game = slide.Game;
        _screensaverNextSwitch = NowMs + CsSaverHoldMs;
        int round = ss.Round;

        _ = Task.Run(() => CcAnalyzeSlideAsync(slide.Path)).ContinueWith(t =>
        {
            var analysis = t.Status == TaskStatus.RanToCompletion && t.Result != null
                ? t.Result
                : CcDefaultAnalysis();
            _dq.TryEnqueue(() =>
            {
                if (_ccSs != ss || ss.Disposed || ss.Round != round) return;
                CcCommitSlide(ss, slide, analysis, first, round);
            });
        }, TaskScheduler.Default);
    }

    private void CcCommitSlide(CcSaverState ss, CcSaverSlide slide, CcSlideAnalysis analysis, bool first, int round)
    {
        int slot = round % 2;
        var inLayer = ss.Layers[slot];
        var outLayer = ss.Layers[1 - slot];
        var inBlur = ss.LayerBlurs[slot];
        var inSprite = ss.Img[slot];

        int maxW = (int)Math.Ceiling(CsW * 1.16f * CsRaster);
        int maxH = (int)Math.Ceiling(CsH * 1.16f * CsRaster);
        if (Images.AcquireReuse(slide.Path, maxW, maxH) is not { } surf) return;

        Images.Release(ss.Surf[slot]);
        ss.Surf[slot] = surf;
        var brush = (CompositionSurfaceBrush)inSprite.Brush;

        Images.WhenLoaded(surf, () =>
        {
            if (_ccSs != ss || ss.Disposed || ss.Round != round) return;
            brush.Surface = surf;
            brush.HorizontalAlignmentRatio = 0.5f;
            brush.VerticalAlignmentRatio = analysis.HasLetterbox ? 0.5f : 0.542f;

            // 将新画面层置于旧画面层之上、暗角层之下
            ss.Root.Children.Remove(inLayer);
            ss.Root.Children.InsertAbove(inLayer, outLayer);

            bool battery = _screensaverPowerWanted;
            CcApplySmartPan(inSprite, analysis, round % 2 == 1 ? 1 : -1, battery);

            int fadeMs = battery ? 650 : 1550;
            inLayer.StopAnimation("Opacity");
            inLayer.Opacity = 0;
            inLayer.StartAnimation("Opacity", CsS(1, CsMs(fadeMs), CcSaverEase));

            if (!battery && !Reduced && !first)
            {
                inBlur.Properties.InsertScalar("blur.BlurAmount", 9f);
                inBlur.StartAnimation("blur.BlurAmount", CsS(0, CsMs(1050), CcSaverEase));
            }
            else
            {
                inBlur.StopAnimation("blur.BlurAmount");
                inBlur.Properties.InsertScalar("blur.BlurAmount", 0f);
            }

            After(CsMs(fadeMs + 60), () =>
            {
                if (_ccSs == ss && !ss.Disposed && ss.Round == round)
                {
                    outLayer.StopAnimation("Opacity");
                    outLayer.Opacity = 0;
                }
            });

            int chosenZone = CcPickBestZone(ss, analysis);
            void PlaceAndBloom()
            {
                if (_ccSs != ss || ss.Disposed || ss.Round != round) return;
                ss.ActiveZone = chosenZone;
                CcPlaceCornerLayers(ss, chosenZone, analysis, animateIn: true);
                CcSaverPaintClock(force: true);
                ss.ClockBox.StartAnimation("Opacity", CsS(1, CsMs(560), CcSaverEase));
            }

            if (first)
            {
                ss.ClockBox.Opacity = 0;
                PlaceAndBloom();
            }
            else
            {
                ss.ClockBox.StartAnimation("Opacity", CsS(0, CsMs(380), Bezier.EaseOut));
                ss.BokehV.StartAnimation("Opacity", CsS(0, CsMs(380), Bezier.EaseOut));
                ss.VeilV.StartAnimation("Opacity", CsS(0, CsMs(380), Bezier.EaseOut));
                After(CsMs(410), PlaceAndBloom);
            }
        });
    }

    private void CcApplySmartPan(SpriteVisual sprite, CcSlideAnalysis analysis, int dir, bool battery)
    {
        sprite.StopAnimation("Scale");
        sprite.StopAnimation("Offset");
        float baseScale = analysis.HasLetterbox ? 1.138f : 1.062f;
        float endScale = baseScale + 0.044f;
        float panRatioX = analysis.Aspect > 1.70f ? 0.030f : 0.014f;
        float dx = CsW * panRatioX * dir;
        float dy = analysis.HasLetterbox ? 0f : -CsH * 0.008f;

        if (battery || Reduced)
        {
            sprite.Scale = new Vector3(baseScale, baseScale, 1);
            sprite.Offset = Vector3.Zero;
            return;
        }

        var sc = G.C.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0, new Vector3(baseScale, baseScale, 1), G.Linear);
        sc.InsertKeyFrame(1, new Vector3(endScale, endScale, 1), G.Linear);
        sc.Duration = Gfx.Ms(SsPanMs);

        var off = G.C.CreateVector3KeyFrameAnimation();
        off.InsertKeyFrame(0, new Vector3(dx, 0, 0), G.Linear);
        off.InsertKeyFrame(1, new Vector3(-dx, dy, 0), G.Linear);
        off.Duration = sc.Duration;

        sprite.StartAnimation("Scale", sc);
        sprite.StartAnimation("Offset", off);
    }

    private int CcPickBestZone(CcSaverState ss, CcSlideAnalysis analysis)
    {
        int best = 0;
        float minScore = float.MaxValue;
        for (int z = 0; z < 4; z++)
        {
            float score = analysis.Zones[z].Cost + (z == ss.PrevZone ? 34f : 0f);
            if (score < minScore) { minScore = score; best = z; }
        }
        ss.PrevZone = best;
        return best;
    }

    private void CcPlaceCornerLayers(CcSaverState ss, int zone, CcSlideAnalysis analysis, bool animateIn)
    {
        bool right = zone is 1 or 3;
        bool top = zone is 2 or 3;

        float cx = right ? (CsW - CcCornerMx - CcClockW) : CcCornerMx;
        float cy = top ? CcCornerMy : (CsH - CcCornerMy - CcClockH);
        ss.ClockBox.Offset = new Vector3(cx, cy, 0);

        float bx = right ? (CsW - CcBokehW + 160) : -160;
        float by = top ? -150 : (CsH - CcBokehH + 150);
        ss.BokehV.Offset = new Vector3(bx, by, 0);
        ss.VeilV.Offset = new Vector3(bx, by, 0);

        var st = analysis.Zones[zone];
        float veilAlpha = Math.Clamp((st.MeanLum / 255f) * 0.46f + (st.Clutter / 60f) * 0.14f, 0.16f, 0.54f);
        float bokehAlpha = Math.Clamp((st.Clutter / 38f) * 0.62f + (st.MeanLum > 145f ? 0.34f : 0.14f), 0.32f, 0.88f);

        if (animateIn && !Reduced)
        {
            ss.BokehV.StartAnimation("Opacity", CsS(bokehAlpha, CsMs(620), CcSaverEase));
            ss.VeilV.StartAnimation("Opacity", CsS(veilAlpha, CsMs(620), CcSaverEase));
        }
        else
        {
            ss.BokehV.Opacity = bokehAlpha;
            ss.VeilV.Opacity = veilAlpha;
        }
    }

    /// <summary>纯无框日历 + 玻璃折射体时钟绘制（不显示任何游戏名或标签）。</summary>
    private void CcSaverPaintClock(bool force = false)
    {
        var ss = _ccSs;
        if (ss == null || ss.Disposed) return;
        var now = DateTime.Now;
        bool right = ss.ActiveZone is 1 or 3;
        string hh = now.ToString("HH");
        string mm = now.ToString("mm");
        string date = $"{now.Month}月{now.Day}日 星期{"日一二三四五六"[(int)now.DayOfWeek]}";
        string key = $"{hh}:{mm}|{date}|{(right ? 'R' : 'L')}|{CsRaster:F3}";
        if (!force && key == ss.ClockKey) return;
        ss.ClockKey = key;

        using var fmtDate = new CanvasTextFormat
        {
            FontFamily = RwCjk,
            FontSize = 30,
            FontWeight = new Windows.UI.Text.FontWeight(500),
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        using var fmtTime = new CanvasTextFormat
        {
            FontFamily = RwPoppins,
            FontSize = 158,
            FontWeight = new Windows.UI.Text.FontWeight(600),
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        var dev = G.Device;
        using var layDate = new CanvasTextLayout(dev, date, fmtDate, CcClockW, 60);
        using var layH = new CanvasTextLayout(dev, hh, fmtTime, CcClockW, 200);
        using var layC = new CanvasTextLayout(dev, ":", fmtTime, 120, 200);
        using var layM = new CanvasTextLayout(dev, mm, fmtTime, CcClockW, 200);

        float wDate = (float)layDate.LayoutBounds.Width;
        float wH = (float)layH.LayoutBoundsIncludingTrailingWhitespace.Width;
        float wC = (float)layC.LayoutBoundsIncludingTrailingWhitespace.Width;
        float wM = (float)layM.LayoutBoundsIncludingTrailingWhitespace.Width;
        const float kern = -4f;
        float totalTimeW = wH + kern + wC + kern + wM;

        float xTime = right ? (CcClockW - 8 - totalTimeW) : 8;
        float xH = xTime;
        float xC = xH + wH + kern;
        float xM = xC + wC + kern;
        float xDate = right ? (CcClockW - 12 - wDate - (float)layDate.LayoutBounds.X) : (12 - (float)layDate.LayoutBounds.X);
        const float yDate = 4f, yTime = 38f;

        var glassStops = new[]
        {
            new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(250, 255, 255, 255) },
            new CanvasGradientStop { Position = 0.54f, Color = Color.FromArgb(220, 252, 253, 255) },
            new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(166, 228, 236, 255) },
        };

        CsRepaint(ss.ClockMainV, CcClockW, CcClockH, ds =>
        {
            CcDrawShadowed(ds, shadowDs =>
            {
                shadowDs.DrawTextLayout(layDate, xDate, yDate, Colors.Black);
                shadowDs.DrawTextLayout(layH, xH, yTime, Colors.Black);
                shadowDs.DrawTextLayout(layM, xM, yTime, Colors.Black);
            });

            ds.DrawTextLayout(layDate, xDate, yDate, Color.FromArgb(206, 255, 255, 255));
            using var glass = new CanvasLinearGradientBrush(ds, glassStops)
            {
                StartPoint = new Vector2(0, yTime + 32),
                EndPoint = new Vector2(0, yTime + 170),
            };
            ds.DrawTextLayout(layH, xH, yTime, glass);
            ds.DrawTextLayout(layM, xM, yTime, glass);
        });

        CsRepaint(ss.ClockColonV, CcClockW, CcClockH, ds =>
        {
            CcDrawShadowed(ds, shadowDs => shadowDs.DrawTextLayout(layC, xC, yTime - 8f, Colors.Black));
            using var glass = new CanvasLinearGradientBrush(ds, glassStops)
            {
                StartPoint = new Vector2(0, yTime + 32),
                EndPoint = new Vector2(0, yTime + 170),
            };
            ds.DrawTextLayout(layC, xC, yTime - 8f, glass);
        });
    }

    private static void CcDrawShadowed(CanvasDrawingSession ds, Action<CanvasDrawingSession> drawMask)
    {
        var dev = ds.Device;
        float k = ds.Transform.M11;
        using var cl = new CanvasCommandList(dev);
        using (var sds = cl.CreateDrawingSession())
        {
            sds.Transform = ds.Transform * Matrix3x2.CreateTranslation(0, 5f * k);
            drawMask(sds);
        }
        using var softBlur = new GaussianBlurEffect { Source = cl, BlurAmount = Math.Clamp(12f * k, 0.5f, 48f) };
        using var softOp = new OpacityEffect { Source = softBlur, Opacity = 0.42f };
        using var tightBlur = new GaussianBlurEffect { Source = cl, BlurAmount = Math.Clamp(3.2f * k, 0.5f, 16f) };
        using var tightOp = new OpacityEffect { Source = tightBlur, Opacity = 0.30f };
        var saved = ds.Transform;
        ds.Transform = Matrix3x2.Identity;
        ds.DrawImage(softOp);
        ds.DrawImage(tightOp);
        ds.Transform = saved;
    }

    private void CcStartColonAndDriftAnimations(CcSaverState ss)
    {
        if (_screensaverPowerWanted || Reduced)
        {
            ss.ClockColonV.Opacity = 0.88f;
            return;
        }
        var pulse = G.C.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0f, 0.92f, G.Ease(Bezier.EaseInOut));
        pulse.InsertKeyFrame(1f, 0.36f, G.Ease(Bezier.EaseInOut));
        pulse.Duration = TimeSpan.FromMilliseconds(2000);
        pulse.Direction = AnimationDirection.Alternate;
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        ss.ClockColonV.StartAnimation("Opacity", pulse);

        // 亚像素慢速微漂移：消除任何长时停留残影
        var drift = G.C.CreateVector3KeyFrameAnimation();
        drift.InsertKeyFrame(0f, new Vector3(-10f, -7f, 0), G.Ease(Bezier.EaseInOut));
        drift.InsertKeyFrame(1f, new Vector3(10f, 7f, 0), G.Ease(Bezier.EaseInOut));
        drift.Duration = TimeSpan.FromMilliseconds(13000);
        drift.Direction = AnimationDirection.Alternate;
        drift.IterationBehavior = AnimationIterationBehavior.Forever;
        ss.ClockMainV.StartAnimation("Offset", drift);
        ss.ClockColonV.StartAnimation("Offset", drift);
    }

    private CompositionBrush CcCreateMaskedBackdropBlur(CompositionDrawingSurface maskSurf)
    {
        try
        {
            var blur = new GaussianBlurEffect
            {
                Name = "b",
                BlurAmount = 14f,
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Balanced,
                Source = new CompositionEffectSourceParameter("src"),
            };
            var fxBrush = G.C.CreateEffectFactory(blur).CreateBrush();
            fxBrush.SetSourceParameter("src", G.C.CreateBackdropBrush());
            var mb = G.C.CreateMaskBrush();
            mb.Source = fxBrush;
            var maskBrush = G.C.CreateSurfaceBrush(maskSurf);
            maskBrush.Stretch = CompositionStretch.Fill;
            mb.Mask = maskBrush;
            return mb;
        }
        catch (Exception ex)
        {
            Log.Warn($"cocoon saver backdrop blur fallback: {ex.Message}");
            return G.Solid(Colors.Transparent);
        }
    }

    private CompositionDrawingSurface CcPaintPeripheryVignette()
    {
        int pw = Math.Max(64, (int)Math.Ceiling(CsW * 0.25f));
        int ph = Math.Max(40, (int)Math.Ceiling(CsH * 0.25f));
        var surf = G.Surface(pw, ph);
        using var ds = Microsoft.Graphics.Canvas.UI.Composition.CanvasComposition.CreateDrawingSession(surf);
        ds.Clear(Colors.Transparent);
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(0, 8, 10, 18) },
            new CanvasGradientStop { Position = 0.64f, Color = Color.FromArgb(0, 8, 10, 18) },
            new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(58, 8, 10, 18) },
        };
        using var rad = new CanvasRadialGradientBrush(ds, stops)
        {
            Center = new Vector2(pw / 2f, ph / 2f),
            RadiusX = pw * 0.68f,
            RadiusY = ph * 0.68f,
        };
        ds.FillRectangle(0, 0, pw, ph, rad);
        return surf;
    }

    private CompositionDrawingSurface CcPaintRadialEllipse(float w, float h, Color core)
    {
        int pw = Math.Max(32, (int)Math.Ceiling(w * 0.25f));
        int ph = Math.Max(32, (int)Math.Ceiling(h * 0.25f));
        var surf = G.Surface(pw, ph);
        using var ds = Microsoft.Graphics.Canvas.UI.Composition.CanvasComposition.CreateDrawingSession(surf);
        ds.Clear(Colors.Transparent);
        var stops = new[]
        {
            new CanvasGradientStop { Position = 0.00f, Color = Color.FromArgb(255, core.R, core.G, core.B) },
            new CanvasGradientStop { Position = 0.46f, Color = Color.FromArgb(132, core.R, core.G, core.B) },
            new CanvasGradientStop { Position = 1.00f, Color = Color.FromArgb(0, core.R, core.G, core.B) },
        };
        using var rad = new CanvasRadialGradientBrush(ds, stops)
        {
            Center = new Vector2(pw / 2f, ph / 2f),
            RadiusX = pw / 2f,
            RadiusY = ph / 2f,
        };
        ds.FillRectangle(0, 0, pw, ph, rad);
        return surf;
    }

    // ---------------- 实机截图扫描与 48×30 像素寻位分析 ----------------

    private static List<CcSaverSlide> CcScanGameCaptures(Library lib, List<LibraryItem> fallbackGames)
    {
        var raw = Captures.Scan(lib);
        var seenBursts = new HashSet<int>();
        var filtered = new List<Capture>();
        foreach (var c in raw)
        {
            if (string.IsNullOrEmpty(c.GameId)) continue;
            if (c.Game.Contains("Claw 游戏库", StringComparison.OrdinalIgnoreCase)) continue;
            string stem = Path.GetFileNameWithoutExtension(c.Path);
            if (stem.Contains("控制器布局", StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Burst >= 0 && !seenBursts.Add(c.Burst)) continue;
            if (!File.Exists(c.Path)) continue;
            filtered.Add(c);
        }
        if (filtered.Count == 0) return new();

        // 按游戏分组轮转抽取，避免单次连截几十张同一游戏霸屏
        var groups = filtered
            .GroupBy(c => c.GameId!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Queue<Capture>(g))
            .ToList();

        var result = new List<CcSaverSlide>(filtered.Count);
        while (groups.Count > 0 && result.Count < 80)
        {
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                var q = groups[i];
                var cap = q.Dequeue();
                LibraryItem? owner = null;
                if (cap.GameId != null)
                {
                    lib.ById.TryGetValue(cap.GameId, out owner);
                    if (owner == null && cap.GameId.StartsWith("steam:", StringComparison.Ordinal))
                        lib.ById.TryGetValue(cap.GameId["steam:".Length..], out owner);
                }
                result.Add(new CcSaverSlide(cap.Path, owner));
                if (q.Count == 0) groups.RemoveAt(i);
            }
        }
        return result;
    }

    private async Task<CcSlideAnalysis?> CcAnalyzeSlideAsync(string path)
    {
        lock (_ccSsCache)
        {
            if (_ccSsCache.TryGetValue(path, out var cached)) return cached;
        }
        try
        {
            const int W = 48, H = 30;
            byte[] pixels;
            uint origW, origH;
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var dec = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
                origW = dec.PixelWidth;
                origH = dec.PixelHeight;
                var data = await dec.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Ignore,
                    new BitmapTransform { ScaledWidth = W, ScaledHeight = H, InterpolationMode = BitmapInterpolationMode.Linear },
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                pixels = data.DetachPixelData();
            }

            float Lum(int x, int y)
            {
                int idx = (y * W + x) * 4;
                return 0.299f * pixels[idx] + 0.587f * pixels[idx + 1] + 0.114f * pixels[idx + 2];
            }

            float topLum = 0, botLum = 0, midLum = 0;
            for (int x = 6; x < 42; x++)
            {
                topLum += Lum(x, 1);
                botLum += Lum(x, 28);
                midLum += Lum(x, 15);
            }
            topLum /= 36f; botLum /= 36f; midLum /= 36f;
            bool hasLetterbox = topLum < 16f && botLum < 16f && midLum > 34f;

            (int X0, int X1, int Y0, int Y1, float TopBias)[] regions =
            {
                (0, 20, 19, 29, 0f),   // 0: 左下
                (28, 48, 19, 29, 0f),  // 1: 右下
                (0, 20, 2, 12, 11f),   // 2: 左上
                (28, 48, 2, 12, 11f),  // 3: 右上
            };

            var zones = new CcZoneStat[4];
            for (int z = 0; z < 4; z++)
            {
                var r = regions[z];
                float sumL = 0, sumEdge = 0;
                int n = 0;
                for (int y = r.Y0; y < r.Y1; y++)
                {
                    for (int x = r.X0; x < r.X1; x++)
                    {
                        float l = Lum(x, y);
                        sumL += l;
                        if (x + 1 < r.X1 && y + 1 < r.Y1)
                            sumEdge += MathF.Abs(l - Lum(x + 1, y)) + MathF.Abs(l - Lum(x, y + 1));
                        n++;
                    }
                }
                float meanL = sumL / Math.Max(1, n);
                float clutter = sumEdge / Math.Max(1, n);
                float glare = meanL > 175f ? (meanL - 175f) * 1.35f : meanL * 0.34f;
                float cost = glare + clutter * 1.65f + r.TopBias;
                zones[z] = new CcZoneStat(meanL, clutter, cost);
            }

            var res = new CcSlideAnalysis
            {
                HasLetterbox = hasLetterbox,
                Aspect = origW / (float)Math.Max(1u, origH),
                Zones = zones,
            };
            lock (_ccSsCache)
            {
                if (_ccSsCache.Count > 128) _ccSsCache.Clear();
                _ccSsCache[path] = res;
            }
            return res;
        }
        catch (Exception ex)
        {
            Log.Warn($"cocoon saver analyze {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static CcSlideAnalysis CcDefaultAnalysis() => new()
    {
        HasLetterbox = false,
        Aspect = 1.6f,
        Zones = new[]
        {
            new CcZoneStat(60, 10, 36),
            new CcZoneStat(60, 10, 37),
            new CcZoneStat(80, 12, 58),
            new CcZoneStat(80, 12, 59),
        },
    };
}
