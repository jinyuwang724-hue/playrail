import ast
import asyncio
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

root = Path(__file__).resolve().parents[1]
text = (root / 'plugin/claw_service.py').read_text(encoding='utf-8')
node = next(n for n in ast.walk(ast.parse(text)) if isinstance(n, ast.AsyncFunctionDef) and n.name == 'power_action')
method = '\n'.join(line[4:] for line in text.splitlines()[node.lineno - 1:node.end_lineno])

async def main():
    count = 0
    for action, available, enabled, handled in [('restart', True, True, True), ('shutdown', True, True, True),
            ('restart', False, True, True), ('shutdown', True, False, True), ('restart', True, True, False),
            ('sleep', True, True, True), ('lock', True, True, True)]:
        animation = SimpleNamespace(view=AsyncMock(return_value={'available': available, 'suspendEnabled': enabled}),
                                    power_action=AsyncMock(return_value={'handled': handled}))
        env = {'_system_module': SimpleNamespace(_animation=animation)}
        exec(method, env)
        power = SimpleNamespace(system_set=AsyncMock(return_value={'native': True}))
        await env['power_action'](SimpleNamespace(_power=power), action)
        animated = action in ('restart', 'shutdown') and available and enabled
        assert animation.power_action.await_count == int(animated)
        assert power.system_set.await_count == int(not animated or not handled)
        if animated: animation.power_action.assert_awaited_once_with(action)
        count += 1
    animation = SimpleNamespace(view=AsyncMock(return_value={'available': True, 'suspendEnabled': True}),
                                power_action=AsyncMock(side_effect=RuntimeError('reply lost')))
    env = {'_system_module': SimpleNamespace(_animation=animation)}
    exec(method, env)
    power = SimpleNamespace(system_set=AsyncMock())
    try: await env['power_action'](SimpleNamespace(_power=power), 'restart')
    except RuntimeError: pass
    else: raise AssertionError('Uncertain animation request must not silently issue a duplicate restart')
    power.system_set.assert_not_awaited(); count += 1
    for action in ('reboot /f', None, ''):
        try: await env['power_action'](SimpleNamespace(_power=power), action)
        except ValueError: pass
        else: raise AssertionError('Invalid power action accepted')
        count += 1
    preview_node = next(n for n in ast.walk(ast.parse(text)) if isinstance(n, ast.AsyncFunctionDef) and n.name == 'preview_power_animation')
    preview_method = '\n'.join(line[4:] for line in text.splitlines()[preview_node.lineno - 1:preview_node.end_lineno])
    animation = SimpleNamespace(power_action=AsyncMock(return_value={'handled': True, 'preview': True}))
    env = {'_system_module': SimpleNamespace(_animation=animation)}
    exec(preview_method, env)
    power = SimpleNamespace(system_set=AsyncMock())
    service = SimpleNamespace(_power=power)
    result = await env['preview_power_animation'](service)
    assert result['preview'] is True
    animation.power_action.assert_awaited_once_with('restart', preview=True)
    power.system_set.assert_not_awaited(); count += 1
    try: await env['preview_power_animation'](service, 'shutdown')
    except TypeError: pass
    else: raise AssertionError('Preview endpoint must not accept a destructive action argument')
    count += 1
    animation.power_action.side_effect = RuntimeError('unavailable')
    try: await env['preview_power_animation'](service)
    except RuntimeError: pass
    else: raise AssertionError('Preview failure must propagate')
    power.system_set.assert_not_awaited(); count += 1
    print(f'PASS: {count} animated-power routing, preview safety and no-duplicate checks; no OS power requests executed')

asyncio.run(main())
