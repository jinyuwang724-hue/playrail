"""Install the reviewed two-file repair with backups and normal agent restart/rollback."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import time

spec = importlib.util.spec_from_file_location("agent_deploy", Path(__file__).with_name("Deploy-ScalingSettings.py"))
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage", required=True, type=Path)
    args = parser.parse_args()
    repair = json.loads((args.stage / "repair.json").read_text(encoding="utf-8"))
    app = deploy.HOME / "app"
    if Path(repair["app"]).resolve() != app.resolve() or set(repair["changes"]) != {"claw_power.py", "claw_close_target.py"}:
        raise RuntimeError("Unexpected repair target")
    for name, hashes in repair["changes"].items():
        if digest(app / name) != hashes["baseSha256"]:
            raise RuntimeError("Installed source changed; prepare again: " + name)
        candidate = args.stage / "app" / name
        if digest(candidate) != hashes["sha256"]:
            raise RuntimeError("Candidate changed; prepare and test again: " + name)
        compile(candidate.read_bytes(), name, "exec")
    before = deploy.call("widget_state")
    protected = [deploy.HOME / "current.json"] + [deploy.HOME / "data" / name for name in
                 ("settings.json", "power.json", "game-recognition.json", "game-profiles.json")]
    original = {str(path): digest(path) for path in protected}
    backup = deploy.HOME / "repair-backups" / time.strftime("close-target-%Y%m%d-%H%M%S")
    backup.mkdir(parents=True)
    for name in repair["changes"]:
        if (app / name).exists():
            shutil.copy2(app / name, backup / name)
    for path in protected:
        if path.exists():
            shutil.copy2(path, backup / path.name)
    try:
        # Existing imported modules stay alive until normal shutdown. Publishing
        # first also lets an auto-reconnecting frontend restart with both files.
        for name in ("claw_close_target.py", "claw_power.py"):
            shutil.copy2(args.stage / "app" / name, app / name)
        previous = deploy.stop()
        pid = deploy.start(previous)
        after = deploy.call("widget_state")
        if after["power"]["settings"] != before["power"]["settings"]:
            raise RuntimeError("Power/OSD settings changed during restart")
        if any(digest(Path(path)) != value for path, value in original.items()):
            raise RuntimeError("Saved settings changed during restart")
        for name, hashes in repair["changes"].items():
            if digest(app / name) != hashes["sha256"]:
                raise RuntimeError("Installed repair does not match candidate")
    except Exception:
        for name in repair["changes"]:
            if (backup / name).exists():
                shutil.copy2(backup / name, app / name)
            else:
                (app / name).unlink(missing_ok=True)
        previous = deploy.stop()
        deploy.start(previous)
        raise
    report = {"backup": str(backup), "agentPid": pid, "savedSettingsPreserved": True,
              "changes": repair["changes"], "game": after["system"].get("game")}
    (backup / "deployment.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    (args.stage / "deployment.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report))


if __name__ == "__main__": main()
