using System.Text.Json.Nodes;
using ClawLibrary.Data;
using Microsoft.UI.Dispatching;

namespace ClawLibrary;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _overlayRoutes = new(1, 1);
    private AgentClient? _overlayAgent;
    private DispatcherQueueTimer? _overlayTimer;
    private volatile bool _overlayActive;
    private nint _overlayTarget;
    private bool _overlayRenewing, _overlayInputPrepared;
    private bool _surfaceHideWindow, _surfaceHadOverlay, _surfaceSession;
    private bool _desktopHandoff;

    internal bool OverlayAcceptsGamepad => _overlayActive && _shortcutVisible && !_shortcutInTray
        && _shortcutWindow?.OverlayTargetIsForeground == true;

    /// <summary>快设面板 / 切换器这次是盖在哪个程序上唤出的（游戏模式）；在游戏库自己上面唤出的是 0。</summary>
    internal nint SurfaceTarget => _overlayActive ? _overlayTarget : _surfaceSession && _surfaceHideWindow ? _shortcutWindow?.ReturnWindow ?? 0 : 0;

    private long _lastSpecialRouteAt = -10_000;

    private async Task ActivateOverlayRoute(string route, nint returnWindow)
    {
        await _overlayRoutes.WaitAsync();
        try
        {
            if (_shortcutClosed || _desktopHandoff) return;
            if (route == "game-shell") { RestoreGameShell(true); return; }
            if (route == "keep-game-shell") { if (Stage.ShellMode.GameMode) RestoreGameShell(false); return; }
            string targetRoute = ToolboxActivation.Target(route.Replace(App.ViaShell, ""));
            bool quick = targetRoute == "control-center", switcher = targetRoute == "window-switcher";
            bool special = quick || switcher;
            bool game = Stage.ShellMode.GameMode;
            Log.Info($"route {route} return={returnWindow}");
            // 快设键按一下，固件 / 后台偶尔紧跟着再报一个游戏库开关：它会把刚开的面板连同游戏库一起收走、回到上一个程序。
            // 游戏模式下快设之后 600 ms 内的游戏库开关一律不理
            long routeAt = Environment.TickCount64;
            if (special) _lastSpecialRouteAt = routeAt;
            else if (game && routeAt - _lastSpecialRouteAt < 600 && ToolboxActivation.Target(route.Replace(App.ViaShell, "")) is "library" or "activate")
            { Log.Info($"route {route} ignored: right after a quick-settings route"); return; }
            if (!game && route.StartsWith("minimize-", StringComparison.Ordinal))
            { _stage?.CloseSpecialSurfaces(); CompleteShortcutHide(returnToPrevious: false); return; }
            if (special && !game)
            {
                if (quick) { using var agent = new AgentClient(); await agent.Call("open_gamebar"); }
                return;
            }
            if (quick && _stage?.QuickPanelOpen == true) { ActivateRouteCore(route, returnWindow); return; }
            if (!special && _overlayActive && _shortcutVisible && _stage?.SpecialSurfaceOpen != true
                && route == "restore-library") route = "minimize-library";
            bool closing = !special && route.StartsWith("minimize-", StringComparison.Ordinal);
            nint self = WinRT.Interop.WindowNative.GetWindowHandle(this);
            nint foreground = LibraryWindow.Foreground;
            bool owned = AppWindow.IsVisible && !ToolboxWindow.IsIconic(self)
                && (foreground == self || OverlayAcceptsGamepad);
            if (special && !_surfaceSession)
            {
                _surfaceSession = true;
                _surfaceHideWindow = !owned;
                _surfaceHadOverlay = _overlayActive;
            }
            // Use the window that actually owns input now. A delayed firmware
            // report must not pin the surface to an application already left.
            nint target = foreground != self ? foreground : returnWindow;
            bool candidate = game && !closing && LibraryWindow.CanOverlay(target)
                && !ToolboxWindow.ShellCloaked(self);
            if (!closing && (candidate || special) && _overlayAgent == null)
            {
                _overlayAgent = new AgentClient();
                try
                {
                    if (!_overlayInputPrepared)
                    {
                        await _overlayAgent.Call("refresh_input_layer_apps");
                        _overlayInputPrepared = true;
                    }
                    var state = await _overlayAgent.Call("set_overlay_hold", JsonValue.Create(true));
                    if ((bool?)state?["safe"] != true) throw new IOException("输入层尚未确认隔离");
                    StartOverlayHeartbeat();
                }
                catch (Exception ex)
                {
                    Log.Warn($"shortcut input fallback: {ex.Message}");
                    _overlayInputPrepared = false;
                    _overlayAgent.Dispose(); _overlayAgent = null;
                    candidate = false;
                }
            }
            if (candidate && _overlayAgent != null)
            {
                nint now = LibraryWindow.Foreground;
                if (now != self && LibraryWindow.CanOverlay(now)) target = now;
                _overlayTarget = target; _overlayActive = true;
                _shortcutWindow?.SetOverlayTarget(target);
                _shortcutWindow?.EnsureFullscreenBounds();
            }
            ActivateRouteCore(route, returnWindow);
        }
        catch (Exception ex) { Log.Error("shortcut route", ex); }
        finally { _overlayRoutes.Release(); }
    }

    private void StartOverlayHeartbeat()
    {
        _overlayTimer ??= DispatcherQueue.CreateTimer();
        _overlayTimer.Interval = TimeSpan.FromMilliseconds(400);
        _overlayTimer.Tick -= OverlayHeartbeat; _overlayTimer.Tick += OverlayHeartbeat;
        _overlayTimer.Start();
    }

    private async void OverlayHeartbeat(DispatcherQueueTimer sender, object args)
    {
        if (_overlayAgent == null || _overlayRenewing) return;
        if (!Stage.ShellMode.GameMode || (_shortcutWindow?.OverlayTargetIsForeground != true && _shortcutWindow?.IsForeground != true))
        {
            _stage?.CloseSpecialSurfaces();
            CompleteShortcutHide(returnToPrevious: false);
            return;
        }
        _overlayRenewing = true;
        var client = _overlayAgent;
        try
        {
            var result = await client.Call("set_overlay_hold", JsonValue.Create(true));
            if ((bool?)result?["safe"] != true) throw new IOException("input isolation lost");
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(client, _overlayAgent)) return;
            Log.Warn($"shortcut heartbeat: {ex.Message}");
            _overlayInputPrepared = false;
            _stage?.CloseSpecialSurfaces();
            CompleteShortcutHide(returnToPrevious: false);
        }
        finally { _overlayRenewing = false; }
    }

    private void ReleaseOverlay()
    {
        _overlayActive = false; _overlayTarget = 0;
        _overlayTimer?.Stop();
        _shortcutWindow?.SetOverlayTarget(0);
        _overlayAgent?.Dispose(); _overlayAgent = null;
    }

    private void RestoreOrdinaryPresenter() => _shortcutWindow?.EnsureFullscreenBounds();

    internal void EndSpecialSurface()
    {
        _stage?.SetSpecialInput(false);
        if (_surfaceHideWindow) CompleteShortcutHide();
        else if (!_surfaceHadOverlay) ReleaseOverlay();
        _surfaceSession = false;
    }

    /// <summary>切换器里选了游戏库自己：收起这次唤出，但游戏库留在前台（不回唤出前的程序）。</summary>
    internal void KeepLibraryAfterSurface()
    {
        _stage?.SetSpecialInput(false);
        _surfaceSession = _surfaceHideWindow = false;
        ReleaseOverlay();
        if (Stage.ShellMode.GameMode) RestoreGameShell(true);
    }

    internal bool SwitchToWindow(OpenWindow target)
    {
        if (!OpenWindows.Matches(target)) return false;
        CancelShortcutTransition();
        _stage?.CloseSpecialSurfaces();
        CompleteShortcutHide(returnToPrevious: false);
        return LibraryWindow.BringForward(target.Hwnd);
    }

    internal void BeginDesktopHandoff()
    {
        _desktopHandoff = true;
        CancelShortcutTransition();
        _stage?.CloseSpecialSurfaces();
        CompleteShortcutHide(returnToPrevious: false);
    }

    internal void FinishDesktopHandoff() => _desktopHandoff = false;

    internal void RequestTextInputFocus()
    {
        if (!_overlayActive) return;
        _shortcutWindow?.SetOverlayTarget(0);
        LibraryWindow.BringForward(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }
}
