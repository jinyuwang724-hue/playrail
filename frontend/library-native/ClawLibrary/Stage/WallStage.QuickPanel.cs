// 游戏模式快捷面板（QAM，定稿 2026-11-05，设计稿 docs/qam-final-20261105.html）。
// 左侧一栏 416 宽，从左滑入（可打断弹簧 stiffness 300 / damping 34）。不抢焦点：游戏留在前台继续渲染，
// 面板右边是游戏窗口的 DWM 实时缩略图（不压暗），面板底是开面板那一刻截的一帧游戏画面，做一次模糊（24 px，亮度 82 %，饱和 1.15）+ #09090B 36 %。
// 从上到下：状态栏 → 功耗块 → 亮度 / 音量两条细滑条（不是焦点停靠点：LB/RB 调亮度、LT/RT 调音量）→ 6 行文字选项 → 5 × 3 网格。
// 焦点：功耗块和选项行共用一个白框光标，在行间弹簧移动（460 / 36 / 0.9）；网格里是格子自己的白外框。没有按键提示栏。
// 坐标都是设计 px（1280 × 800），k = 设计 px → DIP。
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.Devices.Radios;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private enum QsKind { Slider, Meter, Row, Cell, Card }

    private sealed class QsItem
    {
        public required string Id;
        public required QsKind Kind;
        public string Name = "", Value = "", Glyph = "", Tip = "", Sub = "";
        public bool On, Disabled, Off, Lamp;
        /// <summary>命中 / 绘制区域（设计 px）；滑条是轨道本身。</summary>
        public float X, Y, W, H;
        public float Level;
        public int Col, Row, Span = 1;
    }

    // ---------------- 尺寸（设计 px） ----------------
    private const float QsW = 416, QsL = 32, QsR = 384, QsHideX = -440;
    private const float QsStatusY = 24, QsPwY = 76, QsPwH = 100, QsPairY = 192, QsRowY = 260, QsRowH = 48, QsGridY = 569, QsCell = 62.4f, QsPitch = 72.4f;
    private const float QsTopH = 250, QsRowsY = 250, QsRowsH = 310, QsGridLY = 500, QsGridLH = 300;
    private static readonly Color QsInk = Theme.Ink, QsBg = Theme.Bg, QsAcc = Theme.Accent, QsRed = Gfx.Rgba(0xff, 0x5a, 0x4e);
    private static readonly Color QsInk2 = Gfx.WithAlpha(Theme.Ink, 0.55), QsOffBg = Gfx.WithAlpha(Theme.Ink, 0.08);
    private static readonly Bezier QsEase = new(.22f, 1f, .36f, 1f);

    private static readonly int[] QsFps = { 0, 30, 40, 60 };
    private static readonly (int V, string N)[] QsRumble = { (0, "关"), (35, "弱"), (70, "中"), (100, "强") };
    private static readonly string[] QsOsdNames = { "关", "简洁", "标准", "详细", "完整" };
    private static readonly string[] QsRowIds = { "fps", "refresh", "scale", "osd", "fan", "rumble" };
    private static readonly string[] QsCol = { "tdp", "fps", "refresh", "scale", "osd", "fan", "rumble", "grid" };
    /// <summary>网格：id、列、行、跨列。左 3×3 靠屏幕外沿，右两列三张宽卡。电源展开时第三行左三格换成 关机 / 重启 / 睡眠。</summary>
    private static readonly (string Id, int C, int R, int W)[] QsLay =
    {
        ("wifi", 0, 0, 1), ("bt", 1, 0, 1), ("air", 2, 0, 1),
        ("shot", 0, 1, 1), ("kbd", 1, 1, 1), ("pad", 2, 1, 1),
        ("pwr", 0, 2, 1), ("desk", 1, 2, 1), ("vrr", 2, 2, 1),
        ("off", 0, 2, 1), ("restart", 1, 2, 1), ("sleep", 2, 2, 1),
        ("win", 3, 0, 2), ("lib", 3, 1, 2), ("quit", 3, 2, 2),
    };
    private static readonly string[] QsPm = { "off", "restart", "sleep" }, QsR2 = { "pwr", "desk", "vrr" };
    private static readonly HashSet<string> QsRisk = new() { "quit", "restart", "off" };

    // ---------------- 状态 ----------------
    private ContainerVisual? _qs;
    private SpriteVisual _qsScrim = null!, _qsPanel = null!;
    private ContainerVisual _qsSlide = null!, _qsGlassBox = null!;
    private SpriteVisual _qsGlassBase = null!, _qsGlass = null!, _qsTopV = null!, _qsRowsV = null!, _qsGridV = null!, _qsCurV = null!, _qsEdgeV = null!;
    private InsetClip _qsGlassClip = null!;
    private CompositionDrawingSurface? _qsTopS, _qsRowsS, _qsGridS, _qsCurS, _qsGlassS;
    private float _qsCurFor;
    private bool _qsOpen, _qsTicking, _qsHideWindowAfter, _qsWaitBackdrop;
    /// <summary>旧接口：0 = 收着，1 = 开着（外面只用来清零）。</summary>
    private float _qsShow, _qsShowTo;
    private float _qsPx = QsHideX, _qsPv, _qsPt = QsHideX;
    private float _qsCy, _qsCh, _qsCvy, _qsCvh, _qsCty, _qsCth;
    private bool _qsCurPlaced;
    private long _qsFrameAt;
    private int _qsVersion, _qsPolls;
    private string _qsFocusId = "tdp";
    private int _qsGc, _qsGr;
    private bool _qsPm;
    private string? _qsArmed;
    private readonly List<QsItem> _qsItems = new();
    private JsonObject? _qsState, _qsDraft;
    private bool? _qsWifi, _qsBt, _qsAir, _qsLossless, _qsVrr;
    private string? _qsSsid, _qsPadMode, _qsVrrRange;
    private int _qsBtCount = -1;
    private DwmThumb? _qsThumb;
    private bool _qsUnderLibrary;
    private SpriteVisual? _qsUnder;
    private nint _qsGame;
    private (int L, int T, int W, int H) _qsGameRect;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _qsPoll;
    private bool _qsSaving;

    internal bool QuickPanelOpen => _qsOpen;
    /// <summary>旧的列表式设计预览已去掉；触控导航还在读它。</summary>
    private static bool QsRowsStyle => false;
    private float QsK => Math.Min((float)View.Width / 1280, (float)View.Height / 800);
    private float QsViewH => (float)(View.Height / QsK);

    // ---------------- 小补间（只给面板里的颜色 / 透明度 / 位移用） ----------------

    private sealed class QsTw { public float From, To; public long T0; public float Dur; public Bezier E; }
    private readonly Dictionary<string, QsTw> _qsTw = new();
    private readonly bool[] _qsLayerWas = new bool[3];

    /// <summary>取补间此刻的值；没有这个键就是 idle。</summary>
    private float QsTv(string key, float idle = 0)
    {
        if (!_qsTw.TryGetValue(key, out var t)) return idle;
        float p = t.Dur <= 0 ? 1 : Math.Clamp((NowMs - t.T0) / t.Dur, 0, 1);
        return t.From + (t.To - t.From) * (float)t.E.Ease(p);
    }

    /// <summary>补到 to；第一次出现的键直接落在 to（打开面板时不重放整组动画）。</summary>
    private void QsTo(string key, float to, float ms, Bezier? ease = null)
    {
        if (!_qsTw.TryGetValue(key, out var t)) { _qsTw[key] = new QsTw { From = to, To = to, Dur = 0, E = QsEase }; return; }
        if (t.To == to) return;
        _qsTw[key] = new QsTw { From = QsTv(key), To = to, T0 = NowMs, Dur = Reduced ? 0 : ms, E = ease ?? QsEase };
        QsStartTicking();
    }

    /// <summary>从 from 放一段到 to（数字弹一下、按下缩一下）。</summary>
    private void QsKick(string key, float from, float to, float ms)
    {
        _qsTw[key] = new QsTw { From = from, To = to, T0 = NowMs, Dur = Reduced ? 0 : ms, E = QsEase };
        QsStartTicking();
    }

    private bool QsLayerAnimating(char layer)
    {
        long now = NowMs;
        foreach (var (k, t) in _qsTw) if (k[0] == layer && now - t.T0 < t.Dur + 17) return true;
        return false;
    }

    // ---------------- 开 / 关 ----------------

    /// <summary>面板有自己的窗口（不透明部分只有面板本身，不碰游戏库主窗口）。建不出来时退回旧办法：画在游戏库窗口里。</summary>
    internal bool QuickPanelOwnWindow => _qsWin != null;
    private QamWindow? _qsWin;
    private int _qsSlideGen;
    private bool _qsCurShown;
    private long _qsOpenAt;

    /// <summary>启动后预先建好面板窗口和画面（第一次唤出不用现建）。</summary>
    internal void WarmQuickPanel()
    {
        try { EnsureQsVisuals(); QsBuild(); QsPaintAll(); _ = QsRefresh(_qsVersion, first: true, warm: true); AskCaptureAccessIfForeground(); Log.Info($"quick panel warm own-window={_qsWin != null}"); }
        catch (Exception ex) { Log.Warn($"quick panel warm: {ex.Message}"); }
    }

    /// <summary>
    /// 唤出：面板窗口放到游戏所在的显示器左边、显示（不激活），面板从左边外面弹簧滑入（合成层）。
    /// 冻结帧在后面截，截到了玻璃底再淡入，不等它。
    /// </summary>
    internal void OpenQuickPanel(nint returnWindow)
    {
        _qsOpenAt = NowMs;
        ExitScreensaver(); MarkInput();
        EnsureQsVisuals();
        bool again = _qsOpen;
        _qsOpen = true;
        int v = ++_qsVersion;
        _qsHideWindowAfter = false;
        nint self = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        if (!again)
        {
            _qsFocusId = "tdp"; _qsGc = 0; _qsGr = 0; _qsPm = false; _qsArmed = null;
            _qsTw.Clear(); _qsCurPlaced = false;
            _qsThumb?.Dispose(); _qsThumb = null;
            _qsGame = returnWindow != 0 && returnWindow != self && OpenWindows.Alive(returnWindow) ? returnWindow : 0;
            if (_qsGame != 0 && GetWindowRect(_qsGame, out var r)) _qsGameRect = (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            if (_qsWin == null && _qsGame != 0) _qsThumb = DwmThumb.Register(self, _qsGame);
            if (_ccOpen) CloseControlCenter();
            _qsGlass.StopAnimation("Opacity");
            _qsGlass.Opacity = 0;
            _qsLosslessKnown = false;   // 手柄模式 / VRR 留着上次读到的，开面板时不先变暗再亮
            _qsSlide.StopAnimation("Offset");
            _qsSlide.Offset = new Vector3(QsHideX * QsK, 0, 0);
        }
        _qsPt = 0; _qsShowTo = 1;
        _qsUnderLibrary = ShellMode.GameMode;
        if (_qsUnder != null) _qsUnder.IsVisible = false;   // 不压暗游戏，底下不用垫黑
        _qs!.IsVisible = true;
        QsBuild();
        QsSync();
        QsPaintAll();
        if (_qsWin != null)
        {
            if (!again || !_qsWin.Shown) QsPlaceWindow(_qsGame != 0 ? _qsGame : self);
            _qsWin.Show();
        }
        else { _top.Children.Remove(_qs); _top.Children.InsertAtTop(_qs); }
        QsSlideTo(0);
        if (!again) _ = Task.Run(() => QsCaptureBackdrop(v));
        _ = QsRefresh(v, first: !again);
        _qsPoll ??= _dq.CreateTimer();
        _qsPoll.Interval = TimeSpan.FromMilliseconds(1500);
        _qsPoll.Tick -= QsPollTick; _qsPoll.Tick += QsPollTick;
        _qsPoll.Start();
        Cue(Sfx.MenuIn);
        Log.Info($"quick panel open game={_qsGame} own-window={_qsWin != null} in {NowMs - _qsOpenAt} ms");
    }

    /// <summary>面板窗口：游戏（或游戏库）所在显示器的左边一条，宽 = 面板宽，高 = 整屏。</summary>
    private void QsPlaceWindow(nint over)
    {
        nint mon = MonitorFromWindow(over, 2);
        var mi = new QsMonInfo { Size = Marshal.SizeOf<QsMonInfo>() };
        if (!GetMonitorInfo(mon, ref mi)) return;
        float k = QsK, dpi = Chrome.Dpi;
        int w = (int)Math.Ceiling(QsW * k * dpi), h = mi.Monitor.Bottom - mi.Monitor.Top;
        _qsMon = (mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right - mi.Monitor.Left, h);
        _qsWin!.Place(mi.Monitor.Left, mi.Monitor.Top, w, h);
        _qs!.Size = new Vector2(w / dpi, h / dpi);
    }

    private (int L, int T, int W, int H) _qsMon;

    /// <summary>面板平移：合成层弹簧（stiffness 300 / damping 34），从此刻的位置和速度接着走（可打断）。收起走完再关窗口。</summary>
    private void QsSlideTo(float x)
    {
        _qsPt = x;
        float k = QsK;
        int gen = ++_qsSlideGen;
        if (Reduced)
        {
            _qsSlide.StopAnimation("Offset");
            _qsSlide.Offset = new Vector3(x * k, 0, 0);
            if (!_qsOpen) QsFinishClose();
            return;
        }
        var batch = G.C.CreateScopedBatch(CompositionBatchTypes.Animation);
        _qsSlide.StartAnimation("Offset", G.SpringV3(new Vector3(x * k, 0, 0), new Spring(300, 34)));
        batch.End();
        long t0 = NowMs;
        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            if (gen != _qsSlideGen) return;
            Log.Info($"quick panel slide {(x < 0 ? "out" : "in")} settled {NowMs - t0} ms");
            if (!_qsOpen) QsFinishClose();
        };
    }

    /// <summary>收起：面板滑回去，走完再把面板窗口藏起来（游戏一直在前台，没有别的窗口要退）。</summary>
    internal void CloseQuickPanel(bool hideWindow = true, bool instant = false)
    {
        if (!_qsOpen || _qs == null) return;
        _qsOpen = false;
        _qsPoll?.Stop();
        _qsHideWindowAfter = hideWindow;
        _qsArmed = null;
        Cue(Sfx.MenuOut);
        _qsShowTo = 0;
        if (instant || Reduced)
        {
            ++_qsSlideGen;
            _qsSlide.StopAnimation("Offset");
            _qsSlide.Offset = new Vector3(QsHideX * QsK, 0, 0);
            QsFinishClose();
            return;
        }
        QsSlideTo(QsHideX);
        int v = ++_qsVersion;
        After(1500, () => { if (v == _qsVersion && !_qsOpen && _qs.IsVisible) QsFinishClose(); });
        Log.Info("quick panel close");
    }

    private void QsFinishClose()
    {
        if (_qs == null || _qsOpen) return;
        bool hide = _qsHideWindowAfter;
        _qsHideWindowAfter = false;
        if (_qsWin != null)
        {
            _qsWin.Hide();
            _qs.IsVisible = false;
            if (!_wsOpen) (_window as MainWindow)?.EndQuickOverlay();
            return;
        }
        if (!hide && !_wsOpen) SetSpecialInput(false);
        if (hide && _qsGame != 0 && (_window as MainWindow)?.OverlayAcceptsGamepad != true) LibraryWindow.BringForward(_qsGame);
        if (hide) (_window as MainWindow)?.HideAfterQuickPanel();
        After(hide ? 260 : 0, () =>
        {
            if (_qsOpen) return;
            _qsThumb?.Dispose(); _qsThumb = null;
            if (_qs != null) _qs.IsVisible = false;
        });
    }

    /// <summary>别的入口接手时（CloseSpecialSurfaces）：面板窗口马上藏起来。</summary>
    internal void QsHideNow()
    {
        ++_qsSlideGen;
        _qsWin?.Hide();
    }

    private void EnsureQsVisuals()
    {
        if (_qs != null) return;
        try { _qsWin = new QamWindow(); }
        catch (Exception ex) { _qsWin = null; Log.Warn($"quick panel window: {ex.Message}; drawing in the library window"); }
        _qs = _qsWin != null ? G.Container(relative: false) : G.Container();
        _qs.IsVisible = false;
        _qsScrim = G.C.CreateSpriteVisual();
        _qsScrim.RelativeSizeAdjustment = Vector2.One;
        _qs.Children.InsertAtTop(_qsScrim);
        _qsUnder = G.C.CreateSpriteVisual();
        _qsUnder.Brush = G.C.CreateColorBrush(Gfx.Rgba(0, 0, 0));
        _qsUnder.IsVisible = false;
        _qs.Children.InsertAtTop(_qsUnder);
        // 底：冻结帧（不跟面板走，固定在屏幕上，只按面板右沿裁），没截到时是实底
        _qsGlassBox = G.Container(relative: false);
        _qsGlassClip = G.C.CreateInsetClip();
        _qsGlassBox.Clip = _qsGlassClip;
        _qsGlassBase = G.Sprite(G.Solid(Gfx.Rgba(0x17, 0x17, 0x1a)), relative: false);
        _qsGlass = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _qsGlassBox.Children.InsertAtTop(_qsGlassBase);
        _qsGlassBox.Children.InsertAtTop(_qsGlass);
        _qs.Children.InsertAtTop(_qsGlassBox);
        _qsSlide = G.Container(relative: false);
        _qsTopV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _qsRowsV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _qsGridV = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _qsCurV = G.C.CreateSpriteVisual();
        _qsCurV.Opacity = 0;
        _qsEdgeV = G.Sprite(G.Solid(Gfx.WithAlpha(QsInk, 0.10)), relative: false);
        _qsPanel = _qsTopV;
        _qsSlide.Children.InsertAtTop(_qsTopV);
        _qsSlide.Children.InsertAtTop(_qsRowsV);
        _qsSlide.Children.InsertAtTop(_qsCurV);
        _qsSlide.Children.InsertAtTop(_qsGridV);
        _qsSlide.Children.InsertAtTop(_qsEdgeV);
        _qsSlide.Offset = new Vector3(QsHideX * QsK, 0, 0);
        _qs.Children.InsertAtTop(_qsSlide);
        if (_qsWin != null)
        {
            _qs.Size = new Vector2(QsW * QsK + 2, (float)View.Height);
            _qsWin.SetContent(_qs);
            _qsWin.Prime((int)Math.Ceiling(QsW * QsK * Chrome.Dpi), (int)Math.Ceiling(View.Height * Chrome.Dpi));
        }
        else _top.Children.InsertAtTop(_qs);
    }

    // ---------------- 底：截一帧画面，模糊一次 ----------------

    /// <summary>
    /// 截整块显示器（DWM 合成好的那一帧），截的那一下把面板窗口排除在外，看到的就是底下的游戏 / 游戏库。
    /// 管理员权限的游戏不让普通权限程序按窗口截（「拒绝访问」），按显示器截不受影响。只截一次，烤成一张模糊图。
    /// </summary>
    private async Task QsCaptureBackdrop(int v)
    {
        long t0 = NowMs;
        CanvasBitmap? frame = null;
        string how = "monitor";
        try
        {
            var (bmp, _) = await CaptureMonitorFrameAsync(_qsWin?.Hwnd ?? 0, 500);
            frame = bmp;
        }
        catch (Exception ex) { Log.Warn($"quick panel backdrop: {ex.Message}"); }
        _dq.TryEnqueue(() =>
        {
            if (frame == null || v != _qsVersion || !_qsOpen) { frame?.Dispose(); if (frame == null) Log.Warn($"quick panel backdrop: none ({how})"); return; }
            try { QsBakeBackdrop(frame, (0, 0, (int)frame.SizeInPixels.Width, (int)frame.SizeInPixels.Height)); }
            catch (Exception ex) { Log.Warn($"quick panel backdrop bake: {ex.Message}"); }
            frame.Dispose();
            Log.Info($"quick panel backdrop {how} {NowMs - t0} ms (open +{NowMs - _qsOpenAt} ms)");
        });
    }

    private static bool _qsCaptureAsking;

    /// <summary>
    /// 系统「屏幕截图访问」里本应用的许可（HKCU ConsentStore\graphicsCaptureProgrammatic\&lt;包族名&gt;）：Allow / Deny / Prompt。
    /// 只读，不改。没打包运行（自测）时是 NonPackaged。
    /// </summary>
    internal static string CaptureConsent()
    {
        try
        {
            string name = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\graphicsCaptureProgrammatic\" + name);
            return k?.GetValue("Value") as string ?? "Prompt";
        }
        catch { return "NonPackaged"; }
    }

    /// <summary>
    /// 按程序截别的窗口 / 显示器要用户许可。还没许可时只在游戏库自己在前台时向系统要一次（会弹系统提示，用户点允许）；
    /// 面板 / 游戏在前台时不弹（弹不出来，会一直挂着），直接当没许可。
    /// </summary>
    private async Task<bool> QsCaptureAccess(bool mayPrompt)
    {
        string c = CaptureConsent();
        if (c is "Allow" or "NonPackaged") return true;
        if (c == "Deny" || !mayPrompt || _qsCaptureAsking) return false;
        _qsCaptureAsking = true;
        try
        {
            var op = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Programmatic).AsTask();
            var done = await Task.WhenAny(op, Task.Delay(60000)) == op ? await op : Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.UserPromptRequired;
            Log.Info($"capture access request: {done} (consent now {CaptureConsent()})");
            if (done == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
                try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); } catch { }
            return done == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception ex) { Log.Warn($"capture access: {ex.Message}"); return false; }
        finally { _qsCaptureAsking = false; }
    }

    /// <summary>游戏库在前台时（启动后）先把截屏许可要下来，免得第一次在游戏里唤出时没有玻璃底、截图失败。</summary>
    internal void AskCaptureAccessIfForeground()
    {
        if (CaptureConsent() != "Prompt") return;
        if (GetForegroundWindow() != WinRT.Interop.WindowNative.GetWindowHandle(_window)) { Log.Info("capture access: not asked (library not in front)"); return; }
        _ = QsCaptureAccess(mayPrompt: true);
    }
    private static async Task<CanvasBitmap?> QsGrab(GraphicsCaptureItem? item, int timeoutMs)
    {
        if (item == null) return null;
        var dev = CanvasDevice.GetSharedDevice();
        var done = new TaskCompletionSource<CanvasBitmap?>();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(dev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch { }
        try { session.IsBorderRequired = false; } catch { }
        pool.FrameArrived += (p, _) =>
        {
            using var f = p.TryGetNextFrame();
            if (f == null) return;
            try
            {
                using var src = CanvasBitmap.CreateFromDirect3D11Surface(dev, f.Surface);
                var copy = new CanvasRenderTarget(dev, src.SizeInPixels.Width, src.SizeInPixels.Height, 96);
                using (var ds = copy.CreateDrawingSession()) ds.DrawImage(src);
                if (!done.TrySetResult(copy)) copy.Dispose();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        };
        session.StartCapture();
        return await Task.WhenAny(done.Task, Task.Delay(timeoutMs)) == done.Task ? await done.Task : null;
    }

    /// <summary>截 hwnd 所在显示器的一帧，exclude（面板窗口）在截的时候排除在外。返回帧和显示器矩形（物理像素）。</summary>
    internal async Task<(CanvasBitmap? Frame, (int L, int T, int W, int H) Rect)> CaptureMonitorFrameAsync(nint exclude, int timeoutMs, nint on = 0)
    {
        nint self = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        nint mon = MonitorFromWindow(on != 0 ? on : _qsGame != 0 ? _qsGame : self, 2);
        var mi = new QsMonInfo { Size = Marshal.SizeOf<QsMonInfo>() };
        GetMonitorInfo(mon, ref mi);
        var rect = (mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right - mi.Monitor.Left, mi.Monitor.Bottom - mi.Monitor.Top);
        if (!await QsCaptureAccess(mayPrompt: false)) { Log.Warn($"capture: no screen-capture permission yet (consent {CaptureConsent()})"); return (null, rect); }
        var did = Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor(mon);
        if (exclude != 0) SetWindowDisplayAffinity(exclude, 0x11);
        try
        {
            var item = GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId { Value = did.Value });
            return (await QsGrab(item, timeoutMs), rect);
        }
        finally { if (exclude != 0) SetWindowDisplayAffinity(exclude, 0); }
    }

    /// <summary>CSS filter: blur(24px) brightness(.82) saturate(1.15)，再叠 #09090B 36 %。画在物理像素里，盖住面板能走到的整条左边。</summary>
    private void QsBakeBackdrop(CanvasBitmap frame, (int L, int T, int W, int H) at)
    {
        float dpi = Chrome.Dpi, kd = QsK * dpi;
        int pw = (int)Math.Ceiling(QsW * kd) + 2, ph = (int)Math.Ceiling(View.Height * dpi);
        var s = G.Surface(pw, ph);
        using (var ds = CanvasComposition.CreateDrawingSession(s))
        {
            ds.Clear(Gfx.Rgba(0x17, 0x17, 0x1a));
            const float sat = 1.15f, br = 0.82f;
            using var blur = new GaussianBlurEffect { Source = frame, BlurAmount = 24 * kd, BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Balanced };
            using var cm = new ColorMatrixEffect
            {
                Source = blur, ClampOutput = true,
                ColorMatrix = new Matrix5x4
                {
                    M11 = (0.213f + 0.787f * sat) * br, M12 = (0.213f - 0.213f * sat) * br, M13 = (0.213f - 0.213f * sat) * br,
                    M21 = (0.715f - 0.715f * sat) * br, M22 = (0.715f + 0.285f * sat) * br, M23 = (0.715f - 0.715f * sat) * br,
                    M31 = (0.072f - 0.072f * sat) * br, M32 = (0.072f - 0.072f * sat) * br, M33 = (0.072f + 0.928f * sat) * br,
                    M44 = 1,
                },
            };
            ds.DrawImage(cm, new Windows.Foundation.Rect(at.L, at.T, at.W, at.H), frame.Bounds);
            ds.FillRectangle(0, 0, pw, ph, Gfx.WithAlpha(QsBg, 0.36));
        }
        var brush = (CompositionSurfaceBrush)_qsGlass.Brush;
        (brush.Surface as IDisposable)?.Dispose();
        brush.Surface = s;
        _qsGlassS = s;
        _qsGlass.Size = new Vector2(pw / dpi, ph / dpi);
        _qsGlass.StartAnimation("Opacity", G.Scalar(_qsGlass.Opacity, 1, Reduced ? 1 : 160, Bezier.EaseOut));
    }

    // ---------------- 数据 ----------------

    private bool _qsLosslessKnown;

    private async Task QsRefresh(int v, bool first = false, bool warm = false)
    {
        if (_pagePointer.Active || _qsTouchWrites?.IsBusy == true) return;
        _agent ??= new AgentClient();
        // 读的途中有新的写入排进来：这次读到的是旧值，丢掉（写完会再读一次）
        long rev = _qsTouchWrites?.Revision ?? 0;
        try
        {
            if (await _agent.Call("widget_state") is JsonObject s && (warm || (v == _qsVersion && _qsOpen)) && !_pagePointer.Active && _qsTouchWrites?.IsBusy != true && (_qsTouchWrites?.Revision ?? 0) == rev)
            {
                _qsState = s;
                if (_qsDraft == null || !_qsSaving) _qsDraft = s["power"]?["settings"]?.DeepClone() as JsonObject;
                // 主要数值先画上（不等 Wi-Fi / 蓝牙 / 显示这些慢的读完）
                if (first && _qsOpen) QsRebuild();
            }
        }
        catch (Exception ex) { Log.Warn($"quick panel state: {ex.Message}"); }
        try
        {
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind == RadioKind.WiFi) _qsWifi = r.State == RadioState.On;
                if (r.Kind == RadioKind.Bluetooth) _qsBt = r.State == RadioState.On;
            }
        }
        catch (Exception ex) { Log.Warn($"quick panel radios: {ex.Message}"); }
        _qsAir = await Task.Run(QsAirplane.Get);
        try
        {
            var p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            _qsSsid = p != null && p.IsWlanConnectionProfile ? p.ProfileName : null;
        }
        catch { _qsSsid = null; }
        if (first || _qsPolls++ % 3 == 0) _qsBtCount = _qsBt == true ? await QsBluetoothConnected() : -1;
        if (first)
        {
            try { if (await _agent.Call("get_display_state") is JsonObject d) _qsVrr = d["vrr"] is JsonValue ? Bool(d["vrr"]) : null; }
            catch (Exception ex) { Log.Warn($"quick panel display: {ex.Message}"); }
            _qsVrrRange ??= await Task.Run(QsVrrRange);
            try { if (await _agent.Call("get_gamepad_state") is JsonObject g) _qsPadMode = Str(g["mode"]); }
            catch (Exception ex) { Log.Warn($"quick panel gamepad: {ex.Message}"); }
        }
        if (!_qsLosslessKnown)
        {
            try
            {
                if (await _agent.Call("get_lossless_settings") is JsonObject ls)
                {
                    _qsLosslessKnown = true;
                    _qsLossless = Str(ls["exe"]) != null ? Bool(ls["running"]) : null;
                }
            }
            catch { _qsLosslessKnown = true; _qsLossless = null; }
        }
        if (warm) { QsBuild(); QsPaintAll(); return; }
        if (v != _qsVersion || !_qsOpen) return;
        QsRebuild();
    }

    private static async Task<int> QsBluetoothConnected()
    {
        try
        {
            var sel = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
            var le = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
            var a = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(sel);
            var b = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(le);
            return a.Select(d => d.Name).Concat(b.Select(d => d.Name)).Where(n => !string.IsNullOrEmpty(n)).Distinct().Count();
        }
        catch (Exception ex) { Log.Warn($"quick panel bluetooth devices: {ex.Message}"); return -1; }
    }

    private void QsPollTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object e)
    {
        if (!_qsOpen) { sender.Stop(); return; }
        _ = QsRefresh(_qsVersion);
    }

    private JsonNode? QsGet(string path, JsonNode? root = null)
    {
        JsonNode? n = root ?? _qsState;
        foreach (var part in path.Split('.')) { if (n is not JsonObject o) return null; n = o[part]; }
        return n;
    }

    private double QsNum(string path, double fallback = 0, JsonNode? root = null) =>
        QsGet(path, root) is JsonValue v && (v.TryGetValue(out double d) || (v.TryGetValue(out int i) && (d = i) == i) || (v.TryGetValue(out long l) && (d = l) == l)) ? d : fallback;   // 本地写进去的是 int，后台回来的是 double

    private async Task QsSavePower(string what)
    {
        if (_qsDraft == null) return;
        _qsSaving = true;
        try { _agent ??= new AgentClient(); await _agent.Call("save_power", _qsDraft.DeepClone()); }
        catch (Exception ex) { QsFlash($"{what}没有改成：{ex.Message}"); }
        finally { _qsSaving = false; }
        _ = QsRefresh(_qsVersion);
    }

    private async Task QsCall(string what, string method, params JsonNode?[] args)
    {
        try { _agent ??= new AgentClient(); await _agent.Call(method, args); }
        catch (Exception ex) { QsFlash($"{what}没有改成：{ex.Message}"); }
        _ = QsRefresh(_qsVersion);
    }

    /// <summary>只在出错时提示（成功了看控件本身就知道）。</summary>
    private void QsFlash(string text) => Toast(text);

    // ---------------- 模型 ----------------

    private string QsTitle() => QsGameItem()?.Title ?? Str(QsGet("system.game.title")) ?? "";
    private bool QsInGame => _qsGame != 0 || QsGet("system.game") is JsonObject;
    private bool QsHasGame => _game != null || _qsGame != 0 || _qsTestGame != null;

    /// <summary>自测用（--qsgame=游戏名）：没开游戏也按这个游戏摆退出游戏卡和游戏相关项。</summary>
    internal string? _qsTestGame;

    private LibraryItem? QsGameItem()
    {
        if (_game?.Item is { } g) return g;
        string? title = _qsTestGame ?? Str(QsGet("system.game.title"));
        return title == null ? null : _lib.Games.FirstOrDefault(i => string.Equals(i.Title, title, StringComparison.OrdinalIgnoreCase));
    }

    private QsItem? QsFind(string id) => _qsItems.FirstOrDefault(i => i.Id == id);

    private void QsRebuild() { QsBuild(); QsSync(); QsPaintAll(); }

    private void QsBuild()
    {
        _qsItems.Clear();
        var d = _qsDraft;
        bool game = QsHasGame;

        var (lo, hi) = QsTdpRange();
        double tdp = QsTdpNow(d, lo);
        _qsItems.Add(new QsItem { Id = "tdp", Kind = QsKind.Meter, Value = $"{tdp:0}", Level = (float)((tdp - lo) / Math.Max(1, hi - lo)), Disabled = d == null, X = QsL, Y = QsPwY, W = QsR - QsL, H = QsPwH });

        double bright = QsNum("system.brightness", -1), vol = QsNum("system.volume", -1);
        bool muted = Bool(QsGet("system.muted"));
        // 滑条的 X / W 是轨道（触控按轨道位置换算）
        _qsItems.Add(new QsItem { Id = "brightness", Kind = QsKind.Slider, Name = "亮度", Glyph = "qam.sun", Value = bright < 0 ? "--" : $"{bright:0}", Level = (float)Math.Clamp(Math.Max(0, bright) / 100, 0, 1), Disabled = bright < 0, X = QsL + 30, Y = QsPairY, W = 94, H = 44 });
        _qsItems.Add(new QsItem { Id = "volume", Kind = QsKind.Slider, Name = "音量", Glyph = muted ? "qam.mute" : "qam.vol", Value = vol < 0 ? "--" : $"{vol:0}", On = muted, Level = muted ? 0 : (float)Math.Clamp(Math.Max(0, vol) / 100, 0, 1), Disabled = vol < 0, X = 220 + 30, Y = QsPairY, W = 94, H = 44 });

        for (int i = 0; i < QsRowIds.Length; i++)
        {
            var it = QsRowItem(QsRowIds[i], game);
            it.X = QsL; it.Y = QsRowY + i * QsRowH; it.W = QsR - QsL; it.H = QsRowH;
            _qsItems.Add(it);
        }

        foreach (var (id, c, r, w) in QsLay)
        {
            var it = QsCellItem(id, game, w > 1);
            it.Col = c; it.Row = r; it.Span = w;
            it.X = QsL + c * QsPitch; it.Y = QsGridY + r * QsPitch; it.W = w * QsCell + (w - 1) * 10; it.H = QsCell;
            _qsItems.Add(it);
        }
        // 焦点项没了 / 变得不可选：回到功耗
        var f = QsFind(_qsFocusId);
        if (f == null || f.Disabled || (f.Kind is QsKind.Cell or QsKind.Card && !QsCellShown(f.Id))) { _qsFocusId = "tdp"; _qsArmed = null; }
    }

    private QsItem QsRowItem(string id, bool game)
    {
        var it = new QsItem { Id = id, Kind = QsKind.Row };
        var d = _qsDraft;
        switch (id)
        {
            case "fps":
                double fps = QsNum("system.fpsLimit");
                it.Name = "帧率上限"; it.Value = fps > 0 ? $"{fps:0}" : "不限"; it.Off = fps <= 0; it.Disabled = QsGet("system.fpsLimit") == null; break;
            case "refresh":
                double hz = QsNum("system.refresh");
                it.Name = "刷新率"; it.Value = hz > 0 ? $"{hz:0} Hz" : "--"; it.Disabled = QsRefreshRates().Count < 2; break;
            case "scale":
            {
                bool mg = Bool(QsGet("magpie.enabled")), ls = _qsLossless == true;
                it.Name = "画面缩放"; it.Value = mg ? "Magpie" : ls ? "小黄鸭" : "关"; it.Off = !mg && !ls; it.Lamp = !it.Off;
                it.Disabled = !game || QsScaleOptions().Count < 2; break;
            }
            case "osd":
            {
                int osd = d != null && Bool(QsGet("osd.enabled", d)) ? (int)QsNum("osd.level", 1, d) : 0;
                it.Name = "性能显示"; it.Value = QsOsdNames[Math.Clamp(osd, 0, 4)]; it.Off = osd == 0; it.Disabled = d == null; break;
            }
            case "fan":
                it.Name = "风扇"; it.Value = QsFanName(); it.Disabled = d == null; break;
            case "rumble":
            {
                double rumble = QsGet("rumble.strength") != null ? QsNum("rumble.strength") : 100;
                it.Name = "震动"; it.Value = QsRumble.OrderBy(r => Math.Abs(r.V - rumble)).First().N; it.Disabled = QsGet("rumble") is not JsonObject; break;
            }
        }
        return it;
    }

    private QsItem QsCellItem(string id, bool game, bool card)
    {
        var it = new QsItem { Id = id, Kind = card ? QsKind.Card : QsKind.Cell };
        switch (id)
        {
            case "wifi":
                it.Name = "Wi-Fi"; it.Glyph = "qam.wifi"; it.On = _qsWifi == true; it.Disabled = _qsWifi == null;
                it.Tip = "Wi-Fi · " + (_qsWifi != true ? "关" : _qsSsid ?? "未连接"); break;
            case "bt":
                it.Name = "蓝牙"; it.Glyph = "qam.bt"; it.On = _qsBt == true; it.Disabled = _qsBt == null;
                it.Tip = "蓝牙 · " + (_qsBt != true ? "关" : _qsBtCount > 0 ? $"已连接 {_qsBtCount} 个设备" : "开"); break;
            case "air":
                it.Name = "飞行模式"; it.Glyph = "qam.air"; it.On = _qsAir == true; it.Disabled = _qsAir == null;
                it.Tip = "飞行模式 · " + (_qsAir == true ? "开" : "关"); break;
            case "shot": it.Name = "截图"; it.Glyph = "qam.shot"; it.Disabled = !game; it.Tip = "截图"; break;
            case "kbd": it.Name = "键盘"; it.Glyph = "qam.kbd"; it.Tip = "键盘"; break;
            case "pad":
            {
                bool mouse = _qsPadMode == "desktop";
                it.Name = mouse ? "键鼠模式" : "手柄模式"; it.Glyph = mouse ? "qam.mouse" : "qam.pad"; it.Disabled = _qsPadMode == null; it.Tip = it.Name; break;
            }
            case "pwr": it.Name = "电源"; it.Glyph = "qam.off"; it.Tip = "电源"; break;
            case "desk": it.Name = "桌面"; it.Glyph = "qam.desk"; it.Disabled = !ShellMode.Available; it.Tip = "切换到桌面"; break;
            case "vrr":
                it.Name = "可变刷新率"; it.Glyph = ""; it.On = _qsVrr == true; it.Disabled = _qsVrr == null;
                it.Tip = "可变刷新率 · " + (_qsVrr == true ? (game && _qsVrrRange != null ? "开 · " + _qsVrrRange : "开") : "关"); break;
            case "off": it.Name = "关机"; it.Glyph = "qam.off"; it.Tip = "关机"; break;
            case "restart": it.Name = "重启"; it.Glyph = "qam.restart"; it.Tip = "重启"; break;
            case "sleep": it.Name = "睡眠"; it.Glyph = "qam.sleep"; it.Tip = "睡眠"; break;
            case "win":
            {
                int n = 0;
                try { n = WsList().Count(w => w.Hwnd != WsSelf); } catch { }
                it.Name = "窗口切换"; it.Glyph = "qam.win"; it.Sub = $"{n} 个窗口"; break;
            }
            case "lib": it.Name = "游戏库"; it.Glyph = "qam.lib"; it.Sub = $"{_lib.Games.Count} 款"; break;
            case "quit": it.Name = "退出游戏"; it.Glyph = "qam.quit"; it.Sub = QsTitle(); it.Disabled = !game; break;
        }
        return it;
    }

    private string QsFanName()
    {
        var d = _qsDraft;
        if (d == null) return "--";
        if (Str(QsGet("fan.control", d)) == "auto") return "自动";
        if (Bool(QsGet("fan.full", d))) return "满速";
        return Str(QsGet("fan.mode", d)) switch { "quiet" => "静音", "performance" => "性能", _ => "均衡" };
    }

    private List<string> QsScaleOptions()
    {
        var list = new List<string> { "关" };
        if (Bool(QsGet("magpie.available")) || Bool(QsGet("magpie.enabled"))) list.Add("Magpie");
        if (_qsLossless != null) list.Add("小黄鸭");
        return list;
    }

    private double QsTdpNow(JsonObject? d, double lo)
    {
        if (d == null) return lo;
        if (Bool(d["enabled"]) && Str(d["mode"]) == "manual") return QsNum("manual.tdp", lo, d);
        double target = QsNum("power.target.pl1", -1);
        return target > 0 ? target : QsNum("manual.tdp", lo, d);
    }

    private (double Lo, double Hi) QsTdpRange()
    {
        var quick = (QsGet("quick", _qsDraft) as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue(out double x) ? x : 0).Where(x => x > 0).ToList() ?? new();
        double lo = quick.Count > 0 ? Math.Min(5, quick.Min()) : 5, hi = quick.Count > 0 ? Math.Max(35, quick.Max()) : 35;
        return (lo, hi);
    }

    private List<double> QsRefreshRates() =>
        (QsGet("system.refreshRates") as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue(out double x) ? x : 0).Where(x => x >= 60).Distinct().OrderByDescending(x => x).ToList() ?? new();

    /// <summary>这一格此刻在网格里（电源展开时第三行左三格是关机 / 重启 / 睡眠）。</summary>
    private bool QsCellShown(string id) =>
        QsPm.Contains(id) ? _qsPm : QsR2.Contains(id) ? !_qsPm : true;

    private string? QsCellAt(int r, int c)
    {
        foreach (var (id, cc, rr, w) in QsLay)
        {
            if (rr != r || !QsCellShown(id)) continue;
            if (c >= cc && c < cc + w) return id;
        }
        return null;
    }

    private static bool QsIsGrid(string id) => QsLay.Any(l => l.Id == id);

    private bool QsSelectable(string id) => QsFind(id) is { Disabled: false } it && (it.Kind is not (QsKind.Cell or QsKind.Card) || QsCellShown(id));

    // ---------------- 动画目标 ----------------

    /// <summary>按当前状态给每个元素定补间目标（焦点、开关底色、确认红、提示、选项行箭头、小灯、电源展开）。</summary>
    private void QsSync()
    {
        string f = _qsFocusId;
        QsTo("g.pm", _qsPm ? 1 : 0, 360);
        foreach (var it in _qsItems)
        {
            switch (it.Kind)
            {
                case QsKind.Row:
                    QsTo("r.ch." + it.Id, f == it.Id ? 1 : 0, 160, Bezier.Ease_);
                    QsTo("r.lamp." + it.Id, it.Lamp ? 1 : 0, 300);
                    QsTo("r.dis." + it.Id, it.Disabled ? 1 : 0, 200, Bezier.Ease_);
                    break;
                case QsKind.Cell or QsKind.Card:
                {
                    bool arm = _qsArmed == it.Id;
                    QsTo("g.f." + it.Id, f == it.Id && !arm ? 1 : 0, 160, Bezier.Ease_);
                    QsTo("g.on." + it.Id, it.On && !arm ? 1 : 0, 200, Bezier.Ease_);
                    QsTo("g.arm." + it.Id, arm ? 1 : 0, 200, Bezier.Ease_);
                    QsTo("g.dis." + it.Id, it.Disabled ? 1 : 0, 200, Bezier.Ease_);
                    QsTo("g.tip." + it.Id, it.Kind == QsKind.Cell && QsCellShown(it.Id) && (f == it.Id || arm) ? 1 : 0, 200);
                    break;
                }
            }
        }
        QsTo("t.dis.tdp", QsFind("tdp")?.Disabled == true ? 1 : 0, 200, Bezier.Ease_);
        // 光标只给功耗块和选项行；刚出现（从网格回来 / 刚打开）时直接落位，之后在行间弹簧移动
        bool blk = f == "tdp" || QsRowIds.Contains(f);
        if (blk)
        {
            var it = QsFind(f)!;
            float y = f == "tdp" ? it.Y - 2 : it.Y, h = f == "tdp" ? it.H + 4 : it.H;
            bool snap = !_qsCurPlaced || !_qsCurShown;
            _qsCurPlaced = true;
            QsCursorTo(y, h, true, snap);
        }
        else QsCursorTo(0, 0, false, false);
    }
    // ---------------- 绘制 ----------------

    private void QsDrawInto(ref CompositionDrawingSurface? surface, SpriteVisual v, float w, float h, Action<CanvasDrawingSession> paint)
    {
        float k = QsK, raster = k * Chrome.Dpi;
        var size = new Windows.Foundation.Size(Math.Max(1, Math.Ceiling(w * raster)), Math.Max(1, Math.Ceiling(h * raster)));
        if (surface == null || surface.Size != size)
        {
            var s = G.Surface((int)size.Width, (int)size.Height);
            var brush = (CompositionSurfaceBrush)v.Brush;
            (brush.Surface as IDisposable)?.Dispose();
            brush.Surface = s;
            surface = s;
        }
        using (var ds = CanvasComposition.CreateDrawingSession(surface))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(raster);
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            paint(ds);
        }
        v.Size = new Vector2(w * k, h * k);
    }

    private void QsPaintAll()
    {
        if (_qs == null) return;
        QsPaintLayer('t'); QsPaintLayer('r'); QsPaintLayer('g');
        QsPaintCursor();
        QsLayout();
    }

    private void QsPaintLayer(char layer)
    {
        try
        {
            switch (layer)
            {
                case 't': QsDrawInto(ref _qsTopS, _qsTopV, QsW, QsTopH, QsPaintTop); break;
                case 'r': QsDrawInto(ref _qsRowsS, _qsRowsV, QsW, QsRowsH, ds => { ds.Transform = Matrix3x2.CreateTranslation(0, -QsRowsY) * ds.Transform; QsPaintRows(ds); }); break;
                case 'g': QsDrawInto(ref _qsGridS, _qsGridV, QsW, QsGridLH, ds => { ds.Transform = Matrix3x2.CreateTranslation(0, -QsGridLY) * ds.Transform; QsPaintGrid(ds); }); break;
            }
        }
        catch (Exception ex) { Log.Warn($"quick panel paint {layer}: {ex.Message}"); }
    }

    private static CanvasTextLayout QsLay_(ICanvasResourceCreator ds, string s, FontSpec f, float size, float ls = 0) =>
        TextKit.Layout(ds, s, f, size, 1000, CanvasHorizontalAlignment.Left, ls);

    private static float QsTextW(CanvasDrawingSession ds, string s, FontSpec f, float size, float ls = 0)
    {
        using var l = QsLay_(ds, s, f, size, ls);
        return (float)l.LayoutBounds.Width;
    }

    /// <summary>一行字：行框竖直居中在 cy；right = true 时 x 是右沿。返回宽度。</summary>
    private static float QsTextC(CanvasDrawingSession ds, string s, FontSpec f, float size, float x, float cy, Color c, bool right = false, float ls = 0)
    {
        using var l = QsLay_(ds, s, f, size, ls);
        var b = l.LayoutBounds;
        float w = (float)b.Width;
        ds.DrawTextLayout(l, (right ? x - w : x) - (float)b.X, cy - (float)b.Height / 2 - (float)b.Y, c);
        return w;
    }

    /// <summary>一段字放在基线 baseline 上（中英文各按自己的字形基线，混排对齐）。</summary>
    private static float QsTextB(CanvasDrawingSession ds, string s, FontSpec f, float size, float x, float baseline, Color c, bool right = false)
    {
        using var l = QsLay_(ds, s, f, size);
        var b = l.LayoutBounds;
        var (bl, _) = TextInk.Metrics(ds, f, size, s, "0");
        float w = (float)b.Width;
        ds.DrawTextLayout(l, (right ? x - w : x) - (float)b.X, baseline - bl, c);
        return w;
    }

    private static void QsIcon(CanvasDrawingSession ds, string glyph, float cx, float cy, float px, Color c, float stroke = 1.5f) =>
        LineIcons.Draw(ds, glyph, cx, cy, px / 1.15f, c, stroke: stroke);

    private static Color QsLerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    private void QsPaintTop(CanvasDrawingSession ds)
    {
        // 状态栏：时间；剩余时间、电量、电池图标（17 号）
        float cy = QsStatusY + 18;
        QsTextC(ds, DateTime.Now.ToString("HH:mm"), TextKit.Num(600), 17, QsL, cy, QsInk);
        var bat = SystemInfo.Battery();
        if (bat.HasBattery)
        {
            string pct = $"{bat.Percent}%";
            var (bl, cap) = TextInk.Metrics(ds, TextKit.Num(600), 17, pct, "0");
            float baseY = cy + cap / 2;
            float battW = MathF.Round(cap * 1.9f) + MathF.Max(1.2f, cap * 0.09f) * 0.5f + MathF.Max(1.5f, cap * 0.11f);
            float x = QsR - battW;
            BatteryGlyph.Draw(ds, x, baseY - cap, cap, QsInk, bat, QsAcc, QsRed);
            x -= 8;
            x -= QsTextB(ds, pct, TextKit.Num(600), 17, x, baseY, QsInk, right: true);
            if (!bat.OnAc && QsBatteryLeft() is { } left)
            {
                x -= 8;
                QsTextB(ds, $"剩余 {(int)left.TotalHours}:{left.Minutes:00}", TextKit.Num(400), 17, x, baseY, QsInk2, right: true);
            }
        }

        // 功耗块
        var tdp = QsFind("tdp");
        if (tdp != null)
        {
            using var layer = ds.CreateLayer(1 - 0.75f * QsTv("t.dis.tdp"));
            const float base_ = 122.2f;
            QsIcon(ds, "qam.bolt", QsL + 9, base_ - 9, 18, QsInk2, 1.35f);
            QsTextB(ds, "功耗", TextKit.Ui(600), 15, QsL + 26, base_, QsInk2);
            float wW = QsTextB(ds, "W", TextKit.Ui(600), 17, QsR, base_, QsInk2, right: true);
            QsTextB(ds, tdp.Value, TextKit.Num(700), 40, QsR - wW - 4, base_, QsInk, right: true);
            // 实际 x W · y °C
            double pkg = QsNum("power.actual.package", -1), temp = QsNum("power.live.cpuTemp", -1);
            float sx = QsL + 26, sb = 143.6f;
            const float sp = 3.6f;   // 13 号字一个空格宽（排版量宽时行尾空格不算，手动补）
            sx += QsTextB(ds, "实际", TextKit.Ui(400), 13, sx, sb, QsInk2) + sp;
            sx += QsTextB(ds, pkg >= 0 ? $"{pkg:0.0}" : "--", TextKit.Num(600), 13, sx, sb, QsInk) + sp;
            sx += QsTextB(ds, "W", TextKit.Ui(400), 13, sx, sb, QsInk2) + sp;
            sx += QsTextB(ds, "·", TextKit.Ui(400), 13, sx, sb, QsInk2) + sp;
            sx += QsTextB(ds, temp >= 0 ? $"{temp:0}" : "--", TextKit.Num(600), 13, sx, sb, QsInk) + sp;
            QsTextB(ds, "°C", TextKit.Ui(400), 13, sx, sb, QsInk2);
            // 3 px 轨道 + 8/15/25/35 刻点
            var (lo, hi) = QsTdpRange();
            float ty = QsPwY + QsPwH - 12 - 3, tw = QsR - QsL;
            ds.FillRoundedRectangle(QsL, ty, tw, 3, 1.5f, 1.5f, Gfx.WithAlpha(QsInk, 0.22));
            float p = Math.Clamp(tdp.Level, 0, 1);
            if (p > 0) ds.FillRoundedRectangle(QsL, ty, tw * p, 3, 1.5f, 1.5f, QsInk);
            double now = lo + p * (hi - lo);
            foreach (var t in new[] { 8.0, 15, 25, 35 })
            {
                if (t < lo || t > hi) continue;
                float tx = QsL + tw * (float)((t - lo) / (hi - lo));
                ds.FillRoundedRectangle(tx - 1, ty - 3, 2, 9, 1, 1, t <= now + 0.01 ? QsInk : Gfx.WithAlpha(QsInk, 0.35));
            }
        }

        // 亮度 / 音量：细滑条，被调的那条点亮、数字弹一下
        foreach (var (id, x0, key) in new[] { ("brightness", QsL, "bri"), ("volume", 220f, "vol") })
        {
            var it = QsFind(id);
            if (it == null) continue;
            float hot = QsTv("t.hot." + key), pop = QsTv("t.pop." + key);
            float mcy = QsPairY + 22;
            using var dis = ds.CreateLayer(it.Disabled ? 0.25f : 1);
            QsIcon(ds, it.Glyph, x0 + 10, mcy, 20, QsLerp(QsInk2, QsInk, hot));
            float tx = x0 + 30, tw = 94;
            ds.FillRoundedRectangle(tx, mcy - 1.5f, tw, 3, 1.5f, 1.5f, Gfx.WithAlpha(QsInk, 0.22));
            bool muted = id == "volume" && it.On;
            using (ds.CreateLayer(muted ? 0.3f : 1))
            {
                float lvl = Math.Clamp(it.Level, 0, 1);
                if (lvl > 0) ds.FillRoundedRectangle(tx, mcy - 1.5f, tw * lvl, 3, 1.5f, 1.5f, QsLerp(Gfx.WithAlpha(QsInk, 0.7), QsInk, hot));
                float s = 1 + 0.22f * pop, nx = x0 + 164;
                var t0 = ds.Transform;
                float w = QsTextW(ds, it.Value, TextKit.Num(600), 15);
                ds.Transform = Matrix3x2.CreateScale(s, new Vector2(nx - w / 2, mcy)) * t0;
                QsTextC(ds, it.Value, TextKit.Num(600), 15, nx, mcy, QsLerp(QsInk2, QsInk, hot), right: true);
                ds.Transform = t0;
            }
        }
    }

    private void QsPaintRows(CanvasDrawingSession ds)
    {
        foreach (var it in _qsItems)
        {
            if (it.Kind != QsKind.Row) continue;
            float cy = it.Y + it.H / 2, dis = QsTv("r.dis." + it.Id);
            using var layer = ds.CreateLayer(1 - 0.75f * dis);
            QsTextC(ds, it.Name, TextKit.Ui(400), 15, QsL, cy, QsInk);
            float ch = QsTv("r.ch." + it.Id), kl = QsTv("r.kl." + it.Id), kr = QsTv("r.kr." + it.Id);
            if (ch > 0.004f) QsIcon(ds, "qam.r", QsR - 8 + 4 * kr, cy, 16, Gfx.WithAlpha(QsInk2, QsInk2.A / 255f * ch));
            float vx = QsR - 16 - 10;
            float vw = QsTextC(ds, it.Value, TextKit.Num(it.Off ? (ushort)400 : (ushort)600), 15, vx, cy, it.Off ? QsInk2 : QsInk, right: true);
            float lx = vx - vw - 10 - 16;
            if (ch > 0.004f) QsIcon(ds, "qam.l", lx + 8 - 4 * kl, cy, 16, Gfx.WithAlpha(QsInk2, QsInk2.A / 255f * ch));
            float lamp = QsTv("r.lamp." + it.Id);
            if (lamp > 0.004f)
            {
                float s = 6 * (0.4f + 0.6f * lamp), lcx = lx - 8 - 3;
                ds.FillRoundedRectangle(lcx - s / 2, cy - s / 2, s, s, 2 * s / 6, 2 * s / 6, Gfx.WithAlpha(QsAcc, lamp));
            }
        }
    }

    private readonly Dictionary<(float W, CanvasDevice D), CanvasRenderTarget> _qsGlow = new();

    /// <summary>焦点柔光（box-shadow 0 0 16px 6px 12 % 白）：每种宽度画一次缓存。</summary>
    private CanvasRenderTarget QsGlowFor(CanvasDrawingSession ds, float w)
    {
        if (_qsGlow.TryGetValue((w, ds.Device), out var hit)) return hit;
        float raster = QsK * Chrome.Dpi, pad = 30;
        var rt = new CanvasRenderTarget(ds, w + 2 * pad, QsCell + 2 * pad, 96 * raster);
        using (var cl = new CanvasCommandList(ds))
        {
            using (var c = cl.CreateDrawingSession()) c.FillRoundedRectangle(pad - 6, pad - 6, w + 12, QsCell + 12, 26, 26, Gfx.WithAlpha(QsInk, 0.12));
            using var g = rt.CreateDrawingSession();
            g.Clear(Colors.Transparent);
            using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 8, BorderMode = EffectBorderMode.Soft };
            g.DrawImage(blur);
            // 只留元素外面（和 CSS 外阴影一样，盒子里面不叠）
            g.Blend = CanvasBlend.Copy;
            g.FillRoundedRectangle(pad, pad, w, QsCell, 20, 20, Colors.Transparent);
        }
        _qsGlow[(w, ds.Device)] = rt;
        return rt;
    }

    private void QsPaintGrid(CanvasDrawingSession ds)
    {
        float pm = QsTv("g.pm", _qsPm ? 1 : 0);
        var tips = new List<(QsItem It, float Ox, float Oy, float A)>();
        foreach (var it in _qsItems)
        {
            if (it.Kind is not (QsKind.Cell or QsKind.Card)) continue;
            float vis = 1, dy = 0;
            if (QsPm.Contains(it.Id)) { vis = pm; dy = 10 * (1 - pm); }
            else if (QsR2.Contains(it.Id)) { vis = 1 - pm; dy = -10 * pm; }
            if (vis < 0.004f) continue;
            float dis = QsTv("g.dis." + it.Id), on = QsTv("g.on." + it.Id), arm = QsTv("g.arm." + it.Id), f = QsTv("g.f." + it.Id);
            float press = QsTv("g.pr." + it.Id);
            float x = it.X, y = it.Y + dy, w = it.W, h = it.H;
            var t0 = ds.Transform;
            float sc = 1 - 0.08f * press;
            if (sc != 1) ds.Transform = Matrix3x2.CreateScale(sc, new Vector2(x + w / 2, y + h / 2)) * t0;
            using (var layer = ds.CreateLayer(vis * (1 - 0.75f * dis)))
            {
                if (f > 0.004f)
                {
                    var glow = QsGlowFor(ds, w);
                    ds.DrawImage(glow, new Windows.Foundation.Rect(x - 30, y - 30, w + 60, h + 60), glow.Bounds, f);
                    ds.DrawRoundedRectangle(x - 4.5f, y - 4.5f, w + 9, h + 9, 24.5f, 24.5f, Gfx.WithAlpha(QsInk, f), 3);
                }
                var bg = QsLerp(QsLerp(QsOffBg, Gfx.WithAlpha(QsInk, 0.92), on), QsRed, arm);
                ds.FillRoundedRectangle(x, y, w, h, 20, 20, bg);
                var fg = QsLerp(QsInk, QsBg, Math.Max(on, arm));
                if (it.Kind == QsKind.Cell)
                {
                    if (it.Id == "vrr") QsTextC(ds, "VRR", TextKit.Num(700), 13, x + w / 2 + QsTextW(ds, "VRR", TextKit.Num(700), 13, 0.5f) / 2, y + h / 2, fg, right: true, ls: 0.5f);
                    else QsIcon(ds, it.Glyph, x + w / 2, y + h / 2, 24, fg);
                    float tip = QsTv("g.tip." + it.Id);
                    if (tip > 0.004f) tips.Add((it, 0, dy, tip * vis));
                }
                else
                {
                    // 宽卡：图标块 + 标题 15/600 + 状态小字 12；确认时图标块让开，红底上写「再按一次退出」和后果
                    float gx = x + 12 - 50 * arm;
                    using (ds.CreateLayer(1 - arm))
                    {
                        ds.FillRoundedRectangle(gx, y + (h - 38) / 2, 38, 38, 13, 13, Gfx.WithAlpha(QsInk, 0.10));
                        QsIcon(ds, it.Glyph, gx + 19, y + h / 2, 20, fg);
                    }
                    float tx = x + 62 - 46 * arm;
                    using var clip = ds.CreateLayer(1, new Windows.Foundation.Rect(x, y, w, h));
                    bool armed = _qsArmed == it.Id;
                    string title = armed ? (it.Id == "quit" ? "再按一次退出" : "再按一次") : it.Name;
                    string sub = armed ? "未保存的进度会丢失" : it.Sub;
                    QsTextC(ds, title, TextKit.Ui(600), 15, tx, sub.Length > 0 ? y + 21.85f : y + h / 2, fg);
                    if (sub.Length > 0) QsTextC(ds, sub, TextKit.Num(400), 12, tx, y + 42.85f, armed ? Gfx.WithAlpha(QsBg, 0.7) : QsInk2);
                }
            }
            ds.Transform = t0;
        }
        // 小方块焦点时，上方浮出名字和状态（确认时是红底的后果）
        foreach (var (it, _, dy, a) in tips)
        {
            bool armed = _qsArmed == it.Id;
            string text = armed ? (it.Id == "restart" ? "再按一次重启 · 未保存的进度会丢失" : "再按一次关机 · 未保存的进度会丢失") : it.Tip;
            if (string.IsNullOrEmpty(text)) continue;
            using var l = QsLay_(ds, text, TextKit.Ui(600), 13);
            var b = l.LayoutBounds;
            float bw = (float)b.Width + 22, bh = (float)b.Height + 14;
            float bx = it.Col == 0 ? it.X : it.Col >= 4 ? it.X + it.W - bw : it.X + it.W / 2 - bw / 2;
            bx = Math.Clamp(bx, 8, QsW - 8 - bw);
            float by = it.Y + dy - 11.6f - bh + 6 * (1 - a);
            using var layer = ds.CreateLayer(a);
            ds.FillRoundedRectangle(bx, by, bw, bh, 12, 12, armed ? QsRed : Gfx.WithAlpha(QsBg, 0.9));
            ds.DrawTextLayout(l, bx + 11 - (float)b.X, by + 7 - (float)b.Y, armed ? QsBg : QsInk);
        }
    }

    /// <summary>光标：3 px 白框 + 柔光，九宫格拉伸到当前行高。</summary>
    private void QsPaintCursor()
    {
        float raster = QsK * Chrome.Dpi;
        if (_qsCurS != null && _qsCurFor == raster) return;
        _qsCurFor = raster;
        const float S = 120, m = 20;
        _qsCurS = G.Surface((int)Math.Ceiling(S * raster), (int)Math.Ceiling(S * raster));
        using (var ds = CanvasComposition.CreateDrawingSession(_qsCurS))
        {
            ds.Clear(Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(raster);
            using (var cl = new CanvasCommandList(ds))
            {
                using (var c = cl.CreateDrawingSession()) c.FillRoundedRectangle(m - 3, m - 3, S - 2 * m + 6, S - 2 * m + 6, 19, 19, Gfx.WithAlpha(QsInk, 0.14));
                using var blur = new GaussianBlurEffect { Source = cl, BlurAmount = 7, BorderMode = EffectBorderMode.Soft };
                ds.DrawImage(blur);
            }
            ds.Blend = CanvasBlend.Copy;
            ds.FillRoundedRectangle(m, m, S - 2 * m, S - 2 * m, 16, 16, Colors.Transparent);
            ds.Blend = CanvasBlend.SourceOver;
            ds.DrawRoundedRectangle(m - 1.5f, m - 1.5f, S - 2 * m + 3, S - 2 * m + 3, 17.5f, 17.5f, QsInk, 3);
        }
        var nine = G.C.CreateNineGridBrush();
        var src = G.C.CreateSurfaceBrush(_qsCurS); src.Stretch = CompositionStretch.Fill; nine.Source = src;
        nine.SetInsets(40 * raster);
        nine.SetInsetScales(1 / raster * QsK);
        _qsCurV.Brush = nine;
    }

    // ---------------- 逐帧：弹簧 + 补间 ----------------

    private int _qsAnimFrames, _qsAnimJanks;
    private long _qsAnimMax;

    /// <summary>只给面板里的小补间（颜色、箭头、提示）逐帧重画；面板平移和光标都是合成层弹簧，不在这里。</summary>
    private void QsStartTicking()
    {
        if (_qsTicking) return;
        _qsFrameAt = NowMs;
        _qsTicking = true;
        _qsAnimFrames = _qsAnimJanks = 0; _qsAnimMax = 0;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += QsFrame;
    }

    private void QsFrame(object? sender, object e)
    {
        long now = NowMs, gap = now - _qsFrameAt;
        if (_qsAnimFrames++ > 0) { _qsAnimMax = Math.Max(_qsAnimMax, gap); if (gap > 25) _qsAnimJanks++; }
        _qsFrameAt = now;
        bool any = false;
        foreach (var (layer, idx) in new[] { ('t', 0), ('r', 1), ('g', 2) })
        {
            bool on = QsLayerAnimating(layer);
            if (on || _qsLayerWas[idx]) QsPaintLayer(layer);
            _qsLayerWas[idx] = on;
            any |= on;
        }
        if (any) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= QsFrame;
        _qsTicking = false;
        if (_qsAnimFrames > 20 || _qsAnimJanks > 0) Log.Info($"quick panel repaint frames={_qsAnimFrames} max={_qsAnimMax}ms janks={_qsAnimJanks}");
    }

    /// <summary>静态排布（尺寸变了才要重摆）：各层位置、右边线、冻结帧按面板右沿裁（表达式绑在面板平移上，合成线程算）。</summary>
    private void QsLayout()
    {
        if (_qs == null) return;
        float k = QsK, dpi = Chrome.Dpi, H = QsViewH;
        _qsTopV.Offset = Vector3.Zero;
        _qsRowsV.Offset = new Vector3(0, QsRowsY * k, 0);
        _qsGridV.Offset = new Vector3(0, QsGridLY * k, 0);
        _qsEdgeV.Offset = new Vector3((QsW - 1) * k, 0, 0);
        _qsEdgeV.Size = new Vector2(Math.Max(1 / dpi, k), H * k);
        float boxW = (QsW + 4) * k;
        if (_qsGlassBox.Size.X != boxW || _qsGlassBox.Size.Y != H * k)
        {
            _qsGlassBox.Size = new Vector2(boxW, H * k);
            _qsGlassBase.Size = new Vector2(boxW, H * k);
            var clip = G.C.CreateExpressionAnimation("Max(0, w - (pw + s.Offset.X))");
            clip.SetReferenceParameter("s", _qsSlide);
            clip.SetScalarParameter("w", boxW);
            clip.SetScalarParameter("pw", QsW * k);
            _qsGlassClip.StartAnimation("RightInset", clip);
        }
        if (_qsThumb is { } th)
        {
            // 退回旧办法（面板画在游戏库窗口里）时：游戏画面从面板右沿开始
            int edge = (int)MathF.Round(QsW * k * dpi);
            var (gl, gt, gw, gh) = _qsGameRect;
            int left = Math.Max(edge, gl), r = gl + gw;
            if (r <= left) th.PlaceCropped(0, 0, 0, 0, 0, 0, 0, 0);
            else
            {
                double sx = (double)th.SourceW / gw, sy = (double)th.SourceH / gh;
                th.PlaceCropped(left, gt, r - left, gh, (int)Math.Round((left - gl) * sx), 0, (int)Math.Round((r - left) * sx), (int)Math.Round(gh * sy), 1);
            }
        }
    }

    /// <summary>光标：合成层弹簧（460 / 36 / 0.9）挪到目标行；snap = 直接放过去（刚出现时）。</summary>
    private void QsCursorTo(float y, float h, bool show, bool snap)
    {
        float k = QsK;
        var off = new Vector3(0, (y - 20) * k, 0);
        var size = new Vector2(QsW * k, (h + 40) * k);
        if (show != _qsCurShown)
        {
            _qsCurShown = show;
            _qsCurV.StartAnimation("Opacity", G.Scalar(_qsCurV.Opacity, show ? 1 : 0, Reduced ? 1 : 160, Bezier.Ease_));
        }
        if (!show) return;
        if (snap || Reduced)
        {
            _qsCurV.StopAnimation("Offset"); _qsCurV.StopAnimation("Size");
            _qsCurV.Offset = off; _qsCurV.Size = size;
            return;
        }
        var spring = new Spring(460, 36, 0.9);
        _qsCurV.StartAnimation("Offset", G.SpringV3(off, spring));
        var sz = G.C.CreateSpringVector2Animation();
        sz.FinalValue = size; sz.DampingRatio = spring.Zeta; sz.Period = spring.Period;
        _qsCurV.StartAnimation("Size", sz);
    }

    // ---------------- 输入 ----------------

    private void QsSetFocus(string id)
    {
        if (_qsFocusId != id) _qsArmed = null;
        _qsFocusId = id;
        if (QsFind(id) is { Kind: QsKind.Cell or QsKind.Card } it) { _qsGc = it.Col; _qsGr = it.Row; }
        QsSync();
        QsPaintLayer('g');
        QsLayout();
    }

    /// <summary>
    /// 上下：功耗 → 6 行选项 → 网格（进网格落在上次的那一列）；左右：功耗 ±1 W、选项直接改值。网格里按格子走，
    /// 宽卡跨两列；第一行再往上回到最后一行选项；电源展开时离开第三行左三格就收回。
    /// </summary>
    private void QsMove(Direction dir)
    {
        string f = _qsFocusId;
        if (QsIsGrid(f)) { QsGridMove(dir); return; }
        if (dir is Direction.Up or Direction.Down)
        {
            int i = Array.IndexOf(QsCol, f), st = dir == Direction.Up ? -1 : 1;
            for (i += st; i >= 0 && i < QsCol.Length; i += st)
            {
                string g = QsCol[i];
                if (g == "grid") { if (QsEnterGrid()) return; continue; }
                if (QsSelectable(g)) { QsSetFocus(g); Cue(Sfx.Nav); return; }
            }
            Cue(Sfx.Bump);
            return;
        }
        QsAdjust(dir == Direction.Left ? -1 : 1);
    }

    private bool QsEnterGrid()
    {
        for (int r = 0; r < 3; r++)
            for (int i = 0; i < 5; i++)
            {
                int c = (_qsGc + i) % 5;
                if (QsCellAt(r, c) is { } id && QsSelectable(id)) { QsSetFocus(id); Cue(Sfx.Nav); return true; }
            }
        return false;
    }

    private void QsGridMove(Direction dir)
    {
        string cur = _qsFocusId;
        int r = _qsGr, c = _qsGc, dr = dir == Direction.Up ? -1 : dir == Direction.Down ? 1 : 0, dc = dir == Direction.Left ? -1 : dir == Direction.Right ? 1 : 0;
        while (true)
        {
            r += dr; c += dc;
            if (c < 0 || c > 4 || r > 2) { Cue(Sfx.Bump); return; }
            if (r < 0)
            {
                if (_qsPm) QsClosePm(false);
                for (int i = QsCol.Length - 2; i >= 0; i--)
                    if (QsSelectable(QsCol[i])) { _qsGr = 0; QsSetFocus(QsCol[i]); Cue(Sfx.Nav); return; }
                Cue(Sfx.Bump); return;
            }
            var id = QsCellAt(r, c);
            if (id == null || id == cur || !QsSelectable(id)) continue;
            if (_qsPm && (r != 2 || c > 2)) QsClosePm(false);
            _qsGr = r; _qsGc = c;
            QsSetFocus(id);
            if (QsFind(id) is { } it) { _qsGc = c; _qsGr = r; _ = it; }
            Cue(Sfx.Nav);
            return;
        }
    }

    private void QsClosePm(bool focusPower)
    {
        _qsPm = false; _qsArmed = null;
        if (focusPower) { _qsGr = 2; _qsGc = 0; _qsFocusId = "pwr"; }
        QsSync(); QsPaintLayer('g');
    }

    /// <summary>LB / RB：亮度 ±5（面板开着时任何焦点都生效）。</summary>
    internal void QsTab(int delta) => QsHot("bri", delta);

    /// <summary>LB/RB 调亮度、LT/RT 调音量：被调的那条点亮 0.9 s、数字弹一下。</summary>
    internal void QsHot(string which, int d)
    {
        if (!_qsOpen) return;
        var it = QsFind(which == "bri" ? "brightness" : "volume");
        if (it == null || it.Disabled) { Cue(Sfx.Bump); return; }
        string path = it.Id;
        double v = Math.Clamp(Math.Round(QsNum("system." + path) / 5) * 5 + d * 5, 0, 100);
        if (_qsState?["system"] is JsonObject sys) { sys[path] = v; if (path == "volume") sys["muted"] = false; }
        QsQueueTouchValue(path == "brightness" ? "set_brightness" : "set_volume", JsonValue.Create(v));
        Cue(d > 0 ? Sfx.SliderUp : Sfx.SliderDown);
        QsHotMark(which);
        QsBuild(); QsPaintLayer('t');
    }

    private long _qsHotAtBri, _qsHotAtVol;
    private void QsHotMark(string which)
    {
        QsTo("t.hot." + which, 1, 200, Bezier.Ease_);
        QsKick("t.pop." + which, 1, 0, 320);
        long at = NowMs;
        if (which == "bri") _qsHotAtBri = at; else _qsHotAtVol = at;
        After(900, () => { if ((which == "bri" ? _qsHotAtBri : _qsHotAtVol) == at && _qsOpen) QsTo("t.hot." + which, 0, 200, Bezier.Ease_); });
    }

    /// <summary>B：先取消确认、收起电源，再收起面板。</summary>
    private void QsBack()
    {
        if (_qsArmed != null) { _qsArmed = null; Cue(Sfx.Back); QsSync(); QsPaintLayer('g'); return; }
        if (_qsPm) { QsClosePm(true); Cue(Sfx.Back); QsSync(); QsPaintLayer('g'); return; }
        CloseQuickPanel();
    }

    /// <summary>← →：功耗 ±1 W（长按连续，后台写入合并），选项循环改值。</summary>
    private void QsAdjust(int s)
    {
        string f = _qsFocusId;
        if (f == "tdp")
        {
            var it = QsFind("tdp");
            if (it == null || it.Disabled || _qsDraft == null) { Cue(Sfx.Bump); return; }
            var (lo, hi) = QsTdpRange();
            double now = Math.Round(QsTdpNow(_qsDraft, lo)), v = Math.Clamp(now + s, lo, hi);
            if (v == now) { Cue(Sfx.Bump); return; }
            QsSetTdp(v);
            QsQueueTouchValue("save_power", _qsDraft.DeepClone());
            Cue(s > 0 ? Sfx.SliderUp : Sfx.SliderDown);
            QsBuild(); QsPaintLayer('t');
            return;
        }
        if (!QsRowIds.Contains(f)) return;
        var row = QsFind(f);
        if (row == null || row.Disabled) { Cue(Sfx.Bump); return; }
        QsCycle(f, s);
        QsKick(s < 0 ? "r.kl." + f : "r.kr." + f, 1, 0, 140);
        Cue(s > 0 ? Sfx.SliderUp : Sfx.SliderDown);
        QsRebuild();
    }

    private void QsSetTdp(double v)
    {
        if (_qsDraft is not { } d) return;
        d["enabled"] = true; d["mode"] = "manual";
        if (d["manual"] is not JsonObject m) { m = new JsonObject(); d["manual"] = m; }
        m["tdp"] = v;
    }

    private static int QsStep(int cur, int n, int s) => cur < 0 ? (s > 0 ? 0 : n - 1) : ((cur + s) % n + n) % n;

    /// <summary>选项行改一格（循环）；本地先变，再发给后台。</summary>
    private void QsCycle(string id, int s)
    {
        var d = _qsDraft;
        switch (id)
        {
            case "fps":
            {
                int cur = Array.IndexOf(QsFps, (int)QsNum("system.fpsLimit"));
                if (cur < 0) { double now = QsNum("system.fpsLimit"); cur = s > 0 ? Array.FindLastIndex(QsFps, x => x < now) : Array.FindIndex(QsFps, x => x > now) + 1; }
                int next = QsFps[QsStep(cur, QsFps.Length, s)];
                if (_qsState?["system"] is JsonObject sys) sys["fpsLimit"] = next;
                QsQueueTouchValue("set_fps_limit", JsonValue.Create(next));
                break;
            }
            case "refresh":
            {
                var rates = QsRefreshRates();
                if (rates.Count < 2) return;
                double hz = rates[QsStep(rates.IndexOf(QsNum("system.refresh")), rates.Count, s)];
                if (_qsState?["system"] is JsonObject sys) sys["refresh"] = hz;
                QsQueueTouchValue("set_refresh_rate", JsonValue.Create(hz));
                break;
            }
            case "scale":
            {
                var opts = QsScaleOptions();
                bool mg = Bool(QsGet("magpie.enabled")), ls = _qsLossless == true;
                string want = opts[QsStep(opts.IndexOf(mg ? "Magpie" : ls ? "小黄鸭" : "关"), opts.Count, s)];
                _ = QsSetScale(want, mg, ls);
                break;
            }
            case "osd" when d != null:
            {
                int cur = Bool(QsGet("osd.enabled", d)) ? (int)QsNum("osd.level", 1, d) : 0;
                int next = QsStep(Math.Min(cur, 3), 4, s);   // 关 / 简洁 / 标准 / 详细（完整只在设置里选）
                if (d["osd"] is not JsonObject osd) { osd = new JsonObject(); d["osd"] = osd; }
                osd["enabled"] = next > 0;
                if (next > 0) osd["level"] = next;
                QsQueueTouchValue("save_power", d.DeepClone());
                break;
            }
            case "fan" when d != null:
            {
                string[] names = { "自动", "静音", "满速" };
                int next = QsStep(Array.IndexOf(names, QsFanName()), names.Length, s);
                if (d["fan"] is not JsonObject fan) { fan = new JsonObject(); d["fan"] = fan; }
                if (next == 0) { fan["control"] = "auto"; fan["full"] = false; }
                else if (next == 1) { fan["control"] = "custom"; fan["mode"] = "quiet"; fan["full"] = false; }
                else { fan["control"] = "custom"; fan["full"] = true; }
                QsQueueTouchValue("save_power", d.DeepClone());
                break;
            }
            case "rumble":
            {
                double cur = QsGet("rumble.strength") != null ? QsNum("rumble.strength") : 100;
                int i = Array.IndexOf(QsRumble, QsRumble.OrderBy(r => Math.Abs(r.V - cur)).First());
                int v = QsRumble[QsStep(i, QsRumble.Length, s)].V;
                if (_qsState?["rumble"] is JsonObject ru) ru["strength"] = v;
                QsQueueTouchValue("set_rumble_strength", JsonValue.Create(v));
                break;
            }
        }
    }

    /// <summary>
    /// 画面缩放三选一：本地先变，后台写入排队（连按只做最后一次）。执行时先读后台此刻的真实状态再补到目标
    /// （toggle_magpie 是开关，不能按次数发）；Magpie 和小黄鸭互斥，先关掉另一个。
    /// </summary>
    private string _qsScaleWant = "关";
    private Task QsSetScale(string want, bool mg, bool ls)
    {
        if (_qsState?["magpie"] is JsonObject m) m["enabled"] = want == "Magpie";
        _qsLossless = _qsLossless == null ? null : want == "小黄鸭";
        _qsScaleWant = want;
        QsQueue("scale", async () =>
        {
            _agent ??= new AgentClient();
            string target = _qsScaleWant;
            bool mgNow = Bool((await _agent.Call("widget_state"))?["magpie"]?["enabled"]);
            bool lsNow = Bool((await _agent.Call("get_lossless_settings"))?["running"]);
            if (lsNow && target != "小黄鸭") await _agent.Call("set_lossless_running", JsonValue.Create(false));
            if (mgNow != (target == "Magpie")) await _agent.Call("toggle_magpie");
            if (!lsNow && target == "小黄鸭") await _agent.Call("set_lossless_running", JsonValue.Create(true));
            Log.Info($"quick panel scale -> {target} (was magpie={mgNow} lossless={lsNow})");
        });
        return Task.CompletedTask;
    }
    private void QsActivate()
    {
        string f = _qsFocusId;
        if (QsRowIds.Contains(f)) { QsAdjust(1); return; }
        if (!QsIsGrid(f)) return;
        var it = QsFind(f);
        if (it == null || it.Disabled) { Cue(Sfx.Bump); return; }
        QsKick("g.pr." + f, 1, 0, 160);
        if (QsRisk.Contains(f) && _qsArmed != f)
        {
            _qsArmed = f;
            Cue(Sfx.ModalShow);
            QsSync(); QsPaintLayer('g');
            return;
        }
        _qsArmed = null;
        QsRun(f);
    }

    /// <summary>网格里按一下。</summary>
    private void QsRun(string id)
    {
        Log.Info($"quick panel run {id}");
        switch (id)
        {
            case "wifi": _ = QsRadio(RadioKind.WiFi); break;
            case "bt": _ = QsRadio(RadioKind.Bluetooth); break;
            case "air": _ = QsAirToggle(); break;
            case "vrr":
            {
                bool next = _qsVrr != true;
                _qsVrr = next;
                Cue(next ? Sfx.ToggleOn : Sfx.ToggleOff);
                QsRebuild();
                QsQueue("set_vrr", async () => { _agent ??= new AgentClient(); await _agent.Call("set_vrr", JsonValue.Create(next)); _dq.TryEnqueue(() => _ = QsReadVrr()); });
                break;
            }
            case "pwr":
                _qsPm = true; _qsGr = 2; _qsGc = 0; _qsFocusId = "off";
                Cue(Sfx.MenuIn);
                QsSync(); QsPaintLayer('g');
                break;
            case "shot":
            {
                CloseQuickPanel();
                nint game = _qsGame;
                if (_qsUnderLibrary || _qsWin != null)
                    After(_qsWin != null ? 350 : 500, async () =>
                    {
                        string? path = await CaptureScreenshotAsync(game);
                        if (path == null) Toast(_shotNeedsPermission ? "截图没能保存：在游戏库里允许「屏幕截图」权限" : "截图没能保存");
                        else { Cue(Sfx.Shutter); Toast("已截图"); }
                    });
                else After(500, () => _ = QsCall("截图", "screenshot"));
                break;
            }
            case "kbd":
                CloseQuickPanel();
                _ = QsCall("屏幕键盘", "toggle_keyboard");
                break;
            case "pad":
            {
                string next = _qsPadMode == "desktop" ? "xinput" : "desktop";
                _qsPadMode = next;
                Cue(Sfx.Activate);
                if (next == "desktop")
                {
                    // 切成键鼠后手柄不再驱动面板：先收起，回到游戏再切
                    CloseQuickPanel();
                    After(400, () => _ = QsCall("手柄模式", "set_gamepad_mode", JsonValue.Create(next)));
                    return;
                }
                _ = QsCall("手柄模式", "set_gamepad_mode", JsonValue.Create(next));
                QsRebuild();
                break;
            }
            case "desk":
                CloseQuickPanel(hideWindow: false, instant: true);
                _ = CcSwitchMode();
                break;
            case "sleep":
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                CloseQuickPanel(hideWindow: false, instant: true);
                if (!Program.Args.Contains("--power-dry")) After(300, () => SystemInfo.Sleep(hwnd));
                break;
            }
            case "restart" or "off":
                CloseQuickPanel(hideWindow: false, instant: true);
                if (!Program.Args.Contains("--power-dry")) _ = CcRequestAnimatedPower(id == "off" ? "shutdown" : "restart");
                break;
            case "win":
                if (_qsWin != null) { CloseQuickPanel(hideWindow: false, instant: true); (_window as MainWindow)?.RouteFromPanel("window-switcher", _qsGame); }
                else OpenSwitcherShortcut();   // 面板滑出去，控制中心「窗口」页同时进来
                break;
            case "lib":
                // 留在游戏库：面板收起、窗口不退下（游戏留在后面）
                _nowPlayingDismissed = true;
                CloseQuickPanel(hideWindow: false);
                if (_qsWin != null) (_window as MainWindow)?.KeepLibraryAfterSurface();
                break;
            case "quit":
                if (_game is { } g) QuitGame(g);
                else if (_qsGame != 0) PostMessageQs(_qsGame, 0x0010, 0, 0);   // WM_CLOSE：像点右上角 × 一样请游戏自己关
                CloseQuickPanel(hideWindow: false);
                break;
        }
    }

    private async Task QsReadVrr()
    {
        try { _agent ??= new AgentClient(); if (await _agent.Call("get_display_state") is JsonObject d && d["vrr"] is JsonValue) _qsVrr = Bool(d["vrr"]); }
        catch (Exception ex) { Log.Warn($"quick panel vrr read: {ex.Message}"); }
        if (_qsOpen) QsRebuild();
    }

    private async Task QsRadio(RadioKind kind)
    {
        string name = kind == RadioKind.WiFi ? "Wi-Fi" : "蓝牙";
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) { QsFlash($"{name}没有切换：没有权限"); return; }
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind != kind) continue;
                bool on = r.State != RadioState.On;
                if (kind == RadioKind.WiFi) _qsWifi = on; else _qsBt = on;
                if (on) _qsAir = false;
                Cue(on ? Sfx.ToggleOn : Sfx.ToggleOff);
                QsRebuild();
                var result = await r.SetStateAsync(on ? RadioState.On : RadioState.Off);
                if (result != RadioAccessStatus.Allowed) QsFlash($"{name}没有切换");
                break;
            }
        }
        catch (Exception ex) { QsFlash($"{name}没有切换：{ex.Message}"); }
        _ = QsRefresh(_qsVersion);
    }

    /// <summary>飞行模式：系统的 RadioManager（和设置 / 操作中心同一个开关）；拿不到就退回把 Wi-Fi 和蓝牙都关掉 / 打开。</summary>
    private async Task QsAirToggle()
    {
        bool next = _qsAir != true;
        _qsAir = next;
        if (next) { _qsWifi = false; _qsBt = false; }
        Cue(next ? Sfx.ToggleOn : Sfx.ToggleOff);
        QsRebuild();
        bool ok = await Task.Run(() => QsAirplane.Set(next));
        if (!ok)
        {
            Log.Warn("quick panel airplane: RadioManager unavailable, toggling radios");
            try
            {
                if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
                    foreach (var r in await Radio.GetRadiosAsync())
                        if (r.Kind is RadioKind.WiFi or RadioKind.Bluetooth) await r.SetStateAsync(next ? RadioState.Off : RadioState.On);
            }
            catch (Exception ex) { QsFlash($"飞行模式没有切换：{ex.Message}"); }
        }
        await Task.Delay(400);
        _ = QsRefresh(_qsVersion);
    }

    // ---------------- 触控 / 鼠标 ----------------

    private QsItem? QsItemAt(double x, double y)
    {
        foreach (var it in _qsItems)
        {
            switch (it.Kind)
            {
                case QsKind.Meter when x >= QsL && x < QsR && y >= QsPwY && y < QsPwY + QsPwH: return it;
                case QsKind.Slider when y >= QsPairY && y < QsPairY + 44 && x >= it.X - 30 && x < it.X + it.W + 40: return it;
                case QsKind.Row when x >= QsL && x < QsR && y >= it.Y && y < it.Y + it.H: return it;
                case QsKind.Cell or QsKind.Card when QsCellShown(it.Id) && x >= it.X && x < it.X + it.W && y >= it.Y && y < it.Y + it.H: return it;
            }
        }
        return null;
    }

    private void QsClick(double sx, double sy)
    {
        float k = QsK;
        double x = sx / k, y = sy / k;
        if (x > QsW) { CloseQuickPanel(); return; }   // 点到游戏那边 = 收起
        var it = QsItemAt(x, y);
        if (it == null || it.Disabled) return;
        switch (it.Kind)
        {
            case QsKind.Meter:
                QsSetFocus("tdp");
                if (y > QsPwY + QsPwH - 35) QsSetLevel(it, QsLevelAt(it, x));
                return;
            case QsKind.Slider:
                if (it.Id == "volume" && x < it.X) { QsToggleMute(); return; }
                QsSetLevel(it, QsLevelAt(it, x));
                return;
            case QsKind.Row:
                QsSetFocus(it.Id);
                QsAdjust(x > QsR - 150 && x < QsR - 90 ? -1 : 1);
                return;
            default:
                if (_qsPm && (it.Row != 2 || it.Col > 2)) QsClosePm(false);
                QsSetFocus(it.Id);
                QsActivate();
                return;
        }
    }

    private void QsToggleMute()
    {
        bool m = !Bool(QsGet("system.muted"));
        if (_qsState?["system"] is JsonObject s2) s2["muted"] = m;
        Cue(m ? Sfx.ToggleOff : Sfx.ToggleOn);
        QsQueueTouchValue("set_mute", JsonValue.Create(m));
        QsHotMark("vol");
        QsBuild(); QsPaintLayer('t');
    }

    /// <summary>触控：功耗按整条轨道，亮度 / 音量按各自的细轨道。</summary>
    private float QsLevelAt(QsItem it, double x) => (float)((x - it.X) / Math.Max(1, it.W));

    private void QsSetLevel(QsItem it, float level)
    {
        level = Math.Clamp(level, 0, 1);
        if (it.Disabled) return;
        if (it.Id is "brightness" or "volume")
        {
            double v = Math.Round(level * 20) * 5;
            if (_qsState?["system"] is JsonObject sys) { sys[it.Id] = v; if (it.Id == "volume") sys["muted"] = false; }
            QsQueueTouchValue(it.Id == "volume" ? "set_volume" : "set_brightness", JsonValue.Create(v));
            QsHotMark(it.Id == "volume" ? "vol" : "bri");
        }
        else if (it.Id == "tdp" && _qsDraft != null)
        {
            var (lo, hi) = QsTdpRange();
            double v = Math.Round(lo + (hi - lo) * level);
            QsSetTdp(v);
            QsQueueTouchValue("save_power", _qsDraft.DeepClone());
        }
        QsBuild(); QsPaintLayer('t');
    }

    private SettingsWriteQueue? _qsTouchWrites;
    // In-memory transport is used only by the opt-in input probe.
    private Func<string, JsonNode, Task>? _qsTouchTransport;
    private Task _qsTouchPending = Task.CompletedTask;
    /// <summary>同一项只留最新的一次写入，按顺序发；写入没完成前不拿后台回读覆盖界面（QsRefresh 看 IsBusy / Revision）。</summary>
    private void QsQueue(string key, Func<Task> write)
    {
        _qsTouchWrites ??= new SettingsWriteQueue(ex => QsFlash(ex.Message), () => { if (_qsOpen) _ = QsRefresh(_qsVersion); });
        Log.Info($"quick panel write queued {key}");
        _qsTouchPending = _qsTouchWrites.Enqueue(key, 60, write);
    }
    private void QsQueueTouchValue(string method, JsonNode value)
    {
        _qsTouchWrites ??= new SettingsWriteQueue(ex => QsFlash(ex.Message), () => { if (_qsOpen) _ = QsRefresh(_qsVersion); });
        var transport = _qsTouchTransport;
        _qsTouchPending = _qsTouchWrites.Enqueue(method, 60, async () =>
        {
            if (transport != null) { await transport(method, value); return; }
            _agent ??= new AgentClient();
            await _agent.Call(method, value);
        });
    }

    private QsItem? QsTouchItemAt(double sx, double sy)
    {
        float k = QsK;
        return QsItemAt(sx / k, sy / k);
    }

    private void QsTouchDrag(string id, double sx)
    {
        var it = _qsItems.FirstOrDefault(item => item.Id == id);
        if (it == null || it.Disabled || it.Kind is not (QsKind.Slider or QsKind.Meter)) return;
        if (it.Kind == QsKind.Meter) _qsFocusId = id;
        QsSetLevel(it, QsLevelAt(it, sx / QsK));
    }

    // ---------------- 状态读数：电池剩余、VRR 范围 ----------------

    private static TimeSpan? QsBatteryLeft()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryLifeTime == uint.MaxValue || s.BatteryLifeTime == 0) return null;
        return TimeSpan.FromSeconds(s.BatteryLifeTime);
    }

    /// <summary>内屏 EDID 里的显示范围（Display Range Limits，标签 0xFD）：可变刷新率的上下限，如「48–120 Hz」。</summary>
    private static string? QsVrrRange()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (root == null) return null;
            foreach (var model in root.GetSubKeyNames())
            {
                using var mk = root.OpenSubKey(model);
                if (mk == null) continue;
                foreach (var inst in mk.GetSubKeyNames())
                {
                    using var ik = mk.OpenSubKey(inst);
                    if (ik?.OpenSubKey("Control") == null) continue;   // 只看现在接着的
                    if (ik.OpenSubKey("Device Parameters")?.GetValue("EDID") is not byte[] e || e.Length < 128) continue;
                    for (int o = 54; o <= 108; o += 18)
                    {
                        if (e[o] != 0 || e[o + 1] != 0 || e[o + 3] != 0xFD) continue;
                        int min = e[o + 5] + ((e[o + 4] & 0x3) == 0x3 ? 255 : 0), max = e[o + 6] + ((e[o + 4] & 0x2) != 0 ? 255 : 0);
                        if (min > 0 && max > min) return $"{min}–{max} Hz";
                    }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"vrr range: {ex.Message}"); }
        return null;
    }

    // ---------------- 自测 ----------------

    /// <summary>自测用（--state=qs / qsnav）：在游戏库里打开快捷面板，挨个走一遍焦点。</summary>
    internal void QuickPanelTest(int steps)
    {
        if (!_qsOpen) OpenQuickPanel(0);
        var dirs = new[] { Direction.Down, Direction.Down, Direction.Right, Direction.Down, Direction.Down, Direction.Down, Direction.Down, Direction.Down, Direction.Right, Direction.Down, Direction.Up, Direction.Up };
        for (int i = 0; i < steps; i++) { int s = i; After(300 * (s + 1), () => { if (_qsOpen) QsMove(dirs[s % dirs.Length]); }); }
    }

    internal string QsFocusTest => $"{_qsFocusId}{(_qsPm ? "+pm" : "")}{(_qsArmed != null ? "!" + _qsArmed : "")}";

    internal void QuickPanelTestClose() => CloseQuickPanel(hideWindow: false);

    /// <summary>自测用（--state=qsapply）：焦点放到某一项上按一次 A，日志里记下按之前 / 之后的值。</summary>
    internal void QuickPanelTestPress(string id)
    {
        if (QsFind(id) is not { } it) { Log.Info($"quick panel test: no {id}"); return; }
        string before = it.Value;
        QsSetFocus(id);
        QsActivate();
        Log.Info($"quick panel test press {id}: {before} -> {QsFind(id)?.Value ?? "?"}");
    }

    internal string QuickPanelTestValue(string id) => QsFind(id)?.Value ?? "?";

    /// <summary>自测用：按名字把焦点 / 状态摆好（&lt;id&gt;、power、arm=&lt;id&gt;）。</summary>
    internal void QuickPanelTestState(string what)
    {
        if (!_qsOpen) OpenQuickPanel(0);
        if (what == "power") { QsSetFocus("pwr"); QsRun("pwr"); }
        else if (what.StartsWith("arm="))
        {
            string id = what[4..];
            if (QsPm.Contains(id)) { _qsPm = true; }
            QsSetFocus(id); _qsArmed = id; QsSync(); QsPaintLayer('g');
        }
        else if (QsFind(what) != null) QsSetFocus(what);
        QsPaintAll();
    }

    [StructLayout(LayoutKind.Sequential)] private struct QsRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct QsMonInfo { public int Size; public QsRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct QsPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out QsRect r);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessageQs(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref QsMonInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out QsPowerStatus s);
}

/// <summary>
/// 系统飞行模式开关：IRadioManager（CLSID 581333F6-…，Windows 设置和操作中心用的同一个），没有公开文档，按虚表调（AOT 下不能用 ComImport）。
/// 虚表：3 IsRMSupported、4 GetUIRadioInstances、5 GetSystemRadioState(out enabled, out policy, out reason)、6 SetSystemRadioState(enabled)。
/// enabled = 1 表示无线电开着（飞行模式关）。
/// </summary>
internal static class QsAirplane
{
    private static readonly Guid Clsid = new("581333f6-28db-41be-bc7a-ff201f12f3f6");
    private static readonly Guid Iid = new("db3afbfb-08e6-46c6-aa70-bf9a34c30ab7");

    public static unsafe bool? Get()
    {
        try
        {
            if (CoCreateInstance(Clsid, 0, 0x17, Iid, out nint rm) != 0 || rm == 0) return null;
            try
            {
                var vt = *(nint**)rm;
                int enabled = 0, policy = 0, reason = 0;
                int hr = ((delegate* unmanaged[Stdcall]<nint, int*, int*, int*, int>)vt[5])(rm, &enabled, &policy, &reason);
                return hr == 0 ? enabled == 0 : null;
            }
            finally { ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)rm)[2])(rm); }
        }
        catch (Exception ex) { Log.Warn($"airplane get: {ex.Message}"); return null; }
    }

    public static unsafe bool Set(bool airplane)
    {
        try
        {
            if (CoCreateInstance(Clsid, 0, 0x17, Iid, out nint rm) != 0 || rm == 0) return false;
            try
            {
                var vt = *(nint**)rm;
                int hr = ((delegate* unmanaged[Stdcall]<nint, int, int>)vt[6])(rm, airplane ? 0 : 1);
                if (hr != 0) Log.Warn($"airplane set: 0x{hr:x8}");
                return hr == 0;
            }
            finally { ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)rm)[2])(rm); }
        }
        catch (Exception ex) { Log.Warn($"airplane set: {ex.Message}"); return false; }
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);
}
