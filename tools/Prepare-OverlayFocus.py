"""Continue the installed 0.1.190 quick-panel baseline without replacing other work."""
from pathlib import Path
import difflib
import hashlib
import json
import os
import shutil

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/overlay-focus-fix-191'
SRC = OUT / 'src'
LIB = SRC / 'frontend/library-native/ClawLibrary'
BASE = ROOT / '.claude/worktrees/bold-jennings-a19012/artifacts/qp/src/frontend/library-native/ClawLibrary'
APP = OUT / 'base/plugin'

def edit(path, old, new):
    text = path.read_text(encoding='utf-8-sig')
    if text.count(old) != 1:
        raise RuntimeError(f'Ambiguous anchor in {path}: {old[:70]}')
    path.write_text(text.replace(old, new, 1), encoding='utf-8')

def main():
    if not APP.is_dir():
        raise RuntimeError('Capture the installed agent baseline in '+str(APP)+' before preparing.')
    # Restore just source files from the reviewed baselines. Build caches stay local.
    shutil.copytree(BASE.parent, LIB.parent, dirs_exist_ok=True,
                    ignore=lambda folder, names: [n for n in names if
                        (Path(folder) == BASE.parent and n in ('b','o','AppPackages')) or
                        (Path(folder) == BASE and n in ('bin','obj'))])
    shutil.copytree(APP, SRC / 'plugin', dirs_exist_ok=True)
    shutil.copytree(ROOT / 'artifacts/enhanced-sleep/final/src/native/InputLayer', SRC / 'native/InputLayer', dirs_exist_ok=True)
    # Native input always uses a new filename; running helpers are never overwritten.
    inp = SRC / 'plugin/claw_input_layer.py'
    edit(inp, '        enhanced = self._root / "bin"',
         '        overlay = self._root / "bin" / ("ClawInput-" + version + "-overlay-r2.exe")\n        if overlay.is_file():\n            return overlay\n        enhanced = self._root / "bin"')
    start = inp.read_text(encoding='utf-8').index('    async def set_hold(')
    end = inp.read_text(encoding='utf-8').index('    def can_translate(', start)
    text = inp.read_text(encoding='utf-8')
    text = text[:start] + '''    async def set_hold(self, value):
        """All virtual output is held, regardless of foreground changes."""
        value = bool(value)
        if value and self._phase != "active":
            await self.reconcile("overlay")
        async with self._lock:
            if value and (self._phase != "active" or self._process is None):
                raise RuntimeError("输入层未就绪，无法隔离游戏手柄")
            changed = bool(self._hold) != value
            self._hold = "overlay" if value else None
            if self._phase == "active" and self._process is not None:
                if changed:
                    await self._call("configure", **self._configure_args())
                for attempt in range(20):
                    status = (await self._call("status")).get("status", {})
                    if not value or status.get("overlayHeld"):
                        break
                    await asyncio.sleep(.01)
                if value and not (status.get("overlayHeld") and status.get("hidhide", {}).get("cloak")
                                  and status.get("hidhide", {}).get("hidden")
                                  and status.get("virtualPad", {}).get("plugged")
                                  and status.get("pad", {}).get("connected")):
                    raise RuntimeError("输入层未确认游戏手柄已归零")
        return {"held": value, "layer": self._phase, "safe": value and self._phase == "active"}

''' + text[end:]
    inp.write_text(text, encoding='utf-8')
    edit(inp, '"notGames": self._not_games + ([self._hold] if self._hold else []),',
         '"notGames": self._not_games,\n                "overlayHold": bool(self._hold),')

    service = SRC / 'plugin/claw_service.py'
    text = service.read_text(encoding='utf-8-sig')
    start, end = text.index('    async def set_overlay_hold('), text.index('    async def set_gamepad_mode(')
    text = text[:start] + '''    async def set_overlay_hold(self, on):
        """Lease owner is tracked by the agent connection, including EOF/expiry."""
        if type(on) is not bool:
            raise ValueError("叠加层状态格式错误")
        self._power.overlay.suppressed = on
        try:
            if on:
                await self._power.overlay.close()
            return await self._input.set_hold(on)
        except Exception:
            self._power.overlay.suppressed = False
            await self._input.set_hold(False)
            raise

''' + text[end:]
    service.write_text(text, encoding='utf-8')

    agent = SRC / 'plugin/claw_agent.py'
    edit(agent, '        self.service = service_factory(self.emit)',
         '        self.service = service_factory(self.emit)\n        self.overlay_holds = {}\n        self.overlay_lock = asyncio.Lock()\n        self.overlay_watch = None')
    edit(agent, '    async def start(self, port=0):\n', '''    async def overlay_hold(self, connection, on):
        if type(on) is not bool:
            raise ValueError("叠加层状态格式错误")
        async with self.overlay_lock:
            if on:
                if connection not in self.connections:
                    raise RuntimeError("界面已断开")
                try:
                    result = await self.service.set_overlay_hold(True)
                except Exception:
                    self.overlay_holds.pop(connection, None)
                    raise
                self.overlay_holds[connection] = time.monotonic() + 8
                return result
            self.overlay_holds.pop(connection, None)
            if not self.overlay_holds:
                return await self.service.set_overlay_hold(False)
            return {"held": True, "safe": True}

    async def expire_overlay_holds(self):
        while not self.stopped.is_set():
            await asyncio.sleep(1)
            async with self.overlay_lock:
                expired = [c for c, deadline in self.overlay_holds.items() if deadline <= time.monotonic()]
                for c in expired:
                    self.overlay_holds.pop(c, None)
                if expired and not self.overlay_holds:
                    await self.service.set_overlay_hold(False)

    async def start(self, port=0):
        self.overlay_watch = asyncio.create_task(self.expire_overlay_holds())
''')
    edit(agent, '                self.connections.discard(connection)',
         '                self.connections.discard(connection)\n                if connection in self.overlay_holds:\n                    await self.overlay_hold(connection, False)')
    edit(agent, '            elif method in METHODS:',
         '            elif method == "set_overlay_hold":\n                if len(args) != 1:\n                    raise ValueError("叠加层参数错误")\n                result = await self.overlay_hold(connection, args[0])\n            elif method in METHODS:')
    edit(agent, '        self.closed = True\n        self.stopped.set()',
         '        self.closed = True\n        self.stopped.set()\n        if self.overlay_watch:\n            self.overlay_watch.cancel()')

    native = SRC / 'native/InputLayer/Program.cs'
    shutil.copy2(ROOT / 'tests/OverlayFocus/OverlayInputGate.cs', native.parent / 'OverlayInputGate.cs')
    edit(native, '        bool highRes;', '        bool highRes;\n        volatile bool overlayHold, overlayHeld;\n        volatile int configRevision;')
    edit(native, '            layoutName = ""; // re-pick the layout on the next tick',
         '            overlayHold = c.ContainsKey("overlayHold") && Convert.ToBoolean(c["overlayHold"]);\n            Interlocked.Increment(ref configRevision);\n            layoutName = ""; // re-pick the layout on the next tick')
    edit(native, '            Layout active = null;\n',
         '            Layout active = null;\n            var overlayGate = new OverlayInputGate();\n            int seenConfig = -1;\n            bool blockedBefore = false;\n')
    edit(native, '                    if (now >= nextForeground) {',
         '                    if (now >= nextForeground || seenConfig != configRevision) {\n                        seenConfig = configRevision;')
    edit(native, '                    var report = active == null ? new PadReport() : translator.Update(frame, now, events);',
         '''                    bool held = overlayHold;
                    bool blocked = overlayGate.Block(held, frame);
                    if (blocked && !blockedBefore) translator.ReleaseAll(events);
                    blockedBefore = blocked;
                    var report = blocked || active == null ? new PadReport() : translator.Update(frame, now, events);''')
    edit(native, '                    long padDone = Stopwatch.GetTimestamp();',
         '                    overlayHeld = held && sentOnce && last.Same(new PadReport());\n                    long padDone = Stopwatch.GetTimestamp();')
    edit(native, '{ "foreground", foreground.Name }, { "layout", layoutName },',
         '{ "foreground", foreground.Name }, { "layout", layoutName }, { "overlayHeld", overlayHeld },')

    # UI: preserve existing page and panel rendering; change only window/input ownership.
    shutil.copy2(ROOT / 'tests/OverlayFocus/MainWindow.Overlay.cs', LIB / 'MainWindow.Overlay.cs')
    window = LIB / 'Data/LibraryWindow.cs'
    edit(window, '    private nint _returnWindow;', '    private nint _returnWindow;\n    private nint _overlayTarget;\n    private uint _overlayPid;\n    private Windows.Graphics.RectInt32 _overlayBounds;')
    edit(window, '        if (message == 0x112',
         '        if (_overlayTarget != 0 && message == 0x21) return 3; // WM_MOUSEACTIVATE / MA_NOACTIVATE\n        if (_overlayTarget != 0 && message == 0x0245) return 3; // WM_POINTERACTIVATE / PA_NOACTIVATE\n        if (message == 0x112')
    edit(window, '    public static nint Foreground =>', '''    public static bool CanOverlay(nint window)
    {
        if (window == 0 || !IsWindowVisible(window) || ToolboxWindow.IsIconic(window)) return false;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName is not ("explorer" or "ClawShell" or "steam" or "steamwebhelper" or "GameBar" or "ClawWidget");
        }
        catch { return false; }
    }

    public bool OverlayTargetIsForeground => _overlayTarget != 0 && Foreground == _overlayTarget
        && GetWindowThreadProcessId(_overlayTarget, out uint pid) != 0 && pid == _overlayPid;

    public void SetOverlayTarget(nint target)
    {
        _overlayTarget = target;
        GetWindowThreadProcessId(target, out _overlayPid);
        long style = GetWindowLongPtr(_window, -20).ToInt64();
        SetWindowLongPtr(_window, -20, (nint)(target != 0 ? style | 0x08000000L : style & ~0x08000000L));
    }

    public void ShowOverlay()
    {
        ShowWindow(_window, 4); // SW_SHOWNOACTIVATE (also restores from minimized)
        SetWindowPos(_window, -1, 0, 0, 0, 0, 0x53); // NOACTIVATE | SHOWWINDOW | NOMOVE | NOSIZE
    }

    public static nint Foreground =>''')
    edit(window, '    public void ShowOverlay()\n', '''    public void SetOverlayBounds(Windows.Graphics.RectInt32 bounds)
    {
        _overlayBounds = bounds;
        // OverlappedPresenter retains a nonclient edge even with its title bar
        // disabled. Remove it so client pixels still cover the full monitor.
        long style = GetWindowLongPtr(_window, -16).ToInt64();
        SetWindowLongPtr(_window, -16, (nint)(style & ~0x00C40000L));
        long extended = GetWindowLongPtr(_window, -20).ToInt64();
        SetWindowLongPtr(_window, -20, (nint)(extended & ~0x300L));
        SetWindowPos(_window, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x230);
    }

    public void ShowOverlay()
''')
    edit(window, '    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]',
         '    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);\n    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]')
    app = LIB / 'App.cs'
    edit(app, '    public void ActivateRoute(string route, nint returnWindow = 0)\n    {',
         '    public void ActivateRoute(string route, nint returnWindow = 0) => _ = ActivateOverlayRoute(route, returnWindow);\n\n    private void ActivateRouteCore(string route, nint returnWindow = 0)\n    {')
    edit(app, '    private bool _exiting;', '    private bool _exiting;\n    private int _routeRevision;')
    edit(app, '    private void ActivateRouteCore(string route, nint returnWindow = 0)\n    {',
         '    private void ActivateRouteCore(string route, nint returnWindow = 0)\n    {\n        ++_routeRevision;')
    edit(app, '        bool shown = false;\n        void Show()',
         '        int revision = _routeRevision;\n        bool shown = false;\n        void Show()')
    edit(app, '            ShowShortcutWindow(route);\n        }\n        void OnFrame',
         '            if (revision == _routeRevision && _stage?.QuickPanelOpen == true) ShowShortcutWindow(route);\n        }\n        void OnFrame')
    edit(app, '        if (AppWindow.IsVisible && !ToolboxWindow.IsIconic(hwnd) && route.StartsWith("minimize-", StringComparison.Ordinal) &&',
         '        if (_overlayActive && !quick && route.StartsWith("minimize-", StringComparison.Ordinal)) { HideShortcutWindow(); return; }\n        if (AppWindow.IsVisible && !ToolboxWindow.IsIconic(hwnd) && route.StartsWith("minimize-", StringComparison.Ordinal) &&')
    edit(app, '        ToolboxWindow.Restore(WinRT.Interop.WindowNative.GetWindowHandle(this));',
         '        if (!_overlayActive) ToolboxWindow.Restore(WinRT.Interop.WindowNative.GetWindowHandle(this));')
    edit(app, '        _root.Focus(FocusState.Programmatic);\n    }',
         '        if (!_overlayActive) _root.Focus(FocusState.Programmatic);\n    }')
    shortcut = LIB / 'MainWindow.Shortcut.cs'
    edit(shortcut, '        if (!initial) RestoreShortcutContent(route);',
         '        RestoreOrdinaryPresenter();\n        if (!initial) RestoreShortcutContent(route);')
    edit(shortcut, '            if (e.WindowActivationState == WindowActivationState.Deactivated)',
         '            if (_overlayActive) return;\n            if (e.WindowActivationState == WindowActivationState.Deactivated)')
    edit(shortcut, '    internal void HandOffForeground() => _shortcutWindow?.HandOffForeground();',
         '    internal void HandOffForeground() { ReleaseOverlay(); _shortcutWindow?.HandOffForeground(); }')
    edit(shortcut, '        TryShortcutFocus();\n        StartWhenForeground();',
         '''        if (_overlayActive)
        {
            _shortcutWindow?.ShowOverlay();
            if (!started) AnimateShortcut(true, entrance == ShortcutEntrance.Reveal, motionVersion, () => { });
            Log.Info($"shortcut shown without activation target={_overlayTarget}");
            return;
        }
        TryShortcutFocus();
        StartWhenForeground();''')
    edit(shortcut, '        if (_shortcutWindow?.IsForeground != true)\n', '        if (!_overlayActive && _shortcutWindow?.IsForeground != true)\n')
    edit(shortcut, '        _stage?.SuspendToTray();\n', '        _stage?.SuspendToTray();\n        ReleaseOverlay();\n')
    edit(shortcut, '        _shortcutClosed = true; _shortcutMotion.Cancel();', '        ReleaseOverlay();\n        _shortcutClosed = true; _shortcutMotion.Cancel();')
    stage = LIB / 'Stage/WallStage.cs'
    edit(stage, '&& GetForegroundWindow() == _windowHandle;',
         '&& (GetForegroundWindow() == _windowHandle || (_window as MainWindow)?.OverlayAcceptsGamepad == true);')
    quick = LIB / 'Stage/WallStage.QuickPanel.cs'
    edit(quick, '        EnsureQsVisuals();\n        bool again = _qsOpen;',
         '        ExitScreensaver(); MarkInput();\n        EnsureQsVisuals();\n        bool again = _qsOpen;')
    edit(LIB / 'Stage/WallStage.Screensaver.cs', 'private bool ScreensaverBlocked => _stack.Count == 0 || _ccOpen',
         'private bool ScreensaverBlocked => _stack.Count == 0 || _qsOpen || _ccOpen')
    edit(quick, '            _ = QsHold(true);', '            // The owning window acquires the input/OSD lease before showing.')
    edit(quick, '        _ = QsHold(false);', '        // Keep the lease through the complete close animation.')
    edit(quick, '        if (hide && _qsGame != 0) LibraryWindow.BringForward(_qsGame);',
         '        if (hide && _qsGame != 0 && (_window as MainWindow)?.OverlayAcceptsGamepad != true) LibraryWindow.BringForward(_qsGame);')
    # Text fields explicitly request keyboard focus; ordinary touch remains non-activating.
    edit(LIB / 'Stage/WallStage.Rail.cs', '        _searchBox.Focus(FocusState.Programmatic);',
         '        (_window as MainWindow)?.RequestTextInputFocus();\n        _searchBox.Focus(FocusState.Programmatic);')
    edit(LIB / 'Stage/WallStage.AddGame.cs', '            box.Focus(FocusState.Programmatic);',
         '            (_window as MainWindow)?.RequestTextInputFocus();\n            box.Focus(FocusState.Programmatic);')
    edit(LIB / 'Stage/WallStage.AddGame.cs', 'if (_stSecretBox.Visibility == Visibility.Visible) _stSecretBox.Focus(FocusState.Programmatic);',
         'if (_stSecretBox.Visibility == Visibility.Visible) { (_window as MainWindow)?.RequestTextInputFocus(); _stSecretBox.Focus(FocusState.Programmatic); }')
    edit(window, '        SetWindowPos(_window, -1, 0, 0, 0, 0, 0x53); // NOACTIVATE | SHOWWINDOW | NOMOVE | NOSIZE',
         '        bool shown = SetWindowPos(_window, -1, 0, 0, 0, 0, 0x253); // NOOWNERZORDER | NOACTIVATE | SHOWWINDOW | NOMOVE | NOSIZE\n        Log.Info($"overlay show topmost={shown} style={GetWindowLongPtr(_window, -20).ToInt64():X}");')
    edit(window, '        ShowWindow(_window, 4); // SW_SHOWNOACTIVATE (also restores from minimized)',
         '        ShowWindow(_window, 4); // SW_SHOWNOACTIVATE (also restores from minimized)\n        if (_overlayBounds.Width > 0) SetOverlayBounds(_overlayBounds);')
    window.write_text(window.read_text(encoding='utf-8').replace('0, 0, 0, 0, 0x13);', '0, 0, 0, 0, 0x213);'), encoding='utf-8')
    edit(LIB / 'Package.appxmanifest', 'Version="0.1.190.0"', 'Version="0.1.196.0"')
    snapshot()

def snapshot():
    changes, patch = {}, []
    digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest() if p.exists() else None
    for folder, baseline in [('frontend/library-native/ClawLibrary', BASE), ('plugin', APP), ('native/InputLayer', ROOT / 'artifacts/enhanced-sleep/final/src/native/InputLayer')]:
        for p in (SRC / folder).rglob('*'):
            if not p.is_file() or any(x in ('bin', 'obj', 'Payload') for x in p.relative_to(SRC / folder).parts):
                continue
            rel = p.relative_to(SRC / folder)
            old = baseline / rel
            if digest(p) == digest(old): continue
            name = p.relative_to(SRC).as_posix()
            changes[name] = {'baseSha256': digest(old), 'candidateSha256': digest(p)}
            if p.suffix in ('.cs', '.py', '.appxmanifest'):
                patch.extend(difflib.unified_diff(old.read_text(encoding='utf-8-sig').splitlines(True) if old.exists() else [],
                             p.read_text(encoding='utf-8-sig').splitlines(True), fromfile='a/'+name if old.exists() else '/dev/null', tofile='b/'+name))
    (ROOT / 'tests/OverlayFocus/overlay-focus.patch').write_text(''.join(patch), encoding='utf-8')
    binary = 'plugin/bin/ClawInput-0.17.58-overlay-r2.exe'
    if (SRC / binary).exists():
        changes[binary] = {'baseSha256':None, 'candidateSha256':digest(SRC / binary)}
    (OUT / 'integration.json').write_text(json.dumps({'baseVersion':'0.1.190.0','version':'0.1.196.0','sourceBaseline':str(BASE),'changes':changes}, indent=2), encoding='utf-8')
    print('Prepared', len(changes), 'changed files')

if __name__ == '__main__': main()
