"""Exercise the staged, deployed-version game-limit lifecycle without touching hardware."""
import ast
import asyncio
import json
import os
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'artifacts/driver-frame-limit/backend/claw_power.py'


class Control:
    def __init__(self, data):
        self.game_fps_path = data / 'driver-game-fps-session.json'
        self.game_fps_session = None
        self.game_fps_recovered = False
        self.game_fps_error = ''
        self.fg_game = (1, 'test.exe')
        self.override = {'fpsLimit': 40}
        self.writes = []

    def game_override(self): return self.override

    async def _call(self, command, **args):
        if command == 'driver_game_read': return {'enabled': False, 'value': 60, 'fps': 0}
        # Both apply and recovery must see a durable journal before any write.
        record = json.loads(self.game_fps_path.read_text())
        self.writes.append((command, args, record))
        return {}


tree = ast.parse(SOURCE.read_text(encoding='utf-8-sig'))
power = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == 'PowerControl')
for name in ('_restore_game_fps', '_game_fps_tick', '_write_game_fps_session'):
    node = next(node for node in power.body if getattr(node, 'name', None) == name)
    scope = {'json': json, 'os': os}
    exec(compile(ast.Module(body=[node], type_ignores=[]), str(SOURCE), 'exec'), scope)
    setattr(Control, name, scope[name])


class LifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.control = Control(Path(self.temp.name))

    async def asyncTearDown(self): self.temp.cleanup()

    async def test_game_override_is_journalled_and_not_replayed(self):
        c = self.control
        await c._game_fps_tick()
        await c._game_fps_tick()
        self.assertEqual([entry[0] for entry in c.writes], ['driver_game_set'])
        self.assertEqual(c.writes[0][2]['previousValue'], 60)
        self.assertFalse(c.writes[0][2]['previousEnabled'])
        self.assertTrue(c.game_fps_session['applied'])

    async def test_leaving_game_restores_and_removes_journal(self):
        c = self.control
        await c._game_fps_tick()
        c.fg_game = None
        await c._game_fps_tick()
        self.assertEqual(c.writes[-1][0], 'driver_game_restore')
        self.assertIsNone(c.game_fps_session)
        self.assertFalse(c.game_fps_path.exists())

    async def test_restart_recovers_original_driver_state_first(self):
        c = self.control
        await c._game_fps_tick()
        recovered = Control(c.game_fps_path.parent)
        recovered.fg_game = None
        await recovered._game_fps_tick()
        self.assertEqual(recovered.writes[0][0], 'driver_game_restore')
        self.assertFalse(recovered.game_fps_path.exists())

    async def test_read_failure_never_changes_limit(self):
        c = self.control
        async def fail(command, **args): raise RuntimeError('Driver unavailable')
        c._call = fail
        await c._game_fps_tick()
        self.assertEqual(c.game_fps_error, 'Driver unavailable')
        self.assertFalse(c.game_fps_path.exists())


if __name__ == '__main__': unittest.main()
