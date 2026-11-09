"""Native LS settings round trips and service reload ordering. No real app/process/registry writes."""
import asyncio
import copy
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import AsyncMock, MagicMock, patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def load(name):
    spec = importlib.util.spec_from_file_location(name + "_editor_test", ROOT / "plugin" / (name + ".py"))
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module


ls, svc = load("claw_lossless"), load("claw_service")


class EditorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); self.path = self.root / "native/Settings.xml"
        self.editor = ls.SettingsEditor(self.root / "data", self.path)
        devices = patch.object(ls, "device_choices", return_value=([dict(value=0, label="自动")], [dict(value=0, label="自动")]))
        devices.start(); self.addCleanup(devices.stop)

    def request(self, globals=None, profiles=None, remove=None):
        return dict(revision=self.editor.read()["revision"], globalValues=globals or {}, profiles=profiles or [], remove=remove or [])

    def save(self, **kwargs):
        return self.editor.commit(self.editor.prepare(self.request(**kwargs)), "C:/LS/LosslessScaling.exe")

    def initialize(self): self.save()

    def test_missing_settings_can_be_edited_and_initialized_without_opening_ls(self):
        view = self.editor.read()
        self.assertFalse(view["initialized"]); self.assertFalse(self.path.exists())
        self.save(profiles=[dict(index=0, values={"FrameGeneration":"LSFG3", "LSFG3Multiplier":3.5})])
        self.assertEqual(self.editor.read()["profiles"][0]["values"]["LSFG3Multiplier"], 3.5)

    def test_all_native_profile_controls_roundtrip_to_native_xml_types(self):
        values = {}
        for key, f in ls.PROFILE_FIELDS.items():
            values[key] = not f["default"] if f["kind"] == "toggle" else f["max"] if f["kind"] == "number" else f["choices"][-1]["value"]
        view = self.save(profiles=[dict(index=0, values=values)])
        self.assertEqual({k: view["profiles"][0]["values"][k] for k in values}, values)
        native = ET.parse(self.path).find("GameProfiles/Profile")
        for key, f in ls.PROFILE_FIELDS.items():
            text = native.findtext(key)
            if f["kind"] == "toggle": self.assertIn(text, ("true", "false"))
            elif f["kind"] == "number" and f["integer"]: self.assertNotIn(".", text)

    def test_globals_hotkey_and_native_enum_strings_roundtrip(self):
        view = self.save(globals={"Hotkey":"Ctrl+Shift+F8", "Language":"ChineseSimplified", "Theme":"Dark", "CloseToTray":True})
        root = ET.parse(self.path).getroot()
        self.assertEqual(root.findtext("Hotkey"), "F8")
        self.assertEqual(root.findtext("HotkeyModifierKeys"), "Control Shift")
        self.assertEqual(view["globalValues"]["Hotkey"], "Ctrl+Shift+F8")
        self.assertEqual(root.findtext("Language"), "ChineseSimplified")

    def test_untouched_settings_unknown_nodes_namespaces_comments_and_profiles_survive(self):
        self.initialize()
        root = ET.parse(self.path).getroot()
        ET.SubElement(root, "WindowWidth").text = "1234"
        extra = ET.SubElement(root, "{urn:future}Future", {"{urn:future}version":"2"}); extra.text = "keep me"
        root.append(ET.Comment("preserve comment"))
        custom = ET.SubElement(root.find("GameProfiles"), "Profile")
        ET.SubElement(custom, "Title").text = "游戏一"; ET.SubElement(custom, "Path").text = "game.exe"
        ET.SubElement(custom, "FutureEffect").text = "future-value"
        ET.ElementTree(root).write(self.path, encoding="utf-8", xml_declaration=True)
        before = copy.deepcopy(custom)
        self.save(profiles=[dict(index=0, values={"LSFG3Multiplier":4})])
        result = ET.parse(self.path).getroot()
        self.assertEqual(result.findtext("WindowWidth"), "1234")
        self.assertEqual(result.find("{urn:future}Future").text, "keep me")
        def semantic(node):
            return node.tag, node.attrib, (node.text or "").strip(), [semantic(c) for c in node]
        self.assertEqual(semantic(result.find("GameProfiles")[1]), semantic(before))
        self.assertIn(b"preserve comment", self.path.read_bytes())

    def test_conflicting_external_edit_rejected_before_write(self):
        self.initialize(); req = self.request(profiles=[dict(index=0, values={"DrawFps":False})])
        self.path.write_bytes(self.path.read_bytes() + b"\n")
        before = self.path.read_bytes()
        with self.assertRaisesRegex(ValueError, "别处修改"): self.editor.prepare(req)
        self.assertEqual(self.path.read_bytes(), before)

    def test_second_revision_check_catches_change_while_ls_is_stopping(self):
        self.initialize(); prepared = self.editor.prepare(self.request())
        self.path.write_bytes(self.path.read_bytes() + b"\n"); before = self.path.read_bytes()
        with self.assertRaisesRegex(ValueError, "别处修改"): self.editor.commit(prepared, "ls.exe")
        self.assertEqual(self.path.read_bytes(), before)

    def test_invalid_values_never_touch_native_file(self):
        self.initialize(); before = self.path.read_bytes()
        for values in [{"LSFG3Multiplier":float("nan")}, {"LSFG3Multiplier":21}, {"DrawFps":1}, {"QueueTarget":1.5},
                       {"CaptureApi":"evil"}, {"Unknown":"x"}, {"Path":"game.exe"}, {"Title":" "}]:
            with self.subTest(values=values), self.assertRaises(ValueError): self.editor.prepare(self.request(profiles=[dict(index=0,values=values)]))
        self.assertEqual(self.path.read_bytes(), before)

    def test_malformed_xml_is_not_replaced_with_defaults(self):
        self.path.parent.mkdir(); self.path.write_text("<Settings><broken>")
        before = self.path.read_bytes()
        with self.assertRaises(ValueError): self.editor.read()
        self.assertEqual(before, self.path.read_bytes())

    def test_add_edit_remove_game_profile_preserves_default_identity(self):
        self.save(profiles=[dict(index=None,values={"Title":"测试", "Path":"C:/Games/Game.exe", "AutoScale":True, "LSFG3Multiplier":4})])
        self.assertEqual(len(self.editor.read()["profiles"]),2)
        self.save(profiles=[dict(index=1,values={"Title":"重命名", "LSFG3Multiplier":2.5})])
        self.assertEqual(self.editor.read()["profiles"][1]["values"]["LSFG3Multiplier"],2.5)
        with self.assertRaisesRegex(ValueError,"默认"): self.editor.prepare(self.request(remove=[0]))
        self.save(remove=[1]); self.assertEqual(len(self.editor.read()["profiles"]),1)

    def test_duplicate_or_deleted_indexes_rejected(self):
        self.initialize()
        for req in [self.request(profiles=[dict(index=0,values={}),dict(index=0,values={})]), self.request(profiles=[dict(index=True,values={})]), self.request(remove=[-1])]:
            with self.assertRaises(ValueError): self.editor.prepare(req)

    def test_backup_contains_original_xml_and_write_is_atomic(self):
        self.initialize(); original = self.path.read_bytes()
        self.save(profiles=[dict(index=0,values={"DrawFps":False})])
        self.assertIn(original,[p.read_bytes() for p in (self.root/"data/lossless-backups").glob("*.xml")])
        self.assertEqual(list(self.path.parent.glob(".claw-ls-*.tmp")),[])

    def test_startup_registry_is_rolled_back_if_file_cannot_be_replaced(self):
        self.initialize(); before = self.path.read_bytes()
        prepared = self.editor.prepare(self.request(globals={"StartAtWindowsStartup":True}))
        with patch.object(ls,"set_startup",return_value=(None,)) as set_run, patch.object(ls,"restore_startup") as rollback, patch.object(ls.os,"replace",side_effect=OSError("disk error")):
            with self.assertRaises(OSError): self.editor.commit(prepared,"ls.exe")
            rollback.assert_called_once_with((None,)); set_run.assert_called_once()
        self.assertEqual(self.path.read_bytes(),before)

    def test_hotkeys_reject_invalid_combinations(self):
        for value in ["Ctrl+Ctrl+S","Ctrl+S+X","Ctrl+", "Ctrl", "exe.exe", "A\n"]:
            with self.subTest(value=value), self.assertRaises(ValueError): ls.parse_hotkey(value)
        self.assertEqual(ls.parse_hotkey("Alt+2"),("D2","Alt"))


class ReloadTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp=tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.obj=svc.Service.__new__(svc.Service); self.obj._lossless_lock=asyncio.Lock()
        self.obj._lossless=MagicMock(); self.obj._lossless.exe.return_value="C:/LS/LosslessScaling.exe"
        self.obj._lossless.view.return_value=dict(configured=True,running=True)
        self.obj._lossless_editor=ls.SettingsEditor(Path(self.temp.name)/"data",Path(self.temp.name)/"Settings.xml")
        self.obj._stop_lossless=AsyncMock()
        self.obj._power=types.SimpleNamespace(_call=AsyncMock())
        self.req=dict(revision="missing",globalValues={},profiles=[dict(index=0,values={"LSFG3Multiplier":3})],remove=[])

    async def test_running_application_stops_writes_and_reloads_in_order(self):
        self.obj._lossless.pids.return_value=[17]
        order=[]
        self.obj._stop_lossless.side_effect=lambda _:order.append("stop")
        commit=self.obj._lossless_editor.commit
        def save(*args): order.append("write"); return commit(*args)
        with patch.object(self.obj._lossless_editor,"commit",side_effect=save), patch.object(svc._game_settings_module,"lossless_start",side_effect=lambda _:order.append("start")):
            result=await self.obj.save_lossless_settings(self.req)
        self.assertEqual(order,["stop","write","start"])
        self.assertEqual(result["editor"]["profiles"][0]["values"]["LSFG3Multiplier"],3)

    async def test_stopped_app_is_not_started_by_save(self):
        self.obj._lossless.pids.return_value=[]
        with patch.object(svc._game_settings_module,"lossless_start") as start:
            await self.obj.save_lossless_settings(self.req); start.assert_not_called()
        self.obj._stop_lossless.assert_not_awaited()

    async def test_validation_error_does_not_stop_app(self):
        self.req["profiles"][0]["values"]={"LSFG3Multiplier":100}
        with self.assertRaises(ValueError): await self.obj.save_lossless_settings(self.req)
        self.obj._stop_lossless.assert_not_awaited()

    async def test_game_toggle_retains_custom_frame_generation_settings(self):
        editor=self.obj._lossless_editor
        editor.commit(editor.prepare(dict(revision="missing",globalValues={},profiles=[dict(index=None,values={"Title":"游戏", "Path":"C:/Game.exe", "AutoScale":True, "LSFG3Multiplier":4.5})],remove=[])), "ls.exe")
        self.obj._lossless.pids.return_value=[]
        with patch.object(svc._game_settings_module,"lossless_start") as start:
            await self.obj._set_lossless_game({"exe":"C:/Game.exe"},"游戏",False)
            profile=editor.read()["profiles"][1]["values"]
            self.assertFalse(profile["AutoScale"]); self.assertEqual(profile["LSFG3Multiplier"],4.5)
            start.assert_not_called()
            await self.obj._set_lossless_game({"exe":"C:/Game.exe"},"游戏",True)
            profile=editor.read()["profiles"][1]["values"]
            self.assertTrue(profile["AutoScale"]); self.assertEqual(profile["LSFG3Multiplier"],4.5)
            start.assert_called_once()

    async def test_game_enable_initializes_missing_native_settings_and_starts_background(self):
        self.obj._lossless.pids.return_value=[]
        with patch.object(svc._game_settings_module,"lossless_start") as start:
            await self.obj._set_lossless_game({"exe":"C:/FirstGame.exe"},"游戏",True)
        profiles=self.obj._lossless_editor.read()["profiles"]
        self.assertEqual(len(profiles),2); self.assertTrue(profiles[1]["values"]["AutoScale"])
        start.assert_called_once()

    async def test_failed_commit_restores_previous_running_state(self):
        self.obj._lossless.pids.return_value=[17]
        with patch.object(self.obj._lossless_editor,"commit",side_effect=OSError("fail")), patch.object(svc._game_settings_module,"lossless_start") as start:
            with self.assertRaises(OSError): await self.obj.save_lossless_settings(self.req)
            start.assert_called_once()

    async def test_restart_failure_reports_saved_state_without_losing_changes(self):
        self.obj._lossless.pids.return_value=[17]
        with patch.object(svc._game_settings_module,"lossless_start",side_effect=RuntimeError("start failed")):
            result=await self.obj.save_lossless_settings(self.req)
        self.assertIn("已保存",result["warning"])
        self.assertEqual(self.obj._lossless_editor.read()["profiles"][0]["values"]["LSFG3Multiplier"],3)


if __name__ == "__main__": unittest.main()
