using ClawLibrary.Data;

namespace ClawLibrary.Stage;

public sealed partial class WallStage
{
    private bool? _specialStickNavigates;
    internal bool SwitcherOpen => _wsOpen;
    internal bool SpecialSurfaceOpen => _qsOpen || _wsOpen;

    internal void SetSpecialInput(bool open)
    {
        if (_pad != null)
        {
            if (open) { _specialStickNavigates ??= _pad.StickNavigates; _pad.StickNavigates = true; }
            else if (_specialStickNavigates is bool previous) { _pad.StickNavigates = previous; _specialStickNavigates = null; }
        }
        _rootEl.Overlay.Opacity = open ? 0 : 1;
        _rootEl.Overlay.IsHitTestVisible = !open;
        if (open)
        {
            ExitScreensaver(); MarkInput();
            TouchKeyboard.Hide(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        }
    }

    /// <summary>
    /// 长按快设：控制中心的「窗口」页（控制中心照常从游戏库卡形变出来，再进这一页），不再是单独一张黑底。
    /// 快设面板开着就让它滑出去，同时控制中心进来。
    /// </summary>
    internal void OpenSwitcherShortcut()
    {
        SetSpecialInput(true);
        if (_wsOpen) { _top.Children.Remove(_ws!); _top.Children.InsertAtTop(_ws!); return; }
        if (_qsOpen) CloseQuickPanel(hideWindow: false);
        NowPlayingYield();
        _wsStandalone = false;
        // 快设面板的游戏画面交给下面的淡出接着用，不等面板收完一下子拆掉
        _qsThumb?.Dispose(); _qsThumb = null; if (_qsUnder != null) _qsUnder.IsVisible = false;
        OpenSwitcherFromAnywhere();
        // 盖在别的程序上：那个程序先原样留着，随控制中心进场淡掉
        if ((_window as MainWindow)?.SurfaceTarget is nint over and not 0) FadeWindowAway(over);
    }

    /// <summary>别的入口要接手时收起切换器：卡片沉下去（有动画），不再一下子消失。</summary>
    internal void CloseSwitcherForRoute()
    {
        _wsFromShortcut = _wsStandalone = false;
        CloseSwitcher();
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
        QsHideNow();
        if (_wsOpen) CloseSwitcher(instant: true);
        _wsStandalone = false;
        SetSpecialInput(false);
    }
}

public sealed partial class WallStage
{
    private long _quickKeyAt = -10_000, _menuCcAt = -10_000;
    private bool _ccInstantClose, _ccFadeClose, _ccKeepWall;

    /// <summary>
    /// 每次快设键到达时调用。日志里快设键按下后，控制中心先被一个「菜单」打开、0.75 s 后快设才到：
    /// 那一次控制中心不该出现 —— 直接撤掉（不走形变），快设面板开在原来的墙上。
    /// </summary>
    internal void NoteQuickKey()
    {
        long now = NowMs;
        if (_ccOpen && !_wsOpen && !_wsPending && now - _menuCcAt < 1200)
        {
            Log.Info($"control center undone: opened {now - _menuCcAt} ms before the quick key");
            _ccInstantClose = true;
            CloseControlCenter();
        }
        _quickKeyAt = now;
    }
}
