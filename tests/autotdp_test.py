"""AutoTDP behavior and ownership tests; no real processes, drivers or power writes."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import time
from types import SimpleNamespace
import unittest

ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


auto = load("auto_test_core", ROOT / "plugin/claw_autotdp.py")
power = load("auto_test_power", ROOT / "plugin/claw_power.py")


class GovernorTests(unittest.TestCase):
    def setUp(self):
        self.gov = auto.AutoTDPGovernor()
        self.gov.configure((11, True), 8, 35, 60, 0)
        self.gov.applied(35, 0)

    def step(self, fps, now):
        value = self.gov.update(fps, now - 2, now - 1, now)
        self.gov.applied(value, now)
        return value

    def test_invalid_missing_and_stale_frames_do_not_change_budget(self):
        self.gov.limit = self.gov.applied_limit = 20
        for fps in (0, -1, None, float("nan"), float("inf"), 501, True):
            self.assertEqual(self.gov.update(fps, 8, 9, 10), 20)
        self.assertEqual(self.gov.update(5, 1, 2, 10), 20)
        self.assertEqual(self.gov.phase, "waiting")

    def test_low_fps_recovers_without_package_utilization_gate(self):
        self.gov.limit = self.gov.applied_limit = 16
        self.assertGreaterEqual(self.step(30, 4), 20)
        self.assertEqual(self.gov.phase, "tracking")

    def test_mixed_pre_write_samples_and_settlement_cannot_drive_more_changes(self):
        self.gov.limit = self.gov.applied_limit = 20
        self.gov.applied_at = 10
        self.assertEqual(self.gov.update(10, 9, 11, 12), 20)
        self.assertEqual(self.gov.update(10, 10.1, 11.1, 12.9), 20)
        self.assertGreater(self.gov.update(10, 11, 12, 13), 20)

    def test_stable_fps_probes_slowly_and_low_fps_recovers(self):
        for now in range(3, 13):
            self.step(60, now)
        self.assertEqual(self.gov.limit, 34)
        self.assertGreaterEqual(self.step(40, 13), 35)
        self.assertGreater(self.gov.probe_blocked_until, 13)
        for now in range(16, 26):
            self.assertEqual(self.step(60, now), 35)

    def test_probe_backoff_never_blocks_a_needed_increase(self):
        self.gov.limit = self.gov.applied_limit = 20
        self.gov.probe_blocked_until = 1000
        self.assertGreater(self.step(25, 4), 20)

    def test_restart_target_power_and_pid_reset_old_history(self):
        for session, ceiling, target in (((11, True), 35, 90), ((12, True), 35, 60), ((11, False), 25, 60)):
            self.gov.limit = 8
            self.gov.previous = (5, 60)
            self.gov.history = [(5, 60)]
            self.gov.configure(session, 8, ceiling, target, 10)
            self.assertEqual(self.gov.limit, ceiling)
            self.assertIsNone(self.gov.previous)
            self.assertFalse(self.gov.history)
            self.assertIsNone(self.gov.applied_limit)

    def test_synthetic_delayed_plant_stays_bounded_and_recovers_after_scene_change(self):
        trace = [(0, 35)]
        for now in range(3, 151):
            old_power = next(p for t, p in reversed(trace) if t <= now - 2)
            fps = min(60, (1.8 if 60 <= now < 100 else 2.6) * old_power)
            trace.append((now, self.step(fps, now)))
        self.assertTrue(all(8 <= p <= 35 for _, p in trace))
        self.assertLess(next(p for t, p in trace if t == 59), 30)
        self.assertGreaterEqual(max(p for t, p in trace if 60 <= t <= 75), 33)
        self.assertLess(trace[-1][1], 30)


class Hardware:
    def __init__(self):
        self.pl1, self.pl2 = 26, 29
        self.scheme = "plan-a"
        self.plans = {"plan-a": {"scheme": "plan-a", "ac": 3000, "dc": 3000, "ac1": 3000, "dc1": 3000}}
        self.driver = {"available": True, "control": "off", "profile": "balanced",
                       "profiles": [{"id": x} for x in ("performance", "balanced", "battery")]}
        self.calls, self.fps, self.pid, self.fail_once = [], 60, 11, None

    def power(self):
        return {"raw": json.dumps([self.pl1, self.pl2]), "pl1": self.pl1, "pl2": self.pl2,
                "freq": copy.deepcopy(self.plans[self.scheme])}

    async def call(self, cmd, **args):
        self.calls.append((cmd, args))
        if self.fail_once == cmd:
            self.fail_once = None
            raise RuntimeError("simulated write failure")
        if cmd == "endurance_read":
            return copy.deepcopy(self.driver)
        if cmd == "endurance_set":
            self.driver.update(control=args["control"], profile=args["profile"])
            return copy.deepcopy(self.driver)
        if cmd == "restore_tdp":
            self.pl1, self.pl2 = json.loads(args["raw"])
        elif cmd == "set_tdp":
            self.pl1, self.pl2 = args["pl1"], args["pl2"]
        elif cmd in ("set_freq", "restore_freq"):
            if cmd == "set_freq":
                assert args["scheme"] == self.scheme
            self.plans[args["scheme"]].update(args)
            result = self.power()
            if cmd == "restore_freq":
                result["restoredFreq"] = copy.deepcopy(self.plans[args["scheme"]])
            return result
        elif cmd == "frames":
            return {"pid": self.pid, "sampleWindowMs": 1000, "sampleOffsetMs": 1000,
                    "swapChains": [{"appFps": self.fps, "presentedFps": 4 * self.fps}], "package": 6}
        elif cmd != "read":
            raise AssertionError(cmd)
        return self.power()


class PolicyTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.data = Path(self.temp.name)
        self.hw = Hardware()
        self.now = 0.0
        self.old_time, self.old_charger = power.time, power.on_charger
        self.old_fg, self.old_names = power.foreground_game, power.process_names
        power.time = SimpleNamespace(monotonic=lambda: self.now, time=time.time, strftime=time.strftime)
        power.on_charger = lambda: False
        power.foreground_game = lambda: (11, "game.exe")
        power.process_names = lambda: {11: "game.exe"}
        async def emit(_):
            pass
        self.control = power.PowerControl(ROOT / "plugin", self.data, emit)
        self.control.settings = power.validate({**copy.deepcopy(power.DEFAULTS), "enabled": True, "mode": "manual",
                                               "manual": {"tdp": 26, "freq": 3000}, "boost": 3})
        self.control._persist_settings()
        self.original = copy.deepcopy(self.control.settings)
        self.original_power = self.hw.power()
        async def call(cmd, **args):
            result = await self.hw.call(cmd, **args)
            if cmd == "frames":
                self.control.frames_started = self.now
                self.control.last_frames = result
            elif cmd not in ("endurance_read", "endurance_set"):
                self.control.actual = result
            return result
        self.control._call = call
        self.control.auto_session.call = call
        self.control.endurance.call = call
        async def worker():
            pass
        self.control.endurance.ensure_worker = worker

    async def asyncTearDown(self):
        power.time, power.on_charger = self.old_time, self.old_charger
        power.foreground_game, power.process_names = self.old_fg, self.old_names
        self.temp.cleanup()

    async def enter(self):
        await self.control.save({**self.control.settings, "mode": "autotdp"})

    async def test_entry_backs_up_original_caps_and_disable_restores_them(self):
        await self.enter()
        self.assertEqual(self.hw.pl1, 25)
        self.assertEqual(self.hw.plans["plan-a"]["dc"], 0)
        self.assertEqual(json.loads(self.control.backup_path.read_text())["freq"]["dc"], 3000)
        await self.control.save({**self.control.settings, "enabled": False})
        self.assertEqual(self.hw.power(), self.original_power)
        self.assertFalse(self.control.auto_session.path.exists())

    async def test_switch_auto_intel_manual_keeps_one_owner_and_manual_values(self):
        await self.enter()
        await self.control.save({**self.control.settings, "mode": "smart"})
        self.assertFalse(self.control.auto_session.path.exists())
        self.assertTrue(self.control.endurance.path.exists())
        self.assertEqual(self.hw.driver["control"], "auto")
        await self.control.save({**self.control.settings, "mode": "autotdp"})
        self.assertFalse(self.control.endurance.path.exists())
        self.assertEqual(self.hw.driver["control"], "off")
        await self.control.save(self.original)
        self.assertEqual(self.hw.power(), self.original_power)

    async def test_missing_or_wrong_pid_samples_hold_even_with_low_actual_draw(self):
        await self.enter()
        for now in range(1, 10):
            self.now, self.hw.fps = now, 0
            await self.control._auto_tick()
        self.assertEqual(self.hw.pl1, 25)
        self.hw.pid, self.hw.fps = 12, 120
        self.now = 11
        await self.control._auto_tick()
        self.assertEqual(self.hw.pl1, 25)
        self.assertIsNone(self.control.auto_state["fps"])

    async def test_valid_samples_work_with_osd_off_and_generated_frames_are_ignored(self):
        await self.enter()
        self.control.settings["osd"]["enabled"] = False
        self.hw.fps = 30
        self.control.auto_game = (11, "game.exe")
        self.control._configure_auto()
        self.control.auto_governor.limit = 16
        await self.control._apply(force=True)
        self.now = 4
        await self.control._auto_tick()
        self.assertGreater(self.hw.pl1, 16)  # presented FPS is 120, real FPS only 30
        self.assertEqual(self.control.auto_state["fps"], 30)

    async def test_failed_auto_entry_rolls_back_mode_and_hardware(self):
        self.hw.fail_once = "set_tdp"
        with self.assertRaisesRegex(RuntimeError, "failure"):
            await self.enter()
        self.assertEqual(self.control.settings, self.original)
        self.assertEqual(json.loads(self.control.path.read_text()), self.original)
        self.assertEqual(self.hw.power(), self.original_power)
        self.assertFalse(self.control.auto_session.path.exists())

    async def test_restart_and_multiple_power_plans_recover_without_switching_plan(self):
        await self.enter()
        self.hw.scheme = "plan-b"
        self.hw.plans["plan-b"] = {"scheme": "plan-b", "ac": 4000, "dc": 4000, "ac1": 4000, "dc1": 4000}
        await self.control.auto_session.acquire()
        owner = auto.AutoTDPSession(self.control._call, self.data)
        await owner.release(restore_driver=True)
        self.assertEqual(self.hw.scheme, "plan-b")
        self.assertEqual(self.hw.plans["plan-a"]["dc"], 3000)
        self.assertEqual(self.hw.plans["plan-b"]["dc"], 4000)

    async def test_external_power_writer_pauses_auto_instead_of_fighting(self):
        await self.enter()
        self.hw.pl1 = 14
        self.hw.calls.clear()
        with self.assertRaisesRegex(RuntimeError, "改写"):
            await self.control._auto_tick()
        self.assertFalse(any(c == "set_tdp" for c, _ in self.hw.calls))

    async def test_power_plan_change_starts_a_fresh_control_window(self):
        await self.enter()
        self.control.auto_game = (11, "game.exe")
        self.control._configure_auto()
        self.control.auto_governor.limit = 16
        await self.control._apply(force=True)
        self.hw.scheme = "plan-b"
        self.hw.plans["plan-b"] = {"scheme": "plan-b", "ac": 4000, "dc": 4000, "ac1": 4000, "dc1": 4000}
        await self.control._call("read")  # the production loop refreshes power before its control tick
        self.now = 4
        await self.control._auto_tick()
        self.assertEqual(self.hw.pl1, 25)
        self.assertEqual(self.control.auto_governor.phase, "settling")
        self.assertEqual(self.hw.plans["plan-b"]["dc"], 0)

    async def test_recovery_failure_keeps_a_durable_record(self):
        await self.enter()
        self.hw.fail_once = "restore_freq"
        with self.assertRaises(RuntimeError):
            await self.control.auto_session.release()
        self.assertTrue(self.control.auto_session.path.exists())
        await self.control.auto_session.release()
        self.assertEqual(self.hw.power(), self.original_power)

    async def test_intel_unavailable_does_not_prevent_autotdp(self):
        self.hw.driver = {"available": False}
        await self.enter()
        self.assertEqual(self.hw.pl1, 25)
        await self.control.save(self.original)
        self.assertEqual(self.hw.power(), self.original_power)

    async def test_bad_settings_and_old_preset_mode_remain_distinct(self):
        self.assertEqual(power.validate(self.original)["mode"], "manual")
        self.assertEqual(power.validate({**self.original, "mode": "auto"})["mode"], "auto")
        for patch in ({"target": 0}, {"target": True}, {"target": 241}, {"floor": 30, "dcMax": 25}, {"acMax": 36}):
            with self.assertRaises(ValueError):
                power.validate({**self.original, "autotdp": {**power.DEFAULTS["autotdp"], **patch}})


if __name__ == "__main__":
    unittest.main()
