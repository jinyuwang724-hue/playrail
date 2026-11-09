import asyncio
import base64
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('compatibility', ROOT / 'plugin/claw_compatibility.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Repairs(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.handovers = []
        async def maintenance(begin, token):
            self.handovers.append(begin)
            return 'a' * 32
        self.manager = module.Compatibility(ROOT / 'plugin', self.directory, maintenance)
        self.manager.config = self.directory / 'config.json'
        self.manager.settings = self.directory / 'settings.json'
        self.manager.helper = self.directory / 'XboxGamingBarHelper.exe'
        self.original = (ROOT / 'artifacts/repair-20261008-1655/XboxGamingBarHelper.exe').read_bytes()
        module.atomic(self.manager.helper, self.original)
        module.atomic(self.manager.settings, {'OtherSetting': 42})
        module.atomic(self.manager.config, {'gaming': {'customStartupApps': [{'name': 'RTSS', 'enabled': True}]}, 'other': 123})
        self.registry = {}
        def registry(path, name, value=...):
            key = (path, name)
            if value is ...:
                return self.registry.get(key)
            self.registry[key] = value
        self.manager.registry = registry
        self.inspection = {'steamPath': r'C:\Software\steam.exe', 'steamProcesses': [], 'home': False,
                           'shortcuts': {'user': {'present': True, 'matches': True, 'admin': False}}}
        self.calls = []
        async def utility(operation, **values):
            self.calls.append((operation, values))
            if operation == 'home':
                self.inspection['home'] = values['value']
            elif operation == 'shortcuts':
                for slot, value in values['values'].items():
                    self.inspection['shortcuts'][slot]['admin'] = value
            return self.inspection
        self.manager.utility = utility

    async def test_patch_roundtrip_and_wrong_version_preserves_file(self):
        patched = module.patch_bytes(self.original, self.manager.patch, True)
        self.assertEqual(module.patch_bytes(patched, self.manager.patch, False), self.original)
        with self.assertRaises(ValueError):
            module.patch_bytes(b'new vendor version', self.manager.patch, True)
        module.atomic(self.manager.helper, b'new vendor version')
        await self.manager.check('set', 'gamebar', True)
        self.assertEqual(self.manager.helper.read_bytes(), b'new vendor version')
        self.assertEqual(self.handovers, [])

    async def test_gamebar_reversible_without_losing_other_settings(self):
        result = await self.manager.check('set', 'gamebar', True)
        self.assertEqual(result['items']['gamebar'], '已启用，Xbox 键打开 Game Bar 主页')
        self.assertEqual(self.handovers, [True, False])
        self.assertEqual(module.digest(self.manager.helper.read_bytes()), self.manager.patch['patchedHash'])
        await self.manager.check()
        self.assertEqual(self.handovers, [True, False], 'healthy checks must not restart the controller helper')
        settings = json.loads(self.manager.settings.read_text())
        settings['OtherSetting'] = 99
        module.atomic(self.manager.settings, settings)
        await self.manager.check('set', 'gamebar', False)
        self.assertEqual(self.manager.helper.read_bytes(), self.original)
        self.assertEqual(json.loads(self.manager.settings.read_text()), {'OtherSetting': 99})
        self.assertFalse(self.inspection['home'])

    async def test_steam_cleanup_and_restore_preserve_later_user_change(self):
        self.registry[(self.manager.RUN, 'Steam')] = '"C:\\Software\\steam.exe" -silent'
        self.registry[(self.manager.LAYERS, self.inspection['steamPath'])] = '~ RUNASADMIN HIGHDPIAWARE'
        await self.manager.check('set', 'steam', True)
        cfg = json.loads(self.manager.config.read_text())
        self.assertEqual(cfg['gaming']['customStartupApps'][0]['processName'], 'steam')
        self.assertTrue(cfg['gaming']['customStartupApps'][0]['enabled'])
        self.assertIn('path', cfg['gaming']['customStartupApps'][0])
        self.assertIsNone(self.registry[(self.manager.RUN, 'Steam')])
        self.assertEqual(self.registry[(self.manager.LAYERS, self.inspection['steamPath'])], '~ HIGHDPIAWARE')
        self.registry[(self.manager.RUN, 'Steam')] = 'user changed startup'
        cfg['other'] = 456
        cfg['gaming']['customStartupApps'].append({'name': 'User application'})
        module.atomic(self.manager.config, cfg)
        await self.manager.check('set', 'steam', False)
        cfg = json.loads(self.manager.config.read_text())
        self.assertEqual(cfg['other'], 456)
        self.assertEqual([a['name'] for a in cfg['gaming']['customStartupApps']], ['RTSS', 'User application'])
        self.assertEqual(self.registry[(self.manager.RUN, 'Steam')], 'user changed startup')
        self.assertFalse(self.inspection['shortcuts']['user']['admin'])

    async def test_failed_write_retains_recovery_before_restarting(self):
        async def fail(value):
            raise OSError('locked')
        with self.assertRaises(OSError):
            await self.manager.owned('steam.test', 'old', 'new', fail, True)
        disk = json.loads(self.manager.path.read_text(encoding='utf-8'))
        self.assertEqual(disk['records']['steam.test'], {'before': 'old', 'after': 'new'})

    async def test_gamebar_failure_always_returns_ownership(self):
        real = self.manager.utility
        async def fail(operation, **values):
            if operation == 'home':
                raise OSError('settings busy')
            return await real(operation, **values)
        self.manager.utility = fail
        await self.manager.check('set', 'gamebar', True)
        self.assertEqual(self.handovers, [True, False])
        self.assertIn('gamebar.home', self.manager.state['records'])
        self.manager.utility = real
        await self.manager.check()
        self.assertTrue(self.inspection['home'])

    async def test_only_original_bad_appid_is_removed_and_restored(self):
        file = self.directory / 'steam_appid.txt'
        module.atomic(file, b'11857803\n\0')
        self.manager.state['appidFiles'] = [{'path': str(file), 'bytes': base64.b64encode(b'11857803\n\0').decode()}]
        await self.manager.check('set', 'steam', True)
        self.assertFalse(file.exists())
        await self.manager.check('set', 'steam', False)
        self.assertEqual(file.read_bytes(), b'11857803\n\0')
        module.atomic(file, b'123456')
        await self.manager.check('set', 'steam', True)
        self.assertEqual(file.read_bytes(), b'123456')

    async def test_healthy_adopted_gamebar_never_stops_helper(self):
        module.atomic(self.manager.helper, module.patch_bytes(self.original, self.manager.patch, True))
        module.atomic(self.manager.settings, {'GameBarFirstSlot_PreserveHome': True})
        self.inspection['home'] = True
        self.manager.state['enabled']['gamebar'] = True
        self.manager.record('gamebar.helper', self.manager.patch['originalHash'], self.manager.patch['patchedHash'])
        self.manager.record('gamebar.flag', None, True)
        self.manager.record('gamebar.home', False, True)
        await self.manager.check()
        self.assertEqual(self.handovers, [])


if __name__ == '__main__':
    unittest.main()
