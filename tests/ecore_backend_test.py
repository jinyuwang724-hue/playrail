"""Core-setting lifecycle tests. Registry writes are simulated; never changes HKLM."""
import asyncio
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = Path(os.environ.get("CLAW_ECORE_PLUGIN_ROOT", ROOT / "plugin"))
spec = importlib.util.spec_from_file_location("power_ecore_test", PLUGIN / "claw_power.py")
power = importlib.util.module_from_spec(spec)
spec.loader.exec_module(power)
ABSENT = {"present": False, "kind": None, "mask": None}
FULL = {"present": True, "kind": "dword", "mask": "3fff"}
PARTIAL = {"present": True, "kind": "qword", "mask": "3"}


class CoreTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.snapshot, self.boot, self.calls = dict(PARTIAL), "boot-one", []
        self.topology = {"groups": 1, "threads": 14, "supported": True}
        for name, fake in (("small_cores_snapshot", lambda: dict(self.snapshot)),
                           ("small_cores_topology", lambda: self.topology),
                           ("boot_session_id", lambda: self.boot)):
            mock = patch.object(power, name, fake)
            mock.start()
            self.addCleanup(mock.stop)
        self.control = power.PowerControl(PLUGIN, self.temp.name, None)
        self.control.view = lambda: {"ecore": self.control.ecore_view()}

        async def helper(command, **args):
            self.calls.append((command, args))
            self.assertTrue(self.control.ecore_path.exists(), "tracking must precede registry writes")
            if command == "ecore_set":
                self.assertTrue(self.control.ecore_backup_path.exists(), "backup must precede registry writes")
                self.snapshot = dict(FULL if args["enabled"] else ABSENT)
            elif command == "ecore_restore":
                self.snapshot = dict(args["original"])
            else:
                self.fail(command)
            return {**self.snapshot, "restartRequired": True}
        self.control._call = helper

    async def test_backup_enable_disable_restore_and_restarts(self):
        first = self.control.ecore_view()
        self.assertFalse(first["enabled"], "a partial mask is not all cores")
        self.assertTrue(first["partial"])
        self.assertIsNone(first["pending"], "first observation cannot prove boot state")
        enabled = (await self.control.set_ecore(True))["ecore"]
        self.assertTrue(enabled["enabled"] and enabled["pending"])
        self.assertEqual(json.loads(self.control.ecore_backup_path.read_text())["original"], PARTIAL)
        # Restarting only the app preserves pending state.
        again = power.PowerControl(PLUGIN, self.temp.name, None)
        self.assertTrue(again.ecore_view()["pending"])
        self.boot = "boot-two"
        self.assertIs(self.control.ecore_view()["pending"], False)
        self.assertTrue((await self.control.set_ecore(False))["ecore"]["pending"])
        self.assertEqual(self.snapshot, ABSENT, "off has the MotionAssistant deletion behavior")
        restored = (await self.control.restore_ecore())["ecore"]
        self.assertEqual(self.snapshot, PARTIAL)
        self.assertTrue(restored["partial"])
        self.assertEqual(json.loads(self.control.ecore_backup_path.read_text())["original"], PARTIAL)

    async def test_preexisting_full_mask_and_absent_original(self):
        self.snapshot = dict(FULL)
        self.assertTrue(self.control.ecore_view()["enabled"])
        self.assertIsNone((await self.control.set_ecore(True))["ecore"]["pending"], "no-op is not proof of a restart")
        await self.control.set_ecore(False)
        await self.control.restore_ecore()
        self.assertEqual(self.snapshot, FULL, "preexisting MotionAssistant setting must be recoverable")
        self.control.ecore_backup_path.unlink()
        self.snapshot = dict(ABSENT)
        await self.control.set_ecore(True)
        await self.control.restore_ecore()
        self.assertEqual(self.snapshot, ABSENT)

    async def test_failed_helper_and_corrupt_backup(self):
        async def fail(*args, **kwargs):
            raise RuntimeError("permission denied")
        self.control._call = fail
        with self.assertRaisesRegex(RuntimeError, "permission denied"):
            await self.control.set_ecore(True)
        self.assertEqual(self.snapshot, PARTIAL)
        self.control.ecore_backup_path.write_text("broken")
        with self.assertRaisesRegex(RuntimeError, "备份无法读取"):
            await self.control.set_ecore(True)
        self.assertEqual(self.calls, [])

    async def test_invalid_and_unsupported_inputs_never_call_helper(self):
        for value in ("on", 1, None):
            with self.assertRaises(ValueError):
                await self.control.set_ecore(value)
        self.topology = {"groups": 2, "threads": 32, "supported": False}
        with self.assertRaises(RuntimeError):
            await self.control.set_ecore(True)
        self.topology = {"groups": 1, "threads": 14, "supported": True}
        self.snapshot = {"present": True, "kind": "unsupported", "mask": None}
        with self.assertRaises(RuntimeError):
            await self.control.set_ecore(False)
        self.assertEqual(self.calls, [])

    async def test_legacy_record_read_errors_and_missing_boot_identity(self):
        self.control.ecore_path.write_text(json.dumps({"boot": 1000, "atBoot": True}))
        self.assertIsNone(self.control.ecore_view()["pending"])
        with patch.object(power, "small_cores_snapshot", side_effect=PermissionError("denied")):
            view = self.control.ecore_view()
            self.assertFalse(view["supported"])
            self.assertEqual(view["error"], "denied")
        self.boot = None
        self.assertIsNone(self.control.ecore_view()["pending"])
        await self.control.set_ecore(True)
        self.assertTrue(self.control.ecore_view()["pending"])
        self.boot = "identity-returned"
        self.assertIsNone(self.control.ecore_view()["pending"], "API recovery is not an observed restart")

    async def test_readback_disagreement_and_storage_failure(self):
        async def disagree(*args, **kwargs):
            return {**FULL, "restartRequired": True}
        self.control._call = disagree
        with self.assertRaisesRegex(RuntimeError, "回读不一致"):
            await self.control.set_ecore(True)
        self.assertEqual(self.snapshot, PARTIAL)
        with patch.object(self.control, "_save_ecore_record", side_effect=OSError("disk full")):
            with self.assertRaisesRegex(RuntimeError, "disk full"):
                await self.control.set_ecore(False)
        self.assertEqual(self.calls, [])

    async def test_external_change_across_restart_stays_unknown(self):
        self.control.ecore_view()
        await self.control.set_ecore(True)
        self.boot, self.snapshot = "boot-two", dict(ABSENT)
        self.assertIsNone(self.control.ecore_view()["pending"])


if __name__ == "__main__":
    unittest.main()
