import importlib.util
import pathlib
import re
import time
import unittest

spec = importlib.util.spec_from_file_location("claw_haptics_test", pathlib.Path(__file__).parents[1] / "repairs/playhub-haptics/claw_navigation_haptics.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Output:
    def __init__(self):
        self.visible = True
        self.indices = [0]
        self.writes = []

    def allowed(self):
        return self.visible

    def targets(self):
        return self.indices

    def write(self, index, left, right):
        self.writes.append((index, left, right))
        return 0


def request(serial=1, duration=20):
    return dict(requestId=serial, vendorId=0x0DB0, productId=0x1901,
                durationMs=duration, side=2, magnitude=0.8)


class FeedbackTests(unittest.TestCase):
    def setUp(self):
        self.output = Output()
        self.actor = module.ClawNavigationHaptics(self.output)
        self.addCleanup(self.actor.close)

    def test_only_exact_hardware_and_bounded_values(self):
        for change in [dict(vendorId=0x045e), dict(productId=1), dict(durationMs=600),
                       dict(magnitude=float("nan")), dict(side=True), dict(requestId=True)]:
            value = request()
            value.update(change)
            self.assertFalse(self.actor.request(value)["handled"])
        self.assertEqual(self.output.writes, [])

    def test_no_output_in_game_or_when_steam_is_not_foreground(self):
        self.output.visible = False
        self.assertEqual(self.actor.request(request())["path"], "steam-ui-only")
        self.assertEqual(self.output.writes, [])

    def test_ambiguous_hardware_is_rejected(self):
        self.output.indices = [0, 1]
        self.assertFalse(self.actor.request(request())["handled"])
        self.assertEqual(self.output.writes, [])

    def test_motor_balance_matches_official_native_output(self):
        official = (pathlib.Path(__file__).parents[1] / "diagnostics/ControllerHapticsService-upstream.cs").read_text(encoding="utf-8")
        match = re.search(r"0\s*=>\s*\(strength,\s*strength\s*\*\s*([\d.]+)\)", official)
        self.assertIsNotNone(match)
        secondary = float(match.group(1))
        for side in (0, 1, 2):
            value = request(side + 1, 250)
            value["side"] = side
            self.assertEqual(self.actor.request(value)["path"], "claw-xinput")
            expected_left = 0.8 if side in (0, 2) else 0.8 * secondary
            expected_right = 0.8 if side in (1, 2) else 0.8 * secondary
            self.assertAlmostEqual(self.output.writes[-1][1], expected_left)
            self.assertAlmostEqual(self.output.writes[-1][2], expected_right)

    def test_expiration_stops_motors_and_stale_stop_does_not_cancel_new_pulse(self):
        self.assertEqual(self.actor.request(request(2, 35))["path"], "claw-xinput")
        self.assertEqual(self.actor.request(dict(stop=True, requestId=1))["path"], "stale-request")
        self.assertEqual(self.output.writes, [(0, 0.8, 0.8)])
        time.sleep(0.07)
        self.assertEqual(self.output.writes[-1], (0, 0, 0))

    def test_lost_focus_and_unload_stop_active_feedback(self):
        self.actor.request(request(1, 250))
        self.output.visible = False
        time.sleep(0.04)
        self.assertEqual(self.output.writes[-1], (0, 0, 0))
        self.output.visible = True
        self.actor.request(request(2, 250))
        self.actor.close()
        self.assertEqual(self.output.writes[-1], (0, 0, 0))
        self.assertEqual(self.actor.request(request(3))["path"], "closed")


if __name__ == "__main__":
    unittest.main()
