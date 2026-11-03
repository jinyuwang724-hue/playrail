"""Regression coverage for the close tile; never terminates an actual application."""
import asyncio
from contextlib import contextmanager
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import AsyncMock

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = Path(sys.argv.pop(1)) if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else ROOT / "plugin"


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, PLUGIN / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


close = load("close_test", "claw_close_target.py")


def app(pid=10, exe="unlisted-game.exe", **extra):
    return {"pid": pid, "created": 100, "exe": exe, "path": "D:\\Games\\" + exe,
            "hwnd": pid * 10, "title": exe, "visible": True, "className": "GameWindow", **extra}


class Probe:
    def __init__(self):
        self.front, self.alive, self.pinned = None, {}, False

    def show(self, value):
        self.front = value
        if value:
            self.alive[value["pid"]] = value

    def foreground(self): return self.front
    def process(self, pid): return self.alive.get(pid)

    @contextmanager
    def hold(self, target):
        if close.identity(self.process(target["pid"])) != close.identity(target):
            raise RuntimeError("Process changed")
        self.pinned = True
        try:
            yield
        finally:
            self.pinned = False


class TargetTests(unittest.TestCase):
    def setUp(self):
        self.probe, self.now = Probe(), 10.0
        self.target = close.CloseTarget(self.probe, lambda: self.now)

    def show(self, value):
        self.probe.show(value)
        return self.target.sample()

    def test_unlisted_game_needs_no_osd_frames_or_launch_hint(self):
        self.assertEqual(self.show(app())["pid"], 10)

    def test_xbox_task_switch_and_return_repeatedly_keep_exact_target(self):
        self.show(app())
        for _ in range(10):
            for exe in ("gamebar.exe", "xboxpcappft.exe", "clawlibrary.exe", "shellexperiencehost.exe", "clawwidget.exe"):
                self.now += 5
                self.assertEqual(self.show(app(20, exe))["identity"], "10:100")
            self.assertEqual(self.show(app())["pid"], 10)

    def test_windows_task_view_retains_but_desktop_clears(self):
        self.show(app())
        self.assertEqual(self.show(app(20, "explorer.exe", className="MultitaskingViewFrame"))["pid"], 10)
        self.assertIsNone(self.show(app(20, "explorer.exe", className="Progman")))
        self.assertIsNone(self.show(app(20, "gamebar.exe")))

    def test_task_switcher_recovers_after_brief_desktop_transition(self):
        for exe, cls in (("explorer.exe", "XamlExplorerHostIslandWindow"), ("xboxpcappft.exe", "Xbox")):
            with self.subTest(exe=exe):
                self.show(app())
                self.assertIsNone(self.show(app(20, "explorer.exe", className="Progman")))
                self.now += .2
                self.assertEqual(self.show(app(20, exe, className=cls))["pid"], 10)
                self.assertEqual(self.show(app(20, "gamebar.exe"))["pid"], 10)

    def test_desktop_target_cannot_resurrect_after_transition_timeout(self):
        self.show(app())
        self.show(app(20, "explorer.exe", className="Progman"))
        self.now += 3
        self.assertIsNone(self.show(app(20, "explorer.exe", className="MultitaskingViewFrame")))
        with self.assertRaises(RuntimeError):
            with self.target.hold(10): pass

    def test_shells_cannot_create_a_target_on_cold_start(self):
        for exe in close.TRANSIENT | close.EXCLUDED:
            with self.subTest(exe=exe):
                self.assertIsNone(self.show(app(20, exe)))

    def test_switching_between_games_replaces_the_close_target(self):
        self.show(app())
        self.assertEqual(self.show(app(30, "other-game.exe"))["pid"], 30)
        self.assertEqual(self.show(app(20, "gamebar.exe"))["pid"], 30)
        with self.assertRaises(RuntimeError):
            with self.target.hold(10): pass

    def test_browser_is_an_application_without_becoming_an_osd_game(self):
        self.assertEqual(self.show(app(30, "msedge.exe"))["pid"], 30)

    def test_exit_while_switcher_is_open_clears_target(self):
        self.show(app())
        self.probe.alive.pop(10)
        self.assertIsNone(self.show(app(20, "xboxpcappft.exe")))

    def test_reused_pid_and_identical_exe_cannot_keep_old_target(self):
        self.show(app())
        self.probe.alive[10] = app(created=200)
        self.assertIsNone(self.show(app(20, "gamebar.exe")))
        with self.assertRaises(RuntimeError):
            with self.target.hold(10): pass

    def test_short_null_foreground_gap_survives_but_long_gap_expires(self):
        self.show(app())
        self.now += .1
        self.assertEqual(self.show(None)["pid"], 10)
        self.now += 3
        self.assertIsNone(self.show(None))

    def test_hidden_and_system_windows_cannot_become_targets(self):
        for value in (app(visible=False), app(path=r"C:\Windows\System32\settings.exe"),
                      app(exe="test-helper.exe"), app(exe="ClawRouter.exe"), app(hwnd=0)):
            self.assertIsNone(self.show(value))

    def test_target_is_held_during_kill_and_released_on_failure(self):
        self.show(app())
        self.show(app(20, "gamebar.exe"))
        with self.target.hold(10):
            self.assertTrue(self.probe.pinned)
        self.assertFalse(self.probe.pinned)
        with self.assertRaises(ValueError):
            with self.target.hold(10): raise ValueError("helper failed")
        self.assertFalse(self.probe.pinned)

    def test_bad_pid_never_enters_a_close_lease(self):
        self.show(app())
        for pid in (True, 10.0, "10", -1, 0, 20):
            with self.assertRaises(RuntimeError):
                with self.target.hold(pid): pass


class PowerTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.power = load("close_power_test", "claw_power.py")
        self.control = self.power.PowerControl(PLUGIN, self.temp.name, AsyncMock())
        self.probe = Probe()
        self.control.close_target = close.CloseTarget(self.probe)
        self.control._call = AsyncMock(return_value={"brightness": 50})
        self.probe.show(app())

    async def asyncTearDown(self):
        if self.control.sys_task:
            await self.control.sys_task
        self.temp.cleanup()

    async def test_widget_refreshes_target_even_when_hardware_cache_is_fresh(self):
        self.assertEqual((await self.control.system_cached())["game"]["pid"], 10)
        self.probe.show(app(30, "other-game.exe"))
        self.assertEqual((await self.control.system_cached())["game"]["pid"], 30)

    async def test_close_from_gamebar_works_without_game_recognition(self):
        await self.control.system_cached()
        self.probe.show(app(20, "gamebar.exe"))

        async def helper(command, **args):
            if command == "kill":
                self.assertTrue(self.probe.pinned)
                self.probe.alive.pop(args["pid"])
            return {}

        self.control._call = AsyncMock(side_effect=helper)
        self.assertIsNone((await self.control.close_game(10))["game"])
        self.assertFalse(self.probe.pinned)

    async def test_stale_widget_cannot_close_a_different_foreground_app(self):
        await self.control.system_cached()
        self.probe.show(app(30, "other-game.exe"))
        self.control._call.reset_mock()
        with self.assertRaises(RuntimeError):
            await self.control.close_game(10)
        self.control._call.assert_not_awaited()

    async def test_sampler_stops_without_leaving_a_task_running(self):
        task = asyncio.create_task(self.control._close_target_loop())
        await asyncio.sleep(.15)
        self.assertEqual(self.control.close_target.view()["pid"], 10)
        self.control.closing = True
        await asyncio.wait_for(task, 1)


if __name__ == "__main__": unittest.main()
