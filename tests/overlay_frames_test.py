import importlib.util
from pathlib import Path
import threading
import time
import unittest

spec = importlib.util.spec_from_file_location("overlay_frames", Path(__file__).parents[1] / "plugin/claw_overlay_frames.py")
frames = importlib.util.module_from_spec(spec)
spec.loader.exec_module(frames)


class FakeWindows:
    def __init__(self, hwnd=100, pid=42, path="D:\\gal\\Game.exe", fullscreen=True):
        self.hwnd, self.pids, self.paths, self.fullscreen = hwnd, {hwnd: pid}, {pid: path}, fullscreen
        self.calls = 0; self.lock = threading.Lock()

    def foreground(self): return self.hwnd
    def pid(self, hwnd): return self.pids.get(hwnd, 0)
    def path(self, pid): return self.paths.get(pid, "")
    def visible(self, hwnd): return self.fullscreen
    def timer(self): return 0
    def sleep(self, timer, seconds): time.sleep(seconds)
    def close_timer(self, timer): pass

    def invalidate(self, hwnd):
        with self.lock: self.calls += 1
        return True


def wait_until(check, timeout=1.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if check(): return True
        time.sleep(.01)
    return False


class OverlayFramesTest(unittest.TestCase):
    def test_pumps_fullscreen_game_only_while_overlay_visible(self):
        windows = FakeWindows(); pump = frames.OverlayFrames(windows)
        self.assertTrue(pump.set_visible(True))
        self.assertTrue(wait_until(lambda: windows.calls >= 3))
        self.assertTrue(pump.set_visible(True))  # repeated open keeps one pump
        pump.set_visible(False)
        self.assertTrue(pump.thread.is_alive())  # lingers for the close animation
        self.assertTrue(wait_until(lambda: not pump.thread.is_alive(), frames.LINGER + 1))
        stopped = windows.calls; time.sleep(.1)
        self.assertEqual(windows.calls, stopped)

    def test_hidden_windows_and_steam_windows_are_left_alone(self):
        for windows in (FakeWindows(fullscreen=False), FakeWindows(path="C:\\Software\\steamwebhelper.exe"),
                        FakeWindows(path="C:\\Software\\gameoverlayui64.exe"), FakeWindows(hwnd=0)):
            pump = frames.OverlayFrames(windows)
            self.assertFalse(pump.set_visible(True))
            time.sleep(.05)
            self.assertEqual(windows.calls, 0)
            pump.close()

    def test_pauses_while_player_is_elsewhere_and_resumes(self):
        windows = FakeWindows(); pump = frames.OverlayFrames(windows)
        pump.set_visible(True)
        self.assertTrue(wait_until(lambda: windows.calls >= 1))
        windows.hwnd = 200  # another window in front: wait, do not end
        time.sleep(.15); paused = windows.calls; time.sleep(.15)
        self.assertEqual(windows.calls, paused)
        self.assertTrue(pump.thread.is_alive())
        windows.hwnd = 100  # back in the game while the menu is still open
        self.assertTrue(wait_until(lambda: windows.calls > paused + 2))
        pump.close()

    def test_finds_the_game_after_opening_without_focus(self):
        windows = FakeWindows(); windows.hwnd = 0; pump = frames.OverlayFrames(windows)
        self.assertFalse(pump.set_visible(True))
        windows.hwnd = 100
        self.assertTrue(wait_until(lambda: windows.calls >= 3))
        pump.close()

    def test_real_timer_paces_faster_than_vsync(self):
        windows = frames.Windows(); timer = windows.timer()
        try:
            start = time.perf_counter()
            for _ in range(50): windows.sleep(timer, frames.INTERVAL)
            average = (time.perf_counter() - start) / 50
        finally:
            windows.close_timer(timer)
        # Event.wait would round to ~31 ms here; a redraw must be pending each 16.7 ms vsync.
        self.assertLess(average, 0.012)

    def test_elevated_game_uses_elevated_helper(self):
        windows = FakeWindows(); started, stopped = [], []
        windows.needs_elevation = lambda pid: True
        windows.start_elevated_pump = lambda hwnd: started.append(hwnd) or 7
        windows.stop_elevated_pump = lambda event: stopped.append(event)
        pump = frames.OverlayFrames(windows)
        pump.set_visible(True)
        self.assertTrue(wait_until(lambda: started == [100]))
        time.sleep(.1)
        self.assertEqual(windows.calls, 0)  # no local repaint the game would ignore
        self.assertEqual(started, [100])     # one helper per window
        pump.close()
        self.assertEqual(stopped, [7])

    def test_no_time_limit_while_menu_stays_open(self):
        now = [0.0]; windows = FakeWindows()
        pump = frames.OverlayFrames(windows, clock=lambda: now[0])
        pump.set_visible(True)
        self.assertTrue(wait_until(lambda: windows.calls >= 1))
        now[0] = 24 * 3600
        before = windows.calls
        self.assertTrue(wait_until(lambda: windows.calls > before + 2))
        self.assertTrue(pump.thread.is_alive())
        pump.close()


if __name__ == "__main__":
    unittest.main()
