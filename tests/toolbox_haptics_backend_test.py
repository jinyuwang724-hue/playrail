"""Exercise the installed toolbox actor, including killed-process recovery."""
import importlib.util
from pathlib import Path
import sys
import unittest

spec = importlib.util.spec_from_file_location("prior_haptic_tests", Path(__file__).with_name("playhub_haptics_backend_test.py"))
prior = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prior)
spec = importlib.util.spec_from_file_location("toolbox_haptic_tests", Path(__file__).parents[1] / "plugin/claw_navigation_haptics.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
prior.module = module
FeedbackTests = prior.FeedbackTests


class RecoveryTests(unittest.TestCase):
    def test_restarted_actor_clears_untracked_residual_pulse(self):
        output = prior.Output()
        output.can_recover = lambda: True
        output.writes.append((0, .8, .8))  # Hardware state outlives the old actor.
        actor = module.ClawNavigationHaptics(output)
        self.assertTrue(actor.recover())
        self.assertEqual(output.writes[-1], (0, 0, 0))
        actor.close()

    def test_recovery_never_touches_a_running_game_or_ambiguous_pad(self):
        for game, indices in [(True, [0]), (False, [0, 1]), (False, [])]:
            output = prior.Output()
            output.indices = indices
            output.can_recover = lambda: not game
            actor = module.ClawNavigationHaptics(output)
            self.assertFalse(actor.recover())
            self.assertEqual(output.writes, [])
            actor.close()


if __name__ == "__main__":
    unittest.main()
