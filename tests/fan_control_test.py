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
        self.control.fan_service.marker = Path(self.temp.name) / "not-installed.json"
        self.control.settings = power.validate(copy.deepcopy(power.DEFAULTS))
        self.factory = [60, 64, 68, 74, 80, 84]
        self.state = {"cpu": self.factory[:], "gpu": self.factory[:],
                      "cpuTable": [40] + self.factory[:], "gpuTable": [40] + self.factory[:],
                      "software": False, "fullSpeed": False, "duty1": 40}
        self.calls, self.cpu_temp, self.fail_once = [], 65, None
        async def call(command, **args):
            self.calls.append((command, args))
            if command == self.fail_once:
                self.fail_once = None
                raise RuntimeError("simulated rejection")
            if command == "thermal":
                return {"package": self.cpu_temp}
            if command == "fan_probe":
                return {"model": "Claw 8 EX AI+ CG3EM"}
            if command == "fan_curve":
                self.state.update(cpu=args["cpu"], gpu=args["gpu"], fullSpeed=args["full"], software=True)
            elif command == "fan_test_curve":
                self.state.update(cpuTable=args["cpu"][:], gpuTable=args["gpu"][:], cpu=args["cpu"][1:],
                                  gpu=args["gpu"][1:], fullSpeed=False, software=args["custom"])
            elif command == "fan_restore":
                self.state.update(cpu=args.get("cpu", self.state["cpu"]), gpu=args.get("gpu", self.state["gpu"]),
                                  fullSpeed=False, software=False)
            elif command not in ("fan_read", "fan_test_done"):
                raise AssertionError(command)
            return copy.deepcopy(self.state)
        self.control._call = call

    async def asyncTearDown(self):
        if self.control.fan_test_session:
            await self.control.fan_test("end")
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

    async def test_diagnostic_lease_blocks_all_software_fan_updates(self):
        await self.control.fan_test("begin")
        config = {"cpu": [40] * 7, "gpu": [40] * 7, "custom": True, "transport": "set_ap"}
        sample = await self.control.fan_test("apply", config)
        self.assertEqual(sample["writes"], 1)
        self.calls.clear()
        for tick in range(12):
            self.cpu_temp = 60 + tick
            await self.control._fan_tick(tick)
        self.assertEqual(self.calls, [])
        self.assertEqual(self.control.view()["fan"]["execution"], "test")
        await self.control.fan_test("end")
        self.assertEqual(self.control.settings["fan"]["mode"], "balanced")
        self.assertEqual(self.control.view()["fan"]["execution"], "software")

    async def test_expired_lease_restores_and_never_reports_active(self):
        await self.control.fan_test("begin")
        self.control.fan_test_session["deadline"] = 0
        with self.assertRaisesRegex(RuntimeError, "超时"):
            await self.control.fan_test("sample")
        self.assertIsNone(self.control.fan_test_session)
        self.assertEqual(self.control.fan_test_result["reason"], "timeout")

    async def test_test_curves_and_live_edits_are_guarded(self):
        await self.control.fan_test("begin")
        for curve in ([0] * 7, [40] * 6, [60, 40, 50, 60, 70, 80, 100], [101] * 7):
            with self.assertRaises(ValueError):
                await self.control.fan_test("apply", {"cpu": curve, "gpu": curve, "custom": True, "transport": "set_ap"})
        edited = copy.deepcopy(self.control.settings)
        edited["fan"]["mode"] = "quiet"
        with self.assertRaisesRegex(RuntimeError, "测试期间"):
            await self.control.save(edited)
        self.assertFalse(any(c == "fan_test_curve" for c, _ in self.calls))

    async def test_overheat_ends_diagnostic_and_restores(self):
        await self.control.fan_test("begin")
        self.cpu_temp = 82
        with self.assertRaisesRegex(RuntimeError, "温度"):
            await self.control.fan_test("sample")
        self.assertIsNone(self.control.fan_test_session)
        self.assertEqual(self.control.fan_test_result["reason"], "temperature_abort")

    async def test_recovery_failure_is_reported_and_factory_control_released(self):
        await self.control.fan_test("begin")
        self.fail_once = "fan_test_curve"
        with self.assertRaisesRegex(RuntimeError, "rejection"):
            await self.control.fan_test("end")
        self.assertIsNone(self.control.fan_test_session)
        self.assertFalse(self.control.fan_test_result["restored"])
        self.assertFalse(self.state["software"])

    async def test_resuming_ignores_pre_test_temperature_cache(self):
        await self.control.fan_test("begin")
        self.control.temperature = 55
        self.control.temperature_at = power.time.monotonic()
        self.cpu_temp = 82
        await self.control.fan_test("end")
        self.assertEqual(self.state["cpu"], [76] * 6)


if __name__ == "__main__":
    unittest.main()
