import importlib.util
import asyncio
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).parents[1]
spec = importlib.util.spec_from_file_location("scaling", ROOT / "plugin/claw_scaling.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ScalingTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.events = []
        async def emit(state):
            self.events.append(state)
        self.scaling = module.Scaling(ROOT / "plugin", self.temp.name, emit)

    async def asyncTearDown(self):
        await self.scaling.close()
        self.temp.cleanup()

    async def test_preferences_survive_reload_without_starting_capture(self):
        await self.scaling.set(dict(enabled=False, mode="fill", filter="nis", sharpness=.75, target=123))
        self.assertIsNone(self.scaling.process)
        other = module.Scaling(ROOT / "plugin", self.temp.name, self.scaling.emit)
        self.assertEqual(other.state["mode"], "fill")
        self.assertEqual(other.state["filter"], "nis")
        self.assertFalse(other.state["enabled"])
        self.assertEqual(other.state["target"], 0)
        self.assertNotIn("enabled", json.loads(self.scaling.path.read_text()))

    async def test_real_ipc_lists_windows_and_closes_owned_process(self):
        windows = await self.scaling.targets()
        self.assertIsInstance(windows, list)
        self.assertTrue(all(type(x["hwnd"]) is int and x["width"] >= 200 for x in windows))
        state = await self.scaling.get()
        self.assertFalse(state["enabled"])
        process = self.scaling.process
        await self.scaling.close()
        self.assertEqual(process.returncode, 0)

    async def test_refresh_never_emits_default_choices_over_saved_preferences(self):
        await self.scaling.set(dict(enabled=False, mode="fill", filter="fsr", sharpness=.75, target=0))
        self.events.clear()
        await self.scaling.targets()
        await self.scaling.get()
        self.assertTrue(self.events)
        self.assertTrue(all(event["mode"] == "fill" and event["filter"] == "fsr" for event in self.events))

    async def test_real_invalid_window_releases_capture_and_reports_error(self):
        with self.assertRaisesRegex(RuntimeError, "窗口已关闭"):
            await self.scaling.set(dict(enabled=True, mode="fit", filter="fsr", sharpness=.5, target=1))
        self.assertFalse(self.scaling.state["enabled"])
        self.assertFalse(self.scaling.state["active"])

    async def test_pause_aggregates_sources_and_does_not_start_a_helper(self):
        await self.scaling.pause("overlay", True)
        await self.scaling.pause("panel", True)
        await self.scaling.pause("overlay", False)
        self.assertEqual(self.scaling.blockers, {"panel"})
        self.assertIsNone(self.scaling.process)
        await self.scaling.targets()
        self.assertTrue((await self.scaling.get())["paused"])
        await self.scaling.pause("panel", False)
        self.assertFalse((await self.scaling.get())["paused"])

    async def test_unchanged_read_and_set_send_no_configure(self):
        await self.scaling.targets()
        commands = []
        original = self.scaling.request
        async def request(command, **args):
            commands.append(command)
            return await original(command, **args)
        self.scaling.request = request
        await self.scaling.get()
        await self.scaling.set(self.scaling.preferences())
        self.assertEqual(commands, ["status"])

    def test_reject_invalid_and_unknown_parameters(self):
        value = dict(enabled=False, mode="auto", filter="linear", sharpness=.5, target=0)
        for patch in ({"mode": "fsr"}, {"filter": "fake"}, {"enabled": 1}, {"sharpness": float("nan")},
                      {"target": True}, {"enabled": True}, {"command": "other.exe"}):
            with self.assertRaises(ValueError):
                module.validate({**value, **patch})


if __name__ == "__main__":
    unittest.main()
