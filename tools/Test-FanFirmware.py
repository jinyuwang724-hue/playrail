"""Controlled EC fan execution test; writes safe curves and restores them in finally.

The installed diagnostic lease pauses host fan control, has a 30-second heartbeat
timeout and an 82 C CPU cutoff. Does not reboot, flash, or edit saved power settings.
"""
import argparse
import asyncio
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time

HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
CURVES = {"A": [40, 40, 40, 40, 75, 90, 100], "B": [60, 60, 60, 60, 90, 95, 100]}


def evaluate_phase(phase):
    samples = phase["samples"]
    valid = [s for s in samples if s["fan"]["software"] and not s["fan"]["fullSpeed"]]
    unchanged = all(s["writes"] == phase["setupWrites"] and s["softwareWrites"] == phase["hostWrites"]
                    and s["fan"]["cpu"] == phase["curve"][1:] and s["fan"]["gpu"] == phase["curve"][1:]
                    for s in samples)
    temps = [s["fan"]["ecTemperature"] for s in samples]
    span = max(temps) - min(temps) if temps else 0
    boundaries = [t for t in (47, 50, 57, 64, 71, 78) if temps and min(temps) < t <= max(temps)]
    response = []
    for key in ("rpm1", "rpm2"):
        low = [s["fan"][key] for s in samples if s["fan"]["ecTemperature"] <= min(temps) + 1] if temps else []
        high = [s["fan"][key] for s in samples if s["fan"]["ecTemperature"] >= max(temps) - 1] if temps else []
        response.append(statistics.median(high) - statistics.median(low) if low and high else 0)
    return {"noHostWrites": unchanged, "controlRetained": len(valid) == len(samples) and bool(samples),
            "ecTemperatureSpan": span, "crossedBoundaries": boundaries, "rpmResponse": response,
            "passed": unchanged and len(valid) == len(samples) and bool(samples)
                      and span >= 6 and len(boundaries) >= 2 and min(response) >= 400}


async def run(args):
    endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
    reader, writer = await asyncio.open_connection("127.0.0.1", endpoint["port"], limit=8 * 1024 * 1024)
    async def send(value):
        writer.write((json.dumps(value) + "\n").encode())
        await writer.drain()
    await send({"hello": endpoint["token"], "client": "fan-firmware-test"})
    if json.loads(await asyncio.wait_for(reader.readline(), 10)).get("hello") != "ok":
        raise RuntimeError("Backend authentication failed")
    ident = 0
    async def call(method, *params):
        nonlocal ident
        ident += 1
        await send({"id": ident, "method": method, "args": params})
        while True:
            line = await asyncio.wait_for(reader.readline(), 45)
            if not line:
                raise RuntimeError("Backend disconnected; diagnostic lease will time out")
            value = json.loads(line)
            if value.get("id") == ident:
                if value.get("error"):
                    raise RuntimeError(value["error"])
                return value["result"]
    report = {"started": datetime.now(timezone.utc).isoformat(), "version": endpoint["version"],
              "stage2": "NOT_PASSED", "stage3": "NOT_RUN_STAGE2_REQUIRED", "phases": [],
              "criteria": {"temperatureSpan": 6, "crossedTemperatureBoundaries": 2,
                           "rpmResponseBothFans": 400, "hostSpeedWrites": 0}}
    def save():
        args.output.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.output.with_suffix(".tmp")
        temporary.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, args.output)
    workers = []
    leased = False
    try:
        original = await call("get_power_state", False)
        report["originalSettings"] = original["settings"]
        report["begin"] = await call("fan_test", "begin")
        leased = True
        save()
        transports = ("legacy", "set_ap") if args.transport == "both" else (args.transport,)
        for transport in transports:
            # First isolate control transport at idle, without a thermal load.
            averages = []
            for duty in (40, 60):
                setup = await call("fan_test", "apply", {"cpu": [duty] * 7, "gpu": [duty] * 7,
                                                         "custom": True, "transport": transport})
                phase = {"transport": transport, "name": "constant-" + str(duty), "curve": [duty] * 7,
                         "setupWrites": setup["writes"], "hostWrites": setup["softwareWrites"], "samples": []}
                report["phases"].append(phase)
                for second in range(12):
                    sample = await call("fan_test", "sample")
                    sample["elapsed"] = second
                    phase["samples"].append(sample)
                    save()
                    await asyncio.sleep(1)
                averages.append([statistics.mean(s["fan"][key] for s in phase["samples"][-5:]) for key in ("rpm1", "rpm2")])
                print(json.dumps({"transport": transport, "duty": duty, "rpm": averages[-1]}, ensure_ascii=True), flush=True)
            distinct = min(b - a for a, b in zip(*averages)) >= 400
            report.setdefault("constantResponse", {})[transport] = {"rpmAverages": averages, "passed": distinct}
            for name, curve in CURVES.items():
                setup = await call("fan_test", "apply", {"cpu": curve, "gpu": curve, "custom": True, "transport": transport})
                phase = {"transport": transport, "name": name, "curve": curve,
                         "setupWrites": setup["writes"], "hostWrites": setup["softwareWrites"], "samples": []}
                report["phases"].append(phase)
                for second in range(10 + args.load_seconds + args.cool_seconds):
                    if second == 10:
                        code = "import time,math\nend=time.monotonic()+" + str(args.load_seconds) + "\nx=1.0\nwhile time.monotonic()<end:\n x=math.sqrt(x+12345.6789)\n"
                        workers = [subprocess.Popen([sys.executable, "-c", code], stdin=subprocess.DEVNULL,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
                                   for _ in range(2)]
                    sample = await call("fan_test", "sample")
                    sample["elapsed"] = second
                    phase["samples"].append(sample)
                    save()
                    await asyncio.sleep(1)
                for worker in workers:
                    worker.wait(timeout=5)
                workers = []
                phase["evaluation"] = evaluate_phase(phase)
                print(json.dumps({"transport": transport, "curve": name, **phase["evaluation"]}), flush=True)
                save()
            matching = [p for p in report["phases"] if p["transport"] == transport and p["name"] in CURVES]
            passed = distinct and all(p["evaluation"]["passed"] for p in matching)
            report.setdefault("transportResults", {})[transport] = "PASS" if passed else "FAIL"
        report["stage2"] = "PASS" if "PASS" in report["transportResults"].values() else "FAIL"
        if report["stage2"] == "PASS":
            report["stage3"] = "NOT_RUN_REBOOT_REQUIRED"
    except Exception as error:
        report["error"] = str(error)
        report["stage2"] = "FAIL"
        print("Test failed: " + str(error), flush=True)
    finally:
        for worker in workers:
            if worker.poll() is None:
                worker.terminate()
            worker.wait(timeout=5)
        try:
            if leased:
                report["end"] = await call("fan_test", "end")
                restored = await call("get_power_state", False)
                report["restored"] = {"settings": restored["settings"], "fan": restored["fan"]}
                if restored["settings"] != report["originalSettings"] or restored["fan"]["error"]:
                    raise RuntimeError("Final settings or fan status did not restore cleanly")
        finally:
            writer.close()
            await writer.wait_closed()
            save()
    print(json.dumps({"stage2": report["stage2"], "stage3": report["stage3"], "results": report.get("transportResults")}), flush=True)
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--transport", choices=("legacy", "set_ap", "both"), default="both")
    parser.add_argument("--load-seconds", type=int, choices=range(10, 31), default=18)
    parser.add_argument("--cool-seconds", type=int, choices=range(10, 61), default=20)
    parser.add_argument("--output", type=Path, default=Path("diagnostics/fan-firmware-verification.json"))
    outcome = asyncio.run(run(parser.parse_args()))
    sys.exit(0 if outcome["stage2"] == "PASS" else 2)
