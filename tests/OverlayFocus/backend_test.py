import asyncio
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'artifacts/overlay-focus-fix-191/src/plugin'
spec = importlib.util.spec_from_file_location('overlay_agent_test', SRC / 'claw_agent.py')
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)

class Service:
    def __init__(self, emit): self.holds = []; self.fail = False
    async def start(self): pass
    async def set_overlay_hold(self, on):
        self.holds.append(on)
        if self.fail and on: raise RuntimeError('engine failed')
        return {'held': on, 'safe': on}
    async def close(self): pass

class Tests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.a = agent.Agent(Service, endpoint=Path(self.temp.name) / 'agent.json')
        await self.a.start()
    async def asyncTearDown(self):
        await self.a.close(); self.temp.cleanup()
    async def connect(self):
        record = json.loads(self.a.endpoint.read_text())
        reader, writer = await asyncio.open_connection('127.0.0.1', record['port'])
        writer.write((json.dumps({'hello':record['token'],'client':'overlay-test'})+'\n').encode()); await writer.drain()
        await reader.readline()
        return reader, writer
    async def call(self, client, on):
        reader, writer = client
        writer.write((json.dumps({'id':1,'method':'set_overlay_hold','args':[on]})+'\n').encode()); await writer.drain()
        return json.loads(await reader.readline())
    async def test_disconnect_releases(self):
        c = await self.connect(); self.assertTrue((await self.call(c, True))['result']['safe'])
        c[1].close(); await c[1].wait_closed(); await asyncio.sleep(.03)
        self.assertEqual(self.a.service.holds, [True, False]); self.assertFalse(self.a.overlay_holds)
    async def test_older_close_cannot_release_new_owner(self):
        a, b = await self.connect(), await self.connect()
        await self.call(a, True); await self.call(b, True)
        a[1].close(); await a[1].wait_closed(); await asyncio.sleep(.03)
        self.assertNotIn(False, self.a.service.holds)
        await self.call(b, False); self.assertFalse(self.a.service.holds[-1]); b[1].close()
    async def test_expired_lease_restores_input(self):
        c = await self.connect(); await self.call(c, True)
        self.a.overlay_holds = {owner: 0 for owner in self.a.overlay_holds}
        await asyncio.sleep(1.05)
        self.assertFalse(self.a.overlay_holds); self.assertFalse(self.a.service.holds[-1]); c[1].close()
    async def test_invalid_and_failed_holds_do_not_own_lease(self):
        c = await self.connect()
        self.assertIn('error', await self.call(c, 'yes'))
        self.assertFalse(self.a.overlay_holds)
        self.a.service.fail = True
        self.assertIn('error', await self.call(c, True)); self.assertFalse(self.a.overlay_holds)
        c[1].close()
    async def test_refresh_renews_lease(self):
        c = await self.connect(); await self.call(c, True)
        owner = next(iter(self.a.overlay_holds)); self.a.overlay_holds[owner] = 1
        await self.call(c, True); self.assertGreater(self.a.overlay_holds[owner], 1)
        c[1].close()

if __name__ == '__main__': unittest.main()
