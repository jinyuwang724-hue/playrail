"""Fan preset, temperature control and factory hand-back checks; no hardware writes."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("fan_control_power_test", ROOT / "plugin/claw_power.py")
power = importlib.util.module_from_spec(spec)
spec.loader.exec_module(power)


class FanTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.control = power.PowerControl(ROOT / "plugin", self.temp.name, lambda _: None)
        self.control.settings = power.validate(copy.deepcopy(power.DEFAULTS))
        self.factory = [60, 64, 68, 74, 80, 84]
        self.state = {"cpu": self.factory[:], "gpu": self.factory[:],
                      "software": False, "fullSpeed": False, "duty1": 40}
        self.calls, self.cpu_temp, self.fail_once = [], 65, None
        async def call(command, **args):
            self.calls.append((command, args))
            if command == self.fail_once:
                self.fail_once = None
                raise RuntimeError("simulated rejection")
            if command == "thermal":
                return {"package": self.cpu_temp}
            if command == "fan_curve":
                self.state.update(cpu=args["cpu"], gpu=args["gpu"], fullSpeed=args["full"], software=True)
            elif command == "fan_restore":
                self.state.update(cpu=args.get("cpu", self.state["cpu"]), gpu=args.get("gpu", self.state["gpu"]),
                                  fullSpeed=False, software=False)
            elif command != "fan_read":
                raise AssertionError(command)
            return copy.deepcopy(self.state)
        self.control._call = call

    async def asyncTearDown(self):
        self.temp.cleanup()

    async def test_three_presets_and_legacy_migration_keep_edits(self):
        self.assertEqual(power.FAN_MODES, ("quiet", "balanced", "performance"))
        old = power._fan({"mode": "firmware", "curves": {"quiet": [25] * 6}})
        self.assertEqual(old["mode"], "balanced")
        self.assertEqual(old["curves"]["quiet"], [25] * 6)

    async def test_saved_curve_controls_both_fans_and_backs_up_factory(self):
        await self.control._apply_fan()
        self.assertEqual(self.state["cpu"], [40] * 6)  # balanced at 65 C
        self.assertEqual(self.state["gpu"], [40] * 6)
        self.assertTrue(self.state["software"])
        self.assertEqual(json.loads(self.control.fan_backup_path.read_text())["cpu"], self.factory)
        self.assertEqual(self.control.view()["fan"]["execution"], "software")

    async def test_same_duty_does_not_rewrite_and_ec_reset_is_repaired(self):
        await self.control._fan_tick(0)
        for tick in range(1, 7):
            await self.control._fan_tick(tick)
        self.assertEqual(self.control.fan_write_count, 1)
        self.state.update(cpu=self.factory[:], software=False)
        await self.control._fan_tick(9)
        self.assertEqual(self.control.fan_write_count, 2)

    async def test_hysteresis_delays_noise_changes_and_heat_bypasses_delay(self):
        governor = power.FanGovernor()
        points = power.fan_points(self.control.settings["fan"])
        self.assertEqual(governor.update(points, 60, 0), 30)
        for second in range(1, 6):
            self.assertEqual(governor.update(points, 70, second), 30)
        self.assertEqual(governor.update(points, 70, 6), 50)
        for second in range(7, 16):
            self.assertEqual(governor.update(points, 60, second), 50)
        self.assertEqual(governor.update(points, 60, 16), 30)
        self.assertEqual(governor.update(points, 95, 17), 76)

    async def test_invalid_or_unavailable_sensor_releases_fixed_duty(self):
        await self.control._fan_tick(0)
        for value in (None, float("nan"), 0, 126, "65"):
            self.cpu_temp = value
            await self.control._fan_tick(3)
            self.assertFalse(self.state["software"])
            self.assertEqual(self.state["cpu"], self.factory)
            self.assertIn("温度", self.control.fan_error)
        self.cpu_temp = 65
        await self.control._fan_tick(6)
        self.assertTrue(self.state["software"])
        self.assertEqual(self.control.fan_error, "")
        self.fail_once = "thermal"
        await self.control._fan_tick(9)
        self.assertFalse(self.state["software"])

    async def test_write_failure_is_reported_and_retried(self):
        self.fail_once = "fan_curve"
        await self.control._fan_tick(0)
        self.assertIn("rejection", self.control.fan_error)
        self.assertEqual(self.control.fan_write_count, 0)
        await self.control._fan_tick(3)
        self.assertEqual(self.control.fan_write_count, 1)

    async def test_full_speed_and_shutdown_always_release_override(self):
        self.control.settings["fan"]["full"] = True
        await self.control._apply_fan()
        self.assertTrue(self.state["fullSpeed"])
        self.assertEqual(self.state["cpu"], [100] * 6)
        self.control.process = type("Process", (), {"returncode": None})()
        await self.control._fan_fallback()
        self.assertFalse(self.state["fullSpeed"])
        self.assertFalse(self.state["software"])
        self.assertEqual(self.state["cpu"], self.factory)

    async def test_existing_backup_is_not_overwritten(self):
        self.control.fan_backup_path.write_text(json.dumps({"cpu": self.factory, "gpu": self.factory}))
        self.state.update(cpu=[35] * 6, software=True)
        await self.control._apply_fan()
        self.assertEqual(json.loads(self.control.fan_backup_path.read_text())["cpu"], self.factory)


if __name__ == "__main__":
    unittest.main()
