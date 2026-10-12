import importlib.util
import asyncio
from pathlib import Path
import sys
import types
import unittest
import re
import time

spec = importlib.util.spec_from_file_location("claw_service", Path(__file__).parents[1] / "plugin" / "claw_service.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class SettingsTests(unittest.TestCase):
    def test_upgrade_keeps_bindings_and_enables_feedback_compatibility(self):
        old = {"enabled": True, "msiAction": "QuickAccess", "qamAction": "MainMenu"}
        settings = module.validate_settings(old)
        self.assertTrue(settings["hapticsEnabled"])
        self.assertEqual({key: settings[key] for key in old}, old)
        self.assertFalse(module.validate_settings({**old, "hapticsEnabled": False})["hapticsEnabled"])
        with self.assertRaises(ValueError):
            module.validate_settings({**old, "hapticsEnabled": 1})
    def test_keyboard_and_command_injection_are_not_actions(self):
        for action in ["Win+G", "powershell.exe", {"command": "shutdown"}, None]:
            with self.assertRaises(ValueError):
                module.validate_settings({"enabled": True, "msiAction": action, "qamAction": "QuickAccess"})

    def test_enable_requires_actual_boolean(self):
        for enabled in ["true", 1, None]:
            with self.assertRaises(ValueError):
                module.validate_settings({"enabled": enabled, "msiAction": "MainMenu", "qamAction": "QuickAccess"})

    def test_extra_fields_cannot_reconfigure_native_paths(self):
        settings = module.validate_settings({"enabled": True, "msiAction": "MainMenu", "qamAction": "None", "handoverDir": "other"})
        self.assertNotIn("handoverDir", settings)


class LifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_new_frontend_resets_steam_report_and_stale_unload_cannot_release_it(self):
        plugin = module.Service.__new__(module.Service)
        plugin._frontend_id, plugin._ready = 'old', True
        plugin._steam_context_revision = 50
        plugin._settings = {'hapticsEnabled': False}
        plugin._haptics = types.SimpleNamespace(stop=lambda: None)
        calls = []
        async def configure(reset_steam_context=False): calls.append(reset_steam_context)
        async def state(): return {'ready': plugin._ready}
        plugin._configure, plugin.get_state = configure, state
        await plugin.frontend_ready(True, 'new')
        self.assertEqual(plugin._steam_context_revision, -1)
        self.assertEqual(calls, [True])
        await plugin.frontend_ready(False, 'old')
        self.assertTrue(plugin._ready)
        self.assertEqual(calls, [True])

    async def read_stop_status(self, stopping):
        reader = asyncio.StreamReader()
        reader.feed_data(b'{"type":"status","configured":true,"enabled":false}\n')
        reader.feed_eof()
        plugin = module.Service.__new__(module.Service)
        plugin._process = types.SimpleNamespace(stdout=reader)
        plugin._settings = {"enabled": True}
        plugin._ready = True
        plugin._stopping = stopping
        plugin._save = lambda: None
        async def emit(*args):
            pass
        plugin._emit = emit
        await plugin._read_output()
        return plugin._settings["enabled"]

    async def test_reload_preserves_enabled_setting(self):
        self.assertTrue(await self.read_stop_status(stopping=True))

    async def test_failure_pauses_enabled_setting(self):
        self.assertFalse(await self.read_stop_status(stopping=False))


class ResultTests(unittest.IsolatedAsyncioTestCase):
    async def test_steam_context_ignores_old_frontend_and_out_of_order_reports(self):
        plugin = module.Service.__new__(module.Service)
        plugin._frontend_id, plugin._ready, plugin._stopping = 'current', True, False
        plugin._steam_context_revision = -1
        messages = []
        async def send(command): messages.append(command)
        plugin._send = send
        self.assertTrue((await plugin.set_steam_context('current', 2, True))['handled'])
        self.assertTrue((await plugin.set_steam_context('current', 3, False))['handled'])
        self.assertFalse((await plugin.set_steam_context('current', 2, True))['handled'])
        self.assertFalse((await plugin.set_steam_context('old', 4, True))['handled'])
        self.assertEqual(messages, [{'command': 'steam_context', 'active': True}, {'command': 'steam_context', 'active': False}])
        plugin._ready = False
        self.assertFalse((await plugin.set_steam_context('current', 5, True))['handled'])
        for revision, active in [(True, True), (-1, True), (6, 1), (6, 'true')]:
            with self.assertRaises(ValueError): await plugin.set_steam_context('current', revision, active)

    async def test_result_drops_nonfinite_or_command_timings(self):
        plugin = module.Service.__new__(module.Service)
        messages = []
        async def send(command):
            messages.append(command)
        plugin._send = send
        await plugin.action_result({"id": "a" * 32, "ok": True, "path": "overlay",
                                    "reason": "x" * 500,
                                    "timings": {"nativeMs": -1, "bridgeMs": float("nan"),
                                                "menuMs": 25.5, "command": "powershell.exe"}})
        self.assertEqual(messages[0]["timings"], {"menuMs": 25.5})
        self.assertEqual(len(messages[0]["reason"]), 256)


class HapticTests(unittest.IsolatedAsyncioTestCase):
    def test_every_pattern_matches_official_first_step(self):
        source = (Path(__file__).parents[1] / "diagnostics/ControllerHapticPatterns-upstream.cs").read_text(encoding="utf-8")
        blocks = re.finditer(r'((?:"[^"]+"\s+or\s+)*"[^"]+"|_)\s*=>\s*\[\s*new\((side|[012]),\s*([\d.]+),\s*(\d+),', source)
        matched = 0
        for block in blocks:
            for action in re.findall(r'"([^"]+)"', block[1]) or ["unknown"]:
                for side in range(3):
                    result = module.navigation_request(dict(pattern=action, side=side, magnitude=.3726774465009565))
                    self.assertEqual(result["durationMs"], int(block[4]))
                    self.assertEqual(result["side"], side if block[2] == "side" else int(block[2]))
                    self.assertAlmostEqual(result["magnitude"], .3726774465009565 * float(block[3]))
                matched += 1
        self.assertEqual(matched, 17)

    async def test_expired_and_disabled_requests_never_reach_output(self):
        plugin = module.Service.__new__(module.Service)
        plugin._stopping = False
        plugin._ready = True
        plugin._settings = {"hapticsEnabled": True}
        plugin._frontend_id = "current"
        plugin._navigation_active = True
        plugin._navigation_revision = 1
        calls = []
        plugin._haptics = types.SimpleNamespace(request=lambda request: calls.append(request))
        request = dict(pattern="confirm", side=2, magnitude=.9, frontendId="current", revision=1,
                       expiresAtMs=int(time.time()*1000)-1)
        self.assertEqual((await plugin.play_navigation_haptic(request))["path"], "expired")
        plugin._settings["hapticsEnabled"] = False
        request["expiresAtMs"] = int(time.time()*1000)+180
        self.assertEqual((await plugin.play_navigation_haptic(request))["path"], "disabled")
        self.assertFalse(calls)

    async def test_feedback_switch_is_independent_of_button_mapping(self):
        plugin = module.Service.__new__(module.Service)
        plugin._settings = dict(enabled=True, msiAction="QuickAccess", qamAction="MainMenu", hapticsEnabled=True)
        stopped = []
        plugin._haptics = types.SimpleNamespace(stop=lambda: stopped.append(True))
        plugin._save = lambda: None
        async def configure(): pass
        plugin._configure = configure
        async def state(): return plugin._settings
        plugin.get_state = state
        settings = await plugin.save_settings(dict(enabled=True, msiAction="QuickAccess", qamAction="MainMenu", hapticsEnabled=False))
        self.assertTrue(settings["enabled"])
        self.assertEqual(settings["msiAction"], "QuickAccess")
        self.assertEqual(stopped, [True])

    async def test_exit_blocks_queued_pulses_and_stale_contexts_cannot_reenable(self):
        plugin = module.Service.__new__(module.Service)
        plugin._stopping = False
        plugin._ready = True
        plugin._frontend_id = "current"
        plugin._settings = {"hapticsEnabled": True}
        plugin._navigation_active = True
        plugin._navigation_revision = 1
        stopped, calls = [], []
        plugin._haptics = types.SimpleNamespace(stop=lambda: stopped.append(True),
            recover=lambda: stopped.append("recover"), request=lambda r: calls.append(r))
        result = await plugin.set_navigation_context("current", 3, False)
        self.assertEqual(result["path"], "navigation-stopped")
        self.assertEqual((await plugin.set_navigation_context("current", 2, True))["path"], "stale-context")
        self.assertEqual((await plugin.set_navigation_context("previous", 4, True))["path"], "stale-frontend")
        request = dict(pattern="confirm", side=2, magnitude=.9, frontendId="current", revision=1,
                       expiresAtMs=int(time.time()*1000)+180)
        self.assertEqual((await plugin.play_navigation_haptic(request))["path"], "navigation-inactive")
        self.assertFalse(calls)
        self.assertEqual(stopped, [True, "recover"])

    async def test_stop_still_works_when_disabled_or_frontend_is_unloaded(self):
        plugin = module.Service.__new__(module.Service)
        plugin._stopping = True
        plugin._ready = False
        plugin._settings = {"hapticsEnabled": False}
        calls = []
        plugin._haptics = types.SimpleNamespace(request=lambda r: calls.append(r) or {"handled": True, "path": "stopped"})
        self.assertEqual((await plugin.play_navigation_haptic(dict(stop=True, requestId=123)))["path"], "stopped")
        self.assertEqual(calls, [dict(stop=True, requestId=123)])


if __name__ == "__main__":
    unittest.main()
