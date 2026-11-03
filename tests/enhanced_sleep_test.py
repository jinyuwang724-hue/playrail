"""Integration boundaries: pause polling, fixed-schema controls, stale ownership."""
import asyncio
import ctypes
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import AsyncMock, patch

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = Path(os.environ.get("CLAW_SLEEP_TEST_PLUGIN", str(ROOT / "plugin")))


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


sleep = load("enhanced_test_client", PLUGIN / "claw_sleep_animation.py")
power = load("enhanced_test_power", PLUGIN / "claw_power.py")


class SleepTests(unittest.IsolatedAsyncioTestCase):
    async def test_strict_controls(self):
        with patch.object(sleep, "_request", return_value={"ok": True}) as request:
            for name in ("enhanced", "sleep_games", "sleep_fingerprint"):
                await sleep.set_enabled(name, True)
                request.assert_called_with({"command": "set_" + name, "enabled": True})
            for value in (1, "true", None, []):
                with self.assertRaises(ValueError):
                    await sleep.set_enabled("enhanced", value)
            with self.assertRaises(ValueError):
                await sleep.set_enabled("arbitrary", True)
            await sleep.set_game(123)
            request.assert_called_with({"command": "set_game", "pid": 123})
            for value in (-1, True, "123"):
                with self.assertRaises(ValueError):
                    await sleep.set_game(value)

    async def test_sleep_parks_sensor_and_policy_loop(self):
        controller = object.__new__(power.PowerControl)
        controller.closing = False
        controller._fan_tick = AsyncMock()
        controller._call = AsyncMock()
        controller.presentmon_fallback = type("Trace", (), {"close": AsyncMock()})()
        async def parked():
            controller.closing = True
        with patch.object(power._sleep_module, "is_sleeping", return_value=True), \
             patch.object(power._sleep_module, "wait_awake", side_effect=parked) as waiting, \
             patch.object(power.asyncio, "sleep", new=AsyncMock()):
            await controller._loop()
        waiting.assert_awaited_once()
        controller._fan_tick.assert_not_called()
        controller._call.assert_not_called()

    async def test_sleep_parks_close_target_poll(self):
        controller = object.__new__(power.PowerControl)
        controller.closing = False
        async def parked():
            controller.closing = True
        with patch.object(power._sleep_module, "is_sleeping", return_value=True), \
             patch.object(power._sleep_module, "wait_awake", side_effect=parked):
            await controller._close_target_loop()  # No target probe is even created.

    async def test_runtime_ownership_and_stale_records(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(sleep, "_state_dir", return_value=Path(temporary)):
            runtime = Path(temporary) / "enhanced-runtime.json"
            self.assertFalse(sleep.is_sleeping())
            runtime.write_text('{"sleeping":false}')
            self.assertFalse(sleep.is_sleeping())
            runtime.write_text('{"sleeping":true,"ownerPid":2147483647,"ownerStarted":1}')
            self.assertFalse(sleep.is_sleeping())
            handle = sleep._kernel.OpenProcess(0x1000, False, os.getpid())
            try:
                times = [ctypes.c_int64() for _ in range(4)]
                self.assertTrue(sleep._kernel.GetProcessTimes(handle, *(ctypes.byref(t) for t in times)))
                created = times[0].value + 504911232000000000
            finally:
                sleep._kernel.CloseHandle(handle)
            runtime.write_text(json.dumps(dict(sleeping=True, ownerPid=os.getpid(), ownerStarted=created)))
            self.assertTrue(sleep.is_sleeping())
            runtime.write_text(json.dumps(dict(sleeping=True, ownerPid=os.getpid(), ownerStarted=created + 1)))
            self.assertFalse(sleep.is_sleeping())
            runtime.write_text("corrupt")
            self.assertFalse(sleep.is_sleeping())


if __name__ == "__main__":
    unittest.main()
