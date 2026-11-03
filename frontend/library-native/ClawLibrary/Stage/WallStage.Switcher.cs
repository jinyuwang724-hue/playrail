// 控制中心 → 窗口（功能坞的「窗口」）：游戏模式没有任务栏，这一页就是任务切换。
// 底子和截图页一样（控制中心的动态背景、左上 24 px 粗标题）。一排卡片横着走，焦点卡停在正中：
// 卡面 = DWM 实时缩略图（最小化的窗口没有画面，放大号应用图标），卡下一行 = 应用图标 + 窗口标题。
// 正在玩的游戏排第一，其余按最近用过的顺序（Z 序）。← → 选，A 切过去，X 请它关（WM_CLOSE，不强杀），B 返回；点卡 = 选 / 切，左右滑 = 换。
// 进出场：卡片一张接一张淡入、从下面浮上来，出场倒着走；滚动和进出场都是逐帧趋近（可打断），缩略图跟着同一帧挪。
// DWM 缩略图压在我们窗口内容的上面，所以焦点描边画在卡框外面一圈。
using System.Numerics;
using ClawLibrary.Data;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using ClawLibrary.Engine;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private sealed class WsCard
    {
        public required OpenWindow Win;
        public ContainerVisual El = null!;
        public SpriteVisual Box = null!, Ring = null!, Label = null!;
        public DwmThumb? Thumb;
        public bool Closing;
        /// <summary>入场次序（出场倒过来），和列表下标无关：新出现的窗口不重放入场。</summary>
        public int Order;
    }

    private const float WsBoxW = 512, WsBoxH = 320, WsGap = 40, WsBoxY = 196, WsRadius = 14, WsRingGap = 5, WsLabelY = 22, WsIcon = 24;

    private ContainerVisual? _ws;
    private SpriteVisual _wsTitle = null!, _wsEmpty = null!;
    private readonly List<WsCard> _wsCards = new();
    private bool _wsOpen, _wsTicking, _wsStandalone;
    private int _wsFocus, _wsVersion;
    /// <summary>横向滚动（设计 px）与进出场进度（0 = 没出来，1 = 到位）：当前值 → 目标值逐帧趋近。</summary>
    private float _wsScroll, _wsScrollTo, _wsShow, _wsShowTo;
    private long _wsFrameAt;
    private DispatcherQueueTimer? _wsPoll;
    private readonly Dictionary<uint, CanvasBitmap?> _wsIcons = new();

    private float WsPitch => WsBoxW + WsGap;

    // ---------------- 开 / 关 ----------------

    /// <summary>从墙上 / 正在游戏画面直接进：先开控制中心，等它的形变走完再进这一页。</summary>
    internal void OpenSwitcherFromAnywhere()
    {
        if (_ccOpen) { if (!_wsOpen) OpenSwitcher(); return; }
        OpenControlCenter();
        void Enter(int tries)
        {
            if (!_ccOpen) return;
            if (MorphBusy && tries > 0) { After(50, () => Enter(tries - 1)); return; }
            OpenSwitcher();
        }
        Enter(60);
    }

    private void OpenSwitcher()
    {
        if (!_wsStandalone && _cc == null) return;
        _ccK = Math.Min((float)View.Width / 1280, (float)View.Height / 800);
        EnsureWsVisuals();
        _top.Children.Remove(_ws!); _top.Children.InsertAtTop(_ws!);
        _wsOpen = true;
        ++_wsVersion;
        WsReleaseAll();
        var list = OpenWindows.List();
        // 正在玩的游戏排第一
        if (_game is { Pid: > 0 } g)
        {
            var mine = list.Where(w => w.Pid == g.Pid || w.Hwnd == g.Hwnd).ToList();
            list = mine.Concat(list.Except(mine)).ToList();
        }
        for (int i = 0; i < list.Count; i++) _wsCards.Add(WsMakeCard(list[i], i));
        _wsFocus = 0;
        _wsScroll = _wsScrollTo = 0;
        _wsShow = 0; _wsShowTo = 1;
        WsPaintTitle();
        WsPaintEmpty();
        if (!_wsStandalone) foreach (var vis in CcPageParts()) vis.StartAnimation("Opacity", ToScalar(0, 160, EaseOutCss));
        _ws!.IsVisible = true;
        _ws.StopAnimation("Opacity"); _ws.Opacity = 1;
        _wsTitle.Opacity = 0;
        _wsTitle.StartAnimation("Opacity", ToScalar(1, 220, EaseOutCss, 60));
        WsRing(instant: true);
        if (Reduced) _wsShow = 1;
        WsStartTicking();
        _wsPoll ??= _dq.CreateTimer();
        _wsPoll.Interval = TimeSpan.FromMilliseconds(1000);
        _wsPoll.Tick -= WsPollTick;
        _wsPoll.Tick += WsPollTick;
        _wsPoll.Start();
        Log.Info($"window switcher open ({_wsCards.Count})");
    }

    private void CloseSwitcher(bool instant = false)
    {
        if (!_wsOpen || _ws == null) return;
        if (!instant) Cue(Sfx.ModalHide);
        _wsOpen = false;
        _wsPoll?.Stop();
        int v = ++_wsVersion;
        if (!_wsStandalone) foreach (var vis in CcPageParts()) { vis.StopAnimation("Opacity"); if (instant) vis.Opacity = 1; else vis.StartAnimation("Opacity", ToScalar(1, 220, EaseOutCss, 120)); }
        if (instant || Reduced) { WsFinishClose(); return; }
        _wsTitle.StartAnimation("Opacity", ToScalar(0, 140, EaseOutCss));
        _wsEmpty.StartAnimation("Opacity", ToScalar(0, 140, EaseOutCss));
        _wsShowTo = 0;   // 出场：卡片倒着沉下去，走到 0 时在逐帧里收尾
        WsStartTicking();
        After(700, () => { if (v == _wsVersion && !_wsOpen) WsFinishClose(); });
        Log.Info("window switcher close");
    }

    private void WsFinishClose()
    {
        if (_ws == null) return;
        WsReleaseAll();
        _ws.IsVisible = false;
        _wsShow = _wsShowTo = 0;
    }

    private void WsReleaseAll()
    {
        foreach (var c in _wsCards) { c.Thumb?.Dispose(); _ws?.Children.Remove(c.El); }
        _wsCards.Clear();
        foreach (var b in _wsIcons.Values) b?.Dispose();
        _wsIcons.Clear();
    }

    private void EnsureWsVisuals()
    {
        if (_ws != null) return;
        _ws = G.Container();
        _ws.IsVisible = false;
        _wsTitle = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _wsEmpty = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        _ws.Children.InsertAtTop(_wsTitle);
        _ws.Children.InsertAtTop(_wsEmpty);
        var backdrop = G.Sprite(G.C.CreateColorBrush(Windows.UI.Color.FromArgb(255, 9, 9, 11)));
        backdrop.RelativeSizeAdjustment = Vector2.One;
        _ws.Children.InsertAtBottom(backdrop);
        _ws.RelativeSizeAdjustment = Vector2.One;
        _top.Children.InsertAtTop(_ws);
    }

    // ---------------- 卡片 ----------------

    private WsCard WsMakeCard(OpenWindow w, int order)
    {
        var c = new WsCard { Win = w, Order = order };
        c.El = G.Container();
        c.Box = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        c.Ring = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        c.Label = G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        c.Ring.Opacity = 0;
        c.El.Children.InsertAtTop(c.Ring);
        c.El.Children.InsertAtTop(c.Box);
        c.El.Children.InsertAtTop(c.Label);
        c.El.Opacity = 0;
        _ws!.Children.InsertAtTop(c.El);
        if (!w.Minimized) c.Thumb = DwmThumb.Register(WinRT.Interop.WindowNative.GetWindowHandle(_window), w.Hwnd);
        WsPaintCard(c);
        return c;
    }

    private CanvasBitmap? WsAppIcon(OpenWindow w)
    {
        if (_wsIcons.TryGetValue(w.Pid, out var cached)) return cached;
        _wsIcons[w.Pid] = null;
        int version = _wsVersion;
        _ = Task.Run(() => {
            byte[]? pixels = null; int size = 0;
            try { pixels = OpenWindows.IconPixels(w.Hwnd, w.Pid, out size); }
            catch (Exception ex) { Log.Warn($"window icon {w.Process}: {ex.Message}"); }
            _dq.TryEnqueue(() => {
                if (version != _wsVersion || !_wsOpen || pixels == null) return;
                try {
                    _wsIcons[w.Pid] = CanvasBitmap.CreateFromBytes(G.Device, pixels, size, size, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    foreach (var card in _wsCards.Where(c => c.Win.Pid == w.Pid)) WsPaintCard(card);
                } catch (Exception ex) { Log.Warn($"window icon upload: {ex.Message}"); }
            });
        });
        return null;
    }

    /// <summary>卡框（zinc-900 半透，最小化的窗口中间放大号图标）、外圈焦点描边、下面一行图标 + 标题。</summary>
    private void WsPaintCard(WsCard c)
    {
        float k = _ccK, raster = _ccK * Chrome.Dpi;
        var icon = WsAppIcon(c.Win);
        CcSet(c.Box, CcDraw(WsBoxW, WsBoxH, raster, ds =>
        {
            ds.FillRoundedRectangle(0, 0, WsBoxW, WsBoxH, WsRadius, WsRadius, Gfx.Rgba(0x18, 0x18, 0x1b, 0.78));
            if (c.Thumb == null)
            {
                if (icon != null) ds.DrawImage(icon, new Windows.Foundation.Rect(WsBoxW / 2 - 48, WsBoxH / 2 - 48, 96, 96), icon.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                else LineIcons.Draw(ds, "", WsBoxW / 2, WsBoxH / 2, 56, Gfx.WithAlpha(CcText, 0.35f));
            }
        }));
        c.Box.Size = new Vector2(WsBoxW * k, WsBoxH * k);
        float rw = WsBoxW + 2 * WsRingGap, rh = WsBoxH + 2 * WsRingGap, rr = WsRadius + WsRingGap;
        CcSet(c.Ring, CcDraw(rw + 4, rh + 4, raster, ds => ds.DrawRoundedRectangle(2, 2, rw, rh, rr, rr, CcText, 2.5f)));
        c.Ring.Size = new Vector2((rw + 4) * k, (rh + 4) * k);
        c.Ring.Offset = new Vector3(-(WsRingGap + 2) * k, -(WsRingGap + 2) * k, 0);
        const float LabelH = 32;
        CcSet(c.Label, CcDraw(WsBoxW, LabelH, raster, ds =>
        {
            float x = 0;
            if (icon != null) { ds.DrawImage(icon, new Windows.Foundation.Rect(0, (LabelH - WsIcon) / 2, WsIcon, WsIcon), icon.Bounds, 1, CanvasImageInterpolation.HighQualityCubic); x = WsIcon + 10; }
            LineIcons.Draw(ds, "", WsBoxW - 18, LabelH / 2, 14, CcText);
            using var tl = TextKit.Layout(ds, c.Win.Title, TextKit.Ui(500), 17, WsBoxW - x - 48, CanvasHorizontalAlignment.Left);
            tl.Options = CanvasDrawTextOptions.Clip;
            tl.TrimmingSign = CanvasTrimmingSign.Ellipsis;
            tl.TrimmingGranularity = CanvasTextTrimmingGranularity.Character;
            var b = tl.LayoutBounds;
            ds.DrawTextLayout(tl, x - (float)b.X, (LabelH - (float)b.Height) / 2 - (float)b.Y, CcText);
        }));
        c.Label.Size = new Vector2(WsBoxW * k, LabelH * k);
        c.Label.Offset = new Vector3(0, (WsBoxH + WsLabelY) * k, 0);
    }

    private void WsPaintTitle()
    {
        float k = _ccK, raster = _ccK * Chrome.Dpi;
        using var tl = TextKit.Layout(G.Device, "窗口", TextKit.Ui(700), 24, 400, CanvasHorizontalAlignment.Left);
        var bb = tl.LayoutBounds;
        float w = (float)bb.Width + 4, th = (float)bb.Height + 4;
        CcSet(_wsTitle, CcDraw(w, th, raster, ds => ds.DrawTextLayout(tl, -(float)bb.X, -(float)bb.Y, CcText)));
        _wsTitle.Size = new Vector2(w * k, th * k);
        _wsTitle.Offset = new Vector3(40 * k, 32 * k, 0);
    }

    /// <summary>没有别的窗口：屏幕正中一个淡淡的窗口图标，不写字。</summary>
    private void WsPaintEmpty()
    {
        float k = _ccK, raster = _ccK * Chrome.Dpi;
        const float S = 96;
        CcSet(_wsEmpty, CcDraw(S, S, raster, ds => LineIcons.Draw(ds, "", S / 2, S / 2, 64, Gfx.WithAlpha(CcText, 0.3f))));
        _wsEmpty.Size = new Vector2(S * k, S * k);
        _wsEmpty.Offset = new Vector3((640 - S / 2) * k, (WsBoxY + WsBoxH / 2 - S / 2) * k, 0);
        _wsEmpty.StopAnimation("Opacity");
        _wsEmpty.Opacity = 0;
        if (_wsCards.Count == 0) _wsEmpty.StartAnimation("Opacity", ToScalar(1, 240, EaseOutCss, 120));
    }

    /// <summary>焦点描边：焦点卡淡入，其余淡出。</summary>
    private void WsRing(bool instant = false)
    {
        for (int i = 0; i < _wsCards.Count; i++)
        {
            var c = _wsCards[i];
            float to = i == _wsFocus ? 1 : 0;
            c.Ring.StopAnimation("Opacity");
            if (instant || Reduced) c.Ring.Opacity = to;
            else c.Ring.StartAnimation("Opacity", ToScalar(to, 140, EaseOutCss));
            c.Label.Opacity = i == _wsFocus ? 1 : 0.55f;
        }
    }

    // ---------------- 逐帧：滚动、进出场、缩略图 ----------------

    private void WsStartTicking()
    {
        _wsFrameAt = NowMs;
        if (_wsTicking) return;
        _wsTicking = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += WsFrame;
        WsLayout();
    }

    private void WsFrame(object? sender, object e)
    {
        long now = NowMs;
        float dt = Math.Clamp((now - _wsFrameAt) / 1000f, 0, 0.05f);
        _wsFrameAt = now;
        // 临界阻尼式趋近：每帧往目标走一段，目标中途改了就从当前位置接着走（可打断）
        _wsScroll += (_wsScrollTo - _wsScroll) * (1 - MathF.Exp(-dt * 14));
        _wsShow += (_wsShowTo - _wsShow) * (1 - MathF.Exp(-dt * (_wsShowTo > _wsShow ? 9 : 13)));
        bool settled = MathF.Abs(_wsScrollTo - _wsScroll) < 0.3f && MathF.Abs(_wsShowTo - _wsShow) < 0.003f;
        if (settled) { _wsScroll = _wsScrollTo; _wsShow = _wsShowTo; }
        WsLayout();
        if (!settled) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= WsFrame;
        _wsTicking = false;
        if (!_wsOpen && _wsShow <= 0) WsFinishClose();
    }

    /// <summary>每张卡的位置 / 透明度，缩略图同一帧摆到卡框里（按源窗口比例居中放下）。</summary>
    private void WsLayout()
    {
        if (_ws == null) return;
        float k = _ccK, dpi = Chrome.Dpi;
        int n = _wsCards.Count;
        for (int i = 0; i < n; i++)
        {
            var c = _wsCards[i];
            float x = 640 - WsBoxW / 2 + i * WsPitch - _wsScroll;
            // 一张接一张：每张晚 0.08 的进度出来；出场倒着收
            float rank = Math.Min(c.Order, 6) * 0.08f;
            float t = Math.Clamp((_wsShow - rank) / (1 - 0.48f), 0, 1);
            float ease = 1 - (1 - t) * (1 - t) * (1 - t);
            float dy = (1 - ease) * 40;
            float alpha = ease * (c.Closing ? 0.4f : 1);
            c.El.Offset = new Vector3(x * k, (WsBoxY + dy) * k, 0);
            c.El.Opacity = alpha;
            if (c.Thumb is not { } th) continue;
            float bx = x, by = WsBoxY + dy;
            float s = MathF.Min(WsBoxW / th.SourceW, WsBoxH / th.SourceH);
            float tw = th.SourceW * s, tht = th.SourceH * s;
            float tx = bx + (WsBoxW - tw) / 2, ty = by + (WsBoxH - tht) / 2;
            bool onScreen = tx + tw > -40 && tx < 1320;
            double focusDim = i == _wsFocus ? 1 : 0.6;
            th.Place((int)MathF.Round(tx * k * dpi), (int)MathF.Round(ty * k * dpi), (int)MathF.Round(tw * k * dpi), (int)MathF.Round(tht * k * dpi),
                onScreen ? alpha * focusDim : 0);
        }
    }

    // ---------------- 刷新 ----------------

    /// <summary>每秒对一次：关掉的窗口拿走，新开的接在后面；焦点跟着原来那个窗口。</summary>
    private void WsPollTick(DispatcherQueueTimer sender, object e)
    {
        if (!_wsOpen) { sender.Stop(); return; }
        var now = OpenWindows.List();
        var alive = new HashSet<nint>(now.Select(w => w.Hwnd));
        nint focused = _wsFocus < _wsCards.Count ? _wsCards[_wsFocus].Win.Hwnd : 0;
        bool changed = false;
        foreach (var c in _wsCards.Where(c => !alive.Contains(c.Win.Hwnd)).ToList())
        {
            c.Thumb?.Dispose();
            _ws!.Children.Remove(c.El);
            _wsCards.Remove(c);
            changed = true;
        }
        foreach (var w in now)
        {
            var have = _wsCards.FirstOrDefault(c => c.Win.Hwnd == w.Hwnd);
            if (have == null) { _wsCards.Add(WsMakeCard(w, 0)); changed = true; }
            else if (have.Win.Minimized != w.Minimized || have.Win.Title != w.Title)
            {
                // 最小化 / 还原、改了标题：重做这张（最小化的没有画面，换成大图标）
                have.Win = w;
                have.Thumb?.Dispose();
                have.Thumb = w.Minimized ? null : DwmThumb.Register(WinRT.Interop.WindowNative.GetWindowHandle(_window), w.Hwnd);
                WsPaintCard(have);
                changed = true;
            }
        }
        if (!changed) return;
        int keep = _wsCards.FindIndex(c => c.Win.Hwnd == focused);
        _wsFocus = keep >= 0 ? keep : Math.Clamp(_wsFocus, 0, Math.Max(0, _wsCards.Count - 1));
        _wsScrollTo = _wsFocus * WsPitch;
        WsRing();
        _wsEmpty.StopAnimation("Opacity");
        _wsEmpty.StartAnimation("Opacity", ToScalar(_wsCards.Count == 0 ? 1 : 0, 200, EaseOutCss));
        WsStartTicking();
    }

    // ---------------- 输入 ----------------

    private void WsMove(Direction dir)
    {
        if (dir is not (Direction.Left or Direction.Right) || _wsCards.Count == 0) return;
        int next = Math.Clamp(_wsFocus + (dir == Direction.Left ? -1 : 1), 0, _wsCards.Count - 1);
        if (next == _wsFocus) { Cue(Sfx.Bump); return; }
        WsSetFocus(next);
    }

    private void WsSetFocus(int i)
    {
        _wsFocus = i;
        _wsScrollTo = i * WsPitch;
        Cue(Sfx.Nav);
        WsRing();
        WsStartTicking();
    }

    /// <summary>A：切过去。正在玩的游戏走「继续游戏」那条路；其余：我们还在前台时把它送到前台，然后收起控制中心（游戏库留在它后面）。</summary>
    private void WsActivate()
    {
        if (_wsFocus >= _wsCards.Count) return;
        var w = _wsCards[_wsFocus].Win;
        if (!OpenWindows.Matches(w)) { WsPollTick(_wsPoll!, EventArgs.Empty); return; }
        bool ok = (_window as MainWindow)?.SwitchToWindow(w) == true;
        Log.Info($"switch to {w.Process} hwnd={w.Hwnd} foreground={ok}");
        if (!ok) { Cue(Sfx.Error); Toast("没能切过去，请再次长按快设重试"); }
    }

    /// <summary>X：请焦点窗口自己关（和点 × 一样，能存档）。管理员窗口收不到这个请求。</summary>
    private void WsCloseFocused()
    {
        if (_wsFocus >= _wsCards.Count) return;
        var c = _wsCards[_wsFocus];
        if (!OpenWindows.Close(c.Win.Hwnd)) { Cue(Sfx.Error); Toast("这个窗口以管理员身份运行，关不掉"); return; }
        Log.Info($"close window {c.Win.Process} hwnd={c.Win.Hwnd}");
        Cue(Sfx.Activate);
        c.Closing = true;   // 变淡，等它真的关了，下一次刷新拿走
        WsStartTicking();
    }

    private void WsBack()
    {
        bool standalone = _wsStandalone;
        CloseSwitcher(instant: standalone);
        _wsStandalone = false;
        if (standalone) (_window as MainWindow)?.EndSpecialSurface();
    }

    /// <summary>自测用（--state=ws|wsnav）。</summary>
    internal void SwitcherTestState() { if (_ccOpen && !_wsOpen) OpenSwitcher(); }
    internal void SwitcherTestBack() { if (_wsOpen) WsBack(); }
    internal void SwitcherTestPick(string title, bool close)
    {
        int i = _wsCards.FindIndex(c => c.Win.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        Log.Info($"switcher test pick '{title}' -> {i}");
        if (i < 0) return;
        WsSetFocus(i);
        if (close) WsCloseFocused(); else WsActivate();
    }

    /// <summary>点卡：点到焦点卡 = A，点到别的卡 = 把它移到中间。</summary>
    private void WsClick(double sx, double sy)
    {
        float k = _ccK;
        double x = sx / k, y = sy / k;
        if (y < WsBoxY || y > WsBoxY + WsBoxH + WsLabelY + 44) return;
        for (int i = 0; i < _wsCards.Count; i++)
        {
            float cx = 640 - WsBoxW / 2 + i * WsPitch - _wsScroll;
            if (x < cx || x > cx + WsBoxW) continue;
            if (x >= cx + WsBoxW - 44 && y >= WsBoxY + WsBoxH + WsLabelY)
            {
                _wsFocus = i; WsCloseFocused(); return;
            }
            if (i == _wsFocus) WsActivate(); else WsSetFocus(i);
            return;
        }
    }
}
