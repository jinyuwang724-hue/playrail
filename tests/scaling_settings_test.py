"""Scaling setup and preset persistence; only temporary files, no real LS/game/hardware writes."""
import asyncio
import copy
import importlib.util
import json
from pathlib import Path
import struct
import time
import tempfile
import unittest
from unittest.mock import AsyncMock, MagicMock, patch

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    spec = importlib.util.spec_from_file_location(name + "_settings_test", ROOT / "plugin" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


gs, mg, service = load("claw_game_settings"), load("claw_magpie"), load("claw_service")


def make_exe(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    data = bytearray(68)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 60, 64)
    data[64:] = b"PE\0\0"
    path.write_bytes(data)
    return str(path)


class LosslessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.config = gs.LosslessConfig(self.root)
        self.config._detect_at = time.monotonic()  # Keep fixture tests isolated from this machine's auto-detection.
        self.exe = make_exe(self.root / "软件 空格" / "LosslessScaling.exe")
        self.xml = self.root / "Settings.xml"
        self.patcher = patch.object(gs, "LS_SETTINGS", self.xml)
        self.patcher.start()
        self.addCleanup(self.patcher.stop)

    def test_cached_read_never_launches_or_creates_config(self):
        with patch.object(gs, "lossless_exe") as detect, patch.object(gs, "lossless_start") as start:
            self.assertFalse(self.config.view()["configured"])
            self.assertFalse(self.config.path.exists())
            detect.assert_not_called()
            start.assert_not_called()

    def test_running_local_copy_is_detected_without_steam_or_saved_selection(self):
        with patch.object(gs,"lossless_processes",return_value=[dict(pid=123,path=self.exe)]), patch.object(gs,"lossless_exe",return_value=None), patch.object(gs.winreg,"OpenKey",side_effect=OSError), patch.object(gs,"lossless_start") as start:
            result = self.config.view(refresh=True)
            self.assertTrue(result["installed"]); self.assertTrue(result["running"])
            self.assertEqual(result["detectedExe"],self.exe)
            self.assertFalse(self.config.path.exists()); self.assertFalse(self.xml.exists())
            start.assert_not_called()

    def test_auto_detection_uses_startup_path_and_not_account_ownership(self):
        with patch.object(gs,"lossless_processes",return_value=[]), patch.object(gs,"lossless_exe",return_value=None), patch.object(gs.winreg,"OpenKey",return_value=MagicMock()), patch.object(gs.winreg,"QueryValueEx",return_value=(f'"{self.exe}" -StartMinimized',1)):
            self.assertEqual(self.config.view(refresh=True)["detectedExe"],self.exe)

    def test_force_detection_bypasses_missing_install_cache_and_missing_saved_copy(self):
        self.config.path.write_text(json.dumps(dict(exe=str(self.root/"missing/LosslessScaling.exe"))))
        with patch.object(gs,"lossless_processes",return_value=[]), patch.object(gs,"lossless_exe",return_value=self.exe), patch.object(gs.winreg,"OpenKey",side_effect=OSError):
            self.assertFalse(self.config.view()["installed"])
            self.assertTrue(self.config.view(refresh=True)["installed"])
            Path(self.exe).unlink()
            self.assertFalse(self.config.view()["installed"])

    def test_local_config_persists_and_missing_exe_disables_options(self):
        result = self.config.configure("local", self.exe)
        self.assertTrue(result["configured"])
        self.assertFalse(result["settingsReady"])
        self.assertEqual(gs.LosslessConfig(self.root).exe(), self.exe)
        Path(self.exe).unlink()
        self.assertFalse(self.config.view()["configured"])
        self.assertEqual(self.config.view()["exe"], self.exe)

    def test_reject_other_program_invalid_file_and_relative_path_without_writing(self):
        for exe in [make_exe(self.root / "other.exe"), "LosslessScaling.exe", "", None]:
            with self.assertRaises(ValueError): self.config.configure("local", exe)
        Path(self.exe).write_bytes(b"not an executable")
        with self.assertRaises(ValueError): self.config.configure("local", self.exe)
        self.assertFalse(self.config.path.exists())

    def test_failed_steam_detection_preserves_local_selection(self):
        self.config.configure("local", self.exe)
        before = self.config.path.read_bytes()
        with patch.object(gs, "lossless_exe", return_value=None):
            with self.assertRaises(ValueError): self.config.configure("steam")
        self.assertEqual(self.config.path.read_bytes(), before)

    def test_steam_detection_uses_secondary_library_manifest_install_name(self):
        steam, secondary = self.root / "Steam", self.root / "Second Library"
        (steam / "steamapps").mkdir(parents=True)
        exe = make_exe(secondary / "steamapps/common/Custom Install/LosslessScaling.exe")
        (steam / "steamapps/libraryfolders.vdf").write_text('"path" "' + str(secondary).replace("\\", "\\\\") + '"')
        manifest = secondary / "steamapps/appmanifest_993090.acf"
        manifest.write_text('"appid" "993090" "installdir" "Custom Install"')
        with patch.object(gs.winreg, "OpenKey", return_value=MagicMock()), patch.object(gs.winreg, "QueryValueEx", return_value=(str(steam), 1)):
            self.assertEqual(gs.lossless_exe(), exe)
            self.assertTrue(self.config.configure("steam")["configured"])
            manifest.write_text('"appid" "123" "installdir" "Custom Install"')
            self.assertIsNone(gs.lossless_exe())

    def test_config_options_wait_for_default_profile_and_clear_preserves_ls_xml(self):
        self.config.configure("local", self.exe)
        self.xml.write_text('<Settings><GameProfiles><Profile><Title>默认</Title><AutoScale>false</AutoScale></Profile></GameProfiles></Settings>', encoding="utf-8")
        self.assertTrue(self.config.view()["settingsReady"])
        before = self.xml.read_bytes()
        with patch.object(gs,"lossless_exe",return_value=None), patch.object(gs,"lossless_processes",return_value=[]), patch.object(gs.winreg,"OpenKey",side_effect=OSError):
            self.assertFalse(self.config.clear()["configured"])
        self.assertEqual(self.xml.read_bytes(), before)

    def test_bad_saved_json_does_not_enable_options(self):
        for value in ["[]", "null", "{", '{"mode":"local","exe":42}']:
            self.config.path.write_text(value)
            self.assertFalse(self.config.view()["configured"])


class PresetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.engine = mg.Magpie(self.root, self.root, AsyncMock(), windows=MagicMock())
        self.chain = copy.deepcopy(mg.CATALOG["presets"][1]["effects"])

    def save(self, name="测试", old=None, effects=None):
        return self.engine.save_preset({"name": name, "originalName": old, "effects": self.chain if effects is None else effects})

    def test_custom_chain_order_parameters_scales_roundtrip_and_available_to_game(self):
        self.chain.reverse()
        self.chain[-1].update(scalingType=2, scale=[1280, 800])
        self.save()
        engine = mg.Magpie(self.root, self.root, AsyncMock(), windows=MagicMock())
        self.assertIn("测试", engine.presets())
        engine.set_game("game.exe", enabled=True, preset="测试")
        self.assertEqual(engine.game_prefs("game.exe")["pipeline"], self.chain)

    def test_rename_updates_enabled_and_disabled_game_references(self):
        self.save()
        self.engine.set_game("game.exe", enabled=True, preset="测试")
        self.engine.set_game("other.exe", enabled=False, preset="测试")
        self.save("新名字", "测试", effects=[])
        self.assertNotIn("测试", self.engine.presets())
        for exe in ["game.exe", "other.exe"]: self.assertEqual(self.engine.game_info(exe)["preset"], "新名字")
        self.assertEqual(self.engine.game_prefs("game.exe")["pipeline"], [])
        self.assertFalse(self.engine.game_info("other.exe")["enabled"])

    def test_delete_custom_resets_game_reference_to_none_after_restart(self):
        self.save()
        self.engine.set_game("game.exe", enabled=True, preset="测试")
        self.engine.delete_preset("测试")
        engine = mg.Magpie(self.root, self.root, AsyncMock(), windows=MagicMock())
        self.assertEqual(engine.game_info("game.exe")["preset"], "无")
        self.assertEqual(engine.game_prefs("game.exe")["pipeline"], [])

    def test_builtin_override_and_restore_never_mutates_original_catalog(self):
        original = copy.deepcopy(mg.CATALOG)
        name = mg.CATALOG["presets"][0]["name"]
        self.save(name, name, self.chain)
        self.engine.set_game("game.exe", enabled=True, preset=name)
        self.assertEqual(self.engine.game_prefs("game.exe")["pipeline"], self.chain)
        self.engine.delete_preset(name)
        self.assertEqual(self.engine.game_prefs("game.exe")["pipeline"], original["presets"][0]["effects"])
        self.assertEqual(mg.CATALOG, original)

    def test_invalid_names_duplicates_and_chain_never_change_saved_file(self):
        self.save()
        before = self.engine.presets_path.read_bytes()
        for name, old, chain in [("无", None, []), (" ", None, []), ("测试", None, []), ("x", "missing", []), ("x", None, [{"name":"evil"}]), ("x", None, self.chain * 7)]:
            with self.assertRaises(ValueError): self.save(name, old, chain)
        self.assertEqual(self.engine.presets_path.read_bytes(), before)

    def test_write_failure_does_not_publish_unsaved_preset(self):
        with patch.object(self.engine, "_save_presets", side_effect=OSError("disk full")):
            with self.assertRaises(OSError): self.save()
        self.assertNotIn("测试", self.engine.presets())


class ServiceTests(unittest.IsolatedAsyncioTestCase):
    async def test_unconfigured_ls_never_kills_process_or_writes_profiles(self):
        obj = service.Service.__new__(service.Service)
        obj._lossless = MagicMock()
        obj._lossless.exe.return_value = None
        obj._lossless_lock = asyncio.Lock()
        obj._power = MagicMock()
        obj._power._call = AsyncMock()
        with patch.object(service._game_settings_module, "lossless_write") as write:
            with self.assertRaisesRegex(RuntimeError, "完成配置"):
                await obj.game_settings_set({"id":"123", "exe":"game.exe"}, "lossless", True)
            obj._power._call.assert_not_awaited()
            write.assert_not_called()


if __name__ == "__main__":
    unittest.main()
