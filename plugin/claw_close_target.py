"""The close tile follows foreground applications independently of OSD/game classification."""
from contextlib import contextmanager
import ctypes
from ctypes import wintypes as W
import ntpath
import os
import re
import threading
import time


# These surfaces temporarily own focus while the user operates the controller UI.
# They must never replace the application the close tile was showing.
TRANSIENT = {
    "gamebar.exe", "gamebarftserver.exe", "xboxgamebarwidgets.exe", "clawwidget.exe",
    "clawlibrary.exe", "clawperformanceoverlay.exe", "xboxpcapp.exe", "xboxpcappft.exe",
    "gamingmode.exe", "playhub.exe", "gameoverlayui.exe", "gameoverlayui64.exe",
    "steam.exe", "steamwebhelper.exe", "magpie.exe", "losslessscaling.exe",
    "shellexperiencehost.exe", "textinputhost.exe",
}
SWITCHER_CLASSES = {"multitaskingviewframe", "taskswitcherwnd", "xamlexplorerhostislandwindow"}
EXCLUDED = {
    "explorer.exe", "applicationframehost.exe", "searchhost.exe", "startmenuexperiencehost.exe",
    "lockapp.exe", "taskmgr.exe", "codex.exe", "claude.exe", "chatgpt.exe",
    "windowsterminal.exe", "openconsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
    "motionassistant.exe", "rtss.exe", "clash-verge.exe", "sunshine.exe",
    "pluginloader.exe", "pluginloader_noconsole.exe",
}
HELPERS = re.compile(r"(?:^|[-_])(helper|crashreport|crashpad|unins|setup|update)(?:[-_.]|$)", re.I)


def identity(process):
    return (process.get("pid"), process.get("created")) if process else None


class WindowsProbe:
    def __init__(self):
        self.user = ctypes.WinDLL("user32", use_last_error=True)
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.user.GetForegroundWindow.restype = W.HWND
        self.user.GetAncestor.argtypes, self.user.GetAncestor.restype = [W.HWND, W.UINT], W.HWND
        self.user.GetWindowThreadProcessId.argtypes = [W.HWND, ctypes.POINTER(W.DWORD)]
        self.user.GetWindowTextW.argtypes = [W.HWND, W.LPWSTR, ctypes.c_int]
        self.user.GetClassNameW.argtypes = [W.HWND, W.LPWSTR, ctypes.c_int]
        self.user.IsWindowVisible.argtypes = self.user.IsIconic.argtypes = [W.HWND]
        self.kernel.OpenProcess.argtypes, self.kernel.OpenProcess.restype = [W.DWORD, W.BOOL, W.DWORD], W.HANDLE
        self.kernel.CloseHandle.argtypes = [W.HANDLE]
        self.kernel.QueryFullProcessImageNameW.argtypes = [W.HANDLE, W.DWORD, W.LPWSTR, ctypes.POINTER(W.DWORD)]
        self.kernel.GetProcessTimes.argtypes = [W.HANDLE] + [ctypes.POINTER(W.FILETIME)] * 4

    def _process(self, handle, pid):
        path, size = ctypes.create_unicode_buffer(32768), W.DWORD(32768)
        created, exited, kernel, user = (W.FILETIME() for _ in range(4))
        if (not self.kernel.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(size)) or
                not self.kernel.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)) or
                exited.dwLowDateTime or exited.dwHighDateTime):
            return None
        return {"pid": pid, "created": (created.dwHighDateTime << 32) | created.dwLowDateTime,
                "path": path.value, "exe": ntpath.basename(path.value).lower()}

    def process(self, pid):
        handle = self.kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            return None
        try:
            return self._process(handle, pid)
        finally:
            self.kernel.CloseHandle(handle)

    @contextmanager
    def hold(self, target):
        # Keep the process object alive through the helper request, so its PID
        # cannot be recycled between identity validation and the helper's kill.
        handle = self.kernel.OpenProcess(0x1000, False, target["pid"])
        if not handle:
            raise RuntimeError("要关闭的程序已退出或无法访问，请重新确认")
        try:
            if identity(self._process(handle, target["pid"])) != identity(target):
                raise RuntimeError("要关闭的程序已经改变，请重新确认")
            yield
        finally:
            self.kernel.CloseHandle(handle)

    def foreground(self):
        hwnd = self.user.GetForegroundWindow()
        if not hwnd:
            return None
        if not self.user.IsWindowVisible(hwnd):
            hwnd = self.user.GetAncestor(hwnd, 3) or hwnd
        owner = W.DWORD()
        self.user.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        process = self.process(owner.value) if owner.value else None
        if not process:
            return None
        if process["exe"] == "applicationframehost.exe":
            found = []
            callback_type = ctypes.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)

            @callback_type
            def child(window, _):
                name, child_pid = ctypes.create_unicode_buffer(256), W.DWORD()
                self.user.GetClassNameW(window, name, len(name))
                if name.value == "Windows.UI.Core.CoreWindow":
                    self.user.GetWindowThreadProcessId(window, ctypes.byref(child_pid))
                    actual = self.process(child_pid.value) if child_pid.value != owner.value else None
                    if actual:
                        found.append(actual)
                        return False
                return True

            self.user.EnumChildWindows.argtypes = [W.HWND, callback_type, W.LPARAM]
            self.user.EnumChildWindows(hwnd, child, 0)
            if found:
                process = found[0]
        title, name = ctypes.create_unicode_buffer(1024), ctypes.create_unicode_buffer(256)
        self.user.GetWindowTextW(hwnd, title, len(title))
        self.user.GetClassNameW(hwnd, name, len(name))
        return {**process, "hwnd": int(hwnd), "title": title.value or ntpath.splitext(process["exe"])[0],
                "className": name.value, "visible": bool(self.user.IsWindowVisible(hwnd) and not self.user.IsIconic(hwnd))}


class CloseTarget:
    def __init__(self, probe=None, clock=time.monotonic):
        self.probe, self.clock = probe or WindowsProbe(), clock
        self.target = None
        self.available = False
        self.shell_at = None
        self.last_front_at = 0.0
        self.lock = threading.RLock()

    @staticmethod
    def eligible(front):
        exe = front.get("exe", "").lower()
        path = ntpath.normcase(front.get("path", ""))
        windows = ntpath.normcase(os.environ.get("WINDIR", r"C:\Windows"))
        return bool(front.get("pid") != os.getpid() and front.get("created") and front.get("hwnd") and
                    front.get("visible") and path and exe and exe not in TRANSIENT | EXCLUDED and
                    not exe.startswith("claw") and not HELPERS.search(exe) and
                    not path.startswith(windows.rstrip("\\") + "\\"))

    def sample(self):
        with self.lock:
            now, front = self.clock(), self.probe.foreground()
            if self.target and identity(self.probe.process(self.target["pid"])) != identity(self.target):
                self.target = None
            if front is None:
                if now - self.last_front_at > 2:
                    self.target = None
            else:
                self.last_front_at = now
                exe = front.get("exe", "").lower()
                switcher = (exe in {"explorer.exe", "sihost.exe"} and
                            front.get("className", "").lower() in SWITCHER_CLASSES)
                if switcher or exe in {"xboxpcapp.exe", "xboxpcappft.exe"}:
                    # Task View briefly exposes Progman/the taskbar before its
                    # own window appears. Only a real switcher may recover the
                    # hidden target during this bounded shell transition.
                    if self.shell_at is not None and now - self.shell_at <= 2:
                        self.available = bool(self.target)
                    self.shell_at = None
                elif exe in {"explorer.exe", "sihost.exe"}:
                    if self.shell_at is None:
                        self.shell_at = now
                    self.available = False
                    if now - self.shell_at > 2:
                        self.target = None
                elif exe not in TRANSIENT:
                    self.target = dict(front) if self.eligible(front) else None
                    self.available = bool(self.target)
                    self.shell_at = None
            return self.view()

    def view(self):
        with self.lock:
            if not self.target or not self.available:
                return None
            return {**{k: self.target[k] for k in ("pid", "exe", "title", "path", "hwnd")},
                    "identity": f"{self.target['pid']}:{self.target['created']}"}

    @contextmanager
    def hold(self, pid):
        if type(pid) is not int or pid <= 0:
            raise RuntimeError("请选择要关闭的程序")
        with self.lock:
            self.sample()
            target = dict(self.target) if self.target and self.available else None
            if not target or target["pid"] != pid:
                raise RuntimeError("要关闭的程序已经改变，请重新确认")
            lease = self.probe.hold(target)
            lease.__enter__()
        try:
            yield
        finally:
            lease.__exit__(None, None, None)
