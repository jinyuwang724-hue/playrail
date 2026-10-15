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
    // 进场时间线（folia CollectionMorphOverlay）：探针两拍（70 ms 间隔）锁定落点 ≈ 140 ms → +140 ms 交叉淡化（0.28 s）→ +240 ms 淡出（0.18 s）
    private const double CrossfadeMs = 280, FadeMs = 180, SettleMs = 280, FadeAtMs = 520, HeroRevealDelayMs = 120, WallFadeMs = 300;

    private sealed class CcBlock
    {
        public required string Name;
        public required string Icon;
        public string Second = "", Summary = "", Reading = "";
        public bool HasReading;
        public string? CoverPath;
        public string Drawn = "";
        public ContainerVisual El = null!;
        /// <summary>卡的全部内容（El 的不透明度绑着表达式，形变交接时藏 / 显卡片用这一层）。</summary>
        public ContainerVisual Body = null!;
        /// <summary>Body 外面再包一层：形变时藏 / 显卡片动这一层，Body 本身拿来做实时画面（VisualSurface 不受祖先透明度影响）。</summary>
        public ContainerVisual Gate = null!;
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

    /// <summary>移形换影途中的任何输入：快进到结尾（返回 true 表示这次输入被吃掉）。</summary>
    private bool MorphIntercept()
    {
        if (!MorphBusy) return false;
        RushMorph();
        return true;
    }

    /// <summary>≡：墙上 = 打开控制中心（出场形变），控制中心里 = 关掉。</summary>
    public void ToggleControlCenter()
    {
        MarkInput();
        if (MorphIntercept()) return;
        if (_ccOpen) CloseControlCenter(); else OpenControlCenter();
    }

    /// <summary>测试用：游戏库卡封面此刻的状态（有没有图、解码好没有、可见不可见）。</summary>
    internal (bool ok, string info) CcCoverState()
    {
        if (_ccBlocks.Count == 0) return (false, "no blocks");
        var b = _ccBlocks[0];
        bool hasBrush = b.Cover.Brush is CompositionSurfaceBrush { Surface: not null };
        bool loaded = b.CoverRef != null && Images.IsLoaded(b.CoverRef);
        bool ok = b.CoverPath == null || (hasBrush && loaded && b.Cover.Opacity > 0.99f && b.Gate.Opacity > 0.99f && b.Cover.IsVisible);
        return (ok, $"path={Path.GetFileName(Path.GetDirectoryName(b.CoverPath ?? ""))}/{Path.GetFileName(b.CoverPath ?? "-")} brush={hasBrush} loaded={loaded} coverOp={b.Cover.Opacity:F2} gateOp={b.Gate.Opacity:F2} open={_ccOpen} morph={MorphBusy}");
    }

    /// <summary>启动后空闲时把控制中心的视觉先建好、卡面先画好（藏着），按 ≡ 时只刷新读数，第一帧不卡。</summary>
    public void PrewarmControlCenter()
    {
        if (_ccOpen || _stack.Count == 0) return;
        long t0 = NowMs;
        EnsureControlCenterVisuals();
        CcMeasure();
        CcReadData();
        foreach (var b in _ccBlocks) CcPaintCard(b);
        CcPaintLabels();
        Log.Info($"control center prewarmed ({NowMs - t0}ms)");
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
        foreach (var b in _ccBlocks) CcPaintCard(b);   // 内容没变的卡不重画（启动后已经预热画过一遍）
        // 关的时候封面引用已经还给图片缓存（CcReleaseCover）；卡面没变就不会重画，所以这里必须显式重新拿封面
        if (!Program.Args.Contains("--no-cover-fix")) foreach (var b in _ccBlocks) CcSetCover(b);   // --no-cover-fix：测试用，复现旧 bug
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

        if (MorphSlow != 1) G.C.GlobalPlaybackRate = (float)(1 / MorphSlow);   // --slow=N（测试）
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
            b.Body = G.C.CreateContainerVisual();
            b.Gate = G.C.CreateContainerVisual();
            b.El.Children.InsertAtTop(b.Gate);
            b.Gate.Children.InsertAtTop(b.Body);
            foreach (var v in new Visual[] { b.ShadowBase, b.ShadowFocus, b.Face, b.Cover, b.Ring }) b.Body.Children.InsertAtTop(v);
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
        // 解码上限取正方形 2× 封面边长：铺满裁剪时短边也够封面边长（横图按宽解码会只剩一半高度，放大发糊）
        var s = Images.AcquireReuse(b.CoverPath, CoverPx * 2, CoverPx * 2);
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
        // 控制中心还没露出来（被墙和背景板盖着）：直接换成这款游戏的颜色，不做渐变 —— 渐变期间每帧都要在 UI 线程上重画
        // 整张着色器 + 模糊，正好压在起飞那几帧上（实测每次起飞都掉一帧）
        _ccBack.InstantColors = true;
        _ccBack.SetCover(cover, a => _dq.TryEnqueue(() => { a(); CcBackLoop(); }));
        CcBackRun();
    }

    /// <summary>「动态背景」关掉或降低动态效果时只画一帧静止的；窗口不在前台也停。</summary>
    private void CcBackRun()
    {
        if (_ccBack == null) return;
        bool visible = _cc?.IsVisible == true && (_ccOpen || MorphBusy);
        // 形变期间动态背景停在一帧（它在背景板下面，看不出来）：起飞那几帧 GPU 留给飞的图
        bool pauseForMorph = MorphBusy && !Program.Args.Contains("--back-during-morph");
        _ccBack.Run(visible && _backdropOn && !Reduced && _windowActive && !pauseForMorph);
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

    /// <summary>焦点环画在卡外的那一圈（世界坐标）：形变用的实时画面和落点盒子都按它外扩。</summary>
    private const float RingMargin = 6;

    /// <summary>
    /// 墙卡此刻**看得见**的样子在屏幕上的盒子：卡的矩形四边外扩 RingMargin（焦点环），有焦点时再按焦点放大
    /// （透视层 scale = 1 + 16 / 尺寸，绕卡中心）。形变落点 / 起点用它，画面才和真卡逐像素对得上。
    /// </summary>
    private Box HeroBox(Tile t, long now, float margin = RingMargin)
    {
        var b = TileScreenBox(t, now);
        double s = View.Scale;
        var r = t.CurrentRect(now);
        double sx = t.Focused && !t.Expanded ? 1 + 16 / Math.Max(1, r.Width) : 1;
        double sy = t.Focused && !t.Expanded ? 1 + 16 / Math.Max(1, r.Height) : 1;
        double hw = (r.Width / 2 + margin) * sx * s, hh = (r.Height / 2 + margin) * sy * s;
        var c = b.Center;
        return new Box((float)(c.X - hw), (float)(c.Y - hh), (float)(hw * 2), (float)(hh * 2));
    }

    private bool OnScreen(Box b) => b.X + b.W > 0 && b.X < View.Width && b.Y + b.H > 0 && b.Y < View.Height && b.W > 4;

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

    /// <summary>位置 / 尺寸的飞行：定长 ease-out，到点正好停在目标上（不用会过冲的弹簧）；落定后 0.1 s 交接给真卡片。</summary>
    private const double FlyMs = 500, HandoffMs = 100;
    // 从静止起步、轻轻落定（标准 ease-in-out 曲线）：之前的 ease-out 第一帧就是最高速，图像是从墙卡上「弹」出去的
    private static readonly Bezier FlyEase = new(0.4f, 0f, 0.2f, 1f);
    private static readonly Func<double, double> FlyCurve = t => FlyEase.Ease(t);

    /// <summary>
    /// 飞的那张图在动之前先停这么久（两帧）：让它先被合成出来，再在同一条时间轴上藏真卡 ——
    /// 真卡的显隐都是合成线程上的关键帧，和飞行同一批提交，不会出现「两份都不在」或「两份都在」的那一帧。
    /// </summary>
    private const double HandMs = 33;

    /// <summary>
    /// 飞的东西：一张**整图**按等比缩放平移（内容永远不被拉伸、也不会每帧重新裁），外面一个圆角裁切窗口
    /// 从起点的矩形走到终点的矩形。两头都是「同一张图、从正中按铺满裁」：起点那一帧和起点的卡一样，终点那一帧和终点的卡一样，
    /// 中间只是窗口在变、图在等比缩放，看起来就是一张图一步到位地挪过去。
    /// </summary>
    private sealed class Flyer
    {
        public ContainerVisual Box = null!;
        public CompositionRoundedRectangleGeometry Geo = null!;
        public SpriteVisual Img = null!;
        public SpriteVisual? Over;
        public IDisposable? Own;
    }

    private readonly List<Flyer> _flyers = new();

    private void DisposeFlyers()
    {
        foreach (var f in _flyers)
        {
            _fx?.Children.Remove(f.Box);
            if (f.Over != null) _fx?.Children.Remove(f.Over);
            f.Own?.Dispose();
            f.Box.Dispose();
        }
        _flyers.Clear();
    }

    /// <summary>
    /// 飞的那张图：游戏库卡的封面和墙卡显示的是同一张图文件；挑已经解码好、分辨率更高的那一份
    /// （起点、终点都不会因为换了分辨率而变糊 / 变清楚）。
    /// </summary>
    private LoadedImageSurface? FlyerSurface(CcBlock card, Tile tile)
    {
        LoadedImageSurface? best = null;
        foreach (var s in new[] { card.CoverRef, tile.ArtSurface })
            if (s != null && Images.IsLoaded(s) && (best == null || s.DecodedPhysicalSize.Height > best.DecodedPhysicalSize.Height)) best = s;
        return best ?? card.CoverRef ?? tile.ArtSurface;
    }

    /// <summary>
    /// 从 r0 飞到 r1（屏幕 DIP）：整图等比缩放（对数插值）+ 平移，裁切窗口（含圆角）单独插值；三者同一条曲线、同一组采样点。
    /// overlay：起飞时盖在图上的墙卡卡面（字、角标、焦点环），和图一起刚体缩放平移（不拉伸），由调用方淡掉。
    /// </summary>
    private Flyer RigidFly(LoadedImageSurface? surf, Box r0, Box r1, float rad0, float rad1, double delayMs, (Tile tile, Box vis)? overlay = null, bool overlayAtEnd = false)
    {
        var f = new Flyer();
        var sz = surf?.DecodedPhysicalSize ?? default;
        double iw = sz.Width > 0 ? sz.Width : r0.W, ih = sz.Height > 0 ? sz.Height : r0.H;
        double Cover(Box r) => Math.Max(r.W / iw, r.H / ih);
        double s0 = Cover(r0), s1 = Cover(r1);
        Vector2 c0 = r0.Center, c1 = r1.Center;

        f.Box = G.C.CreateContainerVisual();
        f.Box.Size = new Vector2((float)View.Width, (float)View.Height);
        f.Geo = RoundRect(r0.W, r0.H, rad0);
        f.Geo.Offset = new Vector2(r0.X, r0.Y);
        f.Box.Clip = G.C.CreateGeometricClip(f.Geo);
        var brush = surf != null ? (CompositionBrush)G.C.CreateSurfaceBrush(surf) : G.Solid(Gfx.Rgba(0x27, 0x27, 0x2a, 1));
        if (brush is CompositionSurfaceBrush sb) sb.Stretch = CompositionStretch.Fill;   // 精灵的宽高比 = 图的宽高比，Fill 不会拉伸
        f.Img = G.Sprite(brush, relative: false);
        f.Box.Children.InsertAtTop(f.Img);
        _fx!.Children.InsertAtTop(f.Box);

        const int N = 48;
        var imgOff = G.C.CreateVector3KeyFrameAnimation();
        var imgSize = G.C.CreateVector2KeyFrameAnimation();
        var clipOff = G.C.CreateVector2KeyFrameAnimation();
        var clipSize = G.C.CreateVector2KeyFrameAnimation();
        var clipRad = G.C.CreateVector2KeyFrameAnimation();
        Vector3KeyFrameAnimation? ovOff = null;
        Vector2KeyFrameAnimation? ovSize = null;
        if (overlay is { } ov && ov.tile.FaceRoot is { } face)
        {
            var vs = G.C.CreateVisualSurface();
            vs.SourceVisual = face;
            vs.SourceOffset = new Vector2(-RingMargin, -RingMargin);
            vs.SourceSize = ov.tile.El.Size + new Vector2(2 * RingMargin);
            var ob = G.C.CreateSurfaceBrush(vs);
            ob.Stretch = CompositionStretch.Fill;
            f.Over = G.Sprite(ob, relative: false);
            f.Own = vs;
            _fx.Children.InsertAtTop(f.Over);
            ovOff = G.C.CreateVector3KeyFrameAnimation();
            ovSize = G.C.CreateVector2KeyFrameAnimation();
        }
        for (int i = 0; i <= N; i++)
        {
            float u = i / (float)N;
            double e = FlyCurve(u);
            var r = new Box(Lerp(r0.X, r1.X, e), Lerp(r0.Y, r1.Y, e), Lerp(r0.W, r1.W, e), Lerp(r0.H, r1.H, e));
            double s = Math.Max(Math.Exp(Math.Log(s0) + (Math.Log(s1) - Math.Log(s0)) * e), Cover(r));
            var c = Vector2.Lerp(c0, c1, (float)e);
            var d = new Vector2((float)(iw * s), (float)(ih * s));
            imgOff.InsertKeyFrame(u, new Vector3(c - d / 2, 0), G.Linear);
            imgSize.InsertKeyFrame(u, d, G.Linear);
            clipOff.InsertKeyFrame(u, new Vector2(r.X, r.Y), G.Linear);
            clipSize.InsertKeyFrame(u, r.Size, G.Linear);
            float rad = (float)(rad0 + (rad1 - rad0) * e);
            clipRad.InsertKeyFrame(u, new Vector2(rad, rad), G.Linear);
            if (overlay is { } o && ovOff != null && ovSize != null)
            {
                // 卡面那层钉在墙卡那一头（出场 = 起点，进场 = 终点），跟着图一起等比缩放平移
                double sRef = overlayAtEnd ? s1 : s0; var cRef = overlayAtEnd ? c1 : c0;
                float k = (float)(s / sRef);
                ovOff.InsertKeyFrame(u, new Vector3(c + (new Vector2(o.vis.X, o.vis.Y) - cRef) * k, 0), G.Linear);
                ovSize.InsertKeyFrame(u, o.vis.Size * k, G.Linear);
            }
        }
        // 起点值先摆好（延迟期间就是这一帧）
        var dd0 = new Vector2((float)(iw * s0), (float)(ih * s0));
        f.Img.Offset = new Vector3(c0 - dd0 / 2, 0);
        f.Img.Size = dd0;
        if (f.Over != null && overlay is { } o0)
        {
            float k0 = (float)(s0 / (overlayAtEnd ? s1 : s0)); var cr0 = overlayAtEnd ? c1 : c0;
            f.Over.Offset = new Vector3(c0 + (new Vector2(o0.vis.X, o0.vis.Y) - cr0) * k0, 0);
            f.Over.Size = o0.vis.Size * k0;
        }
        void Go(CompositionObject target, string prop, KeyFrameAnimation a)
        {
            a.Duration = Gfx.Ms(FlyMs);
            a.DelayTime = Gfx.Ms(delayMs);
            a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            target.StartAnimation(prop, a);
        }
        Go(f.Img, "Offset", imgOff);
        Go(f.Img, "Size", imgSize);
        Go(f.Geo, "Offset", clipOff);
        Go(f.Geo, "Size", clipSize);
        Go(f.Geo, "CornerRadius", clipRad);
        if (f.Over != null) { Go(f.Over, "Offset", ovOff!); Go(f.Over, "Size", ovSize!); }
        _flyers.Add(f);
        return f;
    }

    private static float Lerp(float a, float b, double t) => (float)(a + (b - a) * t);

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

    /// <summary>首页卡的圆角（卡 12、图 8，焦点卡放大 1.2×）。墙卡是直角。</summary>
    private float HomeFrameRadius => CcRadius * _ccK * CcMaxScale;
    private float HomeCoverRadius => CcImgRadius * _ccK * CcMaxScale;

    /// <summary>
    /// 背景板：控制中心（首页）之上、墙之下的一块主题底色。folia 打开集合时它 0.62 s 淡入盖住首页，
    /// 返回时 0.62 s 淡出露出首页。
    /// </summary>
    private SpriteVisual Plate()
    {
        if (_plate == null)
        {
            _plate = G.Sprite(G.Solid(Theme.Bg));
            _plate.IsVisible = false;
            _top.Children.InsertAbove(_plate, _cc!);
        }
        return _plate;
    }

    private SpriteVisual? _plate;


    /// <summary>
    /// 出场（墙 → 控制中心，= folia 从歌单里返回首页）：
    /// - 首页（控制中心）立刻出现在最底下，焦点在游戏库卡上（1.2×、上浮 6 px），不做自己的开场动画；
    /// - 它上面一块主题底色的背景板 1 → 0（0.62 s，cubic-bezier(0.32,0.72,0,1)）；墙整层 0.3 s 淡掉；
    /// - hero 卡的外框 / 封面 / 标题弹簧（response 0.36、bounce 0.2）飞回游戏库卡此刻的真实矩形：外框收到 0.985，
    ///   封面转角 0 → 5° → 1.6°、模糊 0 → 1 → 8 px，标题模糊 0 → 1 → 8 px，三件透明度 1 / 1 / 0（0、0.72、1 处，0.5 s ease-out）；
    ///   游戏库卡自己的封面和标题从一开始就在飞行的那一层上 0.35 s 淡进来（同一张图、同样的裁切），所以最后几帧是
    ///   卡片自己那张图的一份微斜、变糊、正在淡掉的拷贝叠在真卡片上 —— 真卡片自始至终不藏；
    /// - 其余屏上的卡变成幽灵四散（离 hero 越远越晚越快）。
    /// 返回 false = 没有可飞的 hero，交给普通淡出。
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
        float k = _ccK, s = (float)View.Scale;

        // 背景板 1 → 0，墙整层 0.3 s 淡掉
        var plate = Plate();
        plate.IsVisible = true;
        plate.StartAnimation("Opacity", G.Scalar(1, 0, ExitBackdropMs, ExitEase));
        // 墙的底（底色、动态背景、暗角、竖条）0.3 s 淡掉；墙卡本身不跟着整层淡，由下面各自四散淡出
        _cam.StopAnimation("fade");
        _cam.StartAnimation("fade", G.Scalar(1, 0, WallFadeMs, CcCloseEase));

        // 幽灵四散（最多 16 张，离 hero 最近的）
        float reach = MathF.Min(MathF.Min((float)View.Width, (float)View.Height) / k * 0.3f, 220) * k;
        var squad = tiles.Where(x => x.tile != hero.tile).OrderBy(x => Vector2.Distance(x.box.Center, hero.box.Center)).ToList();   // 屏上所有墙卡都散（真卡自己散：只动内层，不另外画实时画面，起飞那一帧不掉帧）
        float maxDist = MathF.Max(1, squad.Count == 0 ? 1 : squad.Max(x => Vector2.Distance(x.box.Center, mid)));
        double longest = ExitBackdropMs;
        var outEase = new Bezier(0.22f, 1f, 0.36f, 1f);
        var inOut = new Bezier(0.42f, 0f, 0.58f, 1f);
        foreach (var (t, b) in squad)
        {
            var d = b.Center - mid;
            float dist = d.Length();
            var dir = dist > 1 ? d / dist : new Vector2(0, -1);
            uint seed = (uint)(((int)MathF.Round(b.X) * 73856093) ^ ((int)MathF.Round(b.Y) * 19349663));
            double jitter = ((seed % 1000) / 1000.0 - 0.5) * 0.08;
            double n = Math.Min(dist / maxDist, 1);
            double dur = (0.46 + n * 0.3) * 1000, delay = Math.Max(0, (0.04 + n * 0.26 + jitter) * 1000);
            float rot = (seed % 2 == 0 ? 1 : -1) * (1.8f + (seed % 3) * 0.8f);
            // 实测：每张幽灵卡用一份墙卡的实时画面（VisualSurface）时，十几张一起在起飞那一帧渲染，每次都掉一帧；
            // 改成墙卡自己散开（只动卡的内层：位移、缩放、转角、透明度），不多画任何东西
            t.MorphScatter(dir * reach / s, rot, dur, delay);
            longest = Math.Max(longest, dur + delay);
        }

        // hero：只有一张图在飞 —— 墙卡和游戏库卡用的是同一张图（同一份解码好的表面、同样从正中裁），
        // 起点盒子 = 墙卡此刻看得见的图（焦点放大后的矩形），终点 = 游戏库卡的封面（1.2×、上浮后的矩形、圆角）。
        // 真卡的显隐全部是合成线程上的关键帧（和飞行同一批提交、同一时间轴），不在 UI 线程上切，所以不会有空帧或双影。
        var heroArt = HeroBox(hero.tile, now, 0);
        var heroVis = HeroBox(hero.tile, now);
        var b0 = _ccBlocks[0];
        // 墙卡上的字、角标、焦点环：盖一层墙卡卡面的实时画面，跟着图一起刚体缩放平移，起飞后 0.12 s 淡掉
        var img = RigidFly(FlyerSurface(b0, hero.tile), heroArt, homeCover, 0, HomeCoverRadius, HandMs, Program.Args.Contains("--no-overlay") ? null : (hero.tile, heroVis));
        double land = HandMs + FlyMs, total = land + HandoffMs;
        img.Over?.StartAnimation("Opacity", Keys(HandMs + 120, (0, 1, null), ((float)(HandMs / (HandMs + 120)), 1, null), (1, 0, EaseCss)));
        // 真墙卡（hero 和幽灵的原卡）在飞的那份已经画出来之后（HandMs）才藏
        hero.tile.MorphHideAt(HandMs);

        // 游戏库卡：卡底、字、投影、焦点环在原位淡进来；它的封面在落定那一刻、在飞的图下面直接出现，飞的图再淡掉
        var card = b0.Gate;
        var cardCover = b0.Cover;
        bool hold = Program.Args.Contains("--morph-hold");
        card.StartAnimation("Opacity", Keys(total, (0, 0, null), ((float)((HandMs + FlyMs * 0.3) / total), 0, null), ((float)((HandMs + FlyMs * 0.9) / total), 1, EaseCss), (1, 1, null)));
        if (hold) cardCover.Opacity = 0;
        else cardCover.StartAnimation("Opacity", Keys(total, (0, 0, null), ((float)(land / total), 0, null), ((float)(land / total) + 0.0005f, 1, null), (1, 1, null)));
        if (!hold) img.Box.StartAnimation("Opacity", Delayed(Keys(HandoffMs, (0, 1, null), (1, 0, null)), land));

        int v0 = _ccVersion;
        BeginMorph(Math.Max(longest, total) + 30 + (hold ? 2500 : 0), () =>
        {
            DisposeFlyers();
            card.StopAnimation("Opacity");
            card.Opacity = 1;
            cardCover.StopAnimation("Opacity");
            cardCover.Opacity = 1;
            plate.StopAnimation("Opacity");
            plate.IsVisible = false;
            _root.StopAnimation("Opacity");
            _root.Opacity = 1;
            if (_ccOpen && v0 == _ccVersion) _root.IsVisible = false;
            // 墙藏好之后再把墙卡和墙的底复原（同一次提交，看不见中间状态）
            foreach (var t in _tiles.Values) t.Cancel();
            _cam.StopAnimation("fade");
            _cam.InsertScalar("fade", 1);
            if (_ccBack != null) _ccBack.InstantColors = false;
            CcBackRun();   // 形变完了动态背景再动起来
        }, fastForwardMs: 120);
        CcBackRun();   // 形变期间停在一帧
        Log.Info($"morph exit: hero {heroItem?.Title} art {heroArt.X:F1},{heroArt.Y:F1} {heroArt.W:F1}x{heroArt.H:F1} -> cover {homeCover.X:F1},{homeCover.Y:F1} {homeCover.W:F1}x{homeCover.H:F1}, {squad.Count} ghosts");
        return true;
    }

    /// <summary>
    /// 进场（控制中心 → 墙，= folia 从首页打开歌单）：
    /// - 游戏库卡的外框 / 封面 / 标题从卡片此刻的真实矩形起飞（外框 0.97、封面 0.96 + 2.4° + 6 px 模糊、标题 3.5 px 模糊），
    ///   弹簧（response 0.34、bounce 0.16）落到墙上 hero 卡（焦点停过的那款）的矩形；
    /// - 目标量到之后 +140 ms 进入交叉淡化（0.28 s）：封面换成 hero 卡面的实时画面，标题淡掉（卡面上有自己的标题）；
    ///   再 +240 ms 合成层 0.18 s 淡出；
    /// - hero 卡先藏起来，0.12 s 后揭晓（透明度 0.3 s ease-out，尺度 0.985 → 1，0.42 s）；其余屏上的卡沿径向就位；
    /// - 背景板 0 → 1（0.62 s，cubic-bezier(0.22,1,0.36,1)）盖住首页，墙整层 0.3 s 淡入；完了首页藏到下面。
    /// </summary>
    private bool MorphEnter()
    {
        Sync();
        long now = NowMs;
        var tiles = VisibleTiles(now);
        if (tiles.Count == 0) return false;
        Vector2 mid = new((float)View.Width / 2, (float)View.Height / 2);
        string? gameId = SelectedGame()?.Id;
        var hero = tiles.FirstOrDefault(x => gameId != null && ItemFor(x.tile.Slot) == gameId);
        if (hero.tile == null && FocusKey is { } fk) hero = tiles.FirstOrDefault(x => x.tile.Slot.Key == fk);
        if (hero.tile == null) hero = tiles.OrderBy(x => Vector2.Distance(x.box.Center, mid)).First();
        if (_railFocused) ExitRail(false);
        Focus(hero.tile.Slot.Key, false);
        var heroItem = ItemFor(hero.tile.Slot) is { } hid ? Lookup(hid) : null;
        var (homeFrame, homeCover, homeTitle) = HomeCardBoxes();
        float k = _ccK, s = (float)View.Scale;

        // 背景板盖住首页，墙整层淡入
        var plate = Plate();
        plate.IsVisible = true;
        plate.StartAnimation("Opacity", G.Scalar(0, 1, EnterMs, EnterBackdropEase));
        _root.Opacity = 0;
        _root.StartAnimation("Opacity", G.Scalar(0, 1, WallFadeMs, CcCloseEase));
        _root.Opacity = 1;

        // 其余的卡：径向就位
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
            if (!Program.Args.Contains("--no-flyin")) t.MorphFlyIn(dir * reach / s, delay);
        }

        // hero：只有一张图在飞（游戏库卡的封面 = 墙卡同一张图），从卡的封面位飞到墙卡此刻看得见的图的位置。
        // 游戏库卡的封面在飞的图画出来之后（HandMs）才藏；卡底、字、投影在原位淡掉。
        // 墙卡一直藏着，落定那一刻在飞的图下面直接出现，飞的图再 0.15 s 淡掉，露出卡上的字、角标、焦点环。
        bool hold = Program.Args.Contains("--morph-hold");   // 测试：落定后停住不交接，截图对比
        var heroArt = HeroBox(hero.tile, now, 0);
        var b0 = _ccBlocks[0];
        // 墙卡卡面（底部暗角、标题、角标、焦点环）钉在终点、跟着图一起等比缩放平移，飞行最后 0.18 s 淡进来：
        // 落定那一帧合成层就已经和墙卡一模一样，交接时什么都不用再变（之前是落定后再淡出，暗角和标题在「到了之后」才盖上来）
        var heroVis = HeroBox(hero.tile, now);
        var img = RigidFly(FlyerSurface(b0, hero.tile), homeCover, heroArt, HomeCoverRadius, 0, HandMs, Program.Args.Contains("--no-overlay") ? null : (hero.tile, heroVis), overlayAtEnd: true);
        double land = HandMs + FlyMs;
        img.Over?.StartAnimation("Opacity", Keys(land, (0, 0, null), ((float)((land - 180) / land), 0, null), (1, 1, EaseCss)));
        var card = b0.Gate;
        var cardCover = b0.Cover;
        cardCover.StartAnimation("Opacity", Keys(HandMs + 1, (0, 1, null), ((float)(HandMs / (HandMs + 1)), 1, null), (1, 0, null)));
        card.StartAnimation("Opacity", Keys(HandMs + 200, (0, 1, null), ((float)(HandMs / (HandMs + 200)), 1, null), (1, 0, EaseCss)));
        // 控制中心正被背景板盖住：它的动态背景（全屏着色器 + 模糊，在 UI 线程上逐帧画）现在就停，别和飞行抢帧
        CcBackStop();
        hero.tile.MorphHide();
        if (!hold)
        {
            // 落定：墙卡在合成层下面出现，两帧后合成层撤掉（两者此刻画面相同，不做淡出）
            hero.tile.MorphShowAt(land, 1);
            var gone = Keys(land + 34, (0, 1, null), ((float)((land + 33) / (land + 34)), 1, null), (1, 0, null));
            img.Box.StartAnimation("Opacity", gone);
            // 卡面那层（不透明，盖住了下面的图）最后 0.1 s 淡掉露出真墙卡：两者几何完全一样，只是实时画面的栅格化略软，
            // 这样换手只是一次很轻的清晰度过渡；下面的图在 land + 33 已经撤掉，卡面层淡出时不会透出没有暗角的原图
            double tail = land + 33 + 100;
            img.Over?.StartAnimation("Opacity", Keys(tail, (0, 0, null), ((float)((land - 180) / tail), 0, null), ((float)(land / tail), 1, EaseCss), ((float)((land + 33) / tail), 1, null), (1, 0, EaseCss)));
        }

        WallBusy(EnterMs);
        BeginMorph(Math.Max(land + 140, EnterMs) + 30 + (hold ? 2500 : 0), () =>
        {
            if (hold) hero.tile.Cancel();
            card.StopAnimation("Opacity");
            card.Opacity = 1;
            cardCover.StopAnimation("Opacity");
            cardCover.Opacity = 1;
            DisposeFlyers();
            plate.StopAnimation("Opacity");
            plate.IsVisible = false;
            _root.StopAnimation("Opacity");
            _root.Opacity = 1;
            if (!_ccOpen) HideControlCenter();
            UpdateBackdropRunning();
        }, fastForwardMs: 260, fastForward: () =>
        {
            foreach (var t in _tiles.Values) t.Cancel();
        });
        UpdateBackdropRunning();
        Log.Info($"morph enter: hero {heroItem?.Title} cover {homeCover.X:F1},{homeCover.Y:F1} {homeCover.W:F1}x{homeCover.H:F1} -> art {heroArt.X:F1},{heroArt.Y:F1} {heroArt.W:F1}x{heroArt.H:F1}, {tiles.Count - 1} fly-ins");
        return true;
    }

    private static double Seed(string key)
    {
        uint h = 0;
        foreach (char ch in key) h = h * 31 + ch;
        return h % 1000 / 1000.0;
    }

    private Action? _morphFastForward;

    private double _morphFfMs;
    private bool _morphRushing;

    /// <summary>形变开始：到点自动收尾；途中有输入就快进 —— folia：出场 120 ms 后收尾，进场 0.26 s 后收尾（只认第一次输入）。</summary>
    private void BeginMorph(double ms, Action finish, double fastForwardMs = 0, Action? fastForward = null)
    {
        FinishMorph();
        _morphFinish = finish;
        _morphFastForward = fastForward;
        _morphFfMs = fastForwardMs;
        _morphRushing = false;
        _morphTimer = After(ms * MorphSlow, () => { _morphFastForward = null; FinishMorph(); });
    }

    /// <summary>--slow=N：测试用，整段合成动画放慢 N 倍（Compositor.GlobalPlaybackRate），收尾计时器同步拉长。</summary>
    private static double MorphSlow =>
        Program.Args.FirstOrDefault(a => a.StartsWith("--slow=")) is { } s && double.TryParse(s[7..], out var v) && v > 0 ? v : 1;

    private void RushMorph()
    {
        if (_morphRushing || _morphFinish == null) return;
        _morphRushing = true;
        _morphTimer?.Stop();
        if (_morphTimer != null) _pendingTimers.Remove(_morphTimer);
        _morphTimer = After(Math.Max(1, _morphFfMs * MorphSlow), FinishMorph);
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
        if (key is { } k && Engine.Geometry.SlotByKey(k) is { } s && ItemFor(s) is { } id && Lookup(id)?.Kind == ItemKind.Game)
        {
            _ccGameId = id;
            // 记下墙上这张卡用的是哪张图（竖封面 / 方裁 / 大背景按卡的尺寸挑），游戏库卡用同一张
            _ccGameArt = _tiles.TryGetValue(k, out var t) ? t.ArtPath : null;
        }
    }

    private string? _ccGameArt;

    /// <summary>同一张图的高分辨率版（p → q、s → x、h → g；没有就用原图）：游戏库卡放大到 1.2× 也不糊。</summary>
    private static string? HiRes(string? path)
    {
        if (path == null) return null;
        var name = Path.GetFileNameWithoutExtension(path);
        string? hi = name switch { "p" => "q", "s" => "x", "h" => "g", _ => null };
        if (hi == null) return path;
        var alt = Path.Combine(Path.GetDirectoryName(path)!, hi + ".jpg");
        return File.Exists(alt) ? alt : path;
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
        string? cover = recent != null && recent.Id == _ccGameId ? HiRes(_ccGameArt) : null;
        if (cover == null && recent?.Art is { } art)
            foreach (var k in new[] { 'q', 'p', 'v', 'x', 's' })
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
