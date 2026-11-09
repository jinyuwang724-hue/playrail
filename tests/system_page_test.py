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
updates = system._updates_module


class FakeHelper:
    def __init__(self):
        self.calls, self.armed, self.charge = [], ["Intel(R) Wi-Fi 7", "HID 键盘设备"], 0x80

    async def __call__(self, command, **args):
        self.calls.append((command, args))
        if command == "charge_limit_set":
            before = self.charge
            self.charge = args["off"] if args["percent"] == 0 else 0x80 | args["percent"]
            return {"chargeLimitBefore": before}
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

    def test_charge_limit_off_restores_factory_byte(self):
        asyncio.run(self.control.set_charge_limit(80))
        self.assertEqual(self.helper.charge, 0xD0)
        asyncio.run(self.control.set_charge_limit(60))   # a second change keeps the first original
        asyncio.run(self.control.set_charge_limit(0))
        self.assertEqual(self.helper.charge, 0x80)
        with self.assertRaises(ValueError):
            asyncio.run(self.control.set_charge_limit(50))

    def test_versions(self):
        self.assertTrue(updates.newer("32.0.101.9150", "32.0.101.9034"))
        self.assertFalse(updates.newer("32.0.101.8724", "32.0.101.9034"))
        self.assertTrue(updates.newer("E1T91IMS.10E", "E1T91IMS.10D"))
        self.assertFalse(updates.newer("E1T91IMS.10D", "E1T91IMS.10D"))
        self.assertTrue(updates.newer("4.20", "0419"))
        self.assertFalse(updates.newer("4.19", "0419"))

    def test_driver_packages_only_use_msi_china(self):
        self.assertEqual(updates.domestic_package("https://download.msi.com/nb_drivers/a b.zip"),
                         "https://download.msi.cn/nb_drivers/a%20b.zip")
        self.assertIsNone(updates.domestic_package("https://downloadmirror.intel.com/930514/gfx_win_101.9150.exe"))
        self.assertIsNone(updates.domestic_package("https://download.msi.cn.example/nb_drivers/a.zip"))

    def test_msi_matching(self):
        drivers = [{"name": "Intel NPU", "hwid": r"PCI\VEN_8086&DEV_B03E&SUBSYS_15101462&REV_04", "version": "32.0.100.4778"}]
        rows = {"bios": [{"download_id": 1, "download_title": "BIOS", "download_version": "E1T91IMS.10E", "download_url": "https://download.msi.com/bos_exe/nb/E1T91IMS.10E.zip",
                          "download_sha256": "SHA-256:" + "b" * 64 + "<br>"}],
                "driver": [{"download_id": 2, "download_title": "Intel NPU Driver", "download_version": "32.0.100.5000", "download_url": "https://download.msi.com/nb_drivers/cs/npu.zip"},
                           {"download_id": 3, "download_title": "Realtek Audio Driver", "download_version": "99", "download_url": "https://download.msi.com/x.zip"}],
                "firmware": [{"download_id": 4, "download_title": "Firmware", "download_version": "4.19", "download_url": "https://download.msi.com/archive/frm_exe/nb/MSI Controller FW Updater_2608_3101.zip"}]}
        found = {i["id"]: i for i in updates.msi_updates(rows, drivers, "E1T91IMS.10D", "0419")}
        self.assertEqual(sorted(found), ["msi:1", "msi:2"])
        self.assertTrue(found["msi:1"]["confirm"])
        self.assertEqual(found["msi:1"]["sha256"], "b" * 64)
        self.assertEqual(found["msi:2"]["kind"], "driver")
        self.assertNotIn("run", found["msi:2"])

    def test_driver_result_checks_the_bound_version(self):
        async def run():
            version = "32.0.100.4778"

            async def call(command):
                self.assertEqual(command, "pnp_drivers")
                return [{"hwid": r"PCI\VEN_8086&DEV_B03E&REV_04", "version": version}]

            updater = updates.Updates(call, ".", None, None)
            item = {"hwid": r"PCI\VEN_8086&DEV_B03E", "latest": "32.0.100.5540"}
            self.assertTrue((await updater._result(item, {"state": "done", "exitCode": 0})).startswith("failed"))
            self.assertEqual(await updater._result(item, {"state": "done", "exitCode": 3010}), "restart")
            version = item["latest"]
            self.assertEqual(await updater._result(item, {"state": "done", "exitCode": 259}), "done")

        asyncio.run(run())

    def test_setters_reject_odd_values(self):
        for call in (lambda: self.control.set_power_button("display"), lambda: self.control.set_hibernate_after(7),
                     lambda: self.control.set_display_off(0)):
            with self.assertRaises(ValueError):
                asyncio.run(call())
        self.assertEqual(self.helper.calls, [])


if __name__ == "__main__":
    unittest.main()
