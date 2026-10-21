"""Which game a Game Bar screenshot came from.

Game Bar saves captures as "<window title> <y>_<m>_<d> <h>_<mi>_<s>.png" with no game information,
and window titles often differ from the library's names (English title, version suffix, launcher).
The agent watches the Captures folder; for each new image it records the window it came from
(exe path + title) as one JSON line in data\\captures.jsonl. ClawLibrary's 截图 page matches the exe
against its games and only falls back to the filename when a capture has no line here.

Which window: the foreground when the pad's 截图 command fired (note_request), else the foreground
when the file appeared, else the last non-overlay foreground (Game Bar's own capture button puts the
overlay in front). A candidate whose title matches the filename wins; without our own request a
mismatching window is not recorded, so a wrong guess never beats the filename.
"""
import ctypes
from ctypes import wintypes as W
from datetime import datetime
import json
import logging
import os
from pathlib import Path
import re
import threading
import time
import unicodedata
import winreg

CAPTURES_FOLDER_ID = "{EDC0FE71-98D8-4F4A-B920-C8DC133CB165}"
SHELL_FOLDERS = r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"
IMAGES = (".png", ".jpg", ".jpeg")
GAMEBAR_NAME = re.compile(r"^(?P<g>.*?)\s+\d{4}_\d{1,2}_\d{1,2}\s+\d{1,2}_\d{2}_\d{2}(?:\s*\(\d+\))?$")
# Windows in front while capturing that are never what got captured.
OVERLAYS = {"gamebar.exe", "gamebarftserver.exe", "xboxgamebarwidgets.exe", "clawwidget.exe",
            "gameoverlayui.exe", "gameoverlayui64.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
            "searchhost.exe", "textinputhost.exe", "lockapp.exe"}
REQUEST_AGE = 15     # s: a file this long after our own 截图 command belongs to it
LAST_AGE = 30        # s: how long the last non-overlay foreground stays a candidate
MAX_BYTES = 256 * 1024
KEEP_LINES = 1000


def captures_folder():
    """Game Bar's Captures folder (moved in Explorer → User Shell Folders, else Videos\\Captures)."""
    def shell(name):
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, SHELL_FOLDERS) as key:
                value = winreg.QueryValueEx(key, name)[0]
                return os.path.expandvars(value) if isinstance(value, str) and value else None
        except OSError:
            return None
    moved = shell(CAPTURES_FOLDER_ID)
    if moved and os.path.isdir(moved):
        return Path(moved)
    videos = shell("My Video") or os.path.join(os.environ.get("USERPROFILE", ""), "Videos")
    return Path(videos) / "Captures"


def norm(s):
    """Same as the library's Captures.Norm: NFKC, lower case, letters and digits only."""
    return "".join(ch.lower() for ch in unicodedata.normalize("NFKC", s or "") if ch.isalnum())


def window_of(name):
    """The window title part of a Game Bar file name ("" when the name has another shape)."""
    m = GAMEBAR_NAME.match(os.path.splitext(name)[0])
    return m.group("g").strip() if m else ""


def title_matches(title, window):
    a, b = norm(title), norm(window)
    return len(a) >= 2 and len(b) >= 2 and (a == b or a in b or b in a)


class Foreground:
    def __init__(self):
        self.u = ctypes.WinDLL("user32", use_last_error=True)
        self.k = ctypes.WinDLL("kernel32", use_last_error=True)
        self.u.GetForegroundWindow.argtypes, self.u.GetForegroundWindow.restype = [], W.HWND
        self.u.GetWindowThreadProcessId.argtypes = [W.HWND, ctypes.POINTER(W.DWORD)]
        self.u.GetWindowThreadProcessId.restype = W.DWORD
        self.u.GetWindowTextLengthW.argtypes, self.u.GetWindowTextLengthW.restype = [W.HWND], ctypes.c_int
        self.u.GetWindowTextW.argtypes, self.u.GetWindowTextW.restype = [W.HWND, W.LPWSTR, ctypes.c_int], ctypes.c_int
        self.k.OpenProcess.argtypes, self.k.OpenProcess.restype = [W.DWORD, W.BOOL, W.DWORD], W.HANDLE
        self.k.QueryFullProcessImageNameW.argtypes = [W.HANDLE, W.DWORD, W.LPWSTR, ctypes.POINTER(W.DWORD)]
        self.k.QueryFullProcessImageNameW.restype = W.BOOL
        self.k.CloseHandle.argtypes = [W.HANDLE]

    def __call__(self):
        """{"exe", "title"} of the foreground window, or None."""
        hwnd = self.u.GetForegroundWindow()
        if not hwnd:
            return None
        pid = W.DWORD()
        self.u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if not pid.value:
            return None
        handle = self.k.OpenProcess(0x1000, False, pid.value)  # PROCESS_QUERY_LIMITED_INFORMATION: also works for elevated games
        if not handle:
            return None
        try:
            buf, size = ctypes.create_unicode_buffer(1024), W.DWORD(1024)
            exe = buf.value if self.k.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)) else ""
        finally:
            self.k.CloseHandle(handle)
        length = self.u.GetWindowTextLengthW(hwnd)
        title = ctypes.create_unicode_buffer(length + 1)
        self.u.GetWindowTextW(hwnd, title, length + 1)
        return {"exe": exe, "title": title.value} if exe else None


def is_overlay(snap):
    return not snap or not snap["title"] or os.path.basename(snap["exe"]).lower() in OVERLAYS


def pick(window, request, current, last):
    """(snapshot, via) for a new file, or (None, None). Each candidate is a snapshot or None."""
    candidates = [(request, "button"), (None if is_overlay(current) else current, "window"), (last, "window")]
    for snap, via in candidates:
        if snap and title_matches(snap["title"], window):
            return snap, via
    if request:
        return request, "button"
    return None, None


class CaptureLog:
    def __init__(self, data, foreground=None, folder=captures_folder, log=logging.info):
        self.path = Path(data) / "captures.jsonl"
        self.foreground = foreground or Foreground()
        self.folder = folder
        self.log = log
        self._lock = threading.Lock()
        self._request = None   # (monotonic, snapshot) of our last 截图 command
        self._last = None      # (monotonic, snapshot) of the last non-overlay foreground
        self._stop = threading.Event()
        self._thread = None

    def start(self):
        if self._thread is None:
            self._thread = threading.Thread(target=self._run, name="claw-captures", daemon=True)
            self._thread.start()

    def close(self):
        self._stop.set()
        if self._thread:
            self._thread.join(3)

    def note_request(self):
        """Called right before the agent sends Game Bar's screenshot shortcut."""
        try:
            snap = self.foreground()
        except OSError:
            snap = None
        with self._lock:
            self._request = (time.monotonic(), snap) if snap and not is_overlay(snap) else None

    def _run(self):
        folder, known, stamp, resolved = None, None, None, 0
        while not self._stop.wait(1):
            try:
                self.tick_foreground()
                now = time.monotonic()
                if folder is None or now - resolved > 30:
                    found = self.folder()
                    if found != folder:
                        folder, known, stamp = found, None, None
                    resolved = now
                try:
                    changed = os.stat(folder).st_mtime_ns
                except OSError:
                    continue
                if changed == stamp:
                    continue
                stamp = changed
                names = {e.name for e in os.scandir(folder) if e.is_file() and e.name.lower().endswith(IMAGES)}
                if known is not None:
                    for name in sorted(names - known):
                        self.on_new(name)
                known = names
            except Exception as e:  # never let the watcher die
                self.log(f"captures watcher: {e}")

    def tick_foreground(self):
        try:
            snap = self.foreground()
        except OSError:
            return
        if not is_overlay(snap):
            with self._lock:
                self._last = (time.monotonic(), snap)

    def on_new(self, name):
        now = time.monotonic()
        try:
            current = self.foreground()
        except OSError:
            current = None
        with self._lock:
            request = self._request[1] if self._request and now - self._request[0] <= REQUEST_AGE else None
            last = self._last[1] if self._last and now - self._last[0] <= LAST_AGE else None
        snap, via = pick(window_of(name), request, current, last)
        if not snap:
            self.log(f"capture {name}: no matching window")
            return
        if via == "button":
            with self._lock:
                self._request = None   # one press, one file (a burst gets one request per press)
        self.append({"file": name, "exe": snap["exe"], "title": snap["title"], "via": via,
                     "at": datetime.now().isoformat(timespec="seconds")})
        self.log(f"capture {name}: {snap['exe']} ({via})")

    def append(self, entry):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with open(self.path, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")
        if self.path.stat().st_size > MAX_BYTES:
            lines = self.path.read_text(encoding="utf-8").splitlines()[-KEEP_LINES:]
            tmp = self.path.with_suffix(".tmp")
            tmp.write_text("\n".join(lines) + "\n", encoding="utf-8")
            os.replace(tmp, self.path)
