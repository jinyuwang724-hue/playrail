import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("captures", Path(__file__).parents[1] / "plugin/claw_captures.py")
captures = importlib.util.module_from_spec(spec)
spec.loader.exec_module(captures)

WUWA = {"exe": "C:\\Game\\Wuthering Waves Game\\Client\\Binaries\\Win64\\Client-Win64-Shipping.exe", "title": "鸣潮  "}
EDGE = {"exe": "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", "title": "新标签页 - Microsoft Edge"}
GAMEBAR = {"exe": "C:\\Program Files\\WindowsApps\\Microsoft.XboxGamingOverlay\\GameBar.exe", "title": "Game Bar"}


class Fake:
    def __init__(self, snap): self.snap = snap
    def __call__(self): return self.snap


class CapturesTest(unittest.TestCase):
    def test_window_of(self):
        self.assertEqual(captures.window_of("鸣潮   2026_10_8 13_30_34.png"), "鸣潮")
        self.assertEqual(captures.window_of("Zenless Zone Zero 2026_10_21 9_05_01 (2).png"), "Zenless Zone Zero")
        self.assertEqual(captures.window_of("Screenshot.png"), "")

    def test_pick_prefers_title_match(self):
        self.assertEqual(captures.pick("鸣潮", EDGE, WUWA, None), (WUWA, "window"))
        self.assertEqual(captures.pick("鸣潮", None, GAMEBAR, WUWA), (WUWA, "window"))

    def test_request_trusted_without_match(self):
        self.assertEqual(captures.pick("Wuthering Waves", WUWA, EDGE, None), (WUWA, "button"))

    def test_no_guess_without_request(self):
        self.assertEqual(captures.pick("Wuthering Waves", None, EDGE, None), (None, None))
        self.assertEqual(captures.pick("鸣潮", None, GAMEBAR, None), (None, None))

    def test_log_appends_and_consumes_request(self):
        with tempfile.TemporaryDirectory() as data:
            fake = Fake(WUWA)
            log = captures.CaptureLog(data, foreground=fake, log=lambda m: None)
            log.note_request()
            fake.snap = GAMEBAR
            log.on_new("Wuthering Waves 2026_10_21 10_00_00.png")
            log.on_new("Wuthering Waves 2026_10_21 10_00_05.png")   # no second press, no matching window
            lines = [json.loads(l) for l in (Path(data) / "captures.jsonl").read_text(encoding="utf-8").splitlines()]
            self.assertEqual(len(lines), 1)
            self.assertEqual((lines[0]["file"], lines[0]["exe"], lines[0]["via"]),
                             ("Wuthering Waves 2026_10_21 10_00_00.png", WUWA["exe"], "button"))

    def test_trim(self):
        with tempfile.TemporaryDirectory() as data:
            log = captures.CaptureLog(data, foreground=Fake(None), log=lambda m: None)
            for i in range(captures.KEEP_LINES + 1500):
                log.append({"file": f"x {i}.png", "exe": "C:\\" + "a" * 150 + ".exe"})
            lines = (Path(data) / "captures.jsonl").read_text(encoding="utf-8").splitlines()
            self.assertLessEqual(len(lines), captures.KEEP_LINES + 1500)
            self.assertEqual(json.loads(lines[-1])["file"], f"x {captures.KEEP_LINES + 1499}.png")
            self.assertLess((Path(data) / "captures.jsonl").stat().st_size, captures.MAX_BYTES + 1024)

    def test_real_folder_and_foreground(self):
        self.assertTrue(str(captures.captures_folder()))
        captures.Foreground()()   # must not raise


if __name__ == "__main__":
    unittest.main()
