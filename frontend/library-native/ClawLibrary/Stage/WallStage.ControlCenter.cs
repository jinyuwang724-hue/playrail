// 控制中心（≡ 键）：照 folia 网格主页（Grid3DSlider）的参数自己实现的一排卡片。
// - 打开时把墙冻住：拍一张快照、模糊一次（之后是静态图，不做实时模糊），上面压 20 % 黑；卡片直接浮在这层上，没有底板。
// - 一行 6 张卡，焦点那张永远水平居中。整行只由一个滚动量 off 决定位置；每张卡的缩放 / 透明度 / 上浮
//   都是绑在 off 上的表达式动画（合成线程），换焦点只对 off 做一次 350 ms 的关键帧动画。
// - 读数（电量、刷新率、手柄）最多每秒刷新一次，变了才重画那一张卡面。没有数据的地方写「—」，不编数字。
// 尺寸按 1920×1200 物理像素下 folia 的数值（卡宽 312、间距 48），换算成本窗口的 DIP。
using System.Numerics;
using System.Runtime.InteropServices;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // folia 的数值（设计 px，1920×1200 下 1 px = 1 物理像素）
    private const float CcCardW = 312, CcGap = 48, CcPad = 16, CcImgRadius = 8, CcRadius = 12;
    private const float CcTextTop = 8, CcTitleLh = 20, CcSecondLh = 16, CcSummaryLh = 14;
    private const float CcFalloff = 600, CcMaxScale = 1.2f;
    private const double CcScrollMs = 350, CcOpenMs = 620, CcCloseMs = 280, CcStepMs = 200;
    private static readonly Bezier CcScrollEase = new(0.42f, 0f, 0.58f, 1f);
    private static readonly Bezier CcOpenEase = new(0.22f, 1f, 0.36f, 1f);
    private static readonly Bezier CcCloseEase = new(0.4f, 0f, 0.2f, 1f);
    private static readonly Color CcText = Gfx.Rgba(0xf4, 0xf4, 0xf5);
    private static readonly Color CcSecondary = Gfx.Rgba(0x71, 0x71, 0x7a);
    private const string CcIconFont = "Segoe Fluent Icons";
    private const string CcMonoFont = "Cascadia Mono";

    private sealed class CcBlock
    {
        public required string Name;
        public required string Icon;
        public string Second = "", Summary = "", Reading = "";
        public bool HasReading;
        public string? CoverPath;
        public string Drawn = "";
        public ContainerVisual El = null!;
        public SpriteVisual Face = null!, Ring = null!, ShadowBase = null!, ShadowFocus = null!, Cover = null!;
        public LoadedImageSurfaceRef? CoverRef;
    }

    /// <summary>封面图在 ImageCache 里的引用（关时释放）。</summary>
    private sealed record LoadedImageSurfaceRef(Microsoft.UI.Xaml.Media.LoadedImageSurface Surface);

    private ContainerVisual _top = null!;
    private ContainerVisual? _cc, _ccRow;
    private SpriteVisual _ccSnap = null!, _ccDim = null!, _ccTitle = null!, _ccLabel = null!;
    private CompositionPropertySet _ccP = null!;
    private readonly List<CcBlock> _ccBlocks = new();
    private bool _ccOpen;
    private int _ccFocus;
    private int _ccVersion;
    private long _ccLastStep = -10_000;
    private Tween? _ccOff;
    private double _ccOffNow;
    private float _ccK = 1, _ccH;
    private DispatcherQueueTimer? _ccTimer;
    private CompositionVisualSurface? _ccLiveSurface;
    private ICompositionSurface? _ccFrozen;
    private (float k, float dpi) _ccShadowFor;
    private CompositionDrawingSurface? _ccShadowBase, _ccShadowFocus, _ccRingSurface;

    public bool ControlCenterOpen => _ccOpen;

    /// <summary>≡：开 / 关。</summary>
    public void ToggleControlCenter()
    {
        MarkInput();
        if (_ccOpen) CloseControlCenter(); else OpenControlCenter();
    }

    // ---------------- 开 / 关 ----------------

    private void OpenControlCenter()
    {
        if (_stack.Count == 0) return;
        long t0 = NowMs;
        _ccOpen = true;
        int v = ++_ccVersion;
        // 冻住墙：停平移、停动态背景
        PanVelocity = Vector2.Zero;
        _glide = Vector2.Zero;
        if (_searchTyping) _rootEl.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        UpdateBackdropRunning();

        EnsureControlCenterVisuals();
        CcMeasure();
        CcReadData();
        foreach (var b in _ccBlocks) CcPaintCard(b, force: true);
        _ccFocus = Math.Clamp(_ccFocus, 0, _ccBlocks.Count - 1);
        _ccOff = null;
        _ccOffNow = _ccFocus * CcPitch;
        _ccP.StopAnimation("off");
        _ccP.InsertScalar("off", (float)_ccOffNow);
        CcOrder();
        CcPaintLabels();
        CcSnapshot(v);

        _cc!.IsVisible = true;
        _cc.StopAnimation("Opacity");
        _cc.Opacity = 0;
        if (Reduced) _cc.Opacity = 1;
        else { _cc.StartAnimation("Opacity", G.Scalar(0, 1, CcOpenMs, CcOpenEase)); _cc.Opacity = 1; }

        _ccTimer ??= CcMakeTimer();
        _ccTimer.Start();
        Log.Info($"control center open ({NowMs - t0}ms)");
    }

    private void CloseControlCenter()
    {
        if (!_ccOpen || _cc == null) return;
        _ccOpen = false;
        int v = ++_ccVersion;
        _ccTimer?.Stop();
        double ms = Reduced ? 1 : CcCloseMs;
        _cc.StartAnimation("Opacity", G.Scalar(_cc.Opacity, 0, ms, CcCloseEase));
        _cc.Opacity = 0;
        After(ms + 10, () =>
        {
            if (v != _ccVersion) return;   // 关的途中又开了
            _cc.IsVisible = false;
            CcReleaseSnapshot();
            foreach (var b in _ccBlocks) CcReleaseCover(b);
            UpdateBackdropRunning();
        });
        Log.Info("control center close");
    }

    // ---------------- 输入 ----------------

    private void CcMove(Direction dir)
    {
        if (dir is not (Direction.Left or Direction.Right)) return;
        long now = NowMs;
        if (now - _ccLastStep < CcStepMs) return;
        int next = Math.Clamp(_ccFocus + (dir == Direction.Left ? -1 : 1), 0, _ccBlocks.Count - 1);
        if (next == _ccFocus) return;
        _ccLastStep = now;
        CcFocus(next);
    }

    private void CcFocus(int i)
    {
        _ccFocus = i;
        CcOrder();
        CcPaintLabels();
        CcScrollTo(i * CcPitch);
    }

    private void CcActivate()
    {
        var b = _ccBlocks[_ccFocus];
        switch (_ccFocus)
        {
            case 0:   // 游戏库：就是底下这面墙
                CloseControlCenter();
                break;
            case 5:   // 软件设置：竖条里的设置列表
                CloseControlCenter();
                OpenRailSettings();
                break;
            default:
                Toast($"「{b.Name}」页面还没有做");
                break;
        }
    }

    /// <summary>Y：控制中心里 = 设为主页（还不支持，给提示）；墙上仍是游戏设置的占位提示。</summary>
    public void PadY()
    {
        MarkInput();
        if (_ccOpen) Toast("还不支持把板块设为主页");
        else Toast("游戏设置（原型里还没有）");
    }

    private void OpenRailSettings()
    {
        if (!CurrentLayer.Home) return;
        if (_searchOpen) CloseSearch();
        if (!SeamUsable()) { Toast("竖条不在屏幕上：先把墙面移回来"); return; }
        _railModel.SettingsOpen = true;
        EnterRail(View.Height / 2);
        _railFocus = _rail.DefaultFocus(_railModel);
        RepaintRail();
    }

    /// <summary>点卡：点到焦点卡 = A，点到别的卡 = 把它移到中间。</summary>
    private void CcClick(double sx, double sy)
    {
        float w = CcCardW * _ccK, h = _ccH * _ccK;
        float top = (float)View.Height / 2 - h / 2;
        if (sy < top - h * 0.1f || sy > top + h * 1.1f) return;
        double off = CcOffNow();
        for (int i = 0; i < _ccBlocks.Count; i++)
        {
            double left = CcPadX + i * CcPitch - off;
            if (sx < left || sx > left + w) continue;
            if (i == _ccFocus) CcActivate(); else CcFocus(i);
            return;
        }
    }

    // ---------------- 滚动量 ----------------

    private float CcPitch => (CcCardW + CcGap) * _ccK;
    private float CcPadX => ((float)View.Width - CcCardW * _ccK) / 2;

    private double CcOffNow()
    {
        if (_ccOff == null) return _ccOffNow;
        long now = NowMs;
        _ccOffNow = _ccOff.At(now);
        if (_ccOff.Done(now)) _ccOff = null;
        return _ccOffNow;
    }

    private void CcScrollTo(double to)
    {
        double from = CcOffNow();
        if (Reduced || Math.Abs(to - from) < 0.5)
        {
            _ccOff = null; _ccOffNow = to;
            _ccP.StopAnimation("off"); _ccP.InsertScalar("off", (float)to);
            return;
        }
        _ccOff = new Tween { From = from, To = to, Start = NowMs, Ms = CcScrollMs, Ease = CcScrollEase };
        _ccP.StartAnimation("off", G.Scalar(from, to, CcScrollMs, CcScrollEase));
    }

    /// <summary>叠放次序：离焦点越近越靠上（合成层子视觉的次序，焦点一变就重排）。</summary>
    private void CcOrder()
    {
        foreach (var b in _ccBlocks.Select((b, i) => (b, i)).OrderByDescending(x => Math.Abs(x.i - _ccFocus)).Select(x => x.b))
        {
            _ccRow!.Children.Remove(b.El);
            _ccRow.Children.InsertAtTop(b.El);
        }
    }

    // ---------------- 视觉 ----------------

    private void EnsureControlCenterVisuals()
    {
        if (_cc != null) return;
        _cc = G.Container();
        _cc.IsVisible = false;
        _top.Children.InsertAbove(_cc, _root);
        _ccSnap = G.Sprite(null, relative: false);
        _ccDim = G.Sprite(G.Solid(Gfx.Rgba(0, 0, 0, 0.20)));
        _ccRow = G.Container();
        _ccTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _ccLabel = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        foreach (var v in new Visual[] { _ccSnap, _ccDim, _ccRow, _ccTitle, _ccLabel }) _cc.Children.InsertAtTop(v);

        _ccP = G.C.CreatePropertySet();
        foreach (var n in new[] { "off", "vw", "pitch", "padx", "half", "r", "lift", "top" }) _ccP.InsertScalar(n, 0);

        var defs = new (string Name, string Icon)[]
        {
            ("游戏库", ""), ("性能", ""), ("手柄", ""),
            ("显示与声音", ""), ("系统", ""), ("软件设置", ""),
        };
        for (int i = 0; i < defs.Length; i++)
        {
            var b = new CcBlock { Name = defs[i].Name, Icon = defs[i].Icon };
            b.El = G.C.CreateContainerVisual();
            b.ShadowBase = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            b.ShadowFocus = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            b.Face = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            b.Cover = G.Sprite(null, relative: false);
            b.Ring = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
            foreach (var v in new Visual[] { b.ShadowBase, b.ShadowFocus, b.Face, b.Cover, b.Ring }) b.El.Children.InsertAtTop(v);
            // t = 离视口中线的距离 / 600，封顶 1
            string T = $"Min(Abs(p.padx + {i} * p.pitch + p.half - p.off - p.vw / 2) / p.r, 1)";
            CcExpr(b.El, "Offset", $"Vector3(p.padx + {i} * p.pitch - p.off, p.top - p.lift * (1 - {T}), 0)");
            CcExpr(b.El, "Scale", $"Vector3({CcMaxScale} - 0.7 * {T}, {CcMaxScale} - 0.7 * {T}, 1)");
            CcExpr(b.El, "Opacity", $"Max(0.15, 1 - 0.85 * {T})");
            // 焦点描边（32 %）与焦点投影：只在中间附近出现
            CcExpr(b.Ring, "Opacity", $"Max(0, 1 - 2.5 * {T})");
            CcExpr(b.ShadowFocus, "Opacity", $"Max(0, 1 - 1.6 * {T})");
            CcExpr(b.ShadowBase, "Opacity", $"Min(1, 1.6 * {T})");
            _ccRow.Children.InsertAtTop(b.El);
            _ccBlocks.Add(b);
        }
    }

    private void CcExpr(CompositionObject target, string prop, string expr)
    {
        var e = G.C.CreateExpressionAnimation(expr);
        e.SetReferenceParameter("p", _ccP);
        target.StartAnimation(prop, e);
    }

    private void CcMeasure()
    {
        float vw = (float)View.Width, vh = (float)View.Height;
        // 设计 px → DIP：1920×1200 物理像素下 1:1
        _ccK = MathF.Min(vw * Chrome.Dpi / 1920f, vh * Chrome.Dpi / 1200f) / Chrome.Dpi;
        _ccH = CcPad + (CcCardW - 2 * CcPad) + CcPad + CcTextTop + CcTitleLh + 4 + CcSecondLh + 8 + 2 * CcSummaryLh + CcPad;
        float w = CcCardW * _ccK, h = _ccH * _ccK;
        _ccP.InsertScalar("vw", vw);
        _ccP.InsertScalar("pitch", CcPitch);
        _ccP.InsertScalar("padx", CcPadX);
        _ccP.InsertScalar("half", w / 2);
        _ccP.InsertScalar("r", CcFalloff * _ccK);
        _ccP.InsertScalar("lift", 6 * _ccK);
        _ccP.InsertScalar("top", vh / 2 - h / 2);
        _ccSnap.Size = new Vector2(vw, vh);
        foreach (var b in _ccBlocks)
        {
            b.El.Size = new Vector2(w, h);
            b.El.CenterPoint = new Vector3(w / 2, h / 2, 0);
            b.Face.Size = new Vector2(w, h);
            b.Ring.Size = new Vector2(w, h);
            float img = (CcCardW - 2 * CcPad) * _ccK;
            b.Cover.Size = new Vector2(img, img);
            b.Cover.Offset = new Vector3(CcPad * _ccK, CcPad * _ccK, 0);
            var clip = G.C.CreateGeometricClip(CcRoundRect(img, img, CcImgRadius * _ccK));
            b.Cover.Clip = clip;
        }
        CcPaintShadows();
        CcPaintTitle();
    }

    private CompositionRoundedRectangleGeometry CcRoundRect(float w, float h, float r)
    {
        var g = G.C.CreateRoundedRectangleGeometry();
        g.Size = new Vector2(w, h);
        g.CornerRadius = new Vector2(r, r);
        return g;
    }

    /// <summary>按卡最大缩放（1.2×）栅格化，放大到中间时也不糊。</summary>
    private float CcRaster => _ccK * CcMaxScale * Chrome.Dpi;

    private CompositionDrawingSurface CcDraw(float w, float h, float raster, Action<CanvasDrawingSession> paint)
    {
        var surface = G.Surface((int)Math.Ceiling(w * raster), (int)Math.Ceiling(h * raster));
        using var ds = CanvasComposition.CreateDrawingSession(surface);
        ds.Clear(Colors.Transparent);
        ds.Transform = Matrix3x2.CreateScale(raster);
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        paint(ds);
        return surface;
    }

    private static void CcSet(SpriteVisual v, ICompositionSurface s)
    {
        var brush = (CompositionSurfaceBrush)v.Brush;
        if (brush.Surface != s) (brush.Surface as IDisposable)?.Dispose();
        brush.Surface = s;
    }

    /// <summary>投影（静止 / 焦点两档，焦点那档更大更深）与焦点描边：所有卡共用，尺寸变了才重画。</summary>
    private void CcPaintShadows()
    {
        if (_ccShadowFor == (_ccK, Chrome.Dpi) && _ccShadowBase != null) { CcPlaceShadows(); return; }
        _ccShadowFor = (_ccK, Chrome.Dpi);
        float raster = _ccK * Chrome.Dpi;
        CompositionDrawingSurface Shadow(float blur, float dy, double alpha) => CcDraw(CcCardW + 2 * 80, _ccH + 2 * 80, raster, ds =>
        {
            using var cl = new CanvasCommandList(ds);
            using (var c = cl.CreateDrawingSession()) c.FillRoundedRectangle(80, 80 + dy, CcCardW, _ccH, CcRadius, CcRadius, Colors.Black);
            using var sh = new ShadowEffect { Source = cl, BlurAmount = blur, ShadowColor = Gfx.Rgba(0, 0, 0, alpha) };
            ds.DrawImage(sh);
        });
        _ccShadowBase = Shadow(12, 10, 0.45);
        _ccShadowFocus = Shadow(26, 24, 0.7);
        _ccRingSurface = CcDraw(CcCardW, _ccH, CcRaster, ds =>
            ds.DrawRoundedRectangle(0.5f, 0.5f, CcCardW - 1, _ccH - 1, CcRadius - 0.5f, CcRadius - 0.5f, Gfx.WithAlpha(CcText, 0.32), 1));
        CcPlaceShadows();
    }

    private void CcPlaceShadows()
    {
        foreach (var b in _ccBlocks)
        {
            ((CompositionSurfaceBrush)b.ShadowBase.Brush).Surface = _ccShadowBase;
            ((CompositionSurfaceBrush)b.ShadowFocus.Brush).Surface = _ccShadowFocus;
            ((CompositionSurfaceBrush)b.Ring.Brush).Surface = _ccRingSurface;
            foreach (var s in new[] { b.ShadowBase, b.ShadowFocus })
            {
                s.Size = new Vector2((CcCardW + 160) * _ccK, (_ccH + 160) * _ccK);
                s.Offset = new Vector3(-80 * _ccK, -80 * _ccK, 0);
            }
        }
    }

    private void CcPaintTitle()
    {
        float raster = _ccK * Chrome.Dpi;
        using var l = TextKit.Layout(G.Device, "控制中心", TextKit.Ui(700), 24, 100000, CanvasHorizontalAlignment.Left);
        var bb = l.LayoutBounds;
        float w = (float)bb.Width + 4, h = (float)bb.Height + 4;
        CcSet(_ccTitle, CcDraw(w, h, raster, ds => ds.DrawTextLayout(l, -(float)bb.X, -(float)bb.Y, CcText)));
        _ccTitle.Size = new Vector2(w * _ccK, h * _ccK);
        _ccTitle.Offset = new Vector3(40 * _ccK, 32 * _ccK, 0);
    }

    /// <summary>行下面：焦点板块名（24 px 粗体）+ 一行等宽小字（第二行 • 摘要）。换焦点时直接换，不做动画。</summary>
    private void CcPaintLabels()
    {
        var b = _ccBlocks[_ccFocus];
        float raster = _ccK * Chrome.Dpi;
        float maxW = (float)View.Width / _ccK - 80;
        using var name = TextKit.Layout(G.Device, b.Name, TextKit.Ui(700), 24, maxW, CanvasHorizontalAlignment.Center);
        string line = string.IsNullOrEmpty(b.Summary) ? b.Second : $"{b.Second} • {b.Summary}";
        using var mono = TextKit.Layout(G.Device, line, new FontSpec(CcMonoFont, 400), 12, maxW, CanvasHorizontalAlignment.Center);
        float nh = (float)name.LayoutBounds.Height, mh = (float)mono.LayoutBounds.Height;
        float h = nh + 8 + mh + 4;
        CcSet(_ccLabel, CcDraw(maxW, h, raster, ds =>
        {
            ds.DrawTextLayout(name, 0, -(float)name.LayoutBounds.Y, CcText);
            ds.DrawTextLayout(mono, 0, nh + 8 - (float)mono.LayoutBounds.Y, Gfx.WithAlpha(CcText, 0.5));
        }));
        _ccLabel.Size = new Vector2(maxW * _ccK, h * _ccK);
        float rowBottom = (float)View.Height / 2 + _ccH * _ccK * CcMaxScale / 2;
        _ccLabel.Offset = new Vector3(40 * _ccK, rowBottom + 28 * _ccK, 0);
    }

    /// <summary>卡面（folia polaroid）：底 #09090b 95 %、1 px 描边 5 %、方图区 + 文字块。内容没变就不重画。</summary>
    private void CcPaintCard(CcBlock b, bool force = false)
    {
        string key = $"{b.Second}|{b.Summary}|{b.Reading}|{b.HasReading}|{b.CoverPath}|{_ccK}|{Chrome.Dpi}";
        if (!force && key == b.Drawn) return;
        b.Drawn = key;
        float img = CcCardW - 2 * CcPad, textW = img;
        var surface = CcDraw(CcCardW, _ccH, CcRaster, ds =>
        {
            ds.FillRoundedRectangle(0, 0, CcCardW, _ccH, CcRadius, CcRadius, Gfx.WithAlpha(Theme.Bg, 0.95));
            ds.DrawRoundedRectangle(0.5f, 0.5f, CcCardW - 1, _ccH - 1, CcRadius - 0.5f, CcRadius - 0.5f, Gfx.WithAlpha(CcText, 0.05), 1);
            // 方图区：zinc-800 20 % + 一圈很淡的内阴影
            ds.FillRoundedRectangle(CcPad, CcPad, img, img, CcImgRadius, CcImgRadius, Gfx.Rgba(0x27, 0x27, 0x2a, 0.20));
            using (var inner = CanvasGeometry.CreateRoundedRectangle(ds, CcPad, CcPad, img, img, CcImgRadius, CcImgRadius))
            using (ds.CreateLayer(1, inner))
            using (var outside = CanvasGeometry.CreateRectangle(ds, CcPad - 40, CcPad - 40, img + 80, img + 80).CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude))
            using (var cl = new CanvasCommandList(ds))
            {
                using (var c = cl.CreateDrawingSession()) c.FillGeometry(outside, Colors.Black);
                using var sh = new ShadowEffect { Source = cl, BlurAmount = 3, ShadowColor = Gfx.Rgba(0, 0, 0, 0.5) };
                using var moved = new Transform2DEffect { Source = sh, TransformMatrix = Matrix3x2.CreateTranslation(0, 1) };
                ds.DrawImage(moved);
            }
            if (b.CoverPath == null)
            {
                // folia 没封面时的样子：居中 64 px 单色图标（20 %），有读数就写在下面
                float cx = CcPad + img / 2, cy = CcPad + img / 2 - (b.HasReading ? 14 : 0);
                using var icon = TextKit.Layout(ds, b.Icon, new FontSpec(CcIconFont, 400), 64, 200, CanvasHorizontalAlignment.Left);
                var ib = icon.DrawBounds;
                ds.DrawTextLayout(icon, cx - (float)(ib.X + ib.Width / 2), cy - (float)(ib.Y + ib.Height / 2), Gfx.WithAlpha(CcText, 0.20));
                if (b.HasReading)
                {
                    using var r = TextKit.Layout(ds, b.Reading, TextKit.Ui(600), 18, img, CanvasHorizontalAlignment.Center);
                    ds.DrawTextLayout(r, CcPad, cy + 44 - (float)r.LayoutBounds.Y, Gfx.WithAlpha(CcText, 0.55));
                }
            }
            float y = CcPad + img + CcPad + CcTextTop;
            using var title = TextKit.Layout(ds, b.Name, TextKit.Ui(700), 14, textW, CanvasHorizontalAlignment.Left, 0, CcTitleLh);
            ds.DrawTextLayout(title, CcPad, y, CcText);
            y += CcTitleLh + 4;
            using var second = TextKit.Layout(ds, b.Second, TextKit.Ui(500), 12, textW, CanvasHorizontalAlignment.Left, 0, CcSecondLh);
            ds.DrawTextLayout(second, CcPad, y, Gfx.WithAlpha(CcText, 0.5));
            y += CcSecondLh + 8;
            using var summary = TextKit.Layout(ds, b.Summary, TextKit.Ui(400), 10, textW, CanvasHorizontalAlignment.Left, 0, CcSummaryLh, maxLines: 2);
            ds.DrawTextLayout(summary, CcPad, y, Gfx.WithAlpha(CcSecondary, 1));
        });
        CcSet(b.Face, surface);
        CcSetCover(b);
    }

    private void CcSetCover(CcBlock b)
    {
        if (b.CoverPath == null) { CcReleaseCover(b); return; }
        if (b.CoverRef != null) return;
        int px = (int)Math.Ceiling((CcCardW - 2 * CcPad) * CcRaster);
        // 竖封面按宽解码，UniformToFill 居中裁成方的
        var s = Images.Acquire(b.CoverPath, px, px * 3 / 2);
        if (s == null) return;
        b.CoverRef = new LoadedImageSurfaceRef(s);
        var brush = G.C.CreateSurfaceBrush(s);
        brush.Stretch = CompositionStretch.UniformToFill;
        brush.HorizontalAlignmentRatio = 0.5f;
        brush.VerticalAlignmentRatio = 0.5f;
        b.Cover.Brush = brush;
    }

    private void CcReleaseCover(CcBlock b)
    {
        if (b.CoverRef == null) return;
        b.Cover.Brush = null;
        Images.Release(b.CoverRef.Surface);
        b.CoverRef = null;
    }

    // ---------------- 快照：模糊一次 ----------------

    /// <summary>
    /// 墙的快照：先用 VisualSurface + 高斯模糊画一帧，再把这个模糊后的视觉截成一张静态图换上去，
    /// 之后模糊特效与实时视觉表面都扔掉（打开期间不再有任何实时模糊）。截图失败就留着那一帧的特效（墙已冻住，内容不变）。
    /// </summary>
    private void CcSnapshot(int version)
    {
        CcReleaseSnapshot();
        float vw = (float)View.Width, vh = (float)View.Height;
        try
        {
            var vs = G.C.CreateVisualSurface();
            vs.SourceVisual = _root;
            vs.SourceSize = new Vector2(vw, vh);
            _ccLiveSurface = vs;
            var src = G.C.CreateSurfaceBrush(vs);
            src.Stretch = CompositionStretch.Fill;
            var blur = new GaussianBlurEffect
            {
                Name = "blur",
                BlurAmount = 4f * _ccK,   // 约 4 物理像素（1920×1200 下）
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Speed,
                Source = new CompositionEffectSourceParameter("src"),
            };
            var brush = G.C.CreateEffectFactory(blur).CreateBrush();
            brush.SetSourceParameter("src", src);
            _ccSnap.Brush = brush;
        }
        catch (Exception ex) { Log.Warn($"control center blur: {ex.Message}"); _ccSnap.Brush = null; return; }
        _ = CcFreezeAsync(version, vw, vh);
    }

    private async Task CcFreezeAsync(int version, float vw, float vh)
    {
        try
        {
            // 截图尺寸按 DIP 给（按物理像素给会只画进左上角 1/DPI 的一块）；反正是模糊过的图，分辨率低一点看不出来
            var size = new Windows.Graphics.SizeInt32 { Width = (int)Math.Ceiling(vw), Height = (int)Math.Ceiling(vh) };
            var shot = await G.GraphicsDevice.CaptureAsync(_ccSnap, size, DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied, 1f);
            if (version != _ccVersion || !_ccOpen) { (shot as IDisposable)?.Dispose(); return; }
            var still = G.C.CreateSurfaceBrush(shot);
            still.Stretch = CompositionStretch.Fill;
            var live = _ccSnap.Brush;
            _ccSnap.Brush = still;
            live?.Dispose();
            _ccLiveSurface?.Dispose();
            _ccLiveSurface = null;
            _ccFrozen = shot;
            Log.Info("control center snapshot frozen");
        }
        catch (Exception ex) { Log.Warn($"control center capture: {ex.Message}"); }
    }

    private void CcReleaseSnapshot()
    {
        var b = _ccSnap?.Brush;
        if (_ccSnap != null) _ccSnap.Brush = null;
        b?.Dispose();
        _ccLiveSurface?.Dispose();
        _ccLiveSurface = null;
        (_ccFrozen as IDisposable)?.Dispose();
        _ccFrozen = null;
    }

    // ---------------- 数据 ----------------

    private DispatcherQueueTimer CcMakeTimer()
    {
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1);
        t.IsRepeating = true;
        t.Tick += (_, _) =>
        {
            try
            {
                if (!_ccOpen) return;
                var before = _ccBlocks[_ccFocus].Summary;
                CcReadData();
                foreach (var b in _ccBlocks) CcPaintCard(b);
                if (_ccBlocks[_ccFocus].Summary != before) CcPaintLabels();
            }
            catch (Exception ex) { Log.Error("control center timer", ex); }
        };
        return t;
    }

    private void CcReadData()
    {
        // 游戏库：游戏数 + 最近玩过的那款的竖封面
        var games = _lib.Games.Where(g => g.Kind == ItemKind.Game).ToList();
        var recent = games.Where(g => g.LastPlayed is > 0).MaxBy(g => g.LastPlayed);
        var lib = _ccBlocks[0];
        lib.Second = $"{games.Count} 款游戏";
        lib.Summary = recent != null ? $"最近玩过：{recent.Title}" : "还没有游玩记录";
        string? cover = null;
        if (recent?.Art is { } art)
            foreach (var k in new[] { 'p', 'q', 'v', 'x', 's' })
                if (art.Has(k)) { cover = art.File(k); break; }
        if (cover != lib.CoverPath) { CcReleaseCover(lib); lib.CoverPath = cover; }

        // 性能：还没有接入功耗读数，不编数字
        var perf = _ccBlocks[1];
        perf.Second = "功耗与风扇";
        perf.Summary = "功耗读数还没有接入";
        perf.Reading = "— W"; perf.HasReading = true;

        var pad = _ccBlocks[2];
        bool connected = _pad?.Connected ?? false;
        pad.Second = "振动与按键";
        pad.Summary = connected ? "手柄已连接" : "没有检测到手柄";
        pad.Reading = connected ? "已连接" : "—"; pad.HasReading = true;

        var disp = _ccBlocks[3];
        int hz = RefreshRate();
        disp.Second = "亮度、音量与刷新率";
        disp.Summary = hz > 1 ? $"刷新率 {hz} Hz" : "读不到刷新率";
        disp.Reading = hz > 1 ? $"{hz} Hz" : "—"; disp.HasReading = true;

        var sys = _ccBlocks[4];
        var bat = SystemInfo.Battery();
        sys.Second = "电池、电源、更新与维护";
        if (bat.HasBattery)
        {
            string state = bat.Charging ? "充电中" : bat.OnAc ? "已接通电源" : "使用电池";
            sys.Summary = $"电量 {bat.Percent}% · {state}";
            sys.Reading = $"{bat.Percent}%";
        }
        else { sys.Summary = bat.OnAc ? "已接通电源" : "读不到电池"; sys.Reading = "—"; }
        sys.HasReading = true;

        var app = _ccBlocks[5];
        app.Second = "主页与外观";
        app.Summary = $"标题样式：{(Style == TitleStyle.B ? "极简黑体" : "电影字幕")} · 动态背景：{(_backdropOn ? "开" : "关")}";
        app.HasReading = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DevMode
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW")]
    private static extern unsafe int EnumDisplaySettings(char* device, int mode, DevMode* dm);

    /// <summary>主显示器当前刷新率（Hz），读不到返回 0。</summary>
    private static unsafe int RefreshRate()
    {
        try
        {
            DevMode dm = default;
            dm.Size = (ushort)sizeof(DevMode);
            return EnumDisplaySettings(null, -1, &dm) != 0 ? (int)dm.DisplayFrequency : 0;   // ENUM_CURRENT_SETTINGS
        }
        catch { return 0; }
    }
}
