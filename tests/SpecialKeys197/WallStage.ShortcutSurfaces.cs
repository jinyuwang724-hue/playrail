using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    internal bool SwitcherOpen => _wsOpen;
    internal bool SpecialSurfaceOpen => _qsOpen || _wsOpen;

    internal void SetSpecialInput(bool open)
    {
        _rootEl.Overlay.Opacity = open ? 0 : 1;
        _rootEl.Overlay.IsHitTestVisible = !open;
        if (open)
        {
            ExitScreensaver(); MarkInput();
            TouchKeyboard.Hide(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        }
    }

    internal void OpenSwitcherShortcut()
    {
        SetSpecialInput(true);
        if (_wsOpen) { _top.Children.Remove(_ws!); _top.Children.InsertAtTop(_ws!); return; }
        if (_qsOpen) CloseQuickPanel(hideWindow: false, instant: true);
        NowPlayingYield();
        _wsStandalone = true;
        OpenSwitcher();
    }

    internal void CloseSpecialSurfaces()
    {
        ++_qsVersion;
        _qsOpen = _qsHideWindowAfter = false;
        _qsPoll?.Stop();
        if (_qsTicking) Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= QsFrame;
        _qsTicking = false; _qsShow = _qsShowTo = 0;
        _qsThumb?.Dispose(); _qsThumb = null;
        if (_qs != null) _qs.IsVisible = false;
        if (_wsOpen) CloseSwitcher(instant: true);
        SetSpecialInput(false);
    }
}
