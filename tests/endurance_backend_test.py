"""Intel handoff/recovery tests. All hardware calls and process launches are fakes."""
import asyncio
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("endurance_test_module", ROOT / "plugin/claw_endurance.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Hardware:
    def __init__(self):
        self.calls = []
        self.power = {"raw": "manual-26", "pl1": 26, "pl2": 29,
                      "freq": {"scheme": "plan-a", "ac": 3000, "dc": 3000, "ac1": 3000, "dc1": 3000}}
        self.driver = {"available": True, "control": "off", "profile": "balanced",
                       "profiles": [{"id": p} for p in module.PROFILES], "framePacing": 2, "conflicts": 1}
        self.fail_profile = None
        self.worker_failure = False

    async def call(self, command, **args):
        self.calls.append((command, args))
        if command == "read":
            return copy.deepcopy(self.power)
        if command == "endurance_read":
            return copy.deepcopy(self.driver)
        if command == "endurance_set":
            if args["control"] == "auto" and args["profile"] == self.fail_profile:
                raise RuntimeError("driver rejected write")
            self.driver.update(control=args["control"], profile=args["profile"])
            return copy.deepcopy(self.driver)
        if command == "frame_pacing_set":
            self.driver["framePacing"] = args["value"]
            return copy.deepcopy(self.driver)
        if command == "restore_tdp":
            self.power["raw"] = args["raw"]
            return copy.deepcopy(self.power)
        if command == "set_tdp":
            self.power.update(pl1=args["pl1"], pl2=args["pl2"])
            return copy.deepcopy(self.power)
        if command == "set_freq":
            assert args["scheme"] == self.power["freq"]["scheme"]
            self.power["freq"].update(args)
            return copy.deepcopy(self.power)
        raise AssertionError(f"Unexpected hardware command: {command}")

    async def worker(self):
        if self.worker_failure:
            raise RuntimeError("worker failed")


class EnduranceTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.data = Path(self.temp.name)
        self.hw = Hardware()
        self.power_before = copy.deepcopy(self.hw.power)
        self.driver_before = copy.deepcopy(self.hw.driver)
        (self.data / "power-backup.json").write_text(json.dumps({"raw": "firmware-35"}))
        self.owner = module.IntelEndurance(self.hw.call, self.data, self.hw.worker)

    async def asyncTearDown(self):
        self.temp.cleanup()

    async def test_entry_releases_manual_caps_before_enabling_intel(self):
        await self.owner.apply("performance")
        self.assertEqual(self.hw.power["raw"], "firmware-35")
        self.assertTrue(all(self.hw.power["freq"][k] == 0 for k in ("ac", "dc", "ac1", "dc1")))
        self.assertEqual(self.hw.driver["control"], "auto")
        self.assertEqual(self.hw.driver["framePacing"], 0)
        self.assertTrue(self.owner.session()["ready"])

    async def test_polling_and_frame_data_cannot_write_power_or_change_limits(self):
        await self.owner.apply("performance")
        self.hw.calls.clear()
        for _ in range(20):
            await self.owner.apply("performance")
        self.assertTrue(all(command == "endurance_read" for command, _ in self.hw.calls))

    async def test_changing_profile_never_rewrites_msr_or_cpu_caps(self):
        await self.owner.apply("performance")
        self.hw.calls.clear()
        await self.owner.apply("battery")
        self.assertEqual(self.hw.driver["profile"], "battery")
        self.assertFalse(any(c in ("restore_tdp", "set_freq", "set_tdp") for c, _ in self.hw.calls))

    async def test_exit_restores_original_settings_and_removes_journal(self):
        await self.owner.apply("performance")
        await self.owner.release(restore_driver=True)
        self.assertEqual(self.hw.driver, self.driver_before)
        self.assertEqual(self.hw.power, self.power_before)
        self.assertFalse(self.owner.path.exists())

    async def test_manual_exit_disables_an_original_auto_policy(self):
        self.hw.driver.update(control="auto", framePacing=0)
        await self.owner.apply("performance")
        await self.owner.release()
        self.assertEqual(self.hw.driver["control"], "off")
        self.assertEqual(self.hw.power, self.power_before)

    async def test_failed_activation_rolls_back_and_keeps_no_owned_policy(self):
        self.hw.fail_profile = "performance"
        with self.assertRaisesRegex(RuntimeError, "rejected"):
            await self.owner.apply("performance")
        self.assertEqual(self.hw.power, self.power_before)
        self.assertEqual(self.hw.driver, self.driver_before)
        self.assertFalse(self.owner.path.exists())

    async def test_worker_failure_also_rolls_back_the_driver(self):
        self.hw.worker_failure = True
        with self.assertRaisesRegex(RuntimeError, "worker failed"):
            await self.owner.apply("performance")
        self.assertEqual(self.hw.power, self.power_before)
        self.assertEqual(self.hw.driver, self.driver_before)

    async def test_agent_restart_reuses_original_recovery_record(self):
        await self.owner.apply("performance")
        original = self.owner.session()
        owner = module.IntelEndurance(self.hw.call, self.data, self.hw.worker)
        self.hw.calls.clear()
        await owner.apply("performance")
        self.assertEqual(owner.session(), original)
        self.assertEqual([c for c, _ in self.hw.calls], ["endurance_read"])
        await owner.release(restore_driver=True)
        self.assertEqual(self.hw.power, self.power_before)

    async def test_external_disable_is_reported_without_fighting_other_controller(self):
        await self.owner.apply("performance")
        self.hw.driver["control"] = "off"
        self.hw.calls.clear()
        with self.assertRaisesRegex(RuntimeError, "关闭"):
            await self.owner.apply("performance")
        self.assertEqual([c for c, _ in self.hw.calls], ["endurance_read"])

    async def test_interrupted_entry_recovers_before_starting_a_new_session(self):
        await self.owner.apply("performance")
        record = self.owner.session()
        record["ready"] = False
        self.owner.persist(record)
        owner = module.IntelEndurance(self.hw.call, self.data, self.hw.worker)
        await owner.apply("battery")
        self.assertEqual(owner.session()["power"], self.power_before)
        await owner.release(restore_driver=True)
        self.assertEqual(self.hw.power, self.power_before)
        self.assertEqual(self.hw.driver, self.driver_before)

    async def test_recovery_never_applies_old_caps_to_a_new_power_plan(self):
        await self.owner.apply("performance")
        self.hw.power["freq"]["scheme"] = "plan-b"
        self.hw.calls.clear()
        await self.owner.release(restore_driver=True)
        self.assertFalse(any(c == "set_freq" for c, _ in self.hw.calls))

    async def test_unsupported_or_unrestorable_policy_never_writes(self):
        for state in ({"available": False, "error": "unsupported"}, {**self.driver_before, "control": "on"}):
            self.hw.driver = state
            self.hw.calls.clear()
            with self.assertRaises(RuntimeError):
                await self.owner.apply("performance")
            self.assertEqual([c for c, _ in self.hw.calls], ["endurance_read"])

    async def controller(self):
        spec = importlib.util.spec_from_file_location("endurance_power_test", ROOT / "plugin/claw_power.py")
        power = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(power)
        async def emit(_):
            pass
        control = power.PowerControl(ROOT / "plugin", self.data, emit)
        control.settings = power.validate({**power.DEFAULTS, "enabled": True, "mode": "manual",
                                           "manual": {"tdp": 26, "freq": 3000}, "boost": 3})
        control._persist_settings()
        async def call(command, **args):
            value = await self.hw.call(command, **args)
            if command in ("read", "restore_tdp", "set_freq", "set_tdp"):
                control.actual = value
            return value
        control._call = call
        control.endurance.call = call
        control.endurance.ensure_worker = self.hw.worker
        return control

    async def test_save_intel_and_return_to_manual_are_complete_handoffs(self):
        control = await self.controller()
        original = copy.deepcopy(control.settings)
        await control.save({**original, "mode": "smart"})
        self.hw.calls.clear()
        for _ in range(5):
            await control._apply()
        self.assertFalse(any(c in ("set_tdp", "restore_tdp", "set_freq") for c, _ in self.hw.calls))
        self.assertEqual(control.freq_target(), (0, 0))
        await control.save(original)
        self.assertEqual(self.hw.driver["control"], "off")
        self.assertEqual(self.hw.power["pl1"], 26)
        self.assertEqual(self.hw.power["freq"]["dc"], 3000)

    async def test_failed_save_does_not_persist_an_unusable_intel_mode(self):
        control = await self.controller()
        original = copy.deepcopy(control.settings)
        self.hw.fail_profile = "performance"
        with self.assertRaises(RuntimeError):
            await control.save({**original, "mode": "smart"})
        self.assertEqual(control.settings, original)
        self.assertEqual(json.loads(control.path.read_text()), original)
        self.assertEqual(self.hw.driver["control"], "off")


if __name__ == "__main__":
    unittest.main()
