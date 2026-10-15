// 控制中心（≡ 键）：照 folia 网格主页（Grid3DSlider）的参数自己实现的一排卡片；功能块代替歌单。
// - 背景：folia 网格主页底下的「隐现」动态背景（Backdrop，与缝里同一套着色器，这里铺满全屏），取色用游戏库卡上那款游戏的封面；
//   上面压 4 px 模糊 + 20 % 黑，卡片直接浮在上面。只在控制中心露出来时出帧，关掉就停。
// - 一行 6 张卡，焦点那张永远水平居中。整行只由一个滚动量 off 决定位置；每张卡的缩放 / 透明度 / 上浮
//   都是绑在 off 上的表达式动画（合成线程），换焦点只对 off 做一次 350 ms 的关键帧动画。
// - 控制中心是「首页」，游戏库墙面是压在它上面的集合层；两者之间的出入场照 folia 的「移形换影」：
//   进（A 在游戏库卡上）：卡片拆成外框 / 封面 / 标题三件，弹簧飞到墙上 hero 卡的位置；墙的底 0.62 s 淡入，其余卡沿径向就位。
//   出（墙上按 ≡）：墙的底 0.62 s 淡出露出控制中心，hero 三件飞回游戏库卡，其余卡沿径向四散。
//   全部在合成线程：弹簧预先采样成关键帧，只动盒子（位置 / 尺寸）、透明度、转角、模糊。飞行中按任何键 = 快进到结尾。
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
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using static ClawLibrary.Engine.ViewMath;
using Colors = Microsoft.UI.Colors;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    // folia 的数值（设计 px，1920×1200 下 1 px = 1 物理像素）
    private const float CcCardW = 312, CcGap = 48, CcPad = 16, CcImgRadius = 8, CcRadius = 12;
    private const float CcTextTop = 8, CcTitleLh = 20, CcSecondLh = 16, CcSummaryLh = 14;
    private const float CcFalloff = 600, CcMaxScale = 1.2f;
    /// <summary>滑条上下各 96 px 留白（py-24），信息块 pt-6；整组（滑条 + 信息块）在屏幕上垂直居中。</summary>
    private const float CcRowPadY = 96, CcLabelTop = 24, CcLabelH = 32 + 4 + 16 + 16;
    private const double CcScrollMs = 350, CcCloseMs = 280, CcStepMs = 200;
    private static readonly Bezier CcScrollEase = new(0.42f, 0f, 0.58f, 1f);
    private static readonly Bezier CcCloseEase = new(0.4f, 0f, 0.2f, 1f);
    private static readonly Color CcText = Gfx.Rgba(0xf4, 0xf4, 0xf5);
    private static readonly Color CcSecondary = Gfx.Rgba(0x71, 0x71, 0x7a);
    private const string CcIconFont = "Segoe Fluent Icons";
    private const string CcMonoFont = "Cascadia Mono";

    // 移形换影（folia transitions/*）
    private static readonly Bezier EnterBackdropEase = new(0.22f, 1f, 0.36f, 1f);   // 背景板 0.62 s
    private static readonly Bezier ExitEase = new(0.32f, 0.72f, 0f, 1f);            // 出场背景板 0.62 s
    private static readonly Bezier EaseOutCss = new(0f, 0f, 0.58f, 1f);
    private static readonly Bezier EaseCss = new(0.25f, 0.1f, 0.25f, 1f);
    private const double EnterMs = 620, ExitBackdropMs = 620, ExitMs = 500;
    private const double CrossfadeMs = 280, FadeMs = 180, SettleMs = 340, FadeAtMs = 580, HeroRevealMs = 460, HomeFadeMs = 250;

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
        public LoadedImageSurface? CoverRef;
    }

    private ContainerVisual _top = null!;
    private ContainerVisual? _cc, _ccRow;
    private SpriteVisual _ccDim = null!, _ccTitle = null!, _ccLabel = null!;
    private Backdrop? _ccBack;
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
    private (float k, float dpi) _ccShadowFor;
    private CompositionDrawingSurface? _ccShadowBase, _ccShadowFocus, _ccRingSurface;
    private bool _ccBackLoop;
    private long _ccBackLast;

    // 移形换影进行中：输入一律当「快进」
    private ContainerVisual? _fx;
    private Action? _morphFinish;
    private DispatcherQueueTimer? _morphTimer;
    private bool MorphBusy => _morphFinish != null;

    public bool ControlCenterOpen => _ccOpen;

    /// <summary>移形换影途中的任何输入：直接跳到结尾（返回 true 表示这次输入被吃掉）。</summary>
    private bool MorphIntercept()
    {
        if (!MorphBusy) return false;
        FinishMorph();
        return true;
    }

    /// <summary>≡：墙上 = 打开控制中心（出场形变），控制中心里 = 关掉。</summary>
    public void ToggleControlCenter()
    {
        MarkInput();
        if (MorphIntercept()) return;
        if (_ccOpen) CloseControlCenter(); else OpenControlCenter();
    }

    // ---------------- 开 / 关 ----------------

    private void OpenControlCenter()
    {
        if (_stack.Count == 0) return;
        long t0 = NowMs;
        if (_searchOpen) CloseSearch();
        if (_expandedKey != null) Collapse(false);
        _ccOpen = true;
        ++_ccVersion;
        PanVelocity = Vector2.Zero;
        _glide = Vector2.Zero;
        UpdateBackdropRunning();

        EnsureControlCenterVisuals();
        CcMeasure();
        CcReadData();
        foreach (var b in _ccBlocks) CcPaintCard(b, force: true);
        // folia：从集合层返回时首页停在来时的那张卡上 —— 墙是从游戏库卡进来的
        _ccFocus = 0;
        _ccOff = null;
        _ccOffNow = 0;
        _ccP.StopAnimation("off");
        _ccP.InsertScalar("off", 0);
        CcOrder();
        CcPaintLabels();
        _cc!.StopAnimation("Opacity");
        _cc.Opacity = 1;
        _cc.IsVisible = true;
        CcBackStart();
        _ccTimer ??= CcMakeTimer();
        _ccTimer.Start();

        if (Reduced || !MorphExit())
        {
            // 没有能飞的 hero（或降低动态效果）：墙按原来的 0.28 s 淡出
            double ms = Reduced ? 1 : CcCloseMs;
            _root.StartAnimation("Opacity", G.Scalar(1, 0, ms, CcCloseEase));
            int v = _ccVersion;
            After(ms + 10, () => { if (v == _ccVersion && _ccOpen) { _root.IsVisible = false; _root.StopAnimation("Opacity"); _root.Opacity = 1; } });
        }
        Log.Info($"control center open ({NowMs - t0}ms)");
    }

    /// <summary>B / ≡ / A：回到墙。焦点在游戏库卡上时走进场形变，其余按 0.28 s 淡入墙面。</summary>
    private void CloseControlCenter()
    {
        if (!_ccOpen || _cc == null) return;
        _ccOpen = false;
        int v = ++_ccVersion;
        _ccTimer?.Stop();
        _root.StopAnimation("Opacity");
        _root.IsVisible = true;
        if (_ccFocus == 0 && !Reduced && MorphEnter()) { Log.Info("control center close (morph)"); return; }
        double ms = Reduced ? 1 : CcCloseMs;
        _root.Opacity = 0;
        _root.StartAnimation("Opacity", G.Scalar(0, 1, ms, CcCloseEase));
        _root.Opacity = 1;
        After(ms + 10, () => { if (v == _ccVersion) HideControlCenter(); });
        UpdateBackdropRunning();
        Log.Info("control center close");
    }

    private void HideControlCenter()
    {
        if (_cc == null) return;
        _cc.IsVisible = false;
        _cc.StopAnimation("Opacity");
        _cc.Opacity = 1;
        CcBackStop();
        foreach (var b in _ccBlocks) CcReleaseCover(b);
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
            case 0:   // 游戏库：进集合层（进场形变）
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
        if (MorphIntercept()) return;
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
        float top = CcTop;
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
    /// <summary>卡（未缩放）的上沿：滑条 + 信息块整组垂直居中。</summary>
    private float CcTop => ((float)View.Height - (CcRowPadY * 2 + _ccH + CcLabelTop + CcLabelH) * _ccK) / 2 + CcRowPadY * _ccK;

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

    /// <summary>叠放次序：离焦点越近越靠上（folia 的 z = 10 − 9t；焦点一变就重排）。</summary>
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
        // 控制中心是首页，在墙（集合层）的下面；形变合成层在两者之上
        _cc = G.Container();
        _cc.IsVisible = false;
        _top.Children.InsertBelow(_cc, _root);
        _fx = G.Container();
        _top.Children.InsertAbove(_fx, _root);

        _ccBack = new Backdrop(G) { Dpi = Chrome.Dpi };
        _ccDim = G.Sprite(G.Solid(Gfx.Rgba(0, 0, 0, 0.20)));
        _ccRow = G.Container();
        _ccTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _ccLabel = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        foreach (var v in new Visual[] { _ccBack.Root, _ccDim, _ccRow, _ccTitle, _ccLabel }) _cc.Children.InsertAtTop(v);

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
            // folia updateCardTransforms：t = min(|卡中心 − 视口中心| / 600, 1)；scale 1.2 → 0.5，opacity 1 → 0.15，上浮 6 px
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
        _ccP.InsertScalar("top", CcTop);
        foreach (var b in _ccBlocks)
        {
            b.El.Size = new Vector2(w, h);
            b.El.CenterPoint = new Vector3(w / 2, h / 2, 0);
            b.Face.Size = new Vector2(w, h);
            b.Ring.Size = new Vector2(w, h);
            float img = (CcCardW - 2 * CcPad) * _ccK;
            b.Cover.Size = new Vector2(img, img);
            b.Cover.Offset = new Vector3(CcPad * _ccK, CcPad * _ccK, 0);
            b.Cover.Clip = G.C.CreateGeometricClip(RoundRect(img, img, CcImgRadius * _ccK));
        }
        _ccBack!.Dpi = Chrome.Dpi;
        _ccBack.BlurDip = 4 * _ccK;
        _ccBack.Build(vw, vh);
        _ccBack.SetStrip(0, vw);
        CcPaintShadows();
        CcPaintTitle();
    }

    private CompositionRoundedRectangleGeometry RoundRect(float w, float h, float r)
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

    /// <summary>投影（静止 shadow-lg / 焦点更大更深）与焦点描边：所有卡共用，尺寸变了才重画。</summary>
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

    /// <summary>滑条下面：焦点板块名（24 px 粗体）+ 一行等宽小字（第二行 • 摘要）。换焦点时直接换，不做动画。</summary>
    private void CcPaintLabels()
    {
        var b = _ccBlocks[_ccFocus];
        float raster = _ccK * Chrome.Dpi;
        float maxW = (float)View.Width / _ccK - 64;
        using var name = TextKit.Layout(G.Device, b.Name, TextKit.Ui(700), 24, maxW, CanvasHorizontalAlignment.Center, 0, 32);
        string line = string.IsNullOrEmpty(b.Summary) ? b.Second : $"{b.Second} • {b.Summary}";
        using var mono = TextKit.Layout(G.Device, line, new FontSpec(CcMonoFont, 400), 12, maxW, CanvasHorizontalAlignment.Center, 0, 16);
        CcSet(_ccLabel, CcDraw(maxW, CcLabelH, raster, ds =>
        {
            ds.DrawTextLayout(name, 0, 0, CcText);
            ds.DrawTextLayout(mono, 0, 32 + 4, Gfx.WithAlpha(CcText, 0.5));
        }));
        _ccLabel.Size = new Vector2(maxW * _ccK, CcLabelH * _ccK);
        _ccLabel.Offset = new Vector3(32 * _ccK, CcTop + (_ccH + CcRowPadY + CcLabelTop) * _ccK, 0);
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

    private int CoverPx => (int)Math.Ceiling((CcCardW - 2 * CcPad) * CcRaster);

    private void CcSetCover(CcBlock b)
    {
        if (b.CoverPath == null) { CcReleaseCover(b); return; }
        if (b.CoverRef != null) return;
        // 竖封面按宽解码，UniformToFill 居中裁成方的
        var s = Images.AcquireReuse(b.CoverPath, CoverPx, CoverPx * 3 / 2);
        if (s == null) return;
        b.CoverRef = s;
        b.Cover.Brush = CoverBrush(s);
    }

    private CompositionSurfaceBrush CoverBrush(ICompositionSurface s)
    {
        var brush = G.C.CreateSurfaceBrush(s);
        brush.Stretch = CompositionStretch.UniformToFill;
        brush.HorizontalAlignmentRatio = 0.5f;
        brush.VerticalAlignmentRatio = 0.5f;
        return brush;
    }

    private void CcReleaseCover(CcBlock b)
    {
        if (b.CoverRef == null) return;
        b.Cover.Brush = null;
        Images.Release(b.CoverRef);
        b.CoverRef = null;
    }

    // ---------------- 动态背景（folia「隐现」，铺满全屏） ----------------

    private void CcBackStart()
    {
        if (_ccBack == null) return;
        var cover = _ccBlocks[0].CoverPath;
        _ccBack.SetCover(cover, a => _dq.TryEnqueue(() => { a(); CcBackLoop(); }));
        CcBackRun();
    }

    /// <summary>「动态背景」关掉或降低动态效果时只画一帧静止的；窗口不在前台也停。</summary>
    private void CcBackRun()
    {
        if (_ccBack == null) return;
        bool visible = _cc?.IsVisible == true && (_ccOpen || MorphBusy);
        _ccBack.Run(visible && _backdropOn && !Reduced && _windowActive);
        CcBackLoop();
    }

    private void CcBackStop()
    {
        _ccBack?.Run(false);
    }

    private void CcBackLoop()
    {
        if (_ccBackLoop || _ccBack == null || !_ccBack.NeedsFrames) return;
        _ccBackLoop = true;
        _ccBackLast = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnCcBackFrame;
    }

    private void OnCcBackFrame(object? sender, object e)
    {
        try
        {
            long now = NowMs;
            double dt = _ccBackLast == 0 ? 16.7 : Math.Min(100, now - _ccBackLast);
            _ccBackLast = now;
            bool shown = _cc?.IsVisible == true;
            if (shown) _ccBack!.FrameNow(dt);
            if (!shown || !_ccBack!.NeedsFrames)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnCcBackFrame;
                _ccBackLoop = false;
            }
        }
        catch (Exception ex) { Log.Error("control center backdrop", ex); }
    }

    // ---------------- 移形换影 ----------------

    private readonly record struct Box(float X, float Y, float W, float H)
    {
        public Vector3 Pos => new(X, Y, 0);
        public Vector2 Size => new(W, H);
        public Vector2 Center => new(X + W / 2, Y + H / 2);
    }

    /// <summary>游戏库卡此刻（居中、1.2×、上浮 6 px）的外框 / 封面 / 标题行在屏幕上的位置。</summary>
    private (Box frame, Box cover, Box title) HomeCardBoxes()
    {
        float s = CcMaxScale, k = _ccK;
        float w = CcCardW * k * s, h = _ccH * k * s;
        float cx = (float)View.Width / 2, cy = CcTop + _ccH * k / 2 - 6 * k;
        var frame = new Box(cx - w / 2, cy - h / 2, w, h);
        float img = (CcCardW - 2 * CcPad) * k * s, pad = CcPad * k * s;
        var cover = new Box(frame.X + pad, frame.Y + pad, img, img);
        var title = new Box(frame.X + pad, frame.Y + (CcPad + (CcCardW - 2 * CcPad) + CcPad + CcTextTop) * k * s, img, CcTitleLh * k * s);
        return (frame, cover, title);
    }

    /// <summary>墙上一张卡此刻在屏幕上的矩形（与 ClickAt 同一套换算）。</summary>
    private Box TileScreenBox(Tile t, long now)
    {
        var r = t.CurrentRect(now);
        var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
        var off = SplitOffsets(geo.Width, View.Scale);
        double s = View.Scale;
        double baseX = View.LineX - _center.X * s + (t.Side == 0 ? off.Left : off.Right);
        double baseY = View.Height / 2 - _center.Y * s;
        return new Box((float)(baseX + r.X * s), (float)(baseY + r.Y * s), (float)(r.Width * s), (float)(r.Height * s));
    }

    private bool OnScreen(Box b) => b.X + b.W > 0 && b.X < View.Width && b.Y + b.H > 0 && b.Y < View.Height && b.W > 4;

    /// <summary>墙卡的标题大致所在（电影字幕：靠下居中）。</summary>
    private static Box TileTitleBox(Box f) => new(f.X + f.W * 0.1f, f.Y + f.H * 0.66f, f.W * 0.8f, MathF.Max(18, f.H * 0.08f));

    /// <summary>与墙卡同一张封面：竖卡用竖封面，近方的用大方图，横的用背景图。</summary>
    private static string? TileArtPath(LibraryItem? it, Box f)
    {
        if (it?.Art is not { } art) return null;
        float ratio = f.W / Math.Max(1, f.H);
        char[] order = ratio < 0.8f ? new[] { 'q', 'p', 'v', 'x' } : ratio < 1.3f ? new[] { 'x', 's', 'q', 'p' } : new[] { 'h', 'x', 'q', 'p' };
        foreach (var k in order) if (art.Has(k)) return art.File(k);
        return null;
    }

    /// <summary>屏上的墙卡（Sync 之后调用）。</summary>
    private List<(Tile tile, Box box)> VisibleTiles(long now)
    {
        var list = new List<(Tile, Box)>();
        foreach (var t in _tiles.Values)
        {
            if (!HasContent(t.Slot)) continue;
            var b = TileScreenBox(t, now);
            if (OnScreen(b)) list.Add((t, b));
        }
        return list;
    }

    /// <summary>Apple / SwiftUI 签名的弹簧（response、bounce），按 0..1 归一化时间采样。</summary>
    private static Func<double, double> Spring(double responseS, double bounce, double durS)
    {
        double z = 1 - bounce, w0 = 2 * Math.PI / responseS, wd = w0 * Math.Sqrt(1 - z * z);
        return u =>
        {
            if (u >= 1) return 1;
            double t = u * durS;
            return 1 - Math.Exp(-z * w0 * t) * (Math.Cos(wd * t) + z * w0 / wd * Math.Sin(wd * t));
        };
    }

    private const double SpringDurMs = 720;

    /// <summary>形变合成层的三件套：外框（底色 + 描边 + 投影）、封面（两张图交叉淡化 + 模糊 + 转角）、标题（两行字交叉淡化 + 模糊）。</summary>
    private sealed class Composite
    {
        public LayerVisual Frame = null!;
        public ShapeVisual FrameShape = null!;
        public CompositionRoundedRectangleGeometry FrameGeo = null!;
        public ContainerVisual Cover = null!;
        public CompositionRoundedRectangleGeometry CoverGeo = null!;
        public SpriteVisual CoverA = null!, CoverB = null!;
        public CompositionEffectBrush CoverBlurA = null!, CoverBlurB = null!;
        public ContainerVisual Title = null!;
        public SpriteVisual TitleA = null!, TitleB = null!;
        public CompositionEffectBrush TitleBlurA = null!, TitleBlurB = null!;
        public List<LoadedImageSurface> Images = new();
        public List<IDisposable> Surfaces = new();
    }

    private Composite? _composite;

    private CompositionEffectBrush Blurred(CompositionBrush source)
    {
        var fx = new GaussianBlurEffect { Name = "b", BlurAmount = 0, BorderMode = EffectBorderMode.Soft, Source = new CompositionEffectSourceParameter("s") };
        var brush = G.C.CreateEffectFactory(fx, new[] { "b.BlurAmount" }).CreateBrush();
        brush.SetSourceParameter("s", source);
        return brush;
    }

    private (CompositionDrawingSurface surface, Vector2 size) TitleText(string text, float sizeDip)
    {
        float raster = Chrome.Dpi;
        using var l = TextKit.Layout(G.Device, text, TextKit.Ui(700), sizeDip, 100000, CanvasHorizontalAlignment.Left);
        var bb = l.LayoutBounds;
        float w = (float)bb.Width + 8, h = (float)bb.Height + 8;
        var s = CcDraw(w, h, raster, ds =>
        {
            using var cl = new CanvasCommandList(ds);
            using (var c = cl.CreateDrawingSession()) c.DrawTextLayout(l, 2 - (float)bb.X, 3 - (float)bb.Y, Colors.Black);
            using var sh = new ShadowEffect { Source = cl, BlurAmount = 1, ShadowColor = Gfx.Rgba(0, 0, 0, 0.55) };
            ds.DrawImage(sh);
            ds.DrawTextLayout(l, 2 - (float)bb.X, 2 - (float)bb.Y, CcText);
        });
        return (s, new Vector2(w, h));
    }

    private Composite BuildComposite(string? coverA, string? coverB, string titleA, string titleB, float titleSize)
    {
        var c = new Composite();
        // 外框：rounded-2xl、border、shadow 0 10 28 rgba(0,0,0,.3)、底色 = 主题底色
        c.FrameGeo = RoundRect(1, 1, 16 * _ccK);
        var shape = G.C.CreateSpriteShape(c.FrameGeo);
        shape.FillBrush = G.Solid(Theme.Bg);
        shape.StrokeBrush = G.Solid(Gfx.WithAlpha(CcText, 0.10));
        shape.StrokeThickness = 1;
        c.FrameShape = G.C.CreateShapeVisual();
        c.FrameShape.Shapes.Add(shape);
        c.FrameShape.RelativeSizeAdjustment = Vector2.One;
        c.Frame = G.C.CreateLayerVisual();
        var sh = G.C.CreateDropShadow();
        sh.Offset = new Vector3(0, 10, 0); sh.BlurRadius = 28; sh.Color = Gfx.Rgba(0, 0, 0, 0.3);
        c.Frame.Shadow = sh;
        c.Frame.Children.InsertAtTop(c.FrameShape);
        // 封面：rounded-xl 裁切，两张图叠着交叉淡化，各自带可动画的模糊
        c.Cover = G.C.CreateContainerVisual();
        c.CoverGeo = RoundRect(1, 1, 12 * _ccK);
        c.Cover.Clip = G.C.CreateGeometricClip(c.CoverGeo);
        SpriteVisual Img(string? path, out CompositionEffectBrush blur)
        {
            CompositionBrush src;
            var s = path != null ? Images.AcquireReuse(path, CoverPx, CoverPx * 3 / 2) : null;
            if (s != null) { c.Images.Add(s); src = CoverBrush(s); }
            else src = G.Solid(Gfx.Rgba(0x27, 0x27, 0x2a, 0.4));
            blur = Blurred(src);
            var v = G.Sprite(blur);
            return v;
        }
        c.CoverA = Img(coverA, out c.CoverBlurA);
        c.CoverB = Img(coverB, out c.CoverBlurB);
        c.CoverB.Opacity = 0;
        c.Cover.Children.InsertAtTop(c.CoverA);
        c.Cover.Children.InsertAtTop(c.CoverB);
        // 标题：两行字叠在同一处，纯交叉淡化
        c.Title = G.C.CreateContainerVisual();
        SpriteVisual Text(string text, out CompositionEffectBrush blur)
        {
            var (s, size) = TitleText(text, titleSize);
            c.Surfaces.Add(s);
            var src = G.C.CreateSurfaceBrush(s);
            src.Stretch = CompositionStretch.Fill;
            blur = Blurred(src);
            var v = G.Sprite(blur, relative: false);
            v.Size = size;
            v.Offset = new Vector3(-2, -2, 0);
            return v;
        }
        c.TitleA = Text(titleA, out c.TitleBlurA);
        c.TitleB = Text(titleB, out c.TitleBlurB);
        c.TitleB.Opacity = 0;
        c.Title.Children.InsertAtTop(c.TitleA);
        c.Title.Children.InsertAtTop(c.TitleB);
        foreach (var v in new Visual[] { c.Frame, c.Cover, c.Title })
        {
            _fx!.Children.InsertAtTop(v);
            // 等比缩放 / 转角都绕盒子中心
            var e = G.C.CreateExpressionAnimation("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0)");
            v.StartAnimation("CenterPoint", e);
        }
        return c;
    }

    private void DisposeComposite()
    {
        if (_composite is not { } c) return;
        _composite = null;
        foreach (var v in new Visual[] { c.Frame, c.Cover, c.Title }) _fx?.Children.Remove(v);
        foreach (var s in c.Images) Images.Release(s);
        foreach (var s in c.Surfaces) s.Dispose();
        foreach (var b in new CompositionBrush[] { c.CoverBlurA, c.CoverBlurB, c.TitleBlurA, c.TitleBlurB }) b.Dispose();
        c.Frame.Dispose(); c.FrameShape.Dispose(); c.Cover.Dispose(); c.Title.Dispose();
    }

    /// <summary>盒子（位置 + 尺寸）按弹簧从 a 走到 b；外框 / 封面的圆角裁切几何跟着同一条曲线。</summary>
    private void FlyBox(Visual v, Box a, Box b, Func<double, double> curve, CompositionRoundedRectangleGeometry? geo = null)
    {
        v.Offset = a.Pos; v.Size = a.Size;
        v.StartAnimation("Offset", G.SampledV3(a.Pos, b.Pos, SpringDurMs, curve));
        v.StartAnimation("Size", G.SampledV2(a.Size, b.Size, SpringDurMs, curve));
        if (geo != null)
        {
            geo.Size = a.Size;
            geo.StartAnimation("Size", G.SampledV2(a.Size, b.Size, SpringDurMs, curve));
        }
    }

    private ScalarKeyFrameAnimation Keys(double ms, params (float at, float value, Bezier? ease)[] keys)
    {
        var a = G.C.CreateScalarKeyFrameAnimation();
        foreach (var (at, value, ease) in keys) a.InsertKeyFrame(at, value, ease is { } e ? G.Ease(e) : G.Linear);
        a.Duration = Gfx.Ms(ms);
        return a;
    }

    private ScalarKeyFrameAnimation Delayed(ScalarKeyFrameAnimation a, double delayMs)
    {
        a.DelayTime = Gfx.Ms(delayMs);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        return a;
    }

    private Vector3KeyFrameAnimation ScaleKeys(double ms, Func<double, double> curve, float from, float to)
        => G.SampledV3(new Vector3(from, from, 1), new Vector3(to, to, 1), ms, curve);

    /// <summary>
    /// 出场（墙 → 控制中心，folia 返回首页）：墙的底 0.62 s 淡出，露出底下的控制中心；hero 卡的三件套弹簧飞回游戏库卡
    /// （response 0.36、bounce 0.2），透明度 1 / 1 / 0 在 0 / 0.72 / 1 处（0.5 s），封面和标题最后糊到 8 px；
    /// 其余屏上的卡沿径向四散。返回 false = 没有可飞的 hero，交给普通淡出。
    /// </summary>
    private bool MorphExit()
    {
        Sync();
        long now = NowMs;
        var tiles = VisibleTiles(now);
        if (tiles.Count == 0) return false;
        Vector2 mid = new((float)View.Width / 2, (float)View.Height / 2);
        var hero = FocusKey is { } fk && tiles.FirstOrDefault(x => x.tile.Slot.Key == fk) is { tile: not null } f ? f
            : tiles.OrderBy(x => Vector2.Distance(x.box.Center, mid)).First();
        var heroItem = ItemFor(hero.tile.Slot) is { } hid ? Lookup(hid) : null;
        var (homeFrame, homeCover, homeTitle) = HomeCardBoxes();
        var heroTitle = TileTitleBox(hero.box);

        // 背景板（墙的底）淡出
        _cam.StopAnimation("fade");
        _cam.StartAnimation("fade", G.Scalar(1, 0, ExitBackdropMs, ExitEase));

        // 其余的卡：幽灵四散
        float k = _ccK, s = (float)View.Scale;
        float reach = MathF.Min(MathF.Min((float)View.Width, (float)View.Height) / k * 0.3f, 220) * k;
        float maxDist = MathF.Max(1, tiles.Max(x => Vector2.Distance(x.box.Center, mid)));
        double longest = ExitBackdropMs;
        foreach (var (t, b) in tiles)
        {
            if (t == hero.tile) continue;
            var d = b.Center - mid;
            float dist = d.Length();
            var dir = dist > 1 ? d / dist : new Vector2(0, -1);
            uint seed = (uint)(((int)MathF.Round(b.X) * 73856093) ^ ((int)MathF.Round(b.Y) * 19349663));
            double jitter = ((seed % 1000) / 1000.0 - 0.5) * 0.08;
            double n = Math.Min(dist / maxDist, 1);
            double dur = (0.46 + n * 0.3) * 1000, delay = Math.Max(0, (0.04 + n * 0.26 + jitter) * 1000);
            float rot = (seed % 2 == 0 ? 1 : -1) * (1.8f + (seed % 3) * 0.8f);
            t.MorphScatter(dir * reach / s, rot, dur, delay);
            longest = Math.Max(longest, dur + delay);
        }

        // hero 三件套飞回游戏库卡
        hero.tile.MorphHide();
        var c = _composite = BuildComposite(TileArtPath(heroItem, hero.box), _ccBlocks[0].CoverPath, heroItem?.Title ?? "", "游戏库", 14 * k * CcMaxScale);
        var spring = Spring(0.36, 0.2, SpringDurMs / 1000);
        FlyBox(c.Frame, hero.box, homeFrame, spring, c.FrameGeo);
        FlyBox(c.Cover, hero.box, homeCover, spring, c.CoverGeo);
        FlyBox(c.Title, heroTitle, homeTitle, spring);
        // 落点必须与游戏库卡真实的外框 / 封面 / 标题完全重合（folia 原版收在 0.985 / 1.6° / 8 px 模糊，落定时会错开一截）：
        // 外框不缩，封面的转角与模糊在落定前回到 0，只有标题保留最后的模糊淡出
        c.Frame.Scale = Vector3.One;
        var fadeOut = new Bezier(0f, 0f, 0.58f, 1f);
        // --morph-hold（测试用）：合成层落定后不淡出，截图对比落点与卡片是否重合
        bool hold = Program.Args.Contains("--morph-hold");
        if (!hold)
            foreach (var v in new Visual[] { c.Frame, c.Cover, c.Title })
                v.StartAnimation("Opacity", Keys(ExitMs, (0, 1, null), (0.72f, 1, fadeOut), (1, 0, fadeOut)));
        c.Cover.StartAnimation("RotationAngleInDegrees", Keys(ExitMs * 0.72, (0, 0, null), (0.45f, 5, EaseCss), (1, 0, EaseCss)));
        foreach (var b in new[] { c.CoverBlurA, c.CoverBlurB })
            b.Properties.StartAnimation("b.BlurAmount", Keys(ExitMs * 0.72, (0, 0, null), (0.5f, 1.5f, EaseCss), (1, 0, EaseCss)));
        foreach (var b in new[] { c.TitleBlurA, c.TitleBlurB })
            b.Properties.StartAnimation("b.BlurAmount", Keys(ExitMs, (0, 0, null), (0.5f, 1, EaseCss), (1, 8, EaseCss)));
        Log.Info($"morph exit target: frame {homeFrame} cover {homeCover} title {homeTitle} (k {_ccK}, dpi {Chrome.Dpi})");
        // 落点的封面 / 标题 0.35 s 淡进来
        c.CoverB.StartAnimation("Opacity", Keys(ExitMs * 0.7, (0, 0, null), (1, 1, fadeOut)));
        c.TitleB.StartAnimation("Opacity", Keys(ExitMs * 0.7, (0, 0, null), (1, 1, fadeOut)));
        c.TitleA.StartAnimation("Opacity", Keys(ExitMs * 0.7, (0, 1, null), (1, 0, fadeOut)));
        c.CoverA.StartAnimation("Opacity", Keys(ExitMs * 0.7, (0, 1, null), (1, 0, fadeOut)));

        int v0 = _ccVersion;
        BeginMorph(Math.Max(longest, SpringDurMs) + 30 + (hold ? 2500 : 0), () =>
        {
            DisposeComposite();
            foreach (var t in _tiles.Values) t.Cancel();
            if (_ccOpen && v0 == _ccVersion) _root.IsVisible = false;
            _cam.StopAnimation("fade");
            _cam.InsertScalar("fade", 1);
        });
        Log.Info($"morph exit: hero {heroItem?.Title}, {tiles.Count - 1} ghosts");
        return true;
    }

    /// <summary>
    /// 进场（控制中心 → 墙，folia 打开集合）：控制中心 0.25 s 淡出、墙的底 0.62 s 淡入；游戏库卡的三件套弹簧飞到墙上
    /// hero 卡（response 0.34、bounce 0.16），外框 0.97 → 1，封面 0.96 / 2.4° / 6 px 模糊落定，标题 3.5 px 模糊落定、
    /// 中途交叉淡化成 hero 的标题；落定后合成层 0.18 s 淡出、hero 揭晓。其余屏上的卡沿径向就位。
    /// </summary>
    private bool MorphEnter()
    {
        Sync();
        long now = NowMs;
        var tiles = VisibleTiles(now);
        if (tiles.Count == 0) return false;
        Vector2 mid = new((float)View.Width / 2, (float)View.Height / 2);
        // hero：游戏库卡上的那款（墙上最后停过焦点的游戏）在屏上就用它，否则用墙上的焦点卡，再不行用离屏幕中心最近的
        string? recentId = SelectedGame()?.Id;
        var hero = tiles.FirstOrDefault(x => recentId != null && ItemFor(x.tile.Slot) == recentId);
        if (hero.tile == null && FocusKey is { } fk) hero = tiles.FirstOrDefault(x => x.tile.Slot.Key == fk);
        if (hero.tile == null) hero = tiles.OrderBy(x => Vector2.Distance(x.box.Center, mid)).First();
        if (_railFocused) ExitRail(false);
        Focus(hero.tile.Slot.Key, false);
        var heroItem = ItemFor(hero.tile.Slot) is { } hid ? Lookup(hid) : null;
        var (homeFrame, homeCover, homeTitle) = HomeCardBoxes();
        var heroTitle = TileTitleBox(hero.box);

        // 首页淡出、墙的底淡入
        _cc!.StartAnimation("Opacity", G.Scalar(1, 0, HomeFadeMs, new Bezier(0.42f, 0f, 0.58f, 1f)));
        _cc.Opacity = 0;
        _root.Opacity = 1;
        _cam.StopAnimation("fade");
        _cam.InsertScalar("fade", 0);
        _cam.StartAnimation("fade", G.Scalar(0, 1, EnterMs, EnterBackdropEase));

        // 其余的卡：径向就位
        float k = _ccK, s = (float)View.Scale;
        float reach = MathF.Min(MathF.Min((float)View.Width, (float)View.Height) / k * 0.32f, 260) * k;
        float spacing = (float)(Engine.Geometry.Pitch * 2 * View.Scale);
        foreach (var (t, b) in tiles)
        {
            if (t == hero.tile) continue;
            var d = b.Center - hero.box.Center;
            float dist = d.Length();
            var dir = dist > 1 ? d / dist : new Vector2(0, -1);
            double nn = Math.Min(dist / (spacing * 14), 1);
            double eased = nn * nn * (3 - 2 * nn);
            double seed = Seed(ItemFor(t.Slot) ?? t.Slot.Key.ToString());
            double delay = Math.Min(0.04 + eased * 0.34 + seed * 0.04, 0.32) * 1000;
            t.MorphFlyIn(dir * reach / s, delay);
        }

        // 游戏库卡的三件套飞到 hero
        hero.tile.MorphHide();
        hero.tile.MorphReveal(HeroRevealMs);
        var c = _composite = BuildComposite(_ccBlocks[0].CoverPath, TileArtPath(heroItem, hero.box), "游戏库", heroItem?.Title ?? "", 14 * k * CcMaxScale);
        var spring = Spring(0.34, 0.16, SpringDurMs / 1000);
        FlyBox(c.Frame, homeFrame, hero.box, spring, c.FrameGeo);
        FlyBox(c.Cover, homeCover, hero.box, spring, c.CoverGeo);
        FlyBox(c.Title, homeTitle, heroTitle, spring);
        c.Frame.StartAnimation("Scale", ScaleKeys(SpringDurMs, spring, 0.97f, 1));
        c.Cover.StartAnimation("Scale", ScaleKeys(SpringDurMs, spring, 0.96f, 1));
        var rot = G.C.CreateScalarKeyFrameAnimation();
        for (int i = 0; i <= 24; i++) rot.InsertKeyFrame(i / 24f, (float)(2.4 * (1 - spring(i / 24.0))), G.Linear);
        rot.Duration = Gfx.Ms(SpringDurMs);
        c.Cover.StartAnimation("RotationAngleInDegrees", rot);
        ScalarKeyFrameAnimation Sharpen(float from)
        {
            var a = G.C.CreateScalarKeyFrameAnimation();
            for (int i = 0; i <= 24; i++) a.InsertKeyFrame(i / 24f, (float)(from * (1 - spring(i / 24.0))), G.Linear);
            a.Duration = Gfx.Ms(SpringDurMs);
            return a;
        }
        c.CoverBlurA.Properties.StartAnimation("b.BlurAmount", Sharpen(6));
        c.CoverBlurB.Properties.StartAnimation("b.BlurAmount", Sharpen(6));
        c.TitleBlurA.Properties.StartAnimation("b.BlurAmount", Sharpen(3.5f));
        c.TitleBlurB.Properties.StartAnimation("b.BlurAmount", Sharpen(3.5f));
        // 落定后封面 / 标题交叉淡化成 hero 的内容（0.28 s ease），再整体 0.18 s 淡出
        c.CoverB.StartAnimation("Opacity", Delayed(Keys(CrossfadeMs, (0, 0, null), (1, 1, EaseCss)), SettleMs));
        c.CoverA.StartAnimation("Opacity", Delayed(Keys(CrossfadeMs, (0, 1, null), (1, 0, EaseCss)), SettleMs));
        c.TitleB.StartAnimation("Opacity", Delayed(Keys(CrossfadeMs, (0, 0, null), (1, 1, EaseCss)), SettleMs));
        c.TitleA.StartAnimation("Opacity", Delayed(Keys(CrossfadeMs, (0, 1, null), (1, 0, EaseCss)), SettleMs));
        foreach (var v in new Visual[] { c.Frame, c.Cover, c.Title })
            v.StartAnimation("Opacity", Delayed(Keys(FadeMs, (0, 1, null), (1, 0, EaseOutCss)), FadeAtMs));

        WallBusy(EnterMs);
        BeginMorph(Math.Max(FadeAtMs + FadeMs, EnterMs) + 30, () =>
        {
            DisposeComposite();
            _cam.StopAnimation("fade");
            _cam.InsertScalar("fade", 1);
            if (!_ccOpen) HideControlCenter();
            UpdateBackdropRunning();
        }, () =>
        {
            // 快进：卡片直接就位
            foreach (var t in _tiles.Values) t.Cancel();
        });
        UpdateBackdropRunning();
        Log.Info($"morph enter: hero {heroItem?.Title}, {tiles.Count - 1} fly-ins");
        return true;
    }

    private static double Seed(string key)
    {
        uint h = 0;
        foreach (char ch in key) h = h * 31 + ch;
        return h % 1000 / 1000.0;
    }

    private Action? _morphFastForward;

    /// <summary>形变开始：到点自动收尾；途中有输入就快进（直接收尾）。</summary>
    private void BeginMorph(double ms, Action finish, Action? fastForward = null)
    {
        FinishMorph();
        _morphFinish = finish;
        _morphFastForward = fastForward;
        _morphTimer = After(ms, () => { _morphFastForward = null; FinishMorph(); });
    }

    private void FinishMorph()
    {
        var f = _morphFinish;
        if (f == null) return;
        _morphFinish = null;
        _morphTimer?.Stop();
        if (_morphTimer != null) _pendingTimers.Remove(_morphTimer);
        _morphTimer = null;
        var ff = _morphFastForward;
        _morphFastForward = null;
        try { ff?.Invoke(); f(); }
        catch (Exception ex) { Log.Error("morph finish", ex); }
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
                CcBackRun();
            }
            catch (Exception ex) { Log.Error("control center timer", ex); }
        };
        return t;
    }

    /// <summary>墙上最后停过焦点的游戏（焦点停在合集、竖条上时不变）。每次换焦点只记一个 id。</summary>
    private string? _ccGameId;

    private void NoteFocusedGame(SlotKey? key)
    {
        if (key is { } k && Engine.Geometry.SlotByKey(k) is { } s && ItemFor(s) is { } id && Lookup(id)?.Kind == ItemKind.Game) _ccGameId = id;
    }

    private LibraryItem? SelectedGame() => (_ccGameId != null ? Lookup(_ccGameId) : null) ?? RecentGame();

    private LibraryItem? RecentGame() =>
        _lib.Games.Where(g => g.Kind == ItemKind.Game && g.LastPlayed is > 0).MaxBy(g => g.LastPlayed);

    private void CcReadData()
    {
        // 游戏库：游戏数 + 墙上最后停过焦点的那款游戏的竖封面（还没停过就用最近玩过的）
        var games = _lib.Games.Where(g => g.Kind == ItemKind.Game).ToList();
        var recent = SelectedGame();
        var lib = _ccBlocks[0];
        lib.Second = $"{games.Count} 款游戏";
        lib.Summary = recent != null ? $"选中的游戏：{recent.Title}" : "还没有游戏";
        string? cover = null;
        if (recent?.Art is { } art)
            foreach (var k in new[] { 'q', 'p', 'v', 'x', 's' })   // q 在前：墙上竖卡用的就是它，多半已经解码过
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
