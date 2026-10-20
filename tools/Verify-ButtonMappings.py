"""Read installed mapping state; --exercise tests safe actions and restores both profiles."""
import argparse
import asyncio
import copy
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location("button_verify_client", Path(__file__).with_name("Verify-FanService.py"))
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)


async def main(args):
    agent = client.Agent()
    await agent.connect()
    before = await agent.call("get_state")
    original = copy.deepcopy(before["settings"])
    report = {"started": datetime.now(timezone.utc).isoformat(), "before": before, "physicalButtonsTested": False,
              "visualActivationVerified": False, "verifiedLayer": "backend dispatch and Steam frontend acknowledgement"}
    try:
        if args.exercise:
            value = copy.deepcopy(original)
            value["buttonMappings"] = {"QS": {"type": "keyboard", "keys": ["F24"]},
                                       "Claw": {"type": "system", "action": "library"}}
            saved = await agent.call("save_settings", value)
            assert all(saved["settings"][k] == original[k] for k in ("enabled", "qamAction", "msiAction", "hapticsEnabled"))
            for source, label in (("QS", "keyboard"), ("Claw", "library")):
                result = await agent.call("test_button_mapping", source, False)
                assert result["ok"], result
                report[label] = result
            if before["status"].get("ready") and before["status"].get("mode") == "BigPicture":
                value["enabled"] = True
                value["steamBindings"] = {"QS": {"type": "system", "action": "steamMainMenu"},
                                           "Claw": {"type": "system", "action": "steamQuickAccess"}}
                await agent.call("save_settings", value)
                for _ in range(40):
                    state = await agent.call("get_state")
                    if state["status"].get("released") or state["status"].get("error"):
                        break
                    await asyncio.sleep(.2)
                assert state["status"].get("released"), state["status"]
                for source, label in (("QS", "steamLeft"), ("Claw", "steamRight"), ("Claw", "steamRightClose")):
                    result = await agent.call("test_button_mapping", source, True)
                    assert result["ok"], result
                    report[label] = result
            else:
                report["steamLive"] = "not connected to an active Big Picture frontend"
            value["enabled"] = original["enabled"]
            value["steamBindings"] = copy.deepcopy(original.get("steamBindings", {}))
            value["buttonMappings"]["QS"] = {"type": "system", "action": "gamebar"}
            await agent.call("save_settings", value)
            await asyncio.sleep(.3)
            result = await agent.call("test_button_mapping", "QS", False)
            assert result["ok"], result
            report["gamebar"] = result
        report["passed"] = True
    finally:
        try:
            if args.exercise:
                await agent.call("save_settings", original)
                await asyncio.sleep(.5)
                report["after"] = await agent.call("get_state")
                assert report["after"]["settings"] == original
                report["settingsRestored"] = True
        finally:
            await agent.close()
            report["finished"] = datetime.now(timezone.utc).isoformat()
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n",encoding="utf-8")
            print(json.dumps({k:report[k] for k in ("passed","keyboard","library","steamLeft","steamRight","gamebar","settingsRestored","physicalButtonsTested") if k in report},ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exercise", action="store_true")
    parser.add_argument("--output", type=Path, default=Path("diagnostics/button-mappings/live-verification.json"))
    asyncio.run(main(parser.parse_args()))
