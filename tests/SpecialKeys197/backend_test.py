"""Exercise the production routing method with asynchronous action spies."""
import ast,asyncio,logging,json,tempfile,time
from pathlib import Path
from types import SimpleNamespace
import unittest
ROOT=Path(__file__).resolve().parents[2]
tree=ast.parse((ROOT/'artifacts/special-keys-197/src/plugin/claw_service.py').read_text(encoding='utf-8-sig'))
method=next(n for c in tree.body if isinstance(c,ast.ClassDef) for n in c.body if isinstance(n,ast.AsyncFunctionDef) and n.name=='_execute_mapping')
namespace={'asyncio':asyncio,'logging':logging}
namespace['_buttons_module']=buttons=SimpleNamespace(session_game_mode=lambda:False)
namespace['_input_module']=SimpleNamespace(validate_command=lambda c:c)
exec(compile(ast.Module(body=[method],type_ignores=[]),'<production-method>','exec'),namespace)
class Service:
    _execute_mapping=namespace['_execute_mapping']
    def __init__(self): self._mapping_lock=asyncio.Lock();self._special_mapping_lock=asyncio.Lock();self.actions=[]
    async def _execute_system_mapping(self,action,event): self.actions.append(action);await asyncio.sleep(0);return action
    async def _emit(self,*a): pass
class Tests(unittest.IsolatedAsyncioTestCase):
    async def test_mode_and_gesture_win_over_saved_mapping(self):
        for game in (True,False):
            buttons.session_game_mode=lambda:game
            for long in (True,False):
                s=Service();r=await s._execute_mapping({'source':'QS','gesture':'long' if long else 'short','command':{'type':'system','action':'steamQuickAccess'}})
                self.assertTrue(r['ok']);self.assertEqual(s.actions,['windowSwitcher' if game and long else 'controlCenter' if game else 'gamebar'])
    async def test_stale_mode_does_not_toggle_wrong_surface(self):
        buttons.session_game_mode=lambda:False
        s=Service();r=await s._execute_mapping({'source':'QS','gameMode':True,'command':None})
        self.assertEqual(r['ignored'],'mode-changed');self.assertFalse(s.actions)
    async def test_rapid_shortcuts_are_serial_and_not_dropped(self):
        buttons.session_game_mode=lambda:True;s=Service()
        await asyncio.gather(*(s._execute_mapping({'source':'QS','command':{'type':'system','action':'gamebar'}}) for _ in range(20)))
        self.assertEqual(s.actions,['controlCenter']*20)
    async def test_claw_mapping_remains_independent(self):
        s=Service();await s._execute_mapping({'source':'Claw','command':{'type':'system','action':'library'}})
        self.assertEqual(s.actions,['library'])

class DiagnosticTests(unittest.TestCase):
    def test_desktop_diagnostic_never_disables_game_isolation(self):
        source=ast.parse((ROOT/'artifacts/special-keys-197/src/plugin/claw_input_layer.py').read_text(encoding='utf-8-sig'))
        wanted=next(n for c in source.body if isinstance(c,ast.ClassDef) for n in c.body if isinstance(n,ast.FunctionDef) and n.name=='wanted')
        mode=[False];ns={'json':json,'time':time,'shell_game_mode':lambda:mode[0],'layouts_bind_buttons':lambda _:True}
        exec(compile(ast.Module(body=[wanted],type_ignores=[]),'<production-wanted>','exec'),ns)
        with tempfile.TemporaryDirectory() as temp:
            data=Path(temp);(data/'hidhide-diagnostic.json').write_text(json.dumps({'until':time.time()+60}))
            layer=SimpleNamespace(_data=data,_pad_mode='xinput',_override=None,desktop=False,layouts=SimpleNamespace(all=lambda:[]))
            self.assertFalse(ns['wanted'](layer));mode[0]=True;self.assertTrue(ns['wanted'](layer))
            mode[0]=False;(data/'hidhide-diagnostic.json').write_text(json.dumps({'until':0}));self.assertTrue(ns['wanted'](layer))
if __name__=='__main__': unittest.main()
