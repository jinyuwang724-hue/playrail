"""Read installed Auto status; --exercise checks mode handoffs, then restores settings."""
import argparse
import asyncio
import copy
from datetime import datetime, timezone
import json
import os
from pathlib import Path

HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"


async def main(exercise, output):
    endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
    reader, writer = await asyncio.open_connection("127.0.0.1", endpoint["port"], limit=8 * 1024 * 1024)
    async def send(value):
        writer.write((json.dumps(value) + "\n").encode("utf-8"))
        await writer.drain()
    await send({"hello": endpoint["token"], "client": "autotdp-verifier"})
    hello = json.loads(await asyncio.wait_for(reader.readline(), 8))
    if hello.get("hello") != "ok":
        raise RuntimeError("Agent authentication failed")
    sequence = 0
    async def call(method, *args):
        nonlocal sequence
        sequence += 1
        await send({"id": sequence, "method": method, "args": args})
        while True:
            message = json.loads(await asyncio.wait_for(reader.readline(), 30))
            if message.get("id") == sequence:
                if message.get("error"):
                    raise RuntimeError(message["error"])
                return message["result"]
    def status(value):
        return {k: value.get(k) for k in ("settings", "target", "actual", "autotdp", "endurance", "error", "charger")}
    report = {"time": datetime.now(timezone.utc).isoformat(), "version": hello.get("version")}
    original = None
    try:
        before = await call("get_power_state", True)
        report["before"] = status(before)
        if exercise:
            if tuple(int(x) for x in hello["version"].split(".")) < (0, 17, 19):
                raise RuntimeError("Installed backend does not yet support AutoTDP")
            original = copy.deepcopy(before["settings"])
            backup = HOME / "repair-backups" / ("autotdp-verify-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
            backup.mkdir(parents=True, exist_ok=False)
            (backup / "settings.json").write_text(json.dumps(original, ensure_ascii=False, indent=2), encoding="utf-8")
            report["backup"] = str(backup)
            auto_settings = {**original, "enabled": True, "mode": "autotdp"}
            for mode in ("autotdp", "smart", "autotdp"):
                result = await call("save_power", {**auto_settings, "mode": mode})
                await asyncio.sleep(2)
                result = await call("get_power_state", True)
                report.setdefault("handoffs", []).append(status(result))
                assert not result["error"], result["error"]
                if mode == "autotdp":
                    assert result["autotdp"]["provider"] == "autotdp"
                    assert result["endurance"].get("control") in (None, "off")
                    assert (HOME / "data/autotdp-session.json").exists()
                    assert not (HOME / "data/intel-endurance-session.json").exists()
                    caps = result["actual"]["freq"]
                    assert all(caps[k] == 0 for k in ("ac", "dc", "ac1", "dc1")), caps
                    assert abs(result["actual"]["pl1"] - result["target"]["pl1"]) < .2
                else:
                    assert result["endurance"]["control"] == "auto"
                    assert not (HOME / "data/autotdp-session.json").exists()
            report["exerciseCompleted"] = True
        else:
            print(json.dumps(report, ensure_ascii=False, indent=2))
    finally:
        try:
            if original is not None:
                await call("save_power", original)
                report["restored"] = status(await call("get_power_state", True))
                assert report["restored"]["settings"] == original
                assert not report["restored"]["error"], report["restored"]["error"]
                report["passed"] = report.get("exerciseCompleted", False)
        finally:
            writer.close()
            await writer.wait_closed()
            if output:
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        if exercise:
            print(json.dumps({"version": report["version"], "passed": report.get("passed", False),
                              "restoredMode": report.get("restored", {}).get("settings", {}).get("mode"),
                              "output": str(output) if output else None}, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exercise", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    asyncio.run(main(args.exercise, args.output))
