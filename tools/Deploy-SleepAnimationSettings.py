"""Install only the staged Python changes; never overwrite a newer module."""
import ctypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import time

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / "artifacts/sleep-animation/settings-v2/integration-r2"
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
FILES = ("claw_system.py", "claw_service.py", "claw_agent.py", "claw_sleep_animation.py")
spec = importlib.util.spec_from_file_location("sleep_deploy", Path(__file__).with_name("Deploy-ScalingSettings.py"))
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    report_dir = STAGE.parent
    integration = json.loads((STAGE / "integration.json").read_text(encoding="utf-8"))
    for name in FILES:
        change = integration["changes"]["plugin/" + name]
        installed = HOME / "app" / name
        expected = change["baseSha256"]
        if (expected is None and installed.exists()) or (expected and (not installed.exists() or digest(installed) != expected)):
            raise RuntimeError("Installed module changed; prepare a fresh integration: " + name)
        compile((STAGE / "plugin" / name).read_bytes(), name, "exec")
    config_names = ("settings.json", "power.json", "magpie.json", "magpie-games.json", "magpie-game-meta.json", "magpie-presets.json", "lossless.json", "rumble.json", "scaling.json", "ecore.json", "gamepad.json")
    saved = {name: digest(HOME / "data" / name) for name in config_names if (HOME / "data" / name).exists()}
    saved["current.json"] = digest(HOME / "current.json")
    endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
    backup = HOME / "repair-backups" / time.strftime("sleep-animation-%Y%m%d-%H%M%S")
    backup.mkdir(parents=True)
    for name in FILES:
        source = HOME / "app" / name
        if source.exists():
            shutil.copy2(source, backup / name)
    try:
        # Publish first: active frontends can restart the task as soon as it exits.
        # The old agent retains its imported modules until its normal shutdown.
        for name in FILES:
            shutil.copy2(STAGE / "plugin" / name, HOME / "app" / name)
        previous = deploy.stop()
        deploy.start(previous)
        state = None
        for _ in range(100):
            time.sleep(.2)
            try:
                current = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
                if current["pid"] != endpoint["pid"]:
                    state = deploy.call("get_system_state")
                    break
            except (OSError, RuntimeError):
                pass
        if state is None or "powerAnimation" not in state:
            raise RuntimeError("Updated system API did not become ready")
    except Exception:
        for name in FILES:
            original = backup / name
            if original.exists():
                shutil.copy2(original, HOME / "app" / name)
        try:
            previous = deploy.stop()
            deploy.start(previous)
        except (OSError, RuntimeError):
            pass
        raise
    changed = [name for name, before in saved.items() if digest(HOME / "current.json" if name == "current.json" else HOME / "data" / name) != before]
    result = {"backup": str(backup), "agentPid": current["pid"], "powerAnimation": state["powerAnimation"],
              "power": state["power"], "settingsChanged": changed,
              "modules": {name: digest(HOME / "app" / name) for name in FILES}}
    (report_dir / "backend-deployment.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"agentPid": current["pid"], "settingsChanged": changed, "animationAvailable": state["powerAnimation"].get("available")}, ensure_ascii=True))


if __name__ == "__main__":
    main()
