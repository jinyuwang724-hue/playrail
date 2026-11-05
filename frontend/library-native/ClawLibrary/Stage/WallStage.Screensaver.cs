// 海报灯光屏保：只在游戏库静置时启动；沿用墙面现有的 82% 熄灯过渡，逐张点亮可见游戏。
// 一秒检查一次空闲和时间，轮换时才更新合成层；静止期间不添加逐帧绘制。
using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using static ClawLibrary.Engine.ViewMath;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private DispatcherQueueTimer? _screensaverTimer;
    private bool _screensaverActive, _screensaverStopped, _screensaverProbe;
    private SlotKey? _screensaverLitKey;
    private string? _screensaverPreviousGame;
    private long _screensaverNextSwitch, _screensaverPauseUntil;
    private long? _screensaverCollapseInput;
    private bool _screensaverRailFocused, _screensaverHudVisible;
    private int _screensaverRailFocus;
    private SpriteVisual? _screensaverClock;
    private string? _screensaverClockText;
    private float _screensaverClockDpi;
    private double _screensaverClockWidth;

    internal bool ScreensaverActive => _screensaverActive;
    private bool ScreensaverForeground => _appearanceProbeRunning || (_windowActive && IsForeground());
    internal bool ScreensaverLights(Tile tile) => _screensaverActive && tile.Slot.Key == _screensaverLitKey;

    private void StartScreensaverTimer()
    {
        if (_screensaverTimer != null) return;
        _screensaverStopped = false;
        _lastInput = NowMs;
        _screensaverTimer = _dq.CreateTimer();
        _screensaverTimer.Interval = TimeSpan.FromSeconds(1);
        _screensaverTimer.IsRepeating = true;
        _screensaverTimer.Tick += (_, _) =>
        {
            try { ScreensaverTick(); }
            catch (Exception ex) { Log.Error("screensaver", ex); ExitScreensaver(); }
        };
        _screensaverTimer.Start();
    }

    private bool ScreensaverBlocked => _wsOpen || _stack.Count == 0 || _qsOpen || _ccOpen || MorphBusy || _searchOpen || _backgroundSettingsOpen
        || _railModel.SettingsOpen || _drag != null || PanVelocity != Vector2.Zero
        || _pad?.Panning == true || NowMs < _wallBusyUntil || NowMs < _screensaverPauseUntil;

    private void ScreensaverTick()
    {
        if (_screensaverStopped) return;
        bool foreground = ScreensaverForeground;
        if (_screensaverCollapseInput is { } inputAt)
        {
            if (_lastInput != inputAt || !foreground || !_prefs.Screensaver) _screensaverCollapseInput = null;
            else if (!ScreensaverBlocked && _expandedKey == null)
            {
                _screensaverCollapseInput = null;
                EnterScreensaver();
            }
            return;
        }
        if (_screensaverActive)
        {
            // 退到后台立即恢复正常墙面，回来后重新计算空闲时间。
            if ((!_prefs.Screensaver && !_screensaverProbe) || !foreground || _ccOpen || MorphBusy || _searchOpen || _backgroundSettingsOpen)
            {
                ExitScreensaver();
                _lastInput = NowMs;
                return;
            }
            RefreshScreensaverClock();
            if (NowMs >= _screensaverNextSwitch) PickScreensaverGame();
            return;
        }
        if (!foreground || ScreensaverBlocked)
        {
            _lastInput = NowMs;
            return;
        }
        if (ScreensaverPolicy.CanStart(_prefs.Screensaver, foreground, false, NowMs, _lastInput, _prefs.ScreensaverIdleSeconds))
        {
            if (_expandedKey != null)
            {
                // 先让展开卡归位，稳定后再进入满屏封面的屏保；新输入会取消这个待进入状态。
                Collapse(false);
                _screensaverCollapseInput = _lastInput;
            }
            else EnterScreensaver();
        }
    }

    /// <summary>只消费唤醒动作；调用方收到 true 后须直接返回，不能接着启动游戏或切换设置。</summary>
    public bool ConsumeScreensaverWake()
    {
        if (!_screensaverActive) return false;
        ExitScreensaver();
        MarkInput();
        return true;
    }

    private bool EnterScreensaver()
    {
        if (_screensaverActive || _screensaverStopped || !ScreensaverForeground || ScreensaverBlocked || _expandedKey != null) return false;
        // 卡盒版开着（三种首页样式都算）：换成统一的新屏保（WallStage.Cases.Screensaver.cs），不再点亮墙上的海报
        if (_cs != null)
        {
            if (_csInsert != null || _lm != null || !CsSaverEnter()) return false;
            _screensaverActive = true;
            _screensaverNextSwitch = NowMs + (long)SsHoldMs;
            PanVelocity = Vector2.Zero;
            Log.Info("screensaver entered (cases)");
            return true;
        }
        if (ScreensaverCandidates().Count == 0) return false;
        _screensaverRailFocused = _railFocused;
        _screensaverRailFocus = _railFocus;
        _screensaverHudVisible = _hud.IsVisible;
        _screensaverActive = true;
        _railFocused = false;
        PanVelocity = Vector2.Zero;
        foreach (var tile in _tiles.Values) tile.SetFocused(false);
        _hud.IsVisible = false;
        _toast.StopAnimation("Opacity");
        _toast.Opacity = 0;
        PickScreensaverGame();
        ApplyLibraryRailVisibility(animate: true);
        RefreshScreensaverClock(force: true);
        Log.Info("screensaver entered");
        return true;
    }

    private void ExitScreensaver(bool restoreFocus = true)
    {
        if (!_screensaverActive) return;
        _screensaverActive = false;
        _screensaverProbe = false;
        if (_csSs != null)
        {
            CsSaverExit();
            _lastInput = NowMs;
            Log.Info("screensaver exited (cases)");
            return;
        }
        _screensaverLitKey = null;
        if (_screensaverClock != null) _screensaverClock.IsVisible = false;
        _railFocused = _screensaverRailFocused && _prefs.LibraryRail;
        _railFocus = _screensaverRailFocus;
        if (restoreFocus && !_railFocused && FocusKey is { } key && _tiles.TryGetValue(key, out var tile)) tile.SetFocused(true);
        _hud.IsVisible = _screensaverHudVisible && _hudOn;
        ApplyLibraryRailVisibility(animate: restoreFocus);
        ApplyWallLook();
        _lastInput = NowMs;
        Log.Info("screensaver exited");
    }

    /// <summary>游戏启动窗口出现前暂缓自动屏保；切到后台时也会重新计时。</summary>
    private void HoldScreensaverForLaunch()
    {
        ExitScreensaver();
        _screensaverPauseUntil = NowMs + 90_000;
        _lastInput = NowMs;
    }

    private void OnScreensaverWindowActivation(bool active)
    {
        if (!active)
        {
            if (!_appearanceProbeRunning) ExitScreensaver();
            _screensaverWakeKey = null;
            _screensaverWakePointers.Clear();
            GamepadInputBlocked = false;
        }
        _lastInput = NowMs;
    }

    private List<Tile> ScreensaverCandidates()
    {
        Sync();
        var candidates = new List<Tile>();
        foreach (var tile in _tiles.Values)
        {
            if (tile.Shown is not { } id || Lookup(id)?.Kind != ItemKind.Game) continue;
            var r = ScreensaverScreenRect(tile);
            double x = r.X, y = r.Y, w = r.Width, h = r.Height;
            double visibleW = Math.Max(0, Math.Min(View.Width, x + w) - Math.Max(0, x));
            double visibleH = Math.Max(0, Math.Min(View.Height, y + h) - Math.Max(0, y));
            // 避开只露出一线的封面，保证观众能看清轮到哪款游戏。
            if (visibleW >= Math.Min(w * 0.45, 100) && visibleH >= Math.Min(h * 0.45, 100)) candidates.Add(tile);
        }
        return candidates;
    }

    private Rect ScreensaverScreenRect(Tile tile)
    {
        var geo = SeamGeometry(_anchorX, _center, _openWidth, View);
        var offsets = SplitOffsets(geo.Width, View.Scale);
        var r = tile.CurrentRect(NowMs);
        return new Rect(View.LineX + (r.X - _center.X) * View.Scale + (tile.Side == 0 ? offsets.Left : offsets.Right),
            View.Height / 2 + (r.Y - _center.Y) * View.Scale, r.Width * View.Scale, r.Height * View.Scale);
    }

    /// <summary>标题只在封面的可见部分排版，并为高亮时四边各外扩 8px 留出余量。</summary>
    internal Rect ScreensaverTitleArea(Tile tile)
    {
        var r = ScreensaverScreenRect(tile);
        double w = r.Width / View.Scale, h = r.Height / View.Scale;
        double left = Math.Clamp(8 - r.X / View.Scale, 0, Math.Max(0, w - 1));
        double top = Math.Clamp(8 - r.Y / View.Scale, 0, Math.Max(0, h - 1));
        double right = Math.Clamp((View.Width - r.X) / View.Scale - 8, left, w);
        double bottom = Math.Clamp((View.Height - r.Y) / View.Scale - 8, top, h);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void PickScreensaverGame()
    {
        if (!_screensaverActive) return;
        if (_csSs != null) { CsSaverShow(); _screensaverNextSwitch = _csSs?.NextSwitch ?? NowMs + 12_000; return; }
        var candidates = ScreensaverCandidates();
        int index = ScreensaverPolicy.PickGame(candidates.Select(t => t.Shown!).ToArray(), _screensaverPreviousGame, Random.Shared);
        if (index < 0) { ExitScreensaver(); return; }
        _screensaverLitKey = candidates[index].Slot.Key;
        _screensaverPreviousGame = candidates[index].Shown;
        _screensaverNextSwitch = NowMs + Math.Max(1, _prefs.ScreensaverIntervalSeconds) * 1000L;
        ApplyWallLook();
    }

    /// <summary>窗口缩放或裁剪补进新卡时，同步其屏保外观，并检查当前点亮卡是否仍看得见。</summary>
    private void RefreshScreensaverTiles()
    {
        if (!_screensaverActive || _csSs != null) return;
        var candidates = ScreensaverCandidates();
        if (_screensaverLitKey == null || !candidates.Any(t => t.Slot.Key == _screensaverLitKey)) PickScreensaverGame();
        else ApplyWallLook();
    }

    private void RefreshScreensaverClock(bool force = false)
    {
        if (!_screensaverActive) return;
        if (_csSs != null) { CsSaverPaintClock(force); return; }
        string text = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (!force && text == _screensaverClockText && Chrome.Dpi == _screensaverClockDpi && View.Width == _screensaverClockWidth) return;
        _screensaverClock ??= G.Sprite(G.C.CreateSurfaceBrush(), relative: false);
        if (_screensaverClock.Parent == null) _top.Children.InsertAtTop(_screensaverClock);
        float k = Math.Clamp((float)View.Width / 1440, 0.85f, 1.25f);
        float w = 142 * k, h = 64 * k, margin = 28 * k;
        var surface = G.Surface((int)Math.Ceiling(w * Chrome.Dpi), (int)Math.Ceiling(h * Chrome.Dpi));
        using (var ds = CanvasComposition.CreateDrawingSession(surface))
        {
            ds.Clear(Microsoft.UI.Colors.Transparent);
            ds.Transform = Matrix3x2.CreateScale(Chrome.Dpi);
            ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
            ds.FillRoundedRectangle(0, 0, w, h, 14 * k, 14 * k, Gfx.Rgba(9, 9, 11, 0.68));
            using var layout = TextKit.Layout(G.Device, text, TextKit.Ui(500), 36 * k, w, CanvasHorizontalAlignment.Center);
            ds.DrawTextLayout(layout, 0, (h - (float)layout.LayoutBounds.Height) / 2, Theme.Ink);
        }
        var brush = (CompositionSurfaceBrush)_screensaverClock.Brush;
        (brush.Surface as IDisposable)?.Dispose();
        brush.Surface = surface;
        _screensaverClock.Size = new Vector2(w, h);
        _screensaverClock.Offset = new Vector3(Math.Max(margin, (float)View.Width - margin - w), margin, 0);
        _screensaverClock.IsVisible = true;
        _screensaverClockText = text;
        _screensaverClockDpi = Chrome.Dpi;
        _screensaverClockWidth = View.Width;
    }

    private void StopScreensaver()
    {
        _screensaverStopped = true;
        _screensaverTimer?.Stop();
        _screensaverTimer = null;
        ExitScreensaver(restoreFocus: false);
        if (_screensaverClock != null)
        {
            if (_screensaverClock.Brush is CompositionSurfaceBrush brush) (brush.Surface as IDisposable)?.Dispose();
            (_screensaverClock.Brush as IDisposable)?.Dispose();
            _top.Children.Remove(_screensaverClock);
            _screensaverClock.Dispose();
            _screensaverClock = null;
        }
    }

    /// <summary>Bench 入口：立即进入屏保，不写入用户偏好。</summary>
    internal bool ScreensaverTestState()
    {
        _screensaverProbe = true;
        bool started = EnterScreensaver();
        if (!started) _screensaverProbe = false;
        return started;
    }
    internal void ScreensaverNextTestState() => PickScreensaverGame();
}
