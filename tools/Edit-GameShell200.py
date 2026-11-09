from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];SRC=ROOT/'artifacts/game-shell-200/src';LIB=SRC/'frontend/library-native/ClawLibrary'
def edit(p,old,new):
    t=p.read_text(encoding='utf-8-sig')
    if t.count(old)!=1:raise RuntimeError(str(p)+': '+old[:80])
    p.write_text(t.replace(old,new,1),encoding='utf-8')
def main():
    p=SRC/'native/ClawShell/ShellGuard.cs'
    edit(p,'    public interface IDesktopSurface','    public interface IGameSurface { bool EnsureLibrarySurface(bool activate); }\n\n    public interface IDesktopSurface')
    edit(p,'if (_sys.LaunchLibrary()) { _sys.WriteState("game", "boot"); return true; }','if (_sys.LaunchLibrary()) { _sys.WriteState("game", "boot"); if (EnsureSurface(true)) return true; }')
    edit(p,'            if (_gaveUp || _sys.LibraryRunning()) return;','''            if (_gaveUp) return;
            if (_sys.LibraryRunning())
            {
                if (!EnsureSurface(false))
                {
                    RecordFailure("library-window-unavailable");
                    if (!_gaveUp && !_sys.LaunchLibrary()) RecordFailure("library-window-recovery-failed");
                }
                return;
            }''')
    edit(p,'        private void RecordFailure(string what)','        private bool EnsureSurface(bool activate)\n        {\n            var surface = _sys as IGameSurface;\n            return surface == null || surface.EnsureLibrarySurface(activate);\n        }\n\n        private void RecordFailure(string what)')
    edit(p,'            if (!_sys.ExplorerRunning()) { _sys.WriteState("game", "already"); return SwitchResult.Ok; }','''            if (!_sys.ExplorerRunning())
            {
                if ((!_sys.LibraryRunning() && !_sys.LaunchLibrary()) || !EnsureSurface(true))
                { FallbackToDesktop("library-window-unavailable"); return SwitchResult.Failed; }
                _sys.WriteState("game", "already"); return SwitchResult.Ok;
            }''')
    edit(p,'            if (_sys.SteamInstalled() &&','''            if (!EnsureSurface(true))
            { _sys.WriteState("desktop", "refused:library-window"); return SwitchResult.Refused; }
            if (_sys.SteamInstalled() &&''')
    edit(p,'            _sys.WriteState("game", "switched");\n            return SwitchResult.Ok;','''            _sys.WriteState("game", "switched");
            if (!EnsureSurface(true))
            { FallbackToDesktop("library-window-unavailable"); return SwitchResult.Failed; }
            return SwitchResult.Ok;''')
    p=SRC/'native/ClawShell/WindowsShellSystem.cs'
    edit(p,'public sealed class WindowsShellSystem : IShellSystem, IDesktopSurface','public sealed partial class WindowsShellSystem : IShellSystem, IDesktopSurface, IGameSurface')
    edit(p,'ActivateApplication(LibraryAumid, "", 0, out pid)','ActivateApplication(LibraryAumid, "--game-shell", 0, out pid)')
    p=SRC/'native/ClawShell/Program.cs'
    edit(p,'public const string Version = "1.0.0"','public const string Version = "1.0.1"')
    edit(p,'                case "to-game":','                case "ensure-surface": return _sys.EnsureLibrarySurface(args.Contains("--activate")) ? 0 : 1;\n                case "to-game":')
    p=LIB/'Data/LibraryWindow.cs'
    edit(p,'"minimize-control-center", "window-switcher"]','"minimize-control-center", "window-switcher", "game-shell", "keep-game-shell"]')
    edit(p,'    public void ShowOverlay()','''    public void ShowGameShell(bool activate)
    {
        SetTopmost(false);
        ShowWindow(_window, 4);
        EnsureFullscreenBounds();
        SetWindowPos(_window, -2, 0, 0, 0, 0, 0x253);
        if (activate) BringForward(_window);
        else StayBehindForeground();
    }

    public static bool HasForegroundApp(nint self)
    {
        nint fg = Foreground;
        if (fg == self) return true;
        if (fg == 0 || !IsWindow(fg) || !IsWindowVisible(fg) || ToolboxWindow.IsIconic(fg)) return false;
        var cls = new System.Text.StringBuilder(128);
        GetClassName(fg, cls, cls.Capacity);
        return cls.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd");
    }

    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder text, int capacity);

    public void ShowOverlay()''')
    p=LIB/'App.cs'
    edit(p,'        return ("activate", 0);','        if (data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch && launch.Arguments == "--game-shell") return ("game-shell", 0);\n        return ("activate", 0);')
    edit(p,'        _shortcutWindow?.EnsureFullscreenBounds();\n\n        try','        _shortcutWindow?.EnsureFullscreenBounds();\n        InitializeGameShell();\n\n        try')
    edit(p,'        AppWindow.Closing += (_, e) =>\n        {','        AppWindow.Closing += (_, e) =>\n        {\n            if (Stage.ShellMode.GameMode && !_desktopHandoff) { e.Cancel = true; RestoreGameShell(true); return; }')
    edit(p,'    public void ExitLibrary() { _exiting = true; Close(); }','    public void ExitLibrary() { if (Stage.ShellMode.GameMode) { RestoreGameShell(true); return; } _exiting = true; Close(); }')
    p=LIB/'MainWindow.Overlay.cs'
    edit(p,'            string targetRoute =','            if (route == "game-shell") { RestoreGameShell(true); return; }\n            if (route == "keep-game-shell") { if (Stage.ShellMode.GameMode) RestoreGameShell(false); return; }\n            string targetRoute =')
    p=LIB/'MainWindow.Shortcut.cs'
    edit(p,'    private void HideShortcutWindow()\n    {','    private void HideShortcutWindow()\n    {\n        if (Stage.ShellMode.GameMode && !_desktopHandoff) { CancelShortcutTransition(); CompleteShortcutHide(); return; }')
    edit(p,'        _surfaceSession = false;\n        _shortcutMotion.Cancel();','        if (Stage.ShellMode.GameMode && !_desktopHandoff) { ParkGameShell(returnToPrevious); return; }\n        _surfaceSession = false;\n        _shortcutMotion.Cancel();')
    edit(p,'        ReleaseOverlay();\n        _shortcutClosed = true;','        _gameShellTimer?.Stop();\n        ReleaseOverlay();\n        _shortcutClosed = true;')
    p=LIB/'Package.appxmanifest';edit(p,'Version="0.1.199.0"','Version="0.1.200.0"')
if __name__=='__main__':main()
