"""Diagnostic acceptance criteria must reject readback-only and factory-only success."""
import copy
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("fan_firmware_tool", ROOT / "tools/Test-FanFirmware.py")
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


def phase(rpms):
    curve = tool.CURVES["A"]
    rows = [{"writes": 1, "softwareWrites": 3, "fan": {"software": True, "fullSpeed": False,
             "cpu": curve[1:], "gpu": curve[1:], "ecTemperature": temp, "rpm1": rpm, "rpm2": rpm}}
            for temp, rpm in zip((54, 55, 60, 64, 70, 71), rpms)]
    return {"curve": curve, "setupWrites": 1, "hostWrites": 3, "samples": rows}


class VerdictTests(unittest.TestCase):
    def test_stored_tables_with_static_fans_are_failure(self):
        result = tool.evaluate_phase(phase([2900] * 6))
        self.assertTrue(result["noHostWrites"])
        self.assertEqual(result["crossedBoundaries"], [57, 64, 71])
        self.assertFalse(result["passed"])

    def test_cooling_after_previous_profile_cannot_fake_a_positive_response(self):
        self.assertFalse(tool.evaluate_phase(phase([3700, 3100, 2900, 2900, 2900, 2900]))["passed"])

    def test_both_fans_must_respond_without_host_writes(self):
        value = phase([2900, 2900, 3300, 3900, 4500, 4500])
        self.assertTrue(tool.evaluate_phase(value)["passed"])
        changed = copy.deepcopy(value)
        changed["samples"][-1]["softwareWrites"] += 1
        self.assertFalse(tool.evaluate_phase(changed)["passed"])
        changed = copy.deepcopy(value)
        for row in changed["samples"]:
            row["fan"]["rpm2"] = 2900
        self.assertFalse(tool.evaluate_phase(changed)["passed"])

    def test_factory_takeover_or_table_reset_fails(self):
        value = phase([2900, 2900, 3300, 3900, 4500, 4500])
        value["samples"][-1]["fan"]["software"] = False
        self.assertFalse(tool.evaluate_phase(value)["passed"])
        value["samples"][-1]["fan"]["software"] = True
        value["samples"][-1]["fan"]["cpu"] = [60] * 6
        self.assertFalse(tool.evaluate_phase(value)["passed"])


if __name__ == "__main__":
    unittest.main()
