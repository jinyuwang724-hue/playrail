"""Install only the scaling settings modules, backing up and using the normal agent shutdown."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
FILES = ("claw_agent.py", "claw_service.py", "claw_game_settings.py", "claw_lossless.py", "claw_magpie.py", "claw_system.py")


def call(method, *args):
    endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
    with socket.create_connection(("127.0.0.1", endpoint["port"]), timeout=15) as connection:
        stream = connection.makefile("rwb")
        stream.write((json.dumps({"hello": endpoint["token"], "client": "scaling-settings"}) + "\n").encode())
        stream.flush()
        if json.loads(stream.readline()).get("hello") != "ok":
            raise RuntimeError("Agent authentication failed")
        stream.write((json.dumps({"id": 1, "method": method, "args": args}) + "\n").encode())
        stream.flush()
        while line := stream.readline():
            result = json.loads(line)
            if result.get("id") == 1:
                if "error" in result:
                    raise RuntimeError(result["error"])
                return result.get("result")
        raise RuntimeError("Agent disconnected")


def stop():
    endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x00100000, False, endpoint["pid"])
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        call("shutdown")
        if kernel.WaitForSingleObject(handle, 25000) != 0:
            raise RuntimeError("Agent cleanup did not finish")
    finally:
        kernel.CloseHandle(handle)
    return endpoint["pid"]


def start(previous):
    subprocess.run(["schtasks.exe", "/run", "/tn", r"\ClawToolbox\Agent"], check=True,
                   capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW, timeout=10)
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        try:
            endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
            if endpoint["pid"] != previous:
                call("get_magpie_prefs")
                return endpoint["pid"]
        except (OSError, ValueError, RuntimeError):
            pass
        time.sleep(.25)
    raise RuntimeError("Agent did not restart")


if __name__ == "__main__":
    backup = HOME / "repair-backups" / time.strftime("scaling-settings-%Y%m%d-%H%M%S")
    backup.mkdir(parents=True)
    current = (HOME / "current.json").read_bytes()
    for file in FILES:
        compile((ROOT / "plugin" / file).read_bytes(), file, "exec")
        if (HOME / "app" / file).is_file(): shutil.copy2(HOME / "app" / file, backup / file)
    try:
        # Connected clients can auto-start the task as soon as the old agent exits.
        # Publish all modules while the old process still has its imported copies in memory.
        for file in FILES:
            shutil.copy2(ROOT / "plugin" / file, HOME / "app" / file)
        previous = stop()
        pid = start(previous)
        config, catalog = call("get_lossless_config"), call("get_magpie_catalog")
        assert len(catalog["effects"]) > 0 and len(catalog["presets"]) >= 7
        assert current == (HOME / "current.json").read_bytes()
    except Exception:
        for file in FILES:
            if (backup / file).is_file(): shutil.copy2(backup / file, HOME / "app" / file)
            else: (HOME / "app" / file).unlink(missing_ok=True)
        try:
            previous = stop()
        except (OSError, ValueError, RuntimeError):
            if not call("get_magpie_prefs"):
                raise
            raise RuntimeError("Could not stop candidate; backups retained at " + str(backup))
        start(previous)
        raise
    report = {"backup": str(backup), "pid": pid, "configured": config["configured"],
              "effects": len(catalog["effects"]), "presets": len(catalog["presets"]),
              "hashes": {f: hashlib.sha256((HOME / "app" / f).read_bytes()).hexdigest() for f in FILES}}
    (backup / "deployment.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True))
