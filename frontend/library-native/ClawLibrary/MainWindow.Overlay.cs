using ClawLibrary.Data;

namespace ClawLibrary;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _overlayRoutes = new(1, 1);
    private AgentClient? _gamebarAgent;

    /// <summary>后台 / claw-library:// 送来的路由。快设键就是 Game Bar（复用一条后台连接，不必每次按键都重新握手）。</summary>
    private async Task ActivateOverlayRoute(string route, nint returnWindow)
    {
        if (route.StartsWith("test:", StringComparison.Ordinal)) { _stage?.TestInput(route[5..], returnWindow); return; }
        await _overlayRoutes.WaitAsync();
        try
        {
            if (_shortcutClosed) return;
            string targetRoute = ToolboxActivation.Target(route.Replace(App.ViaShell, ""));
            Log.Info($"route {route} return={returnWindow}");
            if (route.StartsWith("minimize-", StringComparison.Ordinal)) { CompleteShortcutHide(returnToPrevious: false); return; }
            if (targetRoute == "control-center")
            {
                _gamebarAgent ??= new AgentClient();
                await _gamebarAgent.Call("open_gamebar");
                return;
            }
            ActivateRouteCore(route, returnWindow);
        }
        catch (Exception ex) { Log.Error("shortcut route", ex); }
        finally { _overlayRoutes.Release(); }
    }
}
