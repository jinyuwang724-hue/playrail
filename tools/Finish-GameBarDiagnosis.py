"""Keep the useful visibility fix; remove the unsuccessful navigation experiment."""
from pathlib import Path
from importlib.util import spec_from_file_location,module_from_spec
import ast
s=spec_from_file_location('edit',Path(__file__).with_name('Edit-SpecialKeys.py'));m=module_from_spec(s);s.loader.exec_module(m)
SRC=m.SRC;edit=m.edit;LIB=m.LIB
p=SRC/'plugin/claw_gamebar_navigation.py';tree=ast.parse(p.read_text(encoding='utf-8'))
active=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='active')
source='"""Game Bar uses delegated input; foreground HWND alone cannot detect it."""\nimport ctypes,json,os\nfrom ctypes import wintypes\nfrom pathlib import Path\n\n'+ast.unparse(active)+'\n'
(SRC/'plugin/claw_gamebar_visibility.py').write_text(source,encoding='utf-8');p.unlink()
p=SRC/'plugin/claw_service.py'
edit(p,"'claw_gamebar_navigation', PLUGIN_ROOT / 'claw_gamebar_navigation.py'","'claw_gamebar_visibility', PLUGIN_ROOT / 'claw_gamebar_visibility.py'")
t=p.read_text(encoding='utf-8').replace('_gamebar_navigation','_gamebar_visibility')
a=t.index('            self._input.gamebar_bypass = True');b=t.index('            seen = False',a)
t=t[:a]+t[b:]
a=t.index('                    if not seen:\n',t.index('    async def _hold_gamebar_input'))
b=t.index('                elif time.monotonic()',a)
t=t[:a]+'''                    seen = True
                    deadline = time.monotonic() + .6
'''+t[b:]
t=t.replace('                await asyncio.sleep(.016)','                await asyncio.sleep(.1)').replace('            logging.info("Game Bar navigation: samples=%s presses=%s", navigation.samples, navigation.presses)\n','')
t=t.replace('            self._input.gamebar_bypass = False\n            if not self._stopping: await self._input.reconcile("gamebar-closed")\n','')
p.write_text(t,encoding='utf-8')
p=SRC/'plugin/claw_input_layer.py'
edit(p,'        self.gamebar_bypass = False\n','')
edit(p,'        if self.gamebar_bypass and not shell_game_mode(): return False\n','''        # A bounded diagnostic survives the required Windows restart. Game
        # mode always retains isolation; only the desktop comparison is direct.
        try:
            diagnostic = json.loads((self._data / "hidhide-diagnostic.json").read_text(encoding="utf-8"))
            if time.time() < float(diagnostic.get("until", 0)) and not shell_game_mode(): return False
        except (OSError, ValueError, TypeError): pass
''')
p=LIB/'Package.appxmanifest';edit(p,'Version="0.1.198.0"','Version="0.1.199.0"')
