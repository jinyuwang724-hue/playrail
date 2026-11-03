"""Summarize the local SleepStudy without confusing SoC wake with display-on."""
from collections import Counter
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1] / "artifacts/sleep-animation/sleep-diagnostics-20261026"
tree = ET.parse(ROOT / "sleepstudy.xml")
ns = {"n": "http://schemas.microsoft.com/sleepstudy/2012"}
states = list(tree.getroot().find("n:ScenarioInstances", ns))
sessions = []
for state in states:
    if state.get("Type") != "Sleep":
        continue
    attrs = state.attrib
    ticks = int(attrs.get("Duration", 0))
    duration = ticks / 10_000_000
    sessions.append({"at": attrs.get("LocalTimestamp"), "minutes": round(duration / 60, 2),
                     "exit": attrs.get("CsExitReason"), "ac": attrs.get("Ac") == "1",
                     "hardwareLowPowerPercent": round(100 * int(attrs.get("HwLowPowerStateTime", 0)) / ticks, 2) if ticks else None,
                     "dischargeMWh": int(attrs.get("Discharge", 0)), "hibernateAfterSeconds": attrs.get("HibernateTimeoutInSec")})
recent = [{key: state.get(key) for key in ("Type", "LocalTimestamp", "ExitLocalTimestamp", "Duration", "EntryReason", "ExitReason", "CsExitReason")}
          for state in states if (state.get("LocalTimestamp") or "") >= "2026-10-23T17:25:00"]
result = {"sleepSessions": len(sessions), "latestSleepSessions": sessions[-5:], "recentTransitions": recent,
          "stateCounts": dict(Counter(state.get("Type") for state in states))}
(ROOT / "summary.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"latestSleepSessions": sessions[-3:], "recentTransitions": recent[-12:]}, ensure_ascii=True))
