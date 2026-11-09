"""Agent delegation and update acknowledgement tests. Never touches fan hardware."""
import copy
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("service_power_test", ROOT / "plugin/claw_power.py")
power = importlib.util.module_from_spec(spec)
spec.loader.exec_module(power)


class FakeClient:
    installed = True
    def __init__(self, fan):
        self.fan = copy.deepcopy(fan)
        self.calls = []
        self.fail = False
        self.revision = 2
    async def get(self):
        if self.fail:
            raise RuntimeError("service offline")
        return {"settings": copy.deepcopy(self.fan), "target": 60, "execution": "service",
                "temperature": 65, "revision": self.revision, "writeCount": 12,
                "software": self.fan["control"] != "auto", "error": ""}
    async def update(self, fan):
        self.calls.append(copy.deepcopy(fan))
        if self.fail:
            raise RuntimeError("write rejected")
        self.fan = copy.deepcopy(fan)
        self.revision += 1
        return await self.get()


class ServiceAgentTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.control = power.PowerControl(ROOT / "plugin", self.temp.name, lambda _: None)
        self.client = FakeClient(power.validate(copy.deepcopy(power.DEFAULTS))["fan"])
        self.control.fan_service = self.client
        async def forbidden(*args, **kwargs):
            raise AssertionError("UI agent attempted a direct hardware write")
        self.control._call = forbidden
        async def release(*args, **kwargs):
            pass
        self.control._release_auto = release
        self.control.endurance.release = release
        self.control._osd_off = release
    async def asyncTearDown(self):
        self.temp.cleanup()

    async def test_tick_pulls_authoritative_service_settings_without_hardware_calls(self):
        self.client.fan["mode"] = "performance"
        await self.control._fan_tick(1)
        view = self.control.view()
        self.assertEqual(view["settings"]["fan"]["mode"], "performance")
        self.assertEqual((view["fan"]["execution"], view["fan"]["target"], view["fan"]["writeCount"]), ("service", 60, 12))

    async def test_save_waits_for_service_and_persists_acknowledged_settings(self):
        edited = copy.deepcopy(self.control.settings)
        edited["fan"]["mode"] = "quiet"
        result = await self.control.save(edited)
        self.assertEqual(result["settings"]["fan"]["mode"], "quiet")
        self.assertEqual(len(self.client.calls), 1)
        self.assertTrue(self.control.path.exists())

    async def test_failure_is_raised_and_previous_settings_retained(self):
        original = copy.deepcopy(self.control.settings)
        edited = copy.deepcopy(original)
        edited["fan"]["mode"] = "performance"
        self.client.fail = True
        with self.assertRaisesRegex(RuntimeError, "rejected"):
            await self.control.save(edited)
        self.assertEqual(self.control.settings, original)
        self.assertFalse(self.control.path.exists())

    async def test_service_offline_never_falls_back_to_old_writer(self):
        self.client.fail = True
        await self.control._fan_tick(1)
        self.assertEqual(self.control.fan_error, "service offline")
        await self.control._fan_fallback()

    async def test_agent_shutdown_leaves_independent_service_running(self):
        await self.control.close()
        self.assertEqual(self.client.calls, [])
        self.assertEqual(self.client.fan["control"], "custom")

    async def test_old_direct_firmware_test_is_blocked(self):
        with self.assertRaisesRegex(RuntimeError, "独立服务"):
            await self.control.fan_test("begin")

    async def test_cache_failure_rolls_service_back(self):
        original = copy.deepcopy(self.control.settings["fan"])
        edited = copy.deepcopy(self.control.settings)
        edited["fan"]["mode"] = "quiet"
        def fail():
            raise OSError("cache failed")
        self.control._persist_settings = fail
        with self.assertRaisesRegex(OSError, "cache failed"):
            await self.control.save(edited)
        self.assertEqual(self.client.fan, original)
        self.assertEqual(len(self.client.calls), 2)


if __name__ == "__main__":
    unittest.main()
