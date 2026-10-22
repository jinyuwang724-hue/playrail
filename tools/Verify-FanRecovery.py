"""Read-only sampling of the installed fan controller. Never changes a curve or control bit."""
import argparse
import asyncio
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
EXE = Path(os.environ["ProgramFiles"]) / "ClawToolbox/FanService/ClawFanService.exe"
CONFIG = Path(os.environ["ProgramData"]) / "ClawToolbox/FanService/service.json"


async def state():
    proc = await asyncio.create_subprocess_exec(str(EXE), "--client", stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        out, _ = await asyncio.wait_for(proc.communicate(b'{"command":"get"}\n'), 15)
        reply = json.loads(out.decode("utf-8-sig"))
        if not reply.get("ok"):
            raise RuntimeError(reply.get("error"))
        return reply["fan"]
    finally:
        if proc.returncode is None:
            proc.kill()
            await proc.wait()


async def main(seconds, steady_state=False):
    before_hash = hashlib.sha256(CONFIG.read_bytes()).hexdigest()
    report = {"started": datetime.now(timezone.utc).isoformat(), "readOnly": True,
              "test": "steady-state" if steady_state else "cooled-recovery", "samples": []}
    deadline = time.monotonic() + seconds
    while True:
        sample = await state()
        sample["sampledAt"] = datetime.now(timezone.utc).isoformat()
        report["samples"].append(sample)
        if len(report["samples"]) % 5 == 1:
            print(json.dumps({k: sample.get(k) for k in ("temperature", "ecTemperature", "auxiliaryTemperature", "target", "duty1", "duty2", "rpm1", "rpm2", "thermalRecoveryCount", "thermalRecoveryStatus", "error")}), flush=True)
        if time.monotonic() >= deadline:
            break
        await asyncio.sleep(1)
    tail = report["samples"][-10:]
    report["settingsPreserved"] = hashlib.sha256(CONFIG.read_bytes()).hexdigest() == before_hash
    report["confirmedRecoveryObserved"] = any(s.get("thermalRecoveryCount", 0) > 0 for s in report["samples"])
    report["actualOutputsFollowTarget"] = len(tail) == 10 and all(
        s.get("target") is not None and all(abs(s.get(k, -1000) - s["target"]) <= 2 for k in ("duty1", "duty2")) for s in tail)
    report["rpmSettledBelow5000"] = len(tail) == 10 and all(max(s.get("rpm1", 99999), s.get("rpm2", 99999)) < 5000 for s in tail)
    report["noErrors"] = all(not s.get("error") for s in tail)
    required = ["settingsPreserved", "actualOutputsFollowTarget", "rpmSettledBelow5000", "noErrors"]
    if not steady_state:
        required.append("confirmedRecoveryObserved")
    report["passed"] = all(report[k] for k in required)
    folder = ROOT / "diagnostics/fan-recovery-20261022"
    folder.mkdir(parents=True, exist_ok=True)
    name = "steady-state-verification.json" if steady_state else "live-verification.json"
    (folder / name).write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "samples"}), flush=True)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--seconds", type=int, default=60)
    parser.add_argument("--steady-state", action="store_true", help="Verify settled operation; does not claim a recovery event")
    args = parser.parse_args()
    raise SystemExit(asyncio.run(main(max(15, args.seconds), args.steady_state)))
