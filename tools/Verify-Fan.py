"""Read installed fan state; --exercise checks three presets and known 40/60 duty response.

Writes are opt-in and settings are restored in finally. This verifies software
control only; table readback and a varying RPM do not prove autonomous EC curves.
"""
import argparse
import asyncio
import copy
from datetime import datetime, timezone
import json
import os
from pathlib import Path

HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"


async def main(args):
    endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
    reader, writer = await asyncio.open_connection("127.0.0.1", endpoint["port"], limit=8 * 1024 * 1024)
    async def send(value):
        writer.write((json.dumps(value) + "\n").encode())
        await writer.drain()
    await send({"hello": endpoint["token"], "client": "fan-verifier"})
    assert json.loads(await asyncio.wait_for(reader.readline(), 10))["hello"] == "ok"
    ident = 0
    async def call(method, *params):
        nonlocal ident
        ident += 1
        await send({"id": ident, "method": method, "args": params})
        while True:
            reply = json.loads(await asyncio.wait_for(reader.readline(), 30))
            if reply.get("id") == ident:
                if reply.get("error"):
                    raise RuntimeError(reply["error"])
                return reply["result"]
    def snapshot(value):
        return {"time": datetime.now(timezone.utc).isoformat(), "settings": value["settings"]["fan"],
                "fan": value["fan"], "temperature": value["live"].get("cpuTemp")}
    report = {"version": endpoint["version"], "customAutonomousCurve": "Not verified",
              "permanentPersistence": "Not established; no reboot or flash performed"}
    original = None
    try:
        before = await call("get_power_state", False)
        report["before"] = snapshot(before)
        if args.exercise:
            original = copy.deepcopy(before["settings"])
            backup = HOME / "repair-backups" / ("fan-verify-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
            backup.mkdir(parents=True, exist_ok=False)
            (backup / "settings.json").write_text(json.dumps(original, ensure_ascii=False, indent=2), encoding="utf-8")
            for mode in ("quiet", "balanced", "performance"):
                result = await call("save_power", {**original, "fan": {**original["fan"], "mode": mode, "full": False}})
                assert not result["fan"]["error"], result["fan"]["error"]
                assert result["fan"]["software"] and result["fan"]["execution"] == "software"
                assert not result["fan"]["fullSpeed"]
                report.setdefault("presets", []).append(snapshot(result))
            for duty in (40, 60):
                fan = {**original["fan"], "mode": "performance", "full": False,
                       "curves": {**original["fan"]["curves"], "performance": [duty] * 6}}
                result = await call("save_power", {**original, "fan": fan})
                assert not result["fan"]["error"], result["fan"]["error"]
                await asyncio.sleep(6)
                samples = []
                for _ in range(3):
                    result = await call("get_power_state", False)
                    assert result["fan"]["cpu"] == result["fan"]["gpu"] == [duty] * 6
                    assert result["fan"]["software"] and not result["fan"]["fullSpeed"]
                    samples.append(snapshot(result))
                    await asyncio.sleep(2)
                report.setdefault("responses", []).append({"requestedDuty": duty, "samples": samples})
            averages = [sum(s["fan"]["rpm1"] for s in row["samples"]) / 3 for row in report["responses"]]
            report["rpmAverages"] = averages
            report["softwareResponsePassed"] = averages[1] - averages[0] > 400
            assert report["softwareResponsePassed"], "40/60 duty did not produce distinct speeds"
    finally:
        try:
            if original is not None:
                result = await call("save_power", original)
                report["restored"] = snapshot(result)
                assert result["settings"] == original
                assert not result["fan"]["error"], result["fan"]["error"]
        finally:
            writer.close()
            await writer.wait_closed()
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            print(json.dumps({k: report[k] for k in ("version", "softwareResponsePassed", "rpmAverages", "customAutonomousCurve", "permanentPersistence") if k in report}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exercise", action="store_true")
    parser.add_argument("--output", type=Path, default=Path("diagnostics/fan-software-live.json"))
    asyncio.run(main(parser.parse_args()))
