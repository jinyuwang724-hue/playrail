import ast
import hashlib
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/startup-shield/backend'
OUT.mkdir(parents=True, exist_ok=True)
HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox/app'

def method(text, name):
    node = next(n for n in ast.walk(ast.parse(text)) if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)) and n.name == name)
    return ''.join(text.splitlines(True)[node.lineno - 1:node.end_lineno])

changes = {}
for name in ('claw_service.py', 'claw_sleep_animation.py'):
    before = (HOME / name).read_text(encoding='utf-8-sig')
    source = (ROOT / 'plugin' / name).read_text(encoding='utf-8-sig')
    if name == 'claw_service.py':
        old = method(before, 'power_action')
        after = before.replace(old, method(source, 'power_action'), 1)
    else:
        assert 'async def power_action(' not in before
        after = before + '\n\n' + method(source, 'power_action') + '\n'
    compile(after, name, 'exec')
    (OUT / name).write_text(after, encoding='utf-8')
    changes[name] = hashlib.sha256((HOME / name).read_bytes()).hexdigest()
(OUT / 'expected.json').write_text(json.dumps(changes, indent=2), encoding='utf-8')

helper = '''    private async Task CcRequestAnimatedPower(string action)
    {
        try { _agent ??= new(); await _agent.Call("power_action", System.Text.Json.Nodes.JsonValue.Create(action)); }
        catch (Exception ex) { Log.Error("animated power action", ex); Toast("电源操作未完成：" + ex.Message); }
    }

'''
for folder in (ROOT / 'frontend/library-native', ROOT / 'artifacts/startup-shield/src/frontend/library-native'):
    file = folder / 'ClawLibrary/Stage/WallStage.CcChrome.cs'
    text = file.read_text(encoding='utf-8-sig')
    assert 'CcRequestAnimatedPower' not in text
    text = text.replace('    private void CcRunPower(string id)', helper + '    private void CcRunPower(string id)', 1)
    text = text.replace('        Toast($"正在{item.Label}…");', '        if (id is not ("restart" or "shutdown")) Toast($"正在{item.Label}…");', 1)
    text = text.replace('case "restart": SystemInfo.Restart(); break;', 'case "restart": _ = CcRequestAnimatedPower("restart"); break;', 1)
    text = text.replace('case "shutdown": SystemInfo.Shutdown(); break;', 'case "shutdown": _ = CcRequestAnimatedPower("shutdown"); break;', 1)
    file.write_text(text, encoding='utf-8')
print('Prepared power routing over installed backend modules')
