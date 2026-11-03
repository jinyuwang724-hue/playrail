using System.Numerics;
using ClawLibrary.Data;
using ClawLibrary.Engine;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace ClawLibrary;

public sealed partial class MainWindow
{
    private LibraryWindow? _shortcutWindow;
    private int _shortcutVersion;
    private bool _shortcutVisible, _shortcutClosed;
    private volatile bool _shortcutHiding, _shortcutInTray;
    private readonly ShortcutMotionState _shortcutMotion = new();
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _shortcutTimers = new();
    internal bool AcceptsGamepad => !_shortcutHiding && !_shortcutInTray && !_shortcutClosed;

    private void InitializeShortcutWindow(nint previous)
    {
        _shortcutWindow = new LibraryWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), ActivateRoute);
        _shortcutWindow.RememberReturnWindow(previous);
        Activated += (_, e) =>
        {
            if (_overlayActive) return;
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                // Foreground may still be in transit during deactivation: re-check once the switch has settled.
                if (_shortcutWindow?.SetTopmost(false) == true)
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _shortcutWindow?.StayBehindForeground());
                // An explicit switch away must also cancel pending focus retries.
                if (!_shortcutHiding)
                {
                    ++_shortcutVersion; StopShortcutTimers();
                    if (_shortcutMotion.Direction == ShortcutMotionDirection.Enter)
                    {
                        _shortcutMotion.Cancel();
                        _stage?.EndShortcutSoftFocus(); ResetShortcutVisual();
                        Log.Info("shortcut enter cancelled on focus loss; visual settled");
                    }
                }
            }
            else
            {
                _stage?.ResumeFromTray();
                if (_shortcutVisible && !_shortcutHiding) _shortcutWindow?.SetTopmost(true);
            }
        };
    }

    /// <summary>A game (or Steam's install dialog) is about to open: let it come in front of the library.</summary>
    internal void HandOffForeground() { ReleaseOverlay(); _shortcutWindow?.HandOffForeground(); }

    private void StopShortcutTimers()
    {
        foreach (var timer in _shortcutTimers) timer.Stop();
        _shortcutTimers.Clear();
    }

    private void CancelShortcutTransition()
    {
        ++_shortcutVersion; StopShortcutTimers(); _shortcutHiding = false;
    }

    private void ShowShortcutWindow(string route, bool initial = false)
    {
        bool visible = AppWindow.IsVisible && !ToolboxWindow.IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var entrance = _shortcutMotion.Show(initial, visible, _shortcutWindow?.IsForeground == true);
        // 游戏中唤出：直接是「正在游戏」画面，不做从透明开始的进场（透明那几帧露出的是窗口黑底）
        if (entrance == ShortcutEntrance.Reveal && _stage?.NowPlayingShown == true) { _shortcutMotion.Cancel(); entrance = ShortcutEntrance.None; }
        // 快捷设置：第一帧就是游戏画面（缩略图）+ 面板自己滑进来，也不做整窗淡入
        if (entrance is ShortcutEntrance.Reveal or ShortcutEntrance.KeepCurrent && _stage?.SpecialSurfaceOpen == true) { _shortcutMotion.Cancel(); entrance = ShortcutEntrance.None; }
        int motionVersion = _shortcutMotion.Version;
        CancelShortcutTransition();
        _shortcutInTray = false;
        _shortcutVisible = true;
        int version = _shortcutVersion;
        if (entrance == ShortcutEntrance.None)
        {
            _stage?.EndShortcutSoftFocus(); ResetShortcutVisual();
            Log.Info($"shortcut show mode=ForegroundOnly version={motionVersion}");
        }
        if (entrance == ShortcutEntrance.Reveal && _stage?.Reduced == false)
        {
            var visual = ElementCompositionPreview.GetElementVisual(_root);
            visual.StopAnimation("Opacity"); visual.StopAnimation("Scale");
            visual.Opacity = 0; visual.Scale = new Vector3(1.02f, 1.02f, 1);
        }
        RestoreOrdinaryPresenter();
        if (!initial) RestoreShortcutContent(route);
        nint previous = LibraryWindow.Foreground;
        bool started = entrance is ShortcutEntrance.None or ShortcutEntrance.KeepCurrent;
        void StartWhenForeground()
        {
            if (started || !_shortcutMotion.IsCurrent(motionVersion) || _shortcutWindow?.IsForeground != true) return;
            started = true;
            Log.Info($"shortcut show mode={entrance} version={motionVersion}");
            AnimateShortcut(true, entrance == ShortcutEntrance.Reveal, motionVersion, () => { });
        }
        if (_overlayActive)
        {
            _shortcutWindow?.ShowOverlay();
            if (!started) AnimateShortcut(true, entrance == ShortcutEntrance.Reveal, motionVersion, () => { });
            Log.Info($"shortcut shown without activation target={_overlayTarget}");
            return;
        }
        TryShortcutFocus();
        StartWhenForeground();
        // Only retry while the original target still owns foreground. Never
        // repeatedly steal focus after a user switches to a different program.
        foreach (int delay in new[] { 60, 180 })
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.IsRepeating = false; timer.Interval = TimeSpan.FromMilliseconds(delay);
            timer.Tick += (_, _) =>
            {
                timer.Stop(); _shortcutTimers.Remove(timer);
                if (version != _shortcutVersion || _shortcutClosed || _shortcutHiding) return;
                if (_shortcutWindow?.IsForeground != true && LibraryWindow.Foreground == previous) TryShortcutFocus();
                StartWhenForeground();
                if (delay == 180 && _shortcutWindow?.IsForeground != true)
                {
                    _shortcutWindow?.SetTopmost(false);
                    if (_shortcutMotion.IsCurrent(motionVersion))
                    {
                        _shortcutMotion.Cancel(); _stage?.EndShortcutSoftFocus(); ResetShortcutVisual();
                    }
                    Log.Warn("shortcut foreground activation denied by Windows");
                }
            };
            _shortcutTimers.Add(timer); timer.Start();
        }
    }

    private void TryShortcutFocus()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _shortcutWindow?.EnsureFullscreenBounds();
        // The initial fullscreen presenter can rewrite z-order while the first
        // frame is loading; include it in the same bounded focus handoff.
        bool active = _shortcutWindow?.IsForeground == true || LibraryWindow.BringForward(hwnd);
        if (active)
        {
            _shortcutWindow?.SetTopmost(true);
            _root.Focus(FocusState.Programmatic);
        }
        Log.Info($"shortcut foreground={active} hwnd={hwnd} current={LibraryWindow.Foreground}");
    }

    /// <summary>快捷设置收起后：窗口照 Claw 键收起游戏库那样退下去（游戏已经回到前台）。</summary>
    internal void HideAfterQuickPanel() => EndSpecialSurface();

    private void HideShortcutWindow()
    {
        if (Stage.ShellMode.GameMode && !_desktopHandoff) { CancelShortcutTransition(); CompleteShortcutHide(); return; }
        if (_shortcutHiding)
        {
            ShowShortcutWindow("restore-" + ToolboxActivation.Target(_pendingRoute));
            return;
        }
        CancelShortcutTransition(); _shortcutHiding = true;
        int version = _shortcutVersion;
        int motionVersion = _shortcutMotion.Hide();
        if (!_overlayActive && _shortcutWindow?.IsForeground != true)
        {
            _shortcutMotion.Cancel(); _stage?.EndShortcutSoftFocus();
            CompleteShortcutHide(); return;
        }
        AnimateShortcut(false, false, motionVersion, () =>
        {
            if (_shortcutClosed || version != _shortcutVersion || !_shortcutHiding) return;
            CompleteShortcutHide();
        });
    }

    private void CompleteShortcutHide(bool returnToPrevious = true)
    {
        if (Stage.ShellMode.GameMode && !_desktopHandoff) { ParkGameShell(returnToPrevious); return; }
        _surfaceSession = false;
        _shortcutMotion.Cancel();
        bool returnFocus = returnToPrevious && _shortcutWindow?.IsForeground == true;
        _shortcutHiding = true;
        _shortcutVisible = false;
        _shortcutWindow?.SetTopmost(false);
        // Only remove the window from the taskbar once its tray entry exists.
        // A shell failure leaves a recoverable ordinary minimized window.
        bool tray = _tray?.EnsureIcon() == true;
        _shortcutInTray = tray;
        if (tray) AppWindow.Hide();
        else ToolboxWindow.Minimize(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _stage?.SuspendToTray();
        ReleaseOverlay();
        _shortcutHiding = false;
        ResetShortcutVisual();
        _shortcutWindow?.ReturnToPreviousWindow(returnFocus);
        Log.Info(tray ? "library hidden to tray" : "library tray unavailable; minimized");
    }

    private void AnimateShortcut(bool show, bool entrance, int version, Action completed)
    {
        if (!_shortcutMotion.IsCurrent(version)) return;
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        var compositor = visual.Compositor;
        if (_stage?.Reduced != false)
        {
            _shortcutMotion.Complete(version);
            _stage?.EndShortcutSoftFocus(); ResetShortcutVisual(); completed(); return;
        }
        visual.CenterPoint = new Vector3((float)_root.ActualWidth / 2, (float)_root.ActualHeight / 2, 0);
        // Folia softFocus.ts: a full-size layer resolves from soft focus; on
        // departure it drifts slightly forward. No exposed/shrinking edges.
        var easing = show
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, 1), new Vector2(.3f, 1))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(.4f, 0), new Vector2(1, 1));
        double slow = 1;
        if (Program.Args.FirstOrDefault(a => a.StartsWith("--shortcut-slow=")) is { } arg && double.TryParse(arg[16..], out double factor)) slow = Math.Clamp(factor, 1, 4);
        double duration = (show ? 520 : 360) * slow;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(duration);
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, show ? 1 : 0, easing);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = fade.Duration;
        scale.InsertExpressionKeyFrame(0, "this.StartingValue");
        scale.InsertKeyFrame(1, show ? Vector3.One : new Vector3(1.02f, 1.02f, 1), easing);
        _stage.BeginShortcutSoftFocus(show, entrance, duration, easing);
        visual.StartAnimation("Opacity", fade); visual.StartAnimation("Scale", scale);
        Log.Info($"shortcut soft-focus {(show ? "enter" : "leave")} start duration={duration}ms version={version}");
        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            if (_shortcutClosed || !_shortcutMotion.Complete(version)) return;
            _stage?.EndShortcutSoftFocus();
            if (show) ResetShortcutVisual();
            Log.Info($"shortcut soft-focus {(show ? "enter" : "leave")} complete version={version}");
            completed();
        };
        batch.End();
    }

    private void ResetShortcutVisual()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        visual.StopAnimation("Opacity"); visual.StopAnimation("Scale");
        visual.Opacity = 1; visual.Scale = Vector3.One;
    }

    private void DisposeShortcutWindow()
    {
        _gameShellTimer?.Stop();
        ReleaseOverlay();
        _shortcutClosed = true; _shortcutMotion.Cancel(); CancelShortcutTransition();
        _stage?.EndShortcutSoftFocus(); _shortcutWindow?.Dispose();
    }
}
