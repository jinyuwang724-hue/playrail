import asyncio
import copy
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("clash_control", Path(__file__).parents[1] / "plugin" / "claw_clash.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

def fixture():
    return {"mode": "rule", "tun": True, "proxies": {
        "GLOBAL": {"type": "Selector", "now": "DIRECT", "all": ["DIRECT", "overseas"]},
        "overseas": {"type": "Selector", "now": "日本 / 01", "all": ["日本 / 01", "香港 | 02", "automatic"]},
        "OpenAI": {"type": "Selector", "now": "overseas", "all": ["overseas", "DIRECT"]},
        "media": {"type": "Selector", "now": "overseas", "all": ["overseas", "DIRECT"]},
        "automatic": {"type": "URLTest", "now": "香港 | 02", "all": ["日本 / 01", "香港 | 02"]},
        "日本 / 01": {"type": "Vless", "history": [{"delay": 120}]},
        "香港 | 02": {"type": "Vless", "history": []},
        "DIRECT": {"type": "Direct"}}}

class ViewTests(unittest.TestCase):
    def test_default_targets_shared_outbound_and_preserves_names(self):
        view = module.proxy_view(fixture())
        self.assertEqual(view["group"], "overseas")
        self.assertEqual(view["current"], "日本 / 01")
        self.assertEqual(view["delay"], 120)
        self.assertNotIn("automatic", [group["name"] for group in view["groups"]])

    def test_nested_groups_resolve_and_cycles_terminate(self):
        raw = fixture()
        raw["proxies"]["overseas"]["now"] = "automatic"
        self.assertEqual(module.proxy_view(raw)["resolved"], "香港 | 02")
        raw["proxies"]["automatic"]["now"] = "overseas"
        self.assertTrue(module.proxy_view(raw)["available"])

    def test_direct_mode_is_off_even_with_tun_active(self):
        raw = fixture()
        raw["mode"] = "direct"
        view = module.proxy_view(raw)
        self.assertFalse(view["enabled"])
        self.assertTrue(view["tun"])

    def test_removed_preference_falls_back_and_unknown_mode_fails(self):
        raw = fixture()
        self.assertEqual(module.proxy_view(raw, "removed")["group"], "overseas")
        raw["mode"] = "bogus"
        with self.assertRaises(ValueError):
            module.proxy_view(raw)

class ControlTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.control = module.ClashControl(Path(self.temp.name), Path(self.temp.name))
        self.raw = fixture()
        self.calls = []
        self.active = 0
        self.max_active = 0
        async def request(payload):
            self.active += 1
            self.max_active = max(self.max_active, self.active)
            await asyncio.sleep(.001)
            self.calls.append(payload)
            if payload["command"] == "mode":
                self.raw["mode"] = payload["mode"]
            if payload["command"] == "select":
                self.raw["proxies"][payload["group"]]["now"] = payload["node"]
            self.active -= 1
            return copy.deepcopy(self.raw)
        self.control._request = request
        await self.control.refresh()

    async def asyncTearDown(self):
        self.temp.cleanup()

    async def test_real_state_controls_toggle_and_only_rule_direct_are_written(self):
        self.assertFalse((await self.control.apply("enabled", False))["enabled"])
        self.assertTrue((await self.control.apply("enabled", True))["enabled"])
        self.assertEqual([call["mode"] for call in self.calls if call["command"] == "mode"], ["direct", "rule"])
        with self.assertRaises(ValueError):
            await self.control.apply("enabled", "true")

    async def test_node_action_uses_current_group_and_retains_original_unicode(self):
        view = await self.control.apply("node", {"group": "overseas", "node": "香港 | 02"})
        self.assertEqual(view["current"], "香港 | 02")
        self.assertEqual(self.calls[-1], {"command": "select", "group": "overseas", "node": "香港 | 02"})

    async def test_removed_nodes_and_stale_groups_never_mutate_core(self):
        self.raw["proxies"]["overseas"]["all"].remove("香港 | 02")
        with self.assertRaises(ValueError):
            await self.control.apply("node", {"group": "overseas", "node": "香港 | 02"})
        await self.control.apply("group", "OpenAI")
        with self.assertRaises(ValueError):
            await self.control.apply("node", {"group": "overseas", "node": "日本 / 01"})
        self.assertFalse(any(call["command"] == "select" for call in self.calls))

    async def test_operations_serialize_and_group_preference_survives_restart(self):
        await asyncio.gather(self.control.apply("enabled", False), self.control.apply("enabled", True))
        self.assertEqual(self.max_active, 1)
        await self.control.apply("group", "OpenAI")
        restarted = module.ClashControl(Path(self.temp.name), Path(self.temp.name))
        self.assertEqual(restarted._group, "OpenAI")
        self.assertFalse(any(call["command"] == "select" for call in self.calls))

    async def test_connection_failure_has_no_cached_enabled_state(self):
        async def broken(payload):
            raise RuntimeError("unavailable")
        self.control._request = broken
        view = await self.control.refresh()
        self.assertFalse(view["available"])
        self.assertNotIn("enabled", view)

    async def test_mismatched_success_is_reported(self):
        async def ignored(payload):
            return fixture()
        self.control._request = ignored
        with self.assertRaises(RuntimeError):
            await self.control.apply("enabled", False)
        with self.assertRaises(RuntimeError):
            await self.control.apply("node", {"group": "overseas", "node": "香港 | 02"})

if __name__ == "__main__":
    unittest.main()
