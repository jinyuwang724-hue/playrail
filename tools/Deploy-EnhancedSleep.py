"""Deploy the reviewed agent changes with backup, stale-source checks and rollback."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
OUT = Path(sys.argv[1]).resolve()
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
integration = json.loads((OUT / "integration.json").read_text(encoding="utf-8-sig"))
changes = {name.removeprefix("plugin/"): item for name, item in integration["changes"].items() if name.startswith("plugin/")}
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
for name, change in changes.items():
    candidate = OUT / "src/plugin" / name
    installed = HOME / "app" / name
    if digest(candidate) != change["candidateSha256"]:
        raise RuntimeError("Candidate changed: " + name)
    actual = digest(installed) if installed.exists() else None
    if actual not in (change["baseSha256"], change["candidateSha256"]):
        raise RuntimeError("A newer installed file must be merged first: " + name)
    if name.endswith(".py"):
        compile(candidate.read_bytes(), name, "exec")

spec = importlib.util.spec_from_file_location("enhanced_deploy", ROOT / "tools/Deploy-ScalingSettings.py")
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)
backup = HOME / "repair-backups" / time.strftime("enhanced-sleep-%Y%m%d-%H%M%S")
backup.mkdir(parents=True)
config_names = ("settings.json", "power.json", "rumble.json", "gamepad.json", "ecore.json", "magpie.json", "lossless.json", "scaling.json")
config = {name: digest(HOME / "data" / name) for name in config_names if (HOME / "data" / name).exists()}
current = (HOME / "current.json").read_bytes()
for name in changes:
    source = HOME / "app" / name
    if source.exists():
        target = backup / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
try:
    # Publish before shutdown: UI clients can restart the task as soon as it exits.
    # The new executable has a distinct name, so no running binary is overwritten.
    for name in changes:
        target = HOME / "app" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(OUT / "src/plugin" / name, target)
    previous = deploy.stop()
    pid = deploy.start(previous)
    state = deploy.call("get_system_state")
    if (HOME / "current.json").read_bytes() != current:
        raise RuntimeError("Agent launch configuration changed")
except Exception:
    for name in changes:
        if (backup / name).exists():
            shutil.copy2(backup / name, HOME / "app" / name)
    try:
        previous = deploy.stop()
        deploy.start(previous)
    except Exception:
        pass
    raise
report = {"backup": str(backup), "agentPid": pid, "powerAnimation": state.get("powerAnimation"),
          "settingsChanged": [name for name, before in config.items() if digest(HOME / "data" / name) != before],
          "files": {name: digest(HOME / "app" / name) for name in changes}}
(OUT / "agent-deployment.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"agentPid": pid, "settingsChanged": report["settingsChanged"], "backup": str(backup)}, ensure_ascii=False))
