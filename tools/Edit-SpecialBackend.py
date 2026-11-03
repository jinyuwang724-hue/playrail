from pathlib import Path
from importlib.util import spec_from_file_location,module_from_spec

s=spec_from_file_location('edit',Path(__file__).with_name('Edit-SpecialKeys.py'));m=module_from_spec(s);s.loader.exec_module(m)
edit=m.edit;SRC=m.SRC

def main():
    p=SRC/'plugin/claw_button_mappings.py'
    edit(p,'import ctypes\n','import ctypes\nimport json\nimport os\nfrom pathlib import Path\n')
    edit(p,'"steamLibrary", "magpieToggle",','"steamLibrary", "windowSwitcher", "magpieToggle",')
    edit(p,'def selected(settings, source, steam):', '''def session_game_mode():
    try:
        return json.loads((Path(os.environ['LOCALAPPDATA'])/'ClawToolbox/shell/state.json').read_text(encoding='utf-8')).get('mode') == 'game'
    except (OSError, ValueError, KeyError):
        return False


def selected(settings, source, steam):''')
    edit(p,'"minimize-library", "minimize-control-center")','"minimize-library", "minimize-control-center", "window-switcher")')
    edit(p,'    target = "control-center" if action == "controlCenter" else "library"',
         '    if action == "windowSwitcher": return "window-switcher"\n    target = "control-center" if action == "controlCenter" else "library"')
    p=SRC/'plugin/claw_input_layer.py'
    edit(p,'"keyboard": None,','"keyboard": None, "windowSwitcher": None,')
    p=SRC/'plugin/claw_overlay.py'
    edit(p,'    async def close(self):','    async def close(self, timeout=2):')
    edit(p,'            await self._close()\n\n    async def _close(self):','            await self._close(timeout)\n\n    async def _close(self, timeout=2):')
    edit(p,'await asyncio.wait_for(process.wait(), 2)','await asyncio.wait_for(process.wait(), timeout)')
    p=SRC/'plugin/claw_service.py'
    edit(p,'        self._mapping_lock = asyncio.Lock()','        self._mapping_lock = asyncio.Lock()\n        self._special_mapping_lock = asyncio.Lock()\n        self._gamebar_input_task = None\n        self._pad_choice_revision = 0')
    edit(p,'        self._process = await start_router(\n            PLUGIN_ROOT / "bin" / ("ClawRouter-" + version + ".exe"), self._data, config)',
         '        router = PLUGIN_ROOT / "bin" / ("ClawRouter-" + version + "-specialkeys.exe")\n        self._process = await start_router(\n            router if router.is_file() else PLUGIN_ROOT / "bin" / ("ClawRouter-" + version + ".exe"), self._data, config)')
    edit(p,'        if self._stopping or len(self._mapping_tasks) >= 4:',
         '        if self._stopping or (event.get("source") != "QS" and len(self._mapping_tasks) >= 4):')
    edit(p,'        if action in ("library", "controlCenter"):',
         '        if action == "windowSwitcher" and not _buttons_module.session_game_mode():\n            return None\n        if action in ("library", "controlCenter", "windowSwitcher"):')
    edit(p,'            route = _buttons_module.toolbox_route(action, event or {}, mode, context)',
         '            if event and event.get("foregroundWindow"):\n                context["foregroundWindow"] = event["foregroundWindow"]\n            route = _buttons_module.toolbox_route(action, event or {}, mode, context)')
    edit(p,'        async with self._mapping_lock:\n            result =',
         '        async with (self._special_mapping_lock if event.get("source") == "QS" else self._mapping_lock):\n            result =')
    edit(p,'                command = _input_module.validate_command(event.get("command"))', '''                raw = event.get("command")
                if event.get("source") == "QS":
                    game = _buttons_module.session_game_mode()
                    if "gameMode" in event and event["gameMode"] != game:
                        return {"ok": True, "ignored": "mode-changed"}
                    long = event.get("gesture") == "long" or (isinstance(raw, dict) and raw.get("action") == "windowSwitcher")
                    raw = {"type": "system", "action": "windowSwitcher" if game and long else "controlCenter" if game else "gamebar"}
                command = _input_module.validate_command(raw)''')
    edit(p,'            if action in ("gamebar", "screenshot"):\n                await self._allow_gamebar()',
         '            if action in ("gamebar", "screenshot"):\n                if action == "gamebar": self._request_gamebar_input()\n                await self._allow_gamebar()')
    edit(p,'                kind = event.get("type")\n', '''                kind = event.get("type")
                if kind == "desktop_gamebar_input":
                    self._request_gamebar_input()
                    continue
                if kind == "session_mode":
                    await self._emit("session_mode", {"game": event.get("game") is True})
                    if event.get("game"):
                        self._pad_choice_revision += 1
                        await self.set_gamepad_mode("xinput", False)
                    else:
                        await self.set_overlay_hold(False)
                    await self._input.reconcile("session-mode")
                    continue
''')
    edit(p,'    async def set_overlay_hold(self, on):', '''    async def open_gamebar(self):
        if _buttons_module.session_game_mode():
            raise ValueError("游戏模式使用快捷栏")
        self._request_gamebar_input()
        await self._execute_system_mapping("gamebar")
        return {"ok": True}

    def _request_gamebar_input(self):
        if self._gamebar_input_task is None or self._gamebar_input_task.done():
            self._gamebar_input_task = asyncio.create_task(self._hold_gamebar_input())

    async def _hold_gamebar_input(self):
        revision = self._pad_choice_revision
        desktop = self._input.desktop
        try:
            state = await self.get_gamepad_state()
            desktop = desktop or state.get("mode") == "desktop"
            if desktop:
                await self.set_gamepad_mode("xinput", False)
            await self._input.refresh_apps()
            seen = False
            deadline = time.monotonic() + 3
            surfaces = {"gamebar.exe", "gamebarftserver.exe", "clawwidget.exe", "xboxgamebarwidgets.exe", "textinputhost.exe", "osk.exe", "tabtip.exe"}
            while not self._stopping and revision == self._pad_choice_revision:
                foreground = await asyncio.to_thread(_input_module.foreground_exe)
                if foreground in surfaces:
                    seen = True
                    deadline = time.monotonic() + .6
                elif time.monotonic() >= deadline:
                    break
                await asyncio.sleep(.1)
        except Exception:
            logging.exception("Game Bar controller handoff failed")
        finally:
            if desktop and revision == self._pad_choice_revision and not self._stopping and not _buttons_module.session_game_mode():
                await self.set_gamepad_mode("desktop", False)

    async def set_overlay_hold(self, on):''')
    edit(p,'                await self._power.overlay.close()','                await self._power.overlay.close(timeout=.15)')
    edit(p,'        # notify=False: a temporary switch (the game library in front), no mode notice.',
         '        if notify: self._pad_choice_revision += 1\n        # notify=False: a temporary switch (the game library in front), no mode notice.')
    p=SRC/'plugin/claw_agent.py'
    edit(p,'    "set_overlay_hold",','    "set_overlay_hold", "open_gamebar",')

if __name__=='__main__':main()
