"""Install a prepared core-settings integration over the matching live agent.

Preserves the deployed power helper revision, OSD modules and saved settings;
backs up changed files and rolls back the agent if validation fails.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import time

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / "artifacts/ecore/integration"
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
FILES = ("claw_power.py", "claw_service.py", "claw_agent.py", "plugin.json")
spec = importlib.util.spec_from_file_location("agent_deploy", Path(__file__).with_name("Deploy-ScalingSettings.py"))
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    integration = json.loads((STAGE / "integration.json").read_text(encoding="utf-8"))
    for file in FILES:
        if digest(HOME / "app" / file) != integration["changes"]["plugin/" + file]["baseSha256"]:
            raise RuntimeError("Installed source changed; prepare a fresh integration: " + file)
        if file.endswith(".py"):
            compile((STAGE / "plugin" / file).read_bytes(), file, "exec")
    manifest = json.loads((STAGE / "plugin/plugin.json").read_text(encoding="utf-8"))
    binary = "ClawPower-" + manifest["version"] + "-r" + str(manifest["powerHelperRevision"]) + ".exe"
    candidate = ROOT / "artifacts/ecore" / binary
    if not candidate.is_file():
        raise RuntimeError("Compile the current helper candidate first")
    before = deploy.call("get_power_state", False)
    settings_file = HOME / "data/power.json"
    saved_settings = settings_file.read_bytes()
    current = (HOME / "current.json").read_bytes()
    backup = HOME / "repair-backups" / time.strftime("ecore-%Y%m%d-%H%M%S")
    backup.mkdir(parents=True)
    for file in FILES:
        shutil.copy2(HOME / "app" / file, backup / file)
    for file in ("ecore.json", "ecore-backup.json"):
        target = HOME / "data" / file
        if target.exists():
            shutil.copy2(target, backup / file)
    shutil.copy2(settings_file, backup / "power.json")
    # New helper name avoids replacing a running r8 executable.
    shutil.copy2(candidate, HOME / "app/bin" / binary)
    try:
        for file in FILES:
            shutil.copy2(STAGE / "plugin" / file, HOME / "app" / file)
        previous = deploy.stop()
        pid = deploy.start(previous)
        after = deploy.call("get_power_state", False)
        assert "supported" in after["ecore"] and "status" in after["ecore"], after["ecore"]
        assert after["settings"] == before["settings"], "Saved power/OSD settings changed"
        assert saved_settings == settings_file.read_bytes(), "Power configuration changed"
        assert current == (HOME / "current.json").read_bytes(), "Current release pointer changed"
        assert digest(HOME / "app/bin" / binary) == digest(candidate)
    except Exception:
        for file in FILES:
            shutil.copy2(backup / file, HOME / "app" / file)
        previous = deploy.stop()
        for file in ("ecore.json", "ecore-backup.json"):
            if (backup / file).exists():
                shutil.copy2(backup / file, HOME / "data" / file)
        deploy.start(previous)
        raise
    report = {"backup": str(backup), "agentPid": pid, "helper": binary, "savedSettingsPreserved": True,
              "ecore": after["ecore"], "hashes": {file: digest(HOME / "app" / file) for file in FILES}}
    (ROOT / "artifacts/ecore/deployment.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True))


if __name__ == "__main__":
    main()
