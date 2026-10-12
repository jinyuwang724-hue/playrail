"""Targeted MSI Claw UI rumble. No driver, virtual pad, or idle polling."""
import ctypes
from ctypes import wintypes
import math
import os
import threading
import time
import winreg

_HARDWARE = (0x0DB0, 0x1901)


class _Pad(ctypes.Structure):
    _fields_ = [("buttons", wintypes.WORD), ("lt", ctypes.c_ubyte),
                ("rt", ctypes.c_ubyte), ("lx", ctypes.c_short),
                ("ly", ctypes.c_short), ("rx", ctypes.c_short), ("ry", ctypes.c_short)]


class _Vibration(ctypes.Structure):
    _fields_ = [("left", wintypes.WORD), ("right", wintypes.WORD)]


class _Capabilities(ctypes.Structure):
    _fields_ = [("type", ctypes.c_ubyte), ("subtype", ctypes.c_ubyte),
                ("flags", wintypes.WORD), ("pad", _Pad), ("vibration", _Vibration)]


class _Extended(ctypes.Structure):
    _fields_ = [("caps", _Capabilities), ("vendor", wintypes.WORD),
                ("product", wintypes.WORD), ("revision", wintypes.WORD),
                ("unknown", wintypes.WORD), ("unknown2", wintypes.DWORD)]


class WindowsOutput:
    def __init__(self):
        self._kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self._user = ctypes.WinDLL("user32", use_last_error=True)
        self._input = ctypes.WinDLL(os.path.join(os.environ["SystemRoot"], "System32", "xinput1_4.dll"))
        self._kernel.GetProcAddress.argtypes = [wintypes.HMODULE, ctypes.c_void_p]
        self._kernel.GetProcAddress.restype = ctypes.c_void_p
        address = self._kernel.GetProcAddress(self._input._handle, ctypes.c_void_p(108))
        if not address or ctypes.sizeof(_Extended) != 32:
            raise OSError("Exact XInput hardware identity unavailable")
        query_type = ctypes.WINFUNCTYPE(wintypes.DWORD, wintypes.DWORD,
                                      wintypes.DWORD, wintypes.DWORD, ctypes.POINTER(_Extended))
        self._query = query_type(address)
        self._input.XInputSetState.argtypes = [wintypes.DWORD, ctypes.POINTER(_Vibration)]
        self._input.XInputSetState.restype = wintypes.DWORD
        self._user.GetForegroundWindow.restype = wintypes.HWND
        self._user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        self._kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self._kernel.OpenProcess.restype = wintypes.HANDLE
        self._kernel.QueryFullProcessImageNameW.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                                        wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self._kernel.CloseHandle.argtypes = [wintypes.HANDLE]

    def matches(self, index):
        caps = _Extended()
        return self._query(1, index, 0, ctypes.byref(caps)) == 0 and (caps.vendor, caps.product) == _HARDWARE

    def targets(self):
        return [index for index in range(4) if self.matches(index)]

    def write(self, index, left, right):
        # Recheck on every write, including stop, to avoid touching a replaced pad.
        if not self.matches(index):
            return 1167
        motors = _Vibration(round(max(0, min(1, left)) * 65535),
                            round(max(0, min(1, right)) * 65535))
        return self._input.XInputSetState(index, ctypes.byref(motors))

    def can_recover(self):
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
                if int(winreg.QueryValueEx(key, "RunningAppID")[0]) != 0:
                    return False
        except FileNotFoundError:
            pass
        except (OSError, ValueError, TypeError):
            return False
        return True

    def allowed(self):
        if not self.can_recover():
            return False
        pid = wintypes.DWORD()
        hwnd = self._user.GetForegroundWindow()
        if not hwnd:
            return False
        self._user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        handle = self._kernel.OpenProcess(0x1000, False, pid.value)
        if not handle:
            return False
        try:
            name = ctypes.create_unicode_buffer(32768)
            size = wintypes.DWORD(len(name))
            if not self._kernel.QueryFullProcessImageNameW(handle, 0, name, ctypes.byref(size)):
                return False
            return os.path.basename(name.value).lower() in ("steam.exe", "steamwebhelper.exe")
        finally:
            self._kernel.CloseHandle(handle)


class ClawNavigationHaptics:
    def __init__(self, output=None):
        self._output = output
        self._lock = threading.RLock()
        self._cancel = threading.Event()
        self._index = None
        self._request_id = 0
        self._closed = False

    def _stop(self):
        self._cancel.set()
        if self._index is not None and self._output is not None:
            try:
                self._output.write(self._index, 0, 0)
            except Exception:
                pass
        self._index = None

    def request(self, request):
        if not isinstance(request, dict):
            return {"handled": False, "path": "invalid-request"}
        request_id = request.get("requestId")
        if type(request_id) is not int or request_id <= 0:
            return {"handled": False, "path": "invalid-request"}
        is_stop = request.get("stop") is True
        if not is_stop:
            if (request.get("vendorId"), request.get("productId")) != _HARDWARE:
                return {"handled": False, "path": "unsupported-hardware"}
            duration = request.get("durationMs")
            side = request.get("side")
            strength = request.get("magnitude")
            if (type(duration) is not int or not 8 <= duration <= 250
                    or type(side) is not int or side not in (0, 1, 2)
                    or type(strength) not in (float, int)
                    or not math.isfinite(strength) or not 0 <= strength <= 1):
                return {"handled": False, "path": "invalid-request"}
        try:
            with self._lock:
                if self._closed:
                    return {"handled": False, "path": "closed"}
                if request_id <= self._request_id:
                    return {"handled": True, "path": "stale-request"}
                self._request_id = request_id
                if is_stop:
                    self._stop()
                    return {"handled": True, "path": "stopped"}
                if self._output is None:
                    self._output = WindowsOutput()
                if not self._output.allowed():
                    self._stop()
                    return {"handled": True, "path": "steam-ui-only"}
                targets = self._output.targets()
                if len(targets) != 1:
                    self._stop()
                    return {"handled": False, "path": "ambiguous-or-missing-claw"}
                index = targets[0]
                self._cancel.set()
                self._cancel = threading.Event()
                self._index = index
                # Match ControllerHapticsService.Motors: the secondary motor
                # receives 4% for one-sided feedback; side 2 drives both equally.
                left = strength if side in (0, 2) else strength * 0.04
                right = strength if side in (1, 2) else strength * 0.04
                result = self._output.write(index, left, right)
                if result != 0:
                    self._stop()
                    return {"handled": False, "path": "xinput-error", "code": result}
                worker = threading.Thread(target=self._finish,
                    args=(request_id, duration, self._cancel), daemon=True)
                worker.start()
                return {"handled": True, "path": "claw-xinput", "index": index, "durationMs": duration}
        except Exception:
            with self._lock:
                self._stop()
            return {"handled": False, "path": "xinput-unavailable"}

    def _finish(self, request_id, duration, cancellation):
        deadline = time.monotonic() + duration / 1000
        while not cancellation.wait(min(0.008, max(0, deadline - time.monotonic()))):
            with self._lock:
                if request_id != self._request_id or self._closed:
                    return
                try:
                    active = self._output.allowed()
                except Exception:
                    active = False
                if time.monotonic() >= deadline or not active:
                    self._stop()
                    return

    def close(self):
        with self._lock:
            self._closed = True
            self._stop()

    def stop(self):
        with self._lock:
            self._stop()

    def recover(self):
        """Clear a pulse left by a killed predecessor, only with no running game."""
        with self._lock:
            self._stop()
            try:
                if self._output is None:
                    self._output = WindowsOutput()
                if not self._output.can_recover():
                    return False
                targets = self._output.targets()
                return len(targets) == 1 and self._output.write(targets[0], 0, 0) == 0
            except Exception:
                return False
