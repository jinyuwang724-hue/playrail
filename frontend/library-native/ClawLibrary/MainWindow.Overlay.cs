using System.Runtime.InteropServices;
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
    private bool _surfaceHideWindow, _surfaceHadOverlay, _surfaceSession;
    private bool _desktopHandoff;

    internal bool OverlayAcceptsGamepad => _overlayActive && _shortcutVisible && !_shortcutInTray
        && _shortcutWindow?.OverlayTargetIsForeground == true;

    /// <summary>快设面板 / 切换器这次是盖在哪个程序上唤出的（游戏模式）；在游戏库自己上面唤出的是 0。</summary>
    internal nint SurfaceTarget => _overlayActive ? _overlayTarget : _surfaceSession && _surfaceHideWindow ? _shortcutWindow?.ReturnWindow ?? 0 : 0;

    private bool _switchInFlight;
    internal bool WindowSwitchInFlight => _switchInFlight;
    private AgentClient? _gamebarAgent;

    private async Task ActivateOverlayRoute(string route, nint returnWindow)
    {
        if (route.StartsWith("test:", StringComparison.Ordinal)) { _stage?.TestInput(route[5..], returnWindow); return; }
        await _overlayRoutes.WaitAsync();
        try
        {
            if (_shortcutClosed || _desktopHandoff || _switchInFlight) return;
            string targetRoute = ToolboxActivation.Target(route.Replace(App.ViaShell, ""));
            bool quick = targetRoute == "control-center", switcher = targetRoute == "window-switcher";
            Log.Info($"route {route} return={returnWindow}");
            if (route.StartsWith("minimize-", StringComparison.Ordinal))
            { _stage?.CloseSpecialSurfaces(); CompleteShortcutHide(returnToPrevious: false); return; }
            if (quick || switcher)
            {
                // 快设就是 Game Bar：复用一条后台连接，不必每次按键都重新握手
                if (quick) { _gamebarAgent ??= new AgentClient(); await _gamebarAgent.Call("open_gamebar"); }
                return;
            }
            ActivateRouteCore(route, returnWindow);
        }
        catch (Exception ex) { Log.Error("shortcut route", ex); }
        finally { _overlayRoutes.Release(); }
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
        ReleaseOverlay();
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
    }

    private static bool CoversMonitor(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return false;
        return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] private struct WinRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public WinRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out WinRect r);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
