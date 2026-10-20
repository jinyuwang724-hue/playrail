"""系统 page backend tests: settings backup / restore / defaults and the wake-lock journal.
Never touches the helper, the power plan or the real settings folder."""
import asyncio
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("system_page_test", ROOT / "plugin/claw_system.py")
system = importlib.util.module_from_spec(spec)
spec.loader.exec_module(system)


class FakeHelper:
    def __init__(self):
        self.calls, self.armed = [], ["Intel(R) Wi-Fi 7", "HID 键盘设备"]

    async def __call__(self, command, **args):
        self.calls.append((command, args))
        if command == "wake_set":
            if args["lock"]:
                changed, self.armed = self.armed, []
            else:
                changed, self.armed = list(args["restore"]), list(args["restore"])
            return {"changed": changed, "wakeArmed": self.armed}
        return {}


class SystemPageTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        base = Path(self.tmp.name)
        self.data = base / "data"
        (self.data / "input-layer" / "layouts").mkdir(parents=True)
        (self.data / "settings.json").write_text('{"enabled": true}', encoding="utf-8")
        (self.data / "power.json").write_text('{"mode": "autotdp"}', encoding="utf-8")
        (self.data / "input-layer" / "layouts" / "global.json").write_text("{}", encoding="utf-8")
        (self.data / "power-backup.json").write_text('{"raw": "1"}', encoding="utf-8")  # a restore journal
        system.BACKUPS = base / "backups"
        self.helper = FakeHelper()
        self.control = system.SystemControl(self.helper, self.data, ROOT / "plugin", lambda: (80, True))
        self.restarts = 0
        self.control.restart = lambda: setattr(self, "restarts", self.restarts + 1)

    def tearDown(self):
        self.tmp.cleanup()

    def test_backup_holds_only_settings(self):
        name = self.control.backup()["name"]
        with zipfile.ZipFile(system.BACKUPS / name) as z:
            self.assertEqual(sorted(z.namelist()), ["input-layer/layouts/global.json", "power.json", "settings.json"])

    def test_reset_keeps_journals_and_backs_up_first(self):
        result = self.control.reset()
        self.assertFalse((self.data / "settings.json").exists())
        self.assertFalse((self.data / "input-layer" / "layouts" / "global.json").exists())
        self.assertTrue((self.data / "power-backup.json").exists())
        self.assertTrue((system.BACKUPS / result["backup"]).exists())
        self.assertEqual(self.restarts, 1)

    def test_restore_round_trip(self):
        name = self.control.backup()["name"]
        (self.data / "power.json").write_text('{"mode": "manual"}', encoding="utf-8")
        self.control.restore(name)
        self.assertEqual(json.loads((self.data / "power.json").read_text(encoding="utf-8"))["mode"], "autotdp")
        self.assertEqual(self.restarts, 1)

    def test_restore_refuses_foreign_files(self):
        system.BACKUPS.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(system.BACKUPS / "claw-settings-evil.zip", "w") as z:
            z.writestr("../power-backup.json", "{}")
        with self.assertRaises(ValueError):
            self.control.restore("claw-settings-evil.zip")
        with self.assertRaises(ValueError):
            self.control.restore("..\\x.zip")
        self.assertEqual(self.restarts, 0)

    def test_wake_lock_remembers_devices(self):
        asyncio.run(self.control.set_wake_lock(True))
        self.assertEqual(self.helper.armed, [])
        self.assertEqual(sorted(json.loads(self.control.wake_path.read_text(encoding="utf-8"))),
                         sorted(["Intel(R) Wi-Fi 7", "HID 键盘设备"]))
        asyncio.run(self.control.set_wake_lock(False))
        self.assertEqual(sorted(self.helper.armed), sorted(["Intel(R) Wi-Fi 7", "HID 键盘设备"]))
        self.assertFalse(self.control.wake_path.exists())

    def test_setters_reject_odd_values(self):
        for call in (lambda: self.control.set_power_button("display"), lambda: self.control.set_hibernate_after(7),
                     lambda: self.control.set_display_off(0)):
            with self.assertRaises(ValueError):
                asyncio.run(call())
        self.assertEqual(self.helper.calls, [])


if __name__ == "__main__":
    unittest.main()
