using ClawLibrary.Data;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace ClawLibrary;
public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _gameShellTimer;
    private bool _wasGameMode;
    private int _emptyShellTicks;
    private bool _edgeOff;

    private void InitializeGameShell()
    {
        _wasGameMode = Stage.ShellMode.GameMode;
        _gameShellTimer = DispatcherQueue.CreateTimer();
        _gameShellTimer.Interval = TimeSpan.FromMilliseconds(500);
        _gameShellTimer.Tick += (_, _) =>
        {
            if (_shortcutClosed || _desktopHandoff || _switchInFlight || _stage == null || _overlayRoutes.CurrentCount == 0) return;
            bool game = Stage.ShellMode.GameMode;
            if (game != _edgeOff) { _edgeOff = game; EdgeGestures.Set(WinRT.Interop.WindowNative.GetWindowHandle(this), game); Log.Info($"edge gestures {(game ? "off" : "default")}"); }   // 游戏模式下底边上滑不出开始菜单
            if (!game) { _wasGameMode = false; _emptyShellTicks = 0; return; }
            bool entered = !_wasGameMode;
            _wasGameMode = true;
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            bool missing = !AppWindow.IsVisible || ToolboxWindow.IsIconic(hwnd);
            bool hasForeground = LibraryWindow.HasForegroundApp(hwnd);
            _emptyShellTicks = hasForeground ? 0 : _emptyShellTicks + 1;
            if (entered && !_overlayActive && !_stage.SpecialSurfaceOpen) RestoreGameShell(true);
            else if (_emptyShellTicks >= 2) RestoreGameShell(true);
            else if (missing) RestoreGameShell(false);
            else _shortcutWindow?.EnsureFullscreenBounds();
        };
        _gameShellTimer.Start();
        InitializeWindowManager();
    }

    private void RestoreGameShell(bool activate)
    {
        if (_shortcutClosed || _desktopHandoff) return;
        if (_stage == null) { if (activate) _pendingRoute = "game-shell"; return; }
        if (!activate && _overlayActive)
        { _shortcutWindow?.EnsureFullscreenBounds(); _shortcutWindow?.ShowOverlay(); return; }
        if (!activate && _overlayAgent != null && _shortcutWindow?.IsForeground != true)
        { _stage.CloseSpecialSurfaces(); ReleaseOverlay(); _surfaceSession = false; }
        if (activate)
        {
            _shortcutWindow?.RememberReturnWindow(0);
            CancelShortcutTransition();
            _stage.CloseSpecialSurfaces();
            ReleaseOverlay();
            _surfaceSession = false;
            _shortcutMotion.Cancel();
            _stage.EndShortcutSoftFocus();
            ResetShortcutVisual();
        }
        _shortcutInTray = _shortcutHiding = false;
        _shortcutVisible = true;
        _stage.ResumeFromTray();
        _shortcutWindow?.ShowGameShell(activate);
        if (activate) _root.Focus(FocusState.Programmatic);
        _emptyShellTicks = 0;
        Log.Info($"game shell surface restored activate={activate}");
    }

    private void ParkGameShell(bool returnToPrevious)
    {
        bool returnFocus = returnToPrevious && _shortcutWindow?.IsForeground == true;
        _surfaceSession = false;
        _shortcutMotion.Cancel();
        _stage?.EndShortcutSoftFocus();
        _stage?.CloseSpecialSurfaces();
        ReleaseOverlay();
        _shortcutHiding = _shortcutInTray = false;
        _shortcutVisible = true;
        ResetShortcutVisual();
        _stage?.ResumeFromTray();
        _shortcutWindow?.ReturnToPreviousWindow(returnFocus);
        _shortcutWindow?.ShowGameShell(false);
        Log.Info("game shell parked visible behind foreground");
    }

    private void RevealGameShell()
    {
        if (_stage == null || !Stage.ShellMode.GameMode) return;
        _wasGameMode = true;
        if (_stage.Reduced || _shortcutWindow?.IsForeground != true) return;
        _shortcutMotion.Cancel();
        _shortcutMotion.Show(initial: true, windowVisible: true, foreground: true);
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        // The shell overlay provides the fade. Keep this window opaque so its black backing cannot show through.
        ResetShortcutVisual();
        visual.Scale = new Vector3(1.02f, 1.02f, 1);
        AnimateShortcut(true, true, _shortcutMotion.Version, () => { }, gameReveal: true);
    }
}
