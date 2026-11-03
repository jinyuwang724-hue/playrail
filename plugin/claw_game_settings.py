"""Per-game settings for the library's game settings page.

Steam's own per-game properties (launch options, overlay, Steam Cloud, favourite / hidden, verify files) go
through the running Steam client's UI interface (claw_steam_cdp), like Steam's Properties dialog does.
小黄鸭 = Lossless Scaling: a game gets its own LS profile (a copy of LS's default profile with Path = the game's
exe and AutoScale on), so LS scales it by itself whenever that game is in front. LS keeps its settings in memory and
writes them back when it exits, so it is stopped (forcefully, through the elevated power helper), the file
edited, and LS started again minimized (it runs elevated; this machine elevates without a prompt).
Magpie per game lives in claw_magpie (remembered games, auto-start after a launch).
"""
import ctypes
import json
import os
from pathlib import Path
import re
import subprocess
import struct
import time
import hashlib
from ctypes import wintypes as W
import winreg
import xml.etree.ElementTree as ET

NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
LS_SETTINGS = Path(os.environ.get("LOCALAPPDATA", "")) / "Lossless Scaling" / "Settings.xml"
SKIP_EXE = re.compile(r"(?i)(unins|setup|install|redist|vcredist|vc_redist|dxsetup|crash|report|launcher|helper|"
                      r"prereq|updater|easyanticheat|eac_|battleye|be_service|ue4prereq|dotnet|directx)")


def steam_game_exe(folder):
    """The exe that is most likely the game in a Steam install folder: a UE *-Shipping.exe if there is one,
    else the biggest exe that is not an installer / launcher / crash reporter (3 levels deep at most)."""
    root = Path(folder or "")
    if not root.is_dir():
        return None
    found = []
    for depth in range(4):
        for exe in root.glob("/".join(["*"] * depth + ["*.exe"])):
            if SKIP_EXE.search(exe.name):
                continue
            try:
                found.append((exe, exe.stat().st_size))
            except OSError:
                pass
    shipping = [f for f in found if f[0].name.lower().endswith("-shipping.exe")]
    pool = shipping or found
    return str(max(pool, key=lambda f: f[1])[0]) if pool else None


# ---------------- Lossless Scaling ----------------

def lossless_exe():
    """Find an installed Steam copy using its manifest, including secondary libraries."""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            steam = Path(winreg.QueryValueEx(key, "SteamPath")[0])
        libraries = [steam]
        vdf = steam / "steamapps" / "libraryfolders.vdf"
        if vdf.is_file():
            libraries += [Path(p.replace("\\\\", "\\")) for p in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(encoding="utf-8", errors="ignore"))]
        for lib in libraries:
            try:
                manifest = (lib / "steamapps/appmanifest_993090.acf").read_text(encoding="utf-8-sig")
                appid = re.search(r'"appid"\s+"(\d+)"', manifest)
                folder = re.search(r'"installdir"\s+"([^"\\/]+)"', manifest)
                if not appid or appid[1] != "993090" or not folder or folder[1] in (".", ".."):
                    continue
                return checked_lossless_exe(str(lib / "steamapps/common" / folder[1] / "LosslessScaling.exe"))
            except (OSError, ValueError):
                continue
    except OSError:
        pass
    return None


def checked_lossless_exe(value):
    if not isinstance(value, str) or not value or len(value) > 32767 or not os.path.isabs(value):
        raise ValueError("请选择小黄鸭的 LosslessScaling.exe")
    exe = Path(value)
    if exe.name.lower() != "losslessscaling.exe":
        raise ValueError("请选择 LosslessScaling.exe，不能选择其他程序或快捷方式")
    try:
        with exe.open("rb") as stream:
            header = stream.read(64)
            if len(header) != 64 or header[:2] != b"MZ":
                raise ValueError("这个文件不是有效的小黄鸭程序")
            stream.seek(struct.unpack_from("<I", header, 60)[0])
            if stream.read(4) != b"PE\0\0":
                raise ValueError("这个文件不是有效的小黄鸭程序")
    except OSError as error:
        raise ValueError("小黄鸭程序不存在或无法读取，请重新选择") from error
    return str(exe.resolve())


class LosslessConfig:
    """Find the user's installed application. Reading never launches LS or edits profiles."""
    def __init__(self, data):
        self.path = Path(data) / "lossless.json"
        self._detected = None
        self._detect_at = 0

    def saved(self):
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"))
            return value if isinstance(value, dict) else {}
        except (OSError, ValueError):
            return {}

    def exe(self):
        config = self.saved()
        for candidate in (config.get("exe"), self._detected):
            try: return checked_lossless_exe(candidate)
            except ValueError: pass
        if time.monotonic() - self._detect_at < 10: return None
        self._detect_at = time.monotonic()
        candidates = [p["path"] for p in lossless_processes() if p["path"]]
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as key:
                value = winreg.QueryValueEx(key, "LosslessScaling")[0]
                m = re.match(r'\s*"([^"]+)"', value) or re.match(r"\s*(\S+)", value)
                if m: candidates.append(m[1])
        except OSError: pass
        candidates.append(lossless_exe())
        for candidate in candidates:
            try:
                self._detected = checked_lossless_exe(candidate)
                return self._detected
            except ValueError: pass
        return None

    def view(self, refresh=False):
        if refresh:
            self._detected = None
            self._detect_at = 0
        config, exe = self.saved(), self.exe()
        ready, revision = False, "missing"
        if exe:
            try:
                raw = LS_SETTINGS.read_bytes()
                revision = hashlib.sha256(raw).hexdigest()
                _, profiles = _profiles(ET.ElementTree(ET.fromstring(raw)))
                ready = any(not (p.findtext("Path") or "").strip() for p in profiles)
            except (ET.ParseError, OSError):
                pass
        return {"mode": config.get("mode", ""), "exe": config.get("exe", ""),
                "configured": bool(exe), "installed": bool(exe), "running": bool(self.pids(exe)) if exe else False,
                "settingsReady": ready, "detectedExe": exe or "", "revision": revision}

    def pids(self, exe=None):
        exe = exe or self.exe()
        if not exe: return []
        return [p["pid"] for p in lossless_processes() if not p["path"] or _same_exe(p["path"], exe)]

    def configure(self, mode, exe=None):
        if mode == "steam":
            exe = lossless_exe()
            if not exe:
                raise ValueError("没有找到已安装的小黄鸭，请先在 Steam 安装，或选择本地 EXE")
        elif mode != "local":
            raise ValueError("请选择 Steam 检测或本地 EXE")
        value = {"mode": mode, "exe": checked_lossless_exe(exe)}
        temp = self.path.with_suffix(".tmp")
        temp.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
        os.replace(temp, self.path)
        return self.view()

    def clear(self):
        self.path.unlink(missing_ok=True)
        self._detected = None; self._detect_at = 0
        return self.view()


def lossless_pids():
    return [p["pid"] for p in lossless_processes()]


def lossless_processes():
    """A cheap process snapshot, not a tasklist process on every settings-page refresh."""
    class Entry(ctypes.Structure):
        _fields_ = [("size", W.DWORD), ("usage", W.DWORD), ("pid", W.DWORD), ("heap", ctypes.c_size_t),
                    ("module", W.DWORD), ("threads", W.DWORD), ("parent", W.DWORD), ("priority", W.LONG),
                    ("flags", W.DWORD), ("name", W.WCHAR * 260)]
    k = ctypes.WinDLL("kernel32", use_last_error=True)
    k.CreateToolhelp32Snapshot.argtypes, k.CreateToolhelp32Snapshot.restype = [W.DWORD, W.DWORD], W.HANDLE
    k.Process32FirstW.argtypes = k.Process32NextW.argtypes = [W.HANDLE, ctypes.POINTER(Entry)]
    k.OpenProcess.argtypes, k.OpenProcess.restype = [W.DWORD, W.BOOL, W.DWORD], W.HANDLE
    k.QueryFullProcessImageNameW.argtypes = [W.HANDLE, W.DWORD, W.LPWSTR, ctypes.POINTER(W.DWORD)]
    k.CloseHandle.argtypes = [W.HANDLE]
    snapshot = k.CreateToolhelp32Snapshot(2, 0)
    if snapshot in (None, ctypes.c_void_p(-1).value): return []
    result, entry = [], Entry(); entry.size = ctypes.sizeof(entry)
    try:
        more = k.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            if entry.name.lower() == "losslessscaling.exe":
                handle, path = k.OpenProcess(0x1000, False, entry.pid), ""
                if handle:
                    try:
                        buf, size = ctypes.create_unicode_buffer(32768), W.DWORD(32768)
                        if k.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)): path = buf.value
                    finally: k.CloseHandle(handle)
                result.append(dict(pid=entry.pid, path=path))
            more = k.Process32NextW(snapshot, ctypes.byref(entry))
    finally: k.CloseHandle(snapshot)
    return result


def _profiles(tree):
    root = tree.getroot()
    holder = root.find("GameProfiles")
    return holder, (holder.findall("Profile") if holder is not None else [])


def _same_exe(a, b):
    a, b = (a or "").strip().strip('"'), (b or "").strip().strip('"')
    return bool(a) and os.path.normcase(os.path.abspath(a)) == os.path.normcase(os.path.abspath(b)) if os.path.isabs(a) and os.path.isabs(b) \
        else bool(a) and a.lower() == b.lower()


def lossless_enabled(exe):
    if not exe or not LS_SETTINGS.is_file():
        return False
    try:
        _, profiles = _profiles(ET.parse(LS_SETTINGS))
    except (ET.ParseError, OSError):
        return False
    return any(_same_exe(p.findtext("Path"), exe) and p.findtext("AutoScale") == "true" for p in profiles)


def lossless_write(exe, name, enabled):
    """Add / remove this game's auto-scale profile. LS must not be running (it would write its copy back)."""
    tree = ET.parse(LS_SETTINGS)
    holder, profiles = _profiles(tree)
    if holder is None:
        raise RuntimeError("小黄鸭的设置文件不认识")
    mine = [p for p in profiles if _same_exe(p.findtext("Path"), exe)]
    for p in mine:
        holder.remove(p)
    if enabled:
        base = next((p for p in profiles if not (p.findtext("Path") or "").strip()), profiles[0] if profiles else None)
        if base is None:
            raise RuntimeError("小黄鸭还没有默认配置，先打开一次小黄鸭")
        profile = ET.fromstring(ET.tostring(base))
        for tag in ("Title", "Path", "AutoScale", "AutoScaleDelay"):
            if profile.find(tag) is None:
                ET.SubElement(profile, tag)
        # Path goes right after Title (where LS's own serializer puts it)
        path_el = profile.find("Path")
        profile.remove(path_el)
        profile.insert(1, path_el)
        profile.find("Title").text = name or Path(exe).stem
        path_el.text = exe
        profile.find("AutoScale").text = "true"
        profile.find("AutoScaleDelay").text = profile.findtext("AutoScaleDelay") or "0"
        holder.append(profile)
    ET.indent(tree, space="  ")
    # Keep LS's own header (declaration and the root's namespace attributes), which ElementTree drops
    original = LS_SETTINGS.read_text(encoding="utf-8-sig")
    head = re.match(r"(?s)\s*(<\?xml[^>]*\?>\s*)?<Settings\b[^>]*>", original)
    header = head.group(0).strip() if head else '<?xml version="1.0" encoding="utf-8"?>\n<Settings>'
    body = re.sub(r"^<Settings\b[^>]*>", lambda _: header, ET.tostring(tree.getroot(), encoding="unicode"))
    tmp = LS_SETTINGS.with_suffix(".claw-tmp")
    tmp.write_bytes(body.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))
    os.replace(tmp, LS_SETTINGS)


def lossless_start(exe):
    """Reload the installed app in the background, respecting its own administrator preference."""
    try: admin = ET.parse(LS_SETTINGS).getroot().findtext("StartAsAdmin") == "true"
    except (OSError, ET.ParseError): admin = False
    rc = ctypes.windll.shell32.ShellExecuteW(None, "runas" if admin else "open", exe, "-StartMinimized", os.path.dirname(exe), 7)
    if rc <= 32:
        raise RuntimeError("小黄鸭没能启动")
