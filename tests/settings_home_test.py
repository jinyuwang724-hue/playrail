"""Full-screen home regression checks; all registry writes are mocked."""
import importlib.util
import json
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch, MagicMock

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("settings_home_test_subject", ROOT / "plugin/claw_settings.py")
settings = importlib.util.module_from_spec(spec)
spec.loader.exec_module(settings)


class HomeTests(unittest.TestCase):
    def setUp(self):
        settings._home_cache = None
        self.values = {"GamingHomeApp": settings.OWN_HOME, "StartupToGamingHome": 1}
        self.read = patch.object(settings, "_read_gaming", side_effect=self.values.get)
        self.read.start()
        self.addCleanup(self.read.stop)

    def test_unregistered_config_is_not_an_effective_home(self):
        with patch.object(settings, "_gaming_apps", return_value=[]):
            home = settings.home_app()
        self.assertIsNone(home["current"])
        self.assertEqual(home["configured"], settings.OWN_HOME)
        self.assertEqual(home["choices"], [])
        self.assertIn("未注册", home["error"])

    def test_rejects_unregistered_home_before_any_write(self):
        with patch.object(settings, "_gaming_apps", return_value=[]) as discover, \
             patch.object(settings.winreg, "CreateKey") as create:
            with self.assertRaisesRegex(ValueError, "尚未注册"):
                settings.set_home_app(settings.OWN_HOME)
            create.assert_not_called()
            discover.assert_called_once_with(refresh=True)

    def test_registered_home_is_selected_and_read_back(self):
        choices = [{"id": settings.OWN_HOME, "name": "Claw 游戏库"}]
        writes = []
        def write(key, name, reserved, kind, value):
            self.values[name] = value
            writes.append(name)
        with patch.object(settings, "_gaming_apps", return_value=choices), \
             patch.object(settings.winreg, "CreateKey", return_value=MagicMock()), \
             patch.object(settings.winreg, "SetValueEx", side_effect=write):
            home = settings.set_home_app(settings.OWN_HOME)
        self.assertEqual(home["current"], settings.OWN_HOME)
        self.assertIsNone(home["error"])
        self.assertTrue(home["startup"])
        self.assertEqual(writes, ["StartupToGamingHome", "GamingHomeApp"])

    def test_off_keeps_home_and_only_disables_startup(self):
        writes = []
        with patch.object(settings, "_gaming_apps", return_value=[]), \
             patch.object(settings.winreg, "CreateKey", return_value=MagicMock()), \
             patch.object(settings.winreg, "SetValueEx", side_effect=lambda *args: writes.append(args)):
            settings.set_home_app("off")
        self.assertEqual([(w[1], w[4]) for w in writes], [("StartupToGamingHome", 0)])

    def test_catalog_failure_is_visible_and_blocks_writes(self):
        with patch.object(settings, "_gaming_apps", side_effect=RuntimeError("catalog failed")), \
             patch.object(settings.winreg, "CreateKey") as create:
            home = settings.home_app()
            self.assertIsNone(home["current"])
            self.assertIn("无法读取", home["error"])
            with self.assertRaises(RuntimeError):
                settings.set_home_app(settings.OWN_HOME)
            create.assert_not_called()

    def test_empty_and_singleton_catalog_json_remain_arrays_and_refresh(self):
        result = subprocess.CompletedProcess([], 0, b"[]", b"")
        with patch.object(settings.subprocess, "run", return_value=result) as run:
            self.assertEqual(settings._gaming_apps(), [])
            result.stdout = json.dumps([{"id": settings.OWN_HOME, "name": "Claw"}]).encode()
            self.assertEqual(settings._gaming_apps(), [])  # cached
            self.assertEqual(settings._gaming_apps(refresh=True)[0]["id"], settings.OWN_HOME)
            self.assertEqual(run.call_count, 2)


if __name__ == "__main__":
    unittest.main()
