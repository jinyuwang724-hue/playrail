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

    private bool _switchInFlight;
    internal bool WindowSwitchInFlight => _switchInFlight;
    private long _lastSpecialRouteAt = -10_000;
    private AgentClient? _gamebarAgent;

    private async Task ActivateOverlayRoute(string route, nint returnWindow)
    {
        if (route.StartsWith("test:", StringComparison.Ordinal)) { _stage?.TestInput(route[5..], returnWindow); return; }
        await _overlayRoutes.WaitAsync();
        try
        {
            if (_shortcutClosed || _desktopHandoff || _switchInFlight) return;
            if (route == "game-shell") { RestoreGameShell(true); return; }
            if (route == "game-shell-reveal") { RevealGameShell(); return; }
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
            else if (game && (routeAt - _lastSpecialRouteAt < 600 || _stage?.SpecialSurfaceOpen == true) && targetRoute is "library" or "activate")
            { Log.Info($"route {route} ignored: right after a quick-settings route"); return; }
            if (!game && route.StartsWith("minimize-", StringComparison.Ordinal))
            { _stage?.CloseSpecialSurfaces(); CompleteShortcutHide(returnToPrevious: false); return; }
            if (special && !game)
            {
                // 桌面模式的快设就是 Game Bar：复用一条后台连接，不必每次按键都重新握手
                if (quick) { _gamebarAgent ??= new AgentClient(); await _gamebarAgent.Call("open_gamebar"); }
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
            // 快捷面板有自己的窗口：先把面板弹出来（不等后台），输入隔离随后补上
            bool fastQuick = quick && game && !closing && _stage?.QuickPanelOwnWindow == true && _stage?.QuickPanelOpen != true;
            if (fastQuick) ActivateRouteCore(route, returnWindow);
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
            if (!fastQuick) ActivateRouteCore(route, returnWindow);
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

    /// <summary>快捷面板（自己的窗口）收起：游戏库主窗口没动过，只放掉输入隔离、结束这次唤出。</summary>
    internal void EndQuickOverlay()
    {
        _stage?.SetSpecialInput(false);
        _surfaceSession = _surfaceHideWindow = false;
        if (!_shortcutVisible || !Stage.ShellMode.GameMode || _shortcutWindow?.IsForeground != true) ReleaseOverlay();
        Log.Info("quick panel ended (library window untouched)");
    }

    /// <summary>面板里点了要交给游戏库的事（窗口切换、回游戏库）：走一遍正常的唤出路由。</summary>
    internal void RouteFromPanel(string route, nint returnWindow) => ActivateRoute(route, returnWindow);

    internal void EndSpecialSurface()
    {
        _stage?.SetSpecialInput(false);
        if (_surfaceHideWindow) CompleteShortcutHide();
        else if (!_surfaceHadOverlay) ReleaseOverlay();
        _surfaceSession = false;
    }

    /// <summary>切换器里选了游戏库自己：收起这次唤出，但游戏库留在前台（不回唤出前的程序）。</summary>
    internal async Task<bool> KeepLibraryAfterSurface()
    {
        if (_switchInFlight) return false;
        _switchInFlight = true;
        try
        {
            _stage?.SetSpecialInput(false);
            _surfaceSession = _surfaceHideWindow = false;
            ReleaseOverlay();
            if (_shortcutClosed || _desktopHandoff) return false;
            if (Stage.ShellMode.GameMode)
            {
                _wasGameMode = true;
                RestoreGameShell(true);
            }
            nint self = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (!LibraryWindow.OwnsForeground(self))
            {
                await Task.Delay(45);
                if (_shortcutClosed || _desktopHandoff) return false;
                if (!LibraryWindow.OwnsForeground(self)) LibraryWindow.BringForward(self, unlock: true);
                await Task.Delay(55);
            }
            if (_shortcutClosed || _desktopHandoff || !LibraryWindow.OwnsForeground(self)) return false;
            _shortcutWindow?.SetTopmost(true);
            return true;
        }
        finally { _switchInFlight = false; }
    }

    /// <summary>
    /// 切到 target：先收起开始菜单 / 搜索（它们攥着前台时谁都拿不到前台），再切；拿不到前台就重试（最多 3 次，每次带
    /// AttachThreadInput 和一次 Alt 键解锁前台），还是不行就把游戏库拿到前台（绝不留空屏）。返回是否切到了目标。
    /// </summary>
    internal async Task<bool> SwitchToWindow(OpenWindow target)
    {
        if (_switchInFlight) return false;
        _switchInFlight = true;
        long started = Environment.TickCount64;
        nint source = LibraryWindow.Foreground;
        string context = $"target=0x{target.Hwnd:x} pid={target.Pid} process={target.Process} elevated={OpenWindows.Elevated(target.Pid)} coversMonitor={CoversMonitor(target.Hwnd)} exclusive=unknown source=0x{source:x}";
        void Step(string phase, long at, string result) =>
            Log.Info($"window_switch phase={phase} elapsedMs={Environment.TickCount64 - at} totalMs={Environment.TickCount64 - started} foreground=0x{LibraryWindow.Foreground:x} {context} {result}");

        try
        {
            CancelShortcutTransition();
            _stage?.CloseSpecialSurfaces();
            long at = Environment.TickCount64;
            bool dismissed = await Task.Run(() => ShellSurfaces.DismissForeground(250, "(before switch)"));
            Step("dismiss", at, $"ok={dismissed}");
            CompleteShortcutHide(returnToPrevious: false);

            for (int attempt = 1; attempt <= 3 && OpenWindows.Matches(target); attempt++)
            {
                if (_desktopHandoff || _shortcutClosed) return false;
                at = Environment.TickCount64;
                bool immediate = LibraryWindow.BringForward(target.Hwnd, attempt > 1, out int error);
                // Restore/activation of a different GUI thread can complete after SetForegroundWindow returns.
                await Task.Delay(immediate ? 55 : 100);
                bool ok = OpenWindows.Matches(target) && LibraryWindow.OwnsForeground(target.Hwnd);
                Step("activate", at, $"attempt={attempt} immediate={immediate} ok={ok} win32={error}");
                if (ok) return true;

                at = Environment.TickCount64;
                dismissed = await Task.Run(() => ShellSurfaces.DismissForeground(150, "(retry)"));
                Step("retry_dismiss", at, $"attempt={attempt} ok={dismissed}");
            }
        }
        catch (Exception ex) { Step("exception", started, $"hresult=0x{ex.HResult:x}"); }
        finally { _switchInFlight = false; }

        long fallback = Environment.TickCount64;
        if (Stage.ShellMode.GameMode) RestoreGameShell(true);
        Step("fallback", fallback, $"ok={_shortcutWindow?.IsForeground == true}");
        return false;
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
