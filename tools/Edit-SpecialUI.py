from pathlib import Path
from importlib.util import spec_from_file_location,module_from_spec
import re,shutil
s=spec_from_file_location('edit',Path(__file__).with_name('Edit-SpecialKeys.py'));m=module_from_spec(s);s.loader.exec_module(m)
edit=m.edit;LIB=m.LIB;ROOT=m.ROOT

def method(p,start,end,new):
    t=p.read_text(encoding='utf-8-sig');a=t.index(start);b=t.index(end,a)
    p.write_text(t[:a]+new+t[b:],encoding='utf-8')

def main():
    for name in ('MainWindow.Overlay.cs','WallStage.ShortcutSurfaces.cs'):
        dest=LIB/('Stage' if name.startswith('WallStage') else '')/name
        shutil.copyfile(ROOT/'tests/SpecialKeys197'/name,dest)
    p=LIB/'Data/LibraryWindow.cs'
    edit(p,'"minimize-library", "minimize-control-center"]','"minimize-library", "minimize-control-center", "window-switcher"]')
    method(p,'        try\n        {\n            using var p = System.Diagnostics.Process.GetProcessById((int)pid);','    public bool OverlayTargetIsForeground', '''        return pid != 0;
    }

''')
    method(p,'    public void SetOverlayBounds(', '    public void ShowOverlay()', '''    private bool _sizing;
    public void EnsureFullscreenBounds()
    {
        if (_sizing || Program.Args.Contains("--windowed")) return;
        _sizing = true;
        try
        {
            nint monitor = MonitorFromWindow(_overlayTarget != 0 ? _overlayTarget : _window, 2);
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return;
            var r = info.Monitor;
            long style = GetWindowLongPtr(_window, -16).ToInt64();
            long desired = (style & ~0x00CF0000L) | 0x80000000L;
            long extended = GetWindowLongPtr(_window, -20).ToInt64();
            bool changed = style != desired || (extended & 0x300) != 0;
            if (style != desired) SetWindowLongPtr(_window, -16, (nint)desired);
            if ((extended & 0x300) != 0) SetWindowLongPtr(_window, -20, (nint)(extended & ~0x300L));
            int square = 1, noBorder = unchecked((int)0xfffffffe);
            DwmSetWindowAttribute(_window, 33, ref square, 4);
            DwmSetWindowAttribute(_window, 34, ref noBorder, 4);
            if (changed || !GetWindowRect(_window, out var current) || !current.Equals(r))
                SetWindowPos(_window, 0, r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top, 0x234);
        }
        finally { _sizing = false; }
    }

''')
    edit(p,'        if (_overlayBounds.Width > 0) SetOverlayBounds(_overlayBounds);','        EnsureFullscreenBounds();')
    edit(p,'        if (_overlayTarget != 0 && message == 0x21)', '        if (message == 0x007e || message == 0x02e0) Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(EnsureFullscreenBounds);\n        if (_overlayTarget != 0 && message == 0x21)')
    edit(p,'        if (ToolboxWindow.IsIconic(window)) ShowWindow(window, 9);','        if (ToolboxWindow.IsIconic(window)) ShowWindow(window, 9);\n        nint popup = GetLastActivePopup(window);\n        if (popup != 0 && IsWindowVisible(popup)) window = popup;')
    edit(p,'    [UnmanagedFunctionPointer', '''    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint hwnd);
    [UnmanagedFunctionPointer''')
    p=LIB/'App.cs'
    edit(p,'                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);','                var presenter = OverlappedPresenter.Create();\n                presenter.SetBorderAndTitleBar(false, false);\n                presenter.IsResizable = false;\n                appWindow.SetPresenter(presenter);')
    edit(p,'        InitializeShortcutWindow(returnWindow);','        InitializeShortcutWindow(returnWindow);\n        _shortcutWindow?.EnsureFullscreenBounds();')
    edit(p,'                _stage.ActivateShortcut(ToolboxActivation.Target(_pendingRoute));\n                if (_pendingRoute != "activate") ShowShortcutWindow(_pendingRoute, initial: true);','                _shortcutWindow?.EnsureFullscreenBounds();\n                if (_pendingRoute != "activate") ActivateRoute(_pendingRoute, returnWindow);\n                else _stage.ActivateShortcut("library");')
    edit(p,'        bool quick = ToolboxActivation.Target(route) == "control-center";','        bool quick = ToolboxActivation.Target(route) == "control-center";\n        bool switcher = ToolboxActivation.Target(route) == "window-switcher";\n        if (switcher)\n        {\n            _pendingRoute = "window-switcher";\n            _stage?.OpenSwitcherShortcut();\n            ShowShortcutWindow(route);\n            return;\n        }\n        if (quick) { if (_stage?.SwitcherOpen == true) _stage.CloseSpecialSurfaces(); _stage?.SetSpecialInput(true); }\n        else if (_stage?.SwitcherOpen == true) _stage.CloseSpecialSurfaces();')
    edit(p,'        if (quick) { ShowAfterFrames(route, 2); return; }','        if (quick) { ShowAfterFrames(route, 1); return; }')
    edit(p,'        if (!ToolboxActivation.PreservePage(route)) _stage?.ActivateShortcut(_pendingRoute);','        if (_stage?.SpecialSurfaceOpen != true && !ToolboxActivation.PreservePage(route)) _stage?.ActivateShortcut(_pendingRoute);')
    p=LIB/'MainWindow.Shortcut.cs'
    edit(p,'internal void HideAfterQuickPanel() => HideShortcutWindow();','internal void HideAfterQuickPanel() => EndSpecialSurface();')
    edit(p,'    private void CompleteShortcutHide()','    private void CompleteShortcutHide(bool returnToPrevious = true)')
    edit(p,'        bool returnFocus = _shortcutWindow?.IsForeground == true;','        bool returnFocus = returnToPrevious && _shortcutWindow?.IsForeground == true;')
    edit(p,'        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);\n        // The initial','        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);\n        _shortcutWindow?.EnsureFullscreenBounds();\n        // The initial')
    p=LIB/'Stage/WallStage.ShellMode.cs'
    edit(p,'    public static bool Available => File.Exists(Exe);','    public static bool Available => File.Exists(Exe);\n    public static bool GameMode => Available && !ExplorerRunning() && ReadState()?["mode"]?.GetValue<string>() == "game";')
    edit(p,'            int code = await ShellMode.Run(toGame ? "to-game" : "to-desktop");','            if (!toGame) (_window as MainWindow)?.BeginDesktopHandoff();\n            int code = await ShellMode.Run(toGame ? "to-game" : "to-desktop");')
    edit(p,'            else if (!toGame) (_window as MainWindow)?.RunInBackground();','')
    edit(p,'        finally { _shellBusy = false; }','        finally { (_window as MainWindow)?.FinishDesktopHandoff(); _shellBusy = false; }')
    p=LIB/'Data/ToolboxActivation.cs'
    edit(p,'    public static bool SteamHomeRedirect(string route, nint window)\n    {','    public static bool SteamHomeRedirect(string route, nint window)\n    {\n        if (!Stage.ShellMode.GameMode) return false;')
    p=LIB/'Data/OpenWindows.cs'
    edit(p,'    public static bool Alive(nint hwnd)', '    public static bool Matches(OpenWindow window) => IsWindow(window.Hwnd) && GetWindowThreadProcessId(window.Hwnd, out uint pid) != 0 && pid == window.Pid;\n\n    public static bool Alive(nint hwnd)')
    p=LIB/'Stage/WallStage.Switcher.cs'
    edit(p,'private bool _wsOpen, _wsTicking;','private bool _wsOpen, _wsTicking, _wsStandalone;')
    edit(p,'        if (_cc == null) return;\n        EnsureWsVisuals();','        if (!_wsStandalone && _cc == null) return;\n        _ccK = Math.Min((float)View.Width / 1280, (float)View.Height / 800);\n        EnsureWsVisuals();\n        _top.Children.Remove(_ws!); _top.Children.InsertAtTop(_ws!);')
    edit(p,'        foreach (var vis in CcPageParts()) vis.StartAnimation','        if (!_wsStandalone) foreach (var vis in CcPageParts()) vis.StartAnimation')
    edit(p,'        foreach (var vis in CcPageParts()) { vis.StopAnimation','        if (!_wsStandalone) foreach (var vis in CcPageParts()) { vis.StopAnimation')
    edit(p,'        _cc!.Children.InsertAtTop(_ws);','        var backdrop = G.Sprite(G.C.CreateColorBrush(Windows.UI.Color.FromArgb(255, 9, 9, 11)));\n        backdrop.RelativeSizeAdjustment = Vector2.One;\n        _ws.Children.InsertAtBottom(backdrop);\n        _ws.RelativeSizeAdjustment = Vector2.One;\n        _top.Children.InsertAtTop(_ws);')
    method(p,'    private CanvasBitmap? WsAppIcon(', '    /// <summary>卡框', '''    private CanvasBitmap? WsAppIcon(OpenWindow w)
    {
        if (_wsIcons.TryGetValue(w.Pid, out var cached)) return cached;
        _wsIcons[w.Pid] = null;
        int version = _wsVersion;
        _ = Task.Run(() => {
            byte[]? pixels = null; int size = 0;
            try { pixels = OpenWindows.IconPixels(w.Hwnd, w.Pid, out size); }
            catch (Exception ex) { Log.Warn($"window icon {w.Process}: {ex.Message}"); }
            _dq.TryEnqueue(() => {
                if (version != _wsVersion || !_wsOpen || pixels == null) return;
                try {
                    _wsIcons[w.Pid] = CanvasBitmap.CreateFromBytes(G.Device, pixels, size, size, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    foreach (var card in _wsCards.Where(c => c.Win.Pid == w.Pid)) WsPaintCard(card);
                } catch (Exception ex) { Log.Warn($"window icon upload: {ex.Message}"); }
            });
        });
        return null;
    }

''')
    method(p,'        if (_game is { Pending: false } g', '    /// <summary>X：', '''        if (!OpenWindows.Matches(w)) { WsReconcile(); return; }
        bool ok = (_window as MainWindow)?.SwitchToWindow(w) == true;
        Log.Info($"switch to {w.Process} hwnd={w.Hwnd} foreground={ok}");
        if (!ok) { Cue(Sfx.Error); Toast("没能切过去，请再次长按快设重试"); }
    }

''')
    edit(p,'    private void WsBack() => CloseSwitcher();','    private void WsBack()\n    {\n        bool standalone = _wsStandalone;\n        CloseSwitcher(instant: standalone);\n        _wsStandalone = false;\n        if (standalone) (_window as MainWindow)?.EndSpecialSurface();\n    }')
    # Route the top surface before every underlying modal, editor, or animation.
    p=LIB/'Stage/WallStage.cs'
    for anchor,new in [('        if (_ob != null) { ObMove(dir); return; }','        if (_wsOpen) { WsMove(dir); return; }'),('        if (_ob != null) { ObActivate(); return; }','        if (_wsOpen) { WsActivate(); return; }'),('        if (_ob != null) { ObBack(); return; }','        if (_wsOpen) { WsBack(); return; }'),('        if (TouchNavigationClick(sx, sy)) return;','        if (_wsOpen) { WsClick(sx, sy); return; }')]:edit(p,anchor,new+'\n'+anchor)
    edit(p,'public void SwitchTab(int delta) => WithCue(Sfx.Tab, () => {','public void SwitchTab(int delta) => WithCue(Sfx.Tab, () => { if (_wsOpen) { WsMove(delta < 0 ? Direction.Left : Direction.Right); return; }')
    edit(p,'if (e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox) return;','if (!_wsOpen && !_qsOpen && e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox) return;')
    t=p.read_text(encoding='utf-8');t=t.replace('if (_ob == null && !_qsOpen', 'if (!_wsOpen && _ob == null && !_qsOpen');p.write_text(t,encoding='utf-8')
    p=LIB/'Stage/WallStage.ControlCenter.cs'
    for name,body in [('PadY','return;'),('PadView','return;'),('PadX','WsCloseFocused(); return;')]:
        edit(p,'    public void '+name+'()\n    {','    public void '+name+'()\n    {\n        if (_wsOpen) { '+body+' }')
    edit(p,'    public void ToggleControlCenter() => WithCue(Sfx.None, () =>\n    {','    public void ToggleControlCenter() => WithCue(Sfx.None, () =>\n    {\n        if (_wsOpen) { WsBack(); return; }')
    p=LIB/'Stage/WallStage.Touch.Navigation.cs'
    edit(p,'            if (_wsOpen) return new(6, _wsFocus, _wsCards.Count);','')
    edit(p,'            if (_ob != null) return new(1,','            if (_wsOpen) return new(6, _wsFocus, _wsCards.Count);\n            if (_ob != null) return new(1,')
    edit(p,': _ob != null ? 0',': _wsOpen ? 0\n        : _ob != null ? 0')
    p=LIB/'Stage/WallStage.Screensaver.cs'
    edit(p,'private bool ScreensaverBlocked =>','private bool ScreensaverBlocked => _wsOpen ||')

if __name__=='__main__':main()
