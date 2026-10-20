"""Special-button profiles and dispatcher. No hardware input or app launches."""
import asyncio
import copy
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch, AsyncMock

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("button_service_test", ROOT / "plugin/claw_service.py")
service = importlib.util.module_from_spec(spec)
spec.loader.exec_module(service)
BASE = {"enabled": True, "qamAction": "MainMenu", "msiAction": "QuickAccess", "hapticsEnabled": True}


class MappingValidation(unittest.TestCase):
    def test_legacy_values_survive_both_profile_edits(self):
        value = service.validate_settings({**BASE, "buttonMappings": {"Claw": {"type": "system", "action": "library"}},
            "steamBindings": {"QS": {"type": "keyboard", "keys": ["Ctrl", "C"]}}})
        for key, original in BASE.items():
            self.assertEqual(value[key], original)
        self.assertEqual(value["buttonMappings"]["QS"]["action"], "gamebar")
        self.assertEqual(value["steamBindings"]["QS"]["keys"], ["LCtrl", "C"])
        self.assertEqual(service.validate_settings(value), value)

    def test_absent_profiles_do_not_enable_new_ownership(self):
        value = service.validate_settings(BASE)
        self.assertEqual(value["buttonMappings"], {})
        self.assertEqual(value["steamBindings"], {})

    def test_each_context_has_an_independent_command(self):
        value = service.validate_settings({**BASE, "buttonMappings": service._buttons_module.DEFAULT_GENERAL})
        selected = service._buttons_module.selected
        self.assertEqual(selected(value, "QS", False)["action"], "gamebar")
        self.assertEqual(selected(value, "QS", True)["action"], "steamMainMenu")
        self.assertEqual(selected(value, "Claw", False)["action"], "library")
        self.assertEqual(selected(value, "Claw", True)["action"], "steamQuickAccess")

    def test_unsupported_commands_are_rejected(self):
        bad = ({"type": "gamepad", "button": "A"}, {"type": "system", "action": "shutdown"},
               {"type": "shell", "command": "something"}, {"type": "keyboard", "keys": ["X", "X"]})
        for command in bad:
            with self.subTest(command=command), self.assertRaises(ValueError):
                service.validate_settings({**BASE, "buttonMappings": {"QS": command}})
        with self.assertRaises(ValueError):
            service.validate_settings({**BASE, "buttonMappings": {"Unknown": {"type": "none"}}})

    def test_toolbox_toggle_is_limited_to_the_claw_general_context(self):
        route = service._buttons_module.toolbox_route
        for source in ("Claw", "QS", "Guide"):
            for scope in ("general", "steam", "input"):
                for steam in ("SteamClosed", "BigPicture"):
                    for xbox in (False, True, None):
                        for foreground in (False, True):
                            event = {"source": source, "scope": scope}
                            actual = route("library", event, steam, {"xboxFse": xbox, "foregroundLibrary": foreground})
                            eligible = source == "Claw" and scope == "general" and steam != "BigPicture" and xbox is False
                            expected = ("minimize-library" if foreground else "restore-library") if eligible else "library"
                            self.assertEqual(actual, expected, (event, steam, xbox, foreground))


class DispatchTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.s = object.__new__(service.Service)
        self.s._mapping_lock = asyncio.Lock()
        self.s._permission_waiters = {}
        self.s._mapping_menu_waiters = {}
        self.s._mapping_tasks = set()
        self.s._toolbox_last_press = None
        self.s._settings = service.validate_settings({**BASE, "buttonMappings": service._buttons_module.DEFAULT_GENERAL})
        self.s._ready, self.s._stopping = True, False
        self.s._status = {"mode": "BigPicture"}
        self.calls, self.events = [], []
        async def native(command):
            self.calls.append(command)
            if command["command"] == "allow_gamebar":
                self.s._permission_waiters[command["id"]].set_result(True)
        async def helper(command, **args):
            self.calls.append((command, args))
            return {"sent": True}
        async def emit(kind, value):
            self.events.append((kind, value))
            if kind == "router_event" and value.get("type") == "action":
                await self.s.action_result({"id": value["id"], "ok": True, "path": "home"})
        self.s._send = native
        self.s._emit = emit
        self.s._power = type("Power", (), {"_call": staticmethod(helper)})()

    async def test_gamebar_permission_precedes_tagged_shortcut(self):
        result = await self.s.test_button_mapping("QS", False)
        self.assertTrue(result["ok"])
        self.assertEqual(self.calls[0]["command"], "allow_gamebar")
        self.assertEqual(self.calls[1][0], "mapped_input")
        self.assertEqual(self.calls[1][1]["mapping"]["keys"], [0x5B, 0x47])

    async def test_library_launch_uses_fixed_registered_protocol(self):
        with patch.object(service.os, "startfile") as launch:
            result = await self.s.test_button_mapping("Claw", False)
        self.assertTrue(result["ok"])
        launch.assert_called_once_with("claw-library://library")
        self.assertEqual(self.calls, [])

    async def test_steam_menu_reuses_original_frontend_confirmation(self):
        result = await self.s.test_button_mapping("Claw", True)
        self.assertTrue(result["ok"])
        actions = [value for kind, value in self.events if kind == "router_event"]
        self.assertEqual(actions[0]["action"], "QuickAccess")
        self.assertEqual(self.s._settings["msiAction"], "QuickAccess")

    async def test_disabled_mapping_has_no_output(self):
        self.s._settings["buttonMappings"]["QS"] = {"type": "none"}
        self.assertTrue((await self.s.test_button_mapping("QS", False))["ok"])
        self.assertEqual(self.calls, [])

    async def test_failure_is_reported_without_changing_legacy_switch(self):
        with patch.object(service.os, "startfile", side_effect=OSError("not installed")):
            result = await self.s.test_button_mapping("Claw", False)
        self.assertFalse(result["ok"])
        self.assertIn("not installed", result["error"])
        self.assertTrue(self.s._settings["enabled"])

    async def test_foreground_is_captured_before_protocol_activation(self):
        self.s._status = {"mode": "SteamClosed"}
        calls = []
        def context():
            calls.append("snapshot")
            return {"xboxFse": False, "foregroundLibrary": True}
        with patch.object(service._buttons_module, "toolbox_context", side_effect=context), \
             patch.object(service.os, "startfile", side_effect=lambda uri: calls.append(uri)):
            result = await self.s.test_button_mapping("Claw", False)
        self.assertEqual(calls, ["snapshot", "claw-library://minimize-library"])
        self.assertEqual(result["windowRoute"], "minimize-library")

    async def test_background_toolbox_restores_instead_of_minimizing(self):
        self.s._status = {"mode": "SteamClosed"}
        with patch.object(service._buttons_module, "toolbox_context", return_value={"xboxFse": False, "foregroundLibrary": False}), \
             patch.object(service.os, "startfile") as launch:
            await self.s.test_button_mapping("Claw", False)
        launch.assert_called_once_with("claw-library://restore-library")

    async def test_xbox_desktop_retains_open_only_behavior(self):
        self.s._status = {"mode": "SteamClosed"}
        with patch.object(service._buttons_module, "toolbox_context", return_value={"xboxFse": True, "foregroundLibrary": True}), \
             patch.object(service.os, "startfile") as launch:
            await self.s.test_button_mapping("Claw", False)
        launch.assert_called_once_with("claw-library://library")

    async def test_steam_library_override_still_only_opens(self):
        self.s._settings["steamBindings"]["Claw"] = {"type": "system", "action": "library"}
        with patch.object(service._buttons_module, "toolbox_context", side_effect=AssertionError("must not query foreground in Steam")), \
             patch.object(service.os, "startfile") as launch:
            await self.s.test_button_mapping("Claw", True)
        launch.assert_called_once_with("claw-library://library")

    async def test_control_center_mapping_uses_the_same_toggle(self):
        self.s._status = {"mode": "SteamClosed"}
        self.s._settings["buttonMappings"]["Claw"] = {"type": "system", "action": "controlCenter"}
        with patch.object(service._buttons_module, "toolbox_context", return_value={"xboxFse": False, "foregroundLibrary": True}), \
             patch.object(service.os, "startfile") as launch:
            await self.s.test_button_mapping("Claw", False)
        launch.assert_called_once_with("claw-library://minimize-control-center")

    async def test_one_hardware_burst_cannot_open_and_immediately_minimize(self):
        self.s._execute_mapping = AsyncMock(return_value={"ok": True})
        event = {"source": "Claw", "scope": "general", "command": {"type": "system", "action": "library"}}
        with patch.object(service, "time") as clock:
            clock.monotonic.side_effect = [1, 1.1, 1.4]
            for _ in range(3):
                self.s._schedule_mapping(event)
        await asyncio.gather(*self.s._mapping_tasks)
        self.assertEqual(self.s._execute_mapping.await_count, 2)


if __name__ == "__main__":
    unittest.main()
