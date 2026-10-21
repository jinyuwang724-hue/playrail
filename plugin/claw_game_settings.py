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
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as key:
            value = winreg.QueryValueEx(key, "LosslessScaling")[0]
        m = re.match(r'\s*"([^"]+)"', value) or re.match(r"\s*(\S+)", value)
        if m and os.path.isfile(m.group(1)):
            return m.group(1)
    except OSError:
        pass
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            steam = Path(winreg.QueryValueEx(key, "SteamPath")[0])
        libraries = [steam]
        vdf = steam / "steamapps" / "libraryfolders.vdf"
        if vdf.is_file():
            libraries += [Path(p.replace("\\\\", "\\")) for p in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(encoding="utf-8", errors="ignore"))]
        for lib in libraries:
            exe = lib / "steamapps" / "common" / "Lossless Scaling" / "LosslessScaling.exe"
            if exe.is_file():
                return str(exe)
    except OSError:
        pass
    return None


def lossless_pids():
    out = subprocess.run(["tasklist", "/fi", "imagename eq LosslessScaling.exe", "/fo", "csv", "/nh"],
                         capture_output=True, text=True, creationflags=NO_WINDOW).stdout
    return [int(m) for m in re.findall(r'"LosslessScaling\.exe","(\d+)"', out, re.I)]


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
    """Start LS minimized to the tray, elevated like its own StartAsAdmin start-up does."""
    rc = ctypes.windll.shell32.ShellExecuteW(None, "runas", exe, "-StartMinimized", os.path.dirname(exe), 7)
    if rc <= 32:
        raise RuntimeError("小黄鸭没能启动")
