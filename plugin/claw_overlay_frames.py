"""Keep a game presenting while Steam's in-game overlay is open.

Steam draws its gamepad overlay inside the game's own Present calls, in fullscreen
and in windowed mode alike. Engines that only redraw on change (CatSystem2 on a
title screen presents ~0.4 FPS) then freeze or hide the side menus. While the
overlay is visible we only mark the foreground game window dirty so the game
presents again; nothing is injected or hooked. Repeated invalidations coalesce
into one WM_PAINT, so marking every 8 ms keeps a redraw pending for each vsync.
Python's Event.wait rounds to the 15.6 ms system tick (measured 31 ms, ~32 FPS
with judder), so pacing uses a high-resolution waitable timer instead.
"""
import ctypes
from ctypes import wintypes as W
import json
import logging
import os
from pathlib import Path
import threading
import time

INTERVAL = 0.008
# No time limit: a pump that stops while a side menu stays open freezes the game at
# ~0.4 FPS, and Steam then stops showing the menu ("panels retract after a while").
# Steam reports the overlay hidden when its close animation starts; keep the game
# presenting a little longer so the slide-out is not frozen (the "hitch" on close).
LINGER = 1.0
STEAM_PROCESSES = {"steam.exe", "steamwebhelper.exe", "gameoverlayui.exe", "gameoverlayui64.exe"}
PLUGIN_ROOT = Path(__file__).parent
try: PLUGIN_VERSION = json.loads((PLUGIN_ROOT / "plugin.json").read_text(encoding="utf-8"))["version"]
except (OSError, ValueError, KeyError): PLUGIN_VERSION = "0"
# Steam started as administrator launches games elevated, and Windows silently drops
# InvalidateRect from this lower integrity plugin on their windows (the game then
# stays at ~0.4 FPS and the side menus stutter). For those games the bridge exe runs
# the same pump elevated ("--pump"), stopped through a named event.
PUMP_HELPER = PLUGIN_ROOT / "bin" / f"ClawTouchBridge-{PLUGIN_VERSION}.exe"


class Rect(ctypes.Structure):
    _fields_ = [(k, W.LONG) for k in ("left", "top", "right", "bottom")]


class MonitorInfo(ctypes.Structure):
    _fields_ = [("size", W.DWORD), ("monitor", Rect), ("work", Rect), ("flags", W.DWORD)]


class Windows:
    def __init__(self):
        self.u = ctypes.WinDLL("user32", use_last_error=True)
        self.k = ctypes.WinDLL("kernel32", use_last_error=True)
        definitions = {
            "GetForegroundWindow": ([], W.HWND), "GetAncestor": ([W.HWND, W.UINT], W.HWND), "IsWindow": ([W.HWND], W.BOOL),
            "IsWindowVisible": ([W.HWND], W.BOOL), "IsIconic": ([W.HWND], W.BOOL),
            "GetWindowRect": ([W.HWND, ctypes.POINTER(Rect)], W.BOOL),
            "GetWindowThreadProcessId": ([W.HWND, ctypes.POINTER(W.DWORD)], W.DWORD),
            "MonitorFromWindow": ([W.HWND, W.DWORD], W.HANDLE),
            "GetMonitorInfoW": ([W.HANDLE, ctypes.POINTER(MonitorInfo)], W.BOOL),
            "InvalidateRect": ([W.HWND, ctypes.c_void_p, W.BOOL], W.BOOL),
            "SetThreadDpiAwarenessContext": ([W.HANDLE], W.HANDLE),
        }
        for name, (args, result) in definitions.items():
            fn = getattr(self.u, name); fn.argtypes, fn.restype = args, result
        self.k.OpenProcess.argtypes, self.k.OpenProcess.restype = [W.DWORD, W.BOOL, W.DWORD], W.HANDLE
        self.k.CloseHandle.argtypes = [W.HANDLE]
        self.k.QueryFullProcessImageNameW.argtypes = [W.HANDLE, W.DWORD, W.LPWSTR, ctypes.POINTER(W.DWORD)]
        self.k.QueryFullProcessImageNameW.restype = W.BOOL
        self.k.CreateWaitableTimerExW.argtypes = [W.LPVOID, W.LPCWSTR, W.DWORD, W.DWORD]
        self.k.CreateWaitableTimerExW.restype = W.HANDLE
        self.k.SetWaitableTimer.argtypes = [W.HANDLE, ctypes.POINTER(ctypes.c_longlong), W.LONG, W.LPVOID, W.LPVOID, W.BOOL]
        self.k.SetWaitableTimer.restype = W.BOOL
        self.k.WaitForSingleObject.argtypes, self.k.WaitForSingleObject.restype = [W.HANDLE, W.DWORD], W.DWORD

    def timer(self):
        # CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS; one per pump thread.
        return self.k.CreateWaitableTimerExW(None, None, 2, 0x1F0003) or 0

    def sleep(self, timer, seconds):
        due = ctypes.c_longlong(-int(seconds * 10_000_000))
        if timer and self.k.SetWaitableTimer(timer, ctypes.byref(due), 0, None, None, False):
            self.k.WaitForSingleObject(timer, 1000)
        else:
            time.sleep(seconds)

    def close_timer(self, timer):
        if timer: self.k.CloseHandle(timer)

    def foreground(self):
        # CatSystem2 keeps focus on a hidden owned "kcMenuWindow" in fullscreen;
        # the visible game surface is its root owner.
        hwnd = self.u.GetForegroundWindow()
        if not hwnd or self.u.IsWindowVisible(hwnd): return hwnd or 0
        return self.u.GetAncestor(hwnd, 3) or hwnd

    def pid(self, hwnd):
        pid = W.DWORD(); self.u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid)); return pid.value

    def path(self, pid):
        handle = self.k.OpenProcess(0x1000, False, pid)
        if not handle: return ""
        try:
            buf, size = ctypes.create_unicode_buffer(32768), W.DWORD(32768)
            return buf.value if self.k.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)) else ""
        finally:
            self.k.CloseHandle(handle)

    def visible(self, hwnd):
        return bool(self.u.IsWindow(hwnd) and self.u.IsWindowVisible(hwnd) and not self.u.IsIconic(hwnd))

    def invalidate(self, hwnd): return bool(self.u.InvalidateRect(hwnd, None, False))

    def _token_elevated(self, pid):
        a = ctypes.WinDLL("advapi32", use_last_error=True)
        process = self.k.OpenProcess(0x1000, False, pid) if pid else self.k.GetCurrentProcess()
        token, result, length = W.HANDLE(), W.DWORD(), W.DWORD()
        try:
            if not process or not a.OpenProcessToken(process, 8, ctypes.byref(token)): return None
            if not a.GetTokenInformation(token, 20, ctypes.byref(result), 4, ctypes.byref(length)): return None
            return bool(result.value)
        finally:
            if token: self.k.CloseHandle(token)
            if process and pid: self.k.CloseHandle(process)

    def needs_elevation(self, pid):
        """True when this plugin cannot repaint the game's windows itself."""
        if self._token_elevated(0): return False
        return self._token_elevated(pid) is not False  # unreadable token: treat as higher

    def start_elevated_pump(self, hwnd):
        if not PUMP_HELPER.is_file(): return 0
        self.k.CreateEventW.argtypes, self.k.CreateEventW.restype = [W.LPVOID, W.BOOL, W.BOOL, W.LPCWSTR], W.HANDLE
        name = f"Local\\ClawOverlayPump-{os.getpid()}-{int(hwnd)}-{time.monotonic_ns()}"
        event = self.k.CreateEventW(None, True, False, name)
        if not event: return 0
        ok = ctypes.windll.shell32.ShellExecuteW(None, "runas", str(PUMP_HELPER),
            f"--pump {int(hwnd)} {name} {os.getpid()}", str(PUMP_HELPER.parent), 0) > 32
        if not ok: self.k.CloseHandle(event); return 0
        return event

    def stop_elevated_pump(self, event):
        if event: self.k.SetEvent(W.HANDLE(event)); self.k.CloseHandle(event)


class OverlayFrames:
    def __init__(self, windows=None, clock=time.monotonic):
        self.windows, self.clock = windows or Windows(), clock
        self.lock = threading.Lock(); self.stop_event = None; self.thread = None; self.target = None
        self.stop_at = None

    def target_window(self):
        # The in-game overlay leaves the game in the foreground.
        hwnd = self.windows.foreground()
        if not hwnd or not self.windows.visible(hwnd): return None
        pid = self.windows.pid(hwnd)
        if not pid or pid == os.getpid(): return None
        name = Path(self.windows.path(pid)).name.lower()
        if not name or name in STEAM_PROCESSES: return None
        return hwnd, pid

    def set_visible(self, visible):
        with self.lock:
            if not visible:
                if self.thread and self.thread.is_alive(): self.stop_at = self.clock() + LINGER
                return False
            self.stop_at = None
            if self.thread and self.thread.is_alive(): return self.target is not None
            # Start even when the game is not in front this instant (a Steam window can
            # hold focus for a moment while the overlay opens); the pump finds it later.
            self.target, self.stop_event = self.target_window(), threading.Event()
            self.thread = threading.Thread(target=self._pump, args=(self.stop_event,),
                                           name="claw-overlay-frames", daemon=True)
            self.thread.start()
            return self.target is not None

    def _stop_locked(self):
        self.stop_at = None
        event, thread = self.stop_event, self.thread
        self.stop_event = self.thread = self.target = None
        if event: event.set()
        if thread and thread is not threading.current_thread(): thread.join(1)

    def _pump(self, stop):
        # Runs for as long as Steam reports the overlay open. When the player is in
        # another window it only waits; it resumes as soon as the game is in front again,
        # instead of ending for good and leaving an open menu frozen.
        timer = self.windows.timer(); tick = 0; here = False; target = None
        elevated = {}; helper = helper_for = None  # pid -> needs elevated pump; running helper
        try:
            while not stop.is_set():
                stop_at = self.stop_at
                if stop_at is not None and self.clock() >= stop_at: return
                if tick % 8 == 0:
                    target = self.target
                    if target and (self.windows.pid(target[0]) != target[1] or not self.windows.visible(target[0])):
                        target = self.target = None  # the game window closed or was replaced
                    if not target: target = self.target = self.target_window()
                    here = bool(target) and self.windows.foreground() == target[0]
                    if target and target[1] not in elevated:
                        check = getattr(self.windows, "needs_elevation", None)
                        elevated[target[1]] = bool(check and check(target[1]))
                    if target and elevated[target[1]] and helper_for != target[0]:
                        # The elevated helper repaints (and tracks focus) by itself.
                        if helper: self.windows.stop_elevated_pump(helper)
                        helper, helper_for = self.windows.start_elevated_pump(target[0]), target[0]
                        if not helper: logging.warning("Elevated overlay repaint helper could not start")
                if here and not elevated.get(target[1]): self.windows.invalidate(target[0])
                tick += 1
                self.windows.sleep(timer, INTERVAL)
        except Exception:
            logging.exception("Overlay frame pump failed")
        finally:
            if helper: self.windows.stop_elevated_pump(helper)
            self.windows.close_timer(timer)

    def close(self):
        with self.lock: self._stop_locked()
