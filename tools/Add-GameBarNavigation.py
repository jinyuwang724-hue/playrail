from pathlib import Path
from importlib.util import spec_from_file_location,module_from_spec
import shutil
s=spec_from_file_location('edit',Path(__file__).with_name('Edit-SpecialKeys.py'));m=module_from_spec(s);s.loader.exec_module(m)
edit=m.edit;SRC=m.SRC;ROOT=m.ROOT
shutil.copyfile(ROOT/'tests/SpecialKeys197/gamebar_navigation.py',SRC/'plugin/claw_gamebar_navigation.py')
p=SRC/'plugin/claw_service.py'
edit(p,'PLUGIN_ROOT = Path(__file__).resolve().parent','''PLUGIN_ROOT = Path(__file__).resolve().parent
_gb_spec = importlib.util.spec_from_file_location('claw_gamebar_navigation', PLUGIN_ROOT / 'claw_gamebar_navigation.py')
_gamebar_navigation = importlib.util.module_from_spec(_gb_spec)
_gb_spec.loader.exec_module(_gamebar_navigation)''')
edit(p,'            seen = False\n            deadline = time.monotonic() + 3','''            self._input.gamebar_bypass = True
            await self._input.reconcile("gamebar-controller")
            navigation = _gamebar_navigation.Navigation()
            seen = False
            deadline = time.monotonic() + 3''')
edit(p,'                if foreground in surfaces:\n                    seen = True\n                    deadline = time.monotonic() + .6','''                showing = _gamebar_navigation.active() or foreground in surfaces
                if showing:
                    if not seen:
                        await self._power._call("mapped_input", mapping=_input_module.compile_command({"type":"keyboard", "keys":["Tab"]}))
                    seen = True
                    deadline = time.monotonic() + .25
                    for keys in navigation.read():
                        if not _gamebar_navigation.active() and foreground not in surfaces: break
                        await self._power._call("mapped_input", mapping=_input_module.compile_command({"type":"keyboard", "keys":keys}))''')
edit(p,'                await asyncio.sleep(.1)\n        except Exception:\n            logging.exception("Game Bar controller handoff failed")','''                if _buttons_module.session_game_mode(): break
                await asyncio.sleep(.016)
            logging.info("Game Bar navigation: samples=%s presses=%s", navigation.samples, navigation.presses)
        except Exception:
            logging.exception("Game Bar controller handoff failed")''')
edit(p,'        finally:\n            if desktop and revision == self._pad_choice_revision', '        finally:\n            self._input.gamebar_bypass = False\n            if not self._stopping: await self._input.reconcile("gamebar-closed")\n            if desktop and revision == self._pad_choice_revision')
p=SRC/'plugin/claw_input_layer.py'
edit(p,'        self.desktop = False\n        self._desktop_layout','        self.desktop = False\n        self.gamebar_bypass = False\n        self._desktop_layout')
edit(p,'    def wanted(self):\n','    def wanted(self):\n        if self.gamebar_bypass and not shell_game_mode(): return False\n')
