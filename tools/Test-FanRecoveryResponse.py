"""Temporarily exercise the saved active fan profile at 76%, then restore it.

Does not change power, temperature thresholds, protection output or full-speed mode.
The saved settings are restored in finally, including on a temperature abort.
"""
import asyncio
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("fan_verify", ROOT / "tools/Verify-FanRecovery.py")
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)

async def update(fan, revision):
    proc = await asyncio.create_subprocess_exec(str(verify.EXE), "--client", stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, creationflags=0x08000000)
    try:
        out, _ = await asyncio.wait_for(proc.communicate((json.dumps({"command":"update","revision":revision,"fan":fan})+"\n").encode()),15)
        reply=json.loads(out.decode("utf-8-sig"))
        if not reply.get("ok"): raise RuntimeError(reply.get("error"))
        return reply["fan"]
    finally:
        if proc.returncode is None: proc.kill(); await proc.wait()

def restored_response(samples):
    tail=samples[-10:]
    return len(tail)==10 and all(abs(s["duty1"]-s["target"])<=2 and abs(s["duty2"]-s["target"])<=2
        and max(s["rpm1"],s["rpm2"])<5000 and not s.get("error") for s in tail)

async def main():
    before=await verify.state()
    original=copy.deepcopy(before["settings"])
    power=Path(os.environ["LOCALAPPDATA"])/"ClawToolbox/data/power.json"
    power_hash=hashlib.sha256(power.read_bytes()).hexdigest()
    if original["full"] or original["control"]!="custom" or original["mode"]=="performance":
        raise RuntimeError("Test requires the current quiet/balanced custom profile, with full-speed off")
    if max(before["temperature"],before["ecTemperature"])>80 or before.get("auxiliaryTemperature",255)>60:
        raise RuntimeError("Cooling conditions not satisfied")
    report={"test":"76-percent-response-and-restore","before":before,"cap":[],"restored":[]}
    try:
        candidate=copy.deepcopy(original);candidate["curves"][original["mode"]]=[76]*6
        await update(candidate,before["revision"])
        for _ in range(22):
            sample=await verify.state();report["cap"].append(sample)
            if sample.get("error") or max(sample["temperature"],sample["ecTemperature"])>85:
                raise RuntimeError("Temperature or controller guard interrupted the test")
            if len(report["cap"])%5==1:print(json.dumps({"phase":"76%",**{k:sample.get(k) for k in ("temperature","auxiliaryTemperature","target","duty1","duty2","rpm1","rpm2")}}),flush=True)
            await asyncio.sleep(1)
    except Exception as error:
        report["error"]=str(error)
    finally:
        current=await verify.state()
        await update(original,current["revision"])
    # Wait for sustained feedback; a fresh target write can take one EC cycle to appear.
    for _ in range(60):
        sample=await verify.state();report["restored"].append(sample)
        if len(report["restored"])%5==1:print(json.dumps({"phase":"restored",**{k:sample.get(k) for k in ("temperature","auxiliaryTemperature","target","duty1","duty2","rpm1","rpm2")}}),flush=True)
        if len(report["restored"])>=15 and restored_response(report["restored"]):break
        await asyncio.sleep(1)
    cap=report["cap"][-5:];tail=report["restored"][-10:]
    report["capResponseConfirmed"]=len(cap)==5 and all(s["duty1"]==s["duty2"]==76 and 4200<s["rpm1"]<5000 and 4200<s["rpm2"]<5000 for s in cap)
    report["restoredResponseConfirmed"]=restored_response(report["restored"])
    report["protectionPreserved"]=all(s["cpuTable"][7]==s["gpuTable"][7]==94 and not s["fullSpeed"] for s in report["cap"]+report["restored"])
    report["settingsRestored"]=report["restored"][-1]["settings"]==original
    report["powerSettingsPreserved"]=hashlib.sha256(power.read_bytes()).hexdigest()==power_hash
    report["passed"]=not report.get("error") and all(report[k] for k in ("capResponseConfirmed","restoredResponseConfirmed","protectionPreserved","settingsRestored","powerSettingsPreserved"))
    path=ROOT/"diagnostics/fan-recovery-20261022/response-verification.json"
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
    print(json.dumps({k:v for k,v in report.items() if k not in ("before","cap","restored")}),flush=True)
    return 0 if report["passed"] else 1

if __name__=="__main__":raise SystemExit(asyncio.run(main()))
