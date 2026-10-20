"""Back up and reset just Game Bar's settings hive; leave widget profiles in LocalState intact."""
import base64
import ctypes
import json
import os
from pathlib import Path
import sys
import time
import winreg

package = Path(os.environ["LOCALAPPDATA"]) / "Packages/Microsoft.XboxGamingOverlay_8wekyb3d8bbwe"
backup = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox/repair-backups/gamebar-20261018/settings-values.json"
api = ctypes.WinDLL("advapi32", use_last_error=True)
api.RegLoadAppKeyW.argtypes = [ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_void_p), ctypes.c_uint32, ctypes.c_uint32, ctypes.c_uint32]
api.RegDeleteTreeW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
api.RegCloseKey.argtypes = [ctypes.c_void_p]
handle = ctypes.c_void_p()
error = api.RegLoadAppKeyW(str(package / "Settings/settings.dat"), ctypes.byref(handle), 0xF003F, 0, 0)
if error:
    raise OSError(error, "Cannot load Game Bar settings")

def snapshot(key, path=""):
    children, values, _ = winreg.QueryInfoKey(key)
    rows = []
    for i in range(values):
        name, value, kind = winreg.EnumValue(key, i)
        binary = isinstance(value, bytes)
        rows.append([path, name, kind, base64.b64encode(value).decode() if binary else value, binary])
    for i in range(children):
        name = winreg.EnumKey(key, i)
        with winreg.OpenKey(key, name) as child:
            rows.extend(snapshot(child, path + "\\" + name if path else name))
    return rows

try:
    with winreg.OpenKey(handle.value, "", 0, winreg.KEY_ALL_ACCESS) as root:
        if sys.argv[1] == "disable-interceptor":
            with winreg.CreateKeyEx(root, "LocalState\\FeatureGateContainer", 0, winreg.KEY_ALL_ACCESS) as child:
                timestamp = int((time.time() + 11644473600) * 10000000)
                winreg.SetValueEx(child, "enableautomousemodepostbugfixes", 0, 100000011, b"\0" + timestamp.to_bytes(8, "little"))
            winreg.FlushKey(root)
            print("Game Bar automatic mouse input interceptor gate disabled for diagnosis")
        elif sys.argv[1] == "disable-auto-mouse":
            with winreg.CreateKeyEx(root, "LocalState", 0, winreg.KEY_ALL_ACCESS) as child:
                timestamp = int((time.time() + 11644473600) * 10000000)
                winreg.SetValueEx(child, "AutoEnableMouseMode", 0, 100000011, b"\0" + timestamp.to_bytes(8, "little"))
            winreg.FlushKey(root)
            print("Game Bar automatic mouse mode disabled")
        elif sys.argv[1] == "reset":
            if backup.exists():
                raise RuntimeError("Backup already exists; refusing to replace the original")
            rows = snapshot(root)
            backup.parent.mkdir(parents=True, exist_ok=True)
            backup.write_text(json.dumps(rows), encoding="utf-8")
            error = api.RegDeleteTreeW(int(root), "LocalState")
            if error not in (0, 2):
                raise OSError(error, "Cannot reset Game Bar local settings")
            winreg.FlushKey(root)
            print("Game Bar settings backed up and reset; widget profile preserved")
        elif sys.argv[1] == "restore":
            rows = json.loads(backup.read_text(encoding="utf-8"))
            error = api.RegDeleteTreeW(int(root), "LocalState")
            if error not in (0, 2):
                raise OSError(error, "Cannot replace the test settings")
            for path, name, kind, value, binary in rows:
                with winreg.CreateKeyEx(root, path, 0, winreg.KEY_ALL_ACCESS) as child:
                    winreg.SetValueEx(child, name, 0, kind, base64.b64decode(value) if binary else value)
            winreg.FlushKey(root)
            print("Original Game Bar settings restored")
        else:
            raise ValueError("Use reset or restore")
finally:
    api.RegCloseKey(handle)
