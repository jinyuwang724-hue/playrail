"""Live service/UI update checks; writes require --exercise and are restored."""
import argparse
import asyncio
import copy
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import time

HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
EXE = Path(os.environ["ProgramFiles"]) / "ClawToolbox/FanService/ClawFanService.exe"


async def service(command="get", **values):
    process = await asyncio.create_subprocess_exec(str(EXE), "--client", stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    output, _ = await asyncio.wait_for(process.communicate((json.dumps({"command": command, **values}) + "\n").encode()), 15)
    reply = json.loads(output.decode("utf-8-sig"))
    if not reply.get("ok"):
        raise RuntimeError(reply.get("error"))
    return reply["fan"]


class Agent:
    async def connect(self):
        endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
        self.reader, self.writer = await asyncio.open_connection("127.0.0.1", endpoint["port"], limit=8*1024*1024)
        self.ident = 0
        self.writer.write((json.dumps({"hello": endpoint["token"], "client": "fan-service-verifier"}) + "\n").encode())
        await self.writer.drain()
        assert json.loads(await asyncio.wait_for(self.reader.readline(), 10))["hello"] == "ok"
    async def call(self, method, *args):
        self.ident += 1
        self.writer.write((json.dumps({"id": self.ident, "method": method, "args": args}) + "\n").encode())
        await self.writer.drain()
        while True:
            reply = json.loads(await asyncio.wait_for(self.reader.readline(), 35))
            if reply.get("id") == self.ident:
                if reply.get("error"):
                    raise RuntimeError(reply["error"])
                return reply["result"]
    async def close(self):
        self.writer.close()
        await self.writer.wait_closed()


async def main(args):
    report = {"started": datetime.now(timezone.utc).isoformat(), "rebootTested": False, "sleepTested": False, "firmwareFlashed": False}
    initial = await service()
    report["before"] = initial
    agent = Agent()
    await agent.connect()
    original_power = copy.deepcopy((await agent.call("get_power_state", False))["settings"])
    try:
        assert initial["execution"] == "service" and not initial["error"], initial.get("error")
        if args.exercise:
            auto_power = copy.deepcopy(original_power)
            auto_power["fan"].update(control="auto", full=False)
            result = await agent.call("save_power", auto_power)
            snapshot = await service()
            assert snapshot["settings"]["control"] == "auto" and not snapshot["software"] and not snapshot["fullSpeed"]
            report["factoryAutoPassed"] = True
            for mode in ("quiet", "balanced", "performance"):
                next_power = copy.deepcopy(original_power)
                next_power["fan"].update(mode=mode, full=False, control="custom")
                started = time.monotonic()
                result = await agent.call("save_power", next_power)
                snapshot = await service()
                assert not result["fan"]["error"] and result["fan"]["execution"] == "service"
                assert snapshot["settings"]["mode"] == mode and snapshot["software"]
                report.setdefault("switches", []).append({"mode": mode, "milliseconds": round((time.monotonic()-started)*1000), "revision": snapshot["revision"], "target": snapshot["target"]})
            for duty in (40, 60):
                next_power = copy.deepcopy(original_power)
                next_power["fan"].update(mode="performance", full=False, control="custom")
                next_power["fan"]["curves"]["performance"] = [duty] * 6
                started = time.monotonic()
                result = await agent.call("save_power", next_power)
                assert result["fan"]["target"] == duty and not result["fan"]["error"]
                latency = round((time.monotonic()-started)*1000)
                await asyncio.sleep(6)
                samples = []
                for _ in range(3):
                    snapshot = await service()
                    assert snapshot["cpu"] == snapshot["gpu"] == [duty]*6
                    assert not snapshot["error"] and snapshot["temperature"] < 80
                    samples.append({k:snapshot[k] for k in ("rpm1","rpm2","temperature","target","revision")})
                    await asyncio.sleep(1)
                report.setdefault("edits", []).append({"duty":duty,"milliseconds":latency,"samples":samples})
            averages = [sum(s["rpm1"] for s in e["samples"])/3 for e in report["edits"]]
            report["rpmAverages"] = averages
            assert averages[1]-averages[0] > 400, "RPM did not respond to edited curve"
            before = await service()
            inactive = copy.deepcopy(before["settings"])
            inactive["curves"]["quiet"][0] = 21
            result = await service("update", revision=before["revision"], fan=inactive)
            assert result["target"] == before["target"] and result["writeCount"] == before["writeCount"]
            report["inactiveCurveEditPassed"] = True
            # The controller is not a child of the UI agent: explicitly close that
            # agent, then change the active curve through the restricted service API.
            old_pid = (await agent.call("agent_info"))["pid"]
            service_pid = result["servicePid"]
            await agent.call("shutdown")
            await agent.close()
            await asyncio.sleep(2)
            before = await service()
            detached = copy.deepcopy(before["settings"])
            detached["curves"]["performance"] = [50]*6
            result = await service("update", revision=before["revision"], fan=detached)
            assert result["servicePid"] == service_pid and result["target"] == 50 and result["cpu"] == result["gpu"] == [50]*6
            report["agentShutdown"] = {"oldAgentPid":old_pid,"sameServicePid":service_pid,"targetAfterExit":result["target"],"passed":True}
        report["passed"] = True
    finally:
        try:
            if args.exercise:
                current = await service()
                restored = await service("update", revision=current["revision"], fan=initial["settings"])
                assert restored["settings"] == initial["settings"] and not restored["error"]
                report["restored"] = restored
                task = await asyncio.create_subprocess_exec("schtasks.exe", "/run", "/tn", r"\ClawToolbox\Agent",
                    stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
                await task.wait()
                recovery = Agent()
                for attempt in range(40):
                    try:
                        await recovery.connect()
                        break
                    except (OSError, ValueError):
                        if attempt == 39:
                            raise
                        await asyncio.sleep(.5)
                try:
                    await recovery.call("get_power_state", False)
                    result = await recovery.call("save_power", original_power)
                    assert result["settings"] == original_power
                    report["agentCacheRestored"] = True
                finally:
                    await recovery.close()
        finally:
            try:
                await agent.close()
            except Exception:
                pass
            report["finished"] = datetime.now(timezone.utc).isoformat()
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n",encoding="utf-8")
            print(json.dumps({k:report[k] for k in ("passed","switches","rpmAverages","inactiveCurveEditPassed","agentShutdown","agentCacheRestored","rebootTested","sleepTested") if k in report},ensure_ascii=False))



if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exercise",action="store_true")
    parser.add_argument("--output",type=Path,default=Path("diagnostics/fan-service/live-verification.json"))
    asyncio.run(main(parser.parse_args()))
