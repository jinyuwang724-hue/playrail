import asyncio
import importlib.util
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import AsyncMock

spec = importlib.util.spec_from_file_location("magpie", Path(__file__).parents[1] / "plugin/claw_magpie.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

class GeometryTests(unittest.TestCase):
    def test_cover_crops_only_overflow_and_preserves_ratio(self):
        for w, h, sw, sh in [(1920,1080,1920,1200), (640,480,1920,1200), (1280,800,1920,1200), (800,1200,1920,1200)]:
            c = m.calculate_crop(w,h,sw,sh,m.DEFAULTS)
            actual = (w-c["left"]-c["right"])/(h-c["top"]-c["bottom"])
            self.assertAlmostEqual(actual,sw/sh,delta=.004)
            self.assertTrue(c["left"] == 0 or c["top"] == 0)
        self.assertEqual(m.calculate_crop(1920,1080,1920,1200,m.DEFAULTS)["left"],96)

    def test_manual_black_bars_are_removed_before_cover(self):
        c = m.calculate_crop(1280,800,1920,1200,{**m.DEFAULTS,"cropY":40})
        self.assertEqual(c,{"left":64,"right":64,"top":40,"bottom":40})
        self.assertEqual(m.calculate_crop(1280,800,1920,1200,{**m.DEFAULTS,"fill":False,"cropY":40}),
                         {"left":0,"right":0,"top":40,"bottom":40})
        with self.assertRaises(ValueError): m.calculate_crop(640,480,1920,1200,{**m.DEFAULTS,"cropX":300})

    def test_invalid_inputs_cannot_reconfigure_engine(self):
        for patch in [{"strength":float("nan")},{"target":True},{"capture":[]},{"fill":1},{"cropX":401},{"command":"quit"}]:
            with self.assertRaises(ValueError): m.validate({**m.DEFAULTS,"target":123,**patch})

    def test_all_original_presets_validate_and_bad_shader_parameters_rejected(self):
        self.assertEqual(len(m.CATALOG["presets"]), 7)
        for preset in m.CATALOG["presets"]:
            self.assertTrue(m.validate({**m.DEFAULTS, "target":123, "pipeline":preset["effects"]})["pipeline"])
        for stage in [{"name":"../../evil"}, {"name":"FSR\\FSR_RCAS","parameters":{"sharpness":float("nan")}},
                      {"name":"FSR\\FSR_RCAS","parameters":{"sharpness":2}}, {"name":"Bicubic","scalingType":True}]:
            with self.assertRaises(ValueError): m.validate_pipeline([stage])

class ControlTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        (root/"bin/magpie").mkdir(parents=True)
        (root/"bin/magpie/Magpie.exe").write_bytes(b"test")
        self.source = {"hwnd":123,"pid":45,"title":"test game","path":"game.exe","className":"game",
                       "width":640,"height":480,"screenWidth":1920,"screenHeight":1200}
        self.windows = types.SimpleNamespace(describe=lambda _:dict(self.source), hotkey=lambda:0,
            u=types.SimpleNamespace(IsWindow=lambda _:True,GetForegroundWindow=lambda:0),pid=lambda _:45)
        self.engine = m.Magpie(root,root,AsyncMock(),self.windows)
    async def asyncTearDown(self):
        await self.engine.close(); self.temp.cleanup()

    async def test_read_does_not_start_process_or_write_settings(self):
        before = list(Path(self.temp.name).iterdir())
        for _ in range(3): self.assertFalse((await self.engine.get())["enabled"])
        self.assertIsNone(self.engine.process)
        self.assertEqual(before,list(Path(self.temp.name).iterdir()))

    async def test_apply_only_arms_exact_window_and_identical_apply_is_noop(self):
        self.engine.stop_runtime = AsyncMock()
        settings = {**m.DEFAULTS,"target":123}
        await self.engine.apply(settings)
        await self.engine.apply(settings)
        self.assertEqual(self.engine.stop_runtime.await_count,1)
        self.assertTrue(self.engine.state["enabled"])
        self.assertFalse(self.engine.state["active"])
        self.assertIsNone(self.engine.process)

    async def test_menu_pause_keeps_owned_process_and_multiple_blockers(self):
        self.engine.state["enabled"] = True
        process = types.SimpleNamespace(returncode=None)
        self.engine.process = process; self.engine.requested = True
        self.engine.command = AsyncMock(return_value={"ok":True})
        self.engine.stop_runtime = AsyncMock()
        await self.engine.pause("panel",True)
        await self.engine.pause("overlay",True)
        await self.engine.pause("panel",False)
        self.assertEqual(self.engine.blockers,{"overlay"})
        self.assertIs(self.engine.process,process)
        self.engine.command.assert_not_awaited()  # Steam menus never stop scaling
        self.engine.stop_runtime.assert_not_awaited()

    async def test_in_game_overlay_keeps_scaling(self):
        self.engine.state["enabled"] = True
        self.engine.process = types.SimpleNamespace(returncode=None); self.engine.requested = True
        self.engine.command = AsyncMock(return_value={"ok":True})
        self.engine.stop_runtime = AsyncMock()
        state = await self.engine.pause("overlay",True)
        self.engine.command.assert_not_awaited()
        self.assertFalse(state["paused"])

    async def test_side_menu_keeps_fill_without_restart_or_view_change(self):
        self.windows.foreground_root = lambda: 123
        self.windows.output = lambda _: None
        self.engine.source = dict(self.source); self.engine.geometry = self.engine.signature(self.source)
        self.engine.state.update(enabled=True)
        self.engine.process = types.SimpleNamespace(returncode=None)
        self.engine.stop_runtime = AsyncMock()
        self.engine.command = AsyncMock(return_value={"ok":True})
        await self.engine.tick()
        start = self.engine.command.await_args_list[0]
        self.assertEqual(start.args[0], "start"); self.assertTrue(start.kwargs["cover"])
        self.assertEqual(start.kwargs["crop"], [0, 0, 0, 0])  # fill no longer crops the capture
        self.engine.blockers.add("overlay"); self.engine.command.reset_mock()
        await self.engine.tick()
        calls = [c.args[0] for c in self.engine.command.await_args_list]
        self.assertNotIn("stop", calls); self.assertNotIn("start", calls); self.assertNotIn("view", calls)

    async def test_menu_regions_reach_running_magpie_and_reject_bad_input(self):
        self.engine.process = types.SimpleNamespace(returncode=None); self.engine.requested = True
        self.engine.command = AsyncMock(return_value={"ok":True}); self.engine.stop_runtime = AsyncMock()
        await self.engine.set_plain([[0.54, 0, 1, 1]])
        self.engine.command.assert_awaited_once_with("plain", rects=[[0.54, 0.0, 1.0, 1.0]])
        await self.engine.set_plain([[0.54, 0, 1, 1]])  # unchanged: no resend
        self.assertEqual(self.engine.command.await_count, 1)
        for bad in [[[0, 0, 2, 1]], [[1, 0, 0, 1]], "all", [[0, 0, 1, 1]] * 5]:
            with self.assertRaises(ValueError): await self.engine.set_plain(bad)

    async def test_apply_remembers_game_and_restore_forgets_it(self):
        self.engine.stop_runtime = AsyncMock()
        await self.engine.apply({**m.DEFAULTS,"target":123})
        self.assertTrue((await self.engine.get())["remembered"])
        self.assertIn(m.Magpie.game_key("game.exe"), m.json.loads(self.engine.games_path.read_text(encoding="utf-8")))
        await self.engine.stop()
        self.assertFalse((await self.engine.get())["remembered"])
        self.assertEqual(m.json.loads(self.engine.games_path.read_text(encoding="utf-8")), {})

    async def test_launch_of_remembered_game_applies_automatically(self):
        self.engine.games = {m.Magpie.game_key("game.exe"): {**m.DEFAULTS, "strength": .9}}
        self.windows.foreground_root = lambda: 123
        self.engine.apply = AsyncMock()
        await self.engine.auto_start()
        await m.asyncio.wait_for(self.engine.auto_task, 3)
        self.engine.apply.assert_awaited_once()
        self.assertEqual(self.engine.apply.await_args.args[0]["target"], 123)
        self.assertEqual(self.engine.apply.await_args.args[0]["strength"], .9)  # the game's own settings

    async def test_unknown_games_never_auto_apply(self):
        self.engine.apply = AsyncMock()
        await self.engine.auto_start()
        self.assertIsNone(self.engine.auto_task)
        self.engine.apply.assert_not_awaited()

    async def test_own_fullscreen_is_switched_to_windowed_once_before_fill(self):
        full = {**self.source, "width":1920, "height":1200}
        self.windows.describe = lambda _: dict(full)
        toggles = []
        self.windows.foreground_root = lambda: 123
        self.windows.toggle_fullscreen = lambda hwnd: toggles.append(hwnd) or True
        self.engine.source = dict(full); self.engine.geometry = self.engine.signature(full)
        self.engine.state.update(enabled=True, crop={"left":0,"top":0,"right":0,"bottom":0})
        self.engine.process = types.SimpleNamespace(returncode=None)
        self.engine.stop_runtime = AsyncMock()
        self.engine.command = AsyncMock(return_value={"ok":True})
        await self.engine.tick(); await self.engine.tick()
        self.assertEqual(toggles, [123])
        self.engine.command.assert_not_awaited()  # waits for the window change instead of starting
        self.engine.switched_at -= 5
        self.windows.output = lambda _: None
        await self.engine.tick()
        self.assertEqual(toggles, [123])  # a game that ignores Alt+Enter still gets effects
        self.assertEqual(self.engine.command.await_args_list[0].args[0], "start")

if __name__ == "__main__": unittest.main()
