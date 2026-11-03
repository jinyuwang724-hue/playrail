"""Roundtrip the installed core-setting API and restore the exact original value. No reboot."""
import importlib.util
import json
import os
from pathlib import Path
import winreg

ROOT = Path(__file__).resolve().parents[1]
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
spec = importlib.util.spec_from_file_location("agent_deploy", Path(__file__).with_name("Deploy-ScalingSettings.py"))
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)
KEY = r"SYSTEM\CurrentControlSet\Control\Session Manager\kernel\Kgroups\00"


def saved():
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, KEY, 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
            value, kind = winreg.QueryValueEx(key, "SmallProcessorMask")
        return {"present": True, "kind": {winreg.REG_DWORD: "dword", winreg.REG_QWORD: "qword"}[kind], "mask": format(value, "x")}
    except FileNotFoundError:
        return {"present": False, "kind": None, "mask": None}


if __name__ == "__main__":
    original = saved()
    before = deploy.call("get_power_state", False)
    assert before["ecore"]["supported"], before["ecore"]
    settings = (HOME / "data/power.json").read_bytes()
    backup = HOME / "data/ecore-backup.json"
    if backup.exists():
        # restore_ecore returns to the first snapshot, so do not use this test after
        # the user has started using the feature with a different current setting.
        assert json.loads(backup.read_text(encoding="utf-8"))["original"] == original, "Existing original backup differs from current state"
    changed = False
    try:
        try:
            deploy.call("set_ecore", "on")
            raise AssertionError("Non-boolean switch accepted")
        except RuntimeError as error:
            assert "开关格式错误" in str(error)
        assert saved() == original
        # Set the guard before the request: a lost response may still have written.
        changed = True
        off = deploy.call("set_ecore", False)["ecore"]
        assert not off["enabled"] and saved()["present"] is False
        enabled = deploy.call("set_ecore", True)["ecore"]
        assert enabled["enabled"]
        assert saved()["mask"] == format((1 << enabled["threads"]) - 1, "x")
        assert saved()["kind"] == ("qword" if enabled["threads"] >= 32 else "dword")
        assert json.loads(backup.read_text(encoding="utf-8"))["original"] == original
    finally:
        if changed:
            if backup.exists():
                deploy.call("restore_ecore")
            elif saved() != original:
                raise RuntimeError("Registry changed without backup; manual recovery required")
        assert saved() == original, "Original registry value was not restored"
    after = deploy.call("get_power_state", False)
    assert after["settings"] == before["settings"]
    assert settings == (HOME / "data/power.json").read_bytes()
    report = {"offDeletedValue": True, "onWroteFullMask": True, "invalidInputRejected": True,
              "originalRestored": original, "savedSettingsPreserved": True, "rebootPerformed": False,
              "finalState": after["ecore"]}
    (ROOT / "artifacts/ecore/live-verification.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True))
