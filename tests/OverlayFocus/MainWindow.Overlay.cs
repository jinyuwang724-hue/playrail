using System.Text.Json.Nodes;
using ClawLibrary.Data;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace ClawLibrary;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _overlayRoutes = new(1, 1);
    private AgentClient? _overlayAgent;
    private DispatcherQueueTimer? _overlayTimer;
    private volatile bool _overlayActive;
    private nint _overlayTarget;
    private bool _overlayRenewing;
    private bool _overlayPresenter;

    private void EnableOverlayPresenter()
    {
        if (_overlayPresenter) return;
        var bounds = new Windows.Graphics.RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        _shortcutWindow?.SetOverlayBounds(bounds);
        _overlayPresenter = true;
    }

    internal bool OverlayAcceptsGamepad => _overlayActive && _shortcutVisible && !_shortcutInTray
        && _shortcutWindow?.OverlayTargetIsForeground == true;

    // The input hold must be acknowledged before showing any UI over a game.
    private async Task ActivateOverlayRoute(string route, nint returnWindow)
    {
        await _overlayRoutes.WaitAsync();
        try
        {
            if (_shortcutClosed) return;
            bool quick = ToolboxActivation.Target(route.Replace(App.ViaShell, "")) == "control-center";
            // The game stays foreground, so the router's restore-library route
            // must still toggle an already visible library overlay closed.
            if (!quick && _overlayActive && _shortcutVisible && _stage?.QuickPanelOpen != true
                && route == "restore-library") route = "minimize-library";
            bool closing = quick ? _stage?.QuickPanelOpen == true
                : _overlayActive && route.StartsWith("minimize-", StringComparison.Ordinal);
            nint target = _overlayActive ? _overlayTarget : returnWindow != 0 ? returnWindow : LibraryWindow.Foreground;
            bool gameMode = Stage.ShellMode.Available && !Stage.ShellMode.ExplorerRunning();
            bool candidate = gameMode && !closing && LibraryWindow.CanOverlay(target)
                && LibraryWindow.Foreground == target && !ToolboxWindow.ShellCloaked(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (!closing && (candidate || quick) && _overlayAgent == null)
            {
                _overlayAgent ??= new AgentClient();
                try
                {
                    await _overlayAgent.Call("refresh_input_layer_apps");
                    var result = await _overlayAgent.Call("set_overlay_hold", JsonValue.Create(true));
                    if ((bool?)result?["safe"] != true) throw new InvalidOperationException("输入隔离尚未就绪");
                    if (_shortcutClosed || (candidate && LibraryWindow.Foreground != target))
                    {
                        await _overlayAgent.Call("set_overlay_hold", JsonValue.Create(false));
                        return;
                    }
                    _overlayTarget = candidate ? target : 0;
                    _overlayActive = candidate;
                    _shortcutWindow?.SetOverlayTarget(_overlayTarget);
                    if (candidate) EnableOverlayPresenter();
                    StartOverlayHeartbeat();
                    Log.Info($"overlay ready target={target}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"overlay unavailable; foreground fallback: {ex.Message}");
                    _overlayAgent.Dispose(); _overlayAgent = null;
                }
            }
            ActivateRouteCore(route, returnWindow);
        }
        catch (Exception ex) { Log.Error("overlay route", ex); }
        finally { _overlayRoutes.Release(); }
    }

    private void StartOverlayHeartbeat()
    {
        _overlayTimer ??= DispatcherQueue.CreateTimer();
        _overlayTimer.Interval = TimeSpan.FromMilliseconds(500);
        _overlayTimer.Tick -= OverlayHeartbeat;
        _overlayTimer.Tick += OverlayHeartbeat;
        _overlayTimer.Start();
    }

    private async void OverlayHeartbeat(DispatcherQueueTimer sender, object args)
    {
        if (_overlayAgent == null || _overlayRenewing) return;
        // Explicitly switching to a third app also dismisses the overlay.
        if (_shortcutWindow?.OverlayTargetIsForeground != true && _shortcutWindow?.IsForeground != true)
        {
            _stage?.CloseQuickPanel(hideWindow: false, instant: true);
            CompleteShortcutHide();
            return;
        }
        _overlayRenewing = true;
        var client = _overlayAgent!;
        try
        {
            var result = await client.Call("set_overlay_hold", JsonValue.Create(true));
            if ((bool?)result?["safe"] != true) throw new IOException("input isolation lost");
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(client, _overlayAgent)) return;
            Log.Warn($"overlay heartbeat: {ex.Message}");
            // Hide before dropping the hold if the input engine has failed.
            _stage?.CloseQuickPanel(hideWindow: false, instant: true);
            CompleteShortcutHide();
        }
        finally { _overlayRenewing = false; }
    }

    private void ReleaseOverlay()
    {
        _overlayActive = false;
        _overlayTarget = 0;
        _overlayTimer?.Stop();
        _shortcutWindow?.SetOverlayTarget(0);
        // One connection owns the lease. EOF releases it even during app shutdown;
        // an old close can never release a newly opened overlay's lease.
        _overlayAgent?.Dispose(); _overlayAgent = null;
    }

    private void RestoreOrdinaryPresenter()
    {
        // Changing presenter on a minimized window restores and activates it.
        // Defer this until an ordinary foreground activation is actually wanted.
        if (_overlayActive || !_overlayPresenter) return;
        _overlayPresenter = false;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    internal void RequestTextInputFocus()
    {
        if (!_overlayActive) return;
        _shortcutWindow?.SetOverlayTarget(0);
        LibraryWindow.BringForward(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }
}
