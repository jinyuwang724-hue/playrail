"""Deploy only the FSE settings module and use the agent's normal restart path."""
import hashlib
import ctypes
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"


def call(method, args=()):
    endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
    with socket.create_connection(("127.0.0.1", endpoint["port"]), timeout=20) as connection:
        stream = connection.makefile("rwb")
        stream.write((json.dumps({"hello": endpoint["token"], "client": "fse-settings-repair"}) + "\n").encode())
        stream.flush()
        if json.loads(stream.readline()).get("hello") != "ok":
            raise RuntimeError("Agent authentication failed")
        stream.write((json.dumps({"id": 1, "method": method, "args": list(args)}) + "\n").encode())
        stream.flush()
        while True:
            line = stream.readline()
            if not line:
                raise RuntimeError("Agent disconnected before replying")
            response = json.loads(line)
            if response.get("id") == 1:
                if "error" in response:
                    raise RuntimeError(response["error"])
                return response.get("result")


if __name__ == "__main__":
    source = ROOT / "plugin/claw_settings.py"
    target = HOME / "app/claw_settings.py"
    backup = HOME / "repair-backups" / time.strftime("fse-settings-%Y%m%d-%H%M%S")
    backup.mkdir(parents=True)
    shutil.copy2(target, backup / target.name)
    endpoint = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
    current = (HOME / "current.json").read_bytes()
    shutil.copy2(source, target)
    try:
        call("restart_agent")
    except RuntimeError as error:
        # The packaged desktop host can prohibit spawning a breakaway process. In that case
        # shut down normally, wait for cleanup, then use the existing sign-in task.
        if "WinError 5" not in str(error):
            raise
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel.OpenProcess(0x00100000, False, endpoint["pid"])
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            call("shutdown")
            if kernel.WaitForSingleObject(handle, 30000) != 0:
                raise RuntimeError("Agent cleanup did not finish")
        finally:
            kernel.CloseHandle(handle)
        subprocess.run(["schtasks.exe", "/run", "/tn", r"\ClawToolbox\Agent"], check=True,
                       capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW, timeout=10)
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        try:
            fresh = json.loads((HOME / "agent.json").read_text(encoding="utf-8"))
            if fresh["pid"] != endpoint["pid"]:
                view = call("get_settings_state")
                break
        except (OSError, ValueError, RuntimeError):
            pass
        time.sleep(.25)
    else:
        raise RuntimeError(f"Agent did not restart; original module saved at {backup}")
    if (HOME / "current.json").read_bytes() != current:
        raise RuntimeError("Installed agent version changed during repair")
    report = {"backup": str(backup), "agentPid": fresh["pid"], "home": view["home"],
              "moduleHash": hashlib.sha256(target.read_bytes()).hexdigest()}
    (backup / "deployment.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True))
