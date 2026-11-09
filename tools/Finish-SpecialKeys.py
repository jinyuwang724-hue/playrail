from pathlib import Path
from importlib.util import spec_from_file_location,module_from_spec
s=spec_from_file_location('edit',Path(__file__).with_name('Edit-SpecialKeys.py'));m=module_from_spec(s);s.loader.exec_module(m)
edit=m.edit;SRC=m.SRC;LIB=m.LIB

def main():
    p=LIB/'Stage/WallStage.Switcher.cs'
    edit(p,'WsReconcile();','WsPollTick(_wsPoll!, EventArgs.Empty);')
    p=LIB/'MainWindow.Shortcut.cs'
    edit(p,'_stage?.QuickPanelOpen == true) { _shortcutMotion.Cancel(); entrance = ShortcutEntrance.None; }','_stage?.SpecialSurfaceOpen == true) { _shortcutMotion.Cancel(); entrance = ShortcutEntrance.None; }')
    p=SRC/'plugin/claw_button_mappings.py'
    edit(p,"        return json.loads((Path(os.environ['LOCALAPPDATA'])/'ClawToolbox/shell/state.json').read_text(encoding='utf-8')).get('mode') == 'game'",'''        if json.loads((Path(os.environ['LOCALAPPDATA'])/'ClawToolbox/shell/state.json').read_text(encoding='utf-8')).get('mode') != 'game':
            return False
        user = ctypes.WinDLL('user32', use_last_error=True)
        user.GetShellWindow.restype = wintypes.HWND
        shell = user.GetShellWindow()
        if not shell: return True
        pid = wintypes.DWORD()
        user.GetWindowThreadProcessId(wintypes.HWND(shell), ctypes.byref(pid))
        import psutil
        return psutil.Process(pid.value).name().lower() != 'explorer.exe' ''')
    edit(p,'    except (OSError, ValueError, KeyError):\n        return False','    except Exception:\n        return False')
    p=SRC/'plugin/claw_service.py'
    edit(p,'        self._gamebar_input_task = None','        self._gamebar_input_task = None\n        self._session_prepare_task = None\n        self._session_epoch = 0\n        self._session_prepare_lock = asyncio.Lock()')
    edit(p,'''                    await self._emit("session_mode", {"game": event.get("game") is True})
                    if event.get("game"):
                        self._pad_choice_revision += 1
                        await self.set_gamepad_mode("xinput", False)
                    else:
                        await self.set_overlay_hold(False)
                    await self._input.reconcile("session-mode")''','''                    self._session_epoch += 1
                    epoch = self._session_epoch
                    self._session_prepare_task = asyncio.create_task(self._prepare_session(event.get("game") is True, epoch))''')
    edit(p,'    async def open_gamebar(self):','''    async def _prepare_session(self, game, epoch):
        try:
            async with self._session_prepare_lock:
                if self._stopping or epoch != self._session_epoch or game != _buttons_module.session_game_mode(): return
                await self._emit("session_mode", {"game": game})
                if game:
                    self._pad_choice_revision += 1
                    await self.set_gamepad_mode("xinput", False)
                else:
                    await self.set_overlay_hold(False)
                if epoch == self._session_epoch:
                    await self._input.reconcile("session-mode")
        except Exception:
            logging.exception("Session input preparation failed")

    async def open_gamebar(self):''')
    edit(p,'        await self._notifications.close()','        await self._notifications.close()\n        pending = [t for t in (self._gamebar_input_task, self._session_prepare_task) if t and not t.done()]\n        if pending: await asyncio.gather(*pending, return_exceptions=True)')
    p=SRC/'native/ClawShell/ShellGuard.cs'
    edit(p,'    public interface IShellSystem','    public interface IDesktopSurface { bool FocusDesktop(); }\n\n    public interface IShellSystem')
    edit(p,'if (_sys.ExplorerRunning() || _sys.StartExplorer()) { _sys.WriteState("desktop", "switched"); return SwitchResult.Ok; }','''if (_sys.ExplorerRunning() || _sys.StartExplorer()) {
                var desktop = _sys as IDesktopSurface;
                if (desktop != null && !desktop.FocusDesktop()) _sys.Log("desktop focus request did not settle");
                _sys.WriteState("desktop", "switched"); return SwitchResult.Ok;
            }''')
    p=SRC/'native/ClawShell/WindowsShellSystem.cs'
    edit(p,'class WindowsShellSystem : IShellSystem','class WindowsShellSystem : IShellSystem, IDesktopSurface')
    edit(p,'        // ---------- explorer ----------','''        public bool FocusDesktop()
        {
            object shell = null;
            try {
                Type type = Type.GetTypeFromProgID("Shell.Application");
                shell = Activator.CreateInstance(type);
                type.InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                IntPtr desktop = GetShellWindow();
                if (desktop != IntPtr.Zero) { DesktopSetForegroundWindow(desktop); return true; }
            } catch (Exception ex) { Log("focus desktop: " + ex.Message); }
            finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
            return false;
        }
        [DllImport("user32.dll", EntryPoint="SetForegroundWindow")] static extern bool DesktopSetForegroundWindow(IntPtr window);

        // ---------- explorer ----------''')

if __name__=='__main__':main()
