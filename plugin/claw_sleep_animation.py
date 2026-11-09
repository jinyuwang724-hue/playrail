"""Fixed, same-session pipe client for the protected power-animation component."""
import asyncio
import ctypes
from ctypes import wintypes
import functools
import json
import os
from pathlib import Path
import time

_kernel = ctypes.WinDLL("kernel32", use_last_error=True)
_kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
_kernel.CreateFileW.restype = wintypes.HANDLE
_kernel.WaitNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
_kernel.PeekNamedPipe.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
_kernel.ReadFile.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
_kernel.WriteFile.argtypes = _kernel.ReadFile.argtypes
_kernel.CloseHandle.argtypes = [wintypes.HANDLE]
_kernel.ProcessIdToSessionId.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
_kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
_kernel.OpenProcess.restype = wintypes.HANDLE
_kernel.GetProcessTimes.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
_kernel.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
_kernel.OpenEventW.restype = wintypes.HANDLE
_kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
_last = None


@functools.lru_cache(maxsize=1)
def _state_dir():
    advapi = ctypes.WinDLL("advapi32", use_last_error=True)
    advapi.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    advapi.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    advapi.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.LPWSTR)]
    _kernel.GetCurrentProcess.restype = wintypes.HANDLE
    _kernel.LocalFree.argtypes = [ctypes.c_void_p]
    token = wintypes.HANDLE()
    if not advapi.OpenProcessToken(_kernel.GetCurrentProcess(), 8, ctypes.byref(token)):
        raise OSError("无法读取当前用户")
    try:
        size = wintypes.DWORD()
        advapi.GetTokenInformation(token, 1, None, 0, ctypes.byref(size))
        buffer = ctypes.create_string_buffer(size.value)
        if not advapi.GetTokenInformation(token, 1, buffer, size.value, ctypes.byref(size)):
            raise OSError("无法读取当前用户")
        sid = ctypes.cast(buffer, ctypes.POINTER(ctypes.c_void_p)).contents.value
        text = wintypes.LPWSTR()
        if not advapi.ConvertSidToStringSidW(sid, ctypes.byref(text)):
            raise OSError("无法读取当前用户")
        try:
            return Path(os.environ["PROGRAMDATA"]) / "ClawToolbox" / "SleepAnimation" / text.value
        finally:
            _kernel.LocalFree(ctypes.cast(text, ctypes.c_void_p))
    finally:
        _kernel.CloseHandle(token)


def _request(command):
    session = wintypes.DWORD()
    if not _kernel.ProcessIdToSessionId(os.getpid(), ctypes.byref(session)):
        raise RuntimeError("无法读取当前会话")
    pipe = rf"\\.\pipe\ClawSleepAnimation-{session.value}"
    if not _kernel.WaitNamedPipeW(pipe, 1200):
        raise RuntimeError("电源动画后台暂未连接")
    # Specific data rights deliberately exclude FILE_CREATE_PIPE_INSTANCE.
    handle = _kernel.CreateFileW(pipe, 0x120003, 0, None, 3, 0, None)
    if handle == ctypes.c_void_p(-1).value:
        raise RuntimeError("无法连接电源动画后台")
    try:
        payload = json.dumps(command, separators=(",", ":")).encode("utf-8") + b"\n"
        written = wintypes.DWORD()
        if not _kernel.WriteFile(handle, payload, len(payload), ctypes.byref(written), None) or written.value != len(payload):
            raise RuntimeError("电源动画设置未发送")
        deadline, result = time.monotonic() + 3, bytearray()
        while time.monotonic() < deadline and len(result) < 4096:
            available = wintypes.DWORD()
            if not _kernel.PeekNamedPipe(handle, None, 0, None, ctypes.byref(available), None):
                raise RuntimeError("电源动画后台已断开")
            if not available.value:
                time.sleep(0.01)
                continue
            buffer = ctypes.create_string_buffer(min(available.value, 4096 - len(result)))
            read = wintypes.DWORD()
            if not _kernel.ReadFile(handle, buffer, len(buffer), ctypes.byref(read), None):
                raise RuntimeError("电源动画状态读取失败")
            result.extend(buffer.raw[:read.value])
            if b"\n" in result:
                reply = json.loads(result.split(b"\n", 1)[0])
                if not reply.get("ok"):
                    raise RuntimeError(reply.get("error") or "电源动画设置失败")
                return reply["result"]
        raise RuntimeError("电源动画后台响应超时")
    finally:
        _kernel.CloseHandle(handle)


async def view():
    global _last
    try:
        _last = await asyncio.to_thread(_request, {"command": "get"})
        return _last.copy()
    except (OSError, RuntimeError, ValueError) as error:
        result = dict(_last or {})
        try:
            state = _state_dir()
            options = json.loads((state / "settings.json").read_text(encoding="utf-8"))
            result.update(options)
            lease = json.loads((state / "active-lease.json").read_text(encoding="utf-8"))
            names = {1: "sleep", 2: "hibernate"}
            result.update(effectiveButtonAc=names.get(lease["Policy"]["Ac"]), effectiveButtonDc=names.get(lease["Policy"]["Dc"]))
        except (OSError, ValueError, KeyError):
            pass
        return {**result, "installed": True, "available": False, "active": False, "error": str(error)}


async def set_enabled(which, enabled):
    if which not in ("suspend", "resume", "enhanced", "sleep_games", "sleep_fingerprint") or type(enabled) is not bool:
        raise ValueError("动画开关格式错误")
    return await asyncio.to_thread(_request, {"command": "set_" + which, "enabled": enabled})


async def set_action(action):
    if action not in ("sleep", "hibernate", "shutdown"):
        raise ValueError("不支持的电源键动作")
    return await asyncio.to_thread(_request, {"command": "set_action", "action": action})


def enhanced_enabled():
    try:
        return json.loads((_state_dir() / "enhanced-settings.json").read_text(encoding="utf-8")).get("Enabled") is True
    except (OSError, ValueError):
        return False


def is_sleeping():
    """Ignore recovery state from a dead host or a reused process identifier."""
    try:
        state = json.loads((_state_dir() / "enhanced-runtime.json").read_text(encoding="utf-8"))
        if state.get("sleeping") is not True:
            return False
        handle = _kernel.OpenProcess(0x1000, False, int(state["ownerPid"]))
        if not handle:
            return False
        try:
            times = [ctypes.c_int64() for _ in range(4)]
            return bool(_kernel.GetProcessTimes(handle, *(ctypes.byref(t) for t in times))) and times[1].value == 0 and times[0].value + 504911232000000000 == state["ownerStarted"]
        finally:
            _kernel.CloseHandle(handle)
    except (OSError, ValueError, KeyError, TypeError):
        return False


async def wait_awake():
    def wait():
        session = wintypes.DWORD()
        if not _kernel.ProcessIdToSessionId(os.getpid(), ctypes.byref(session)):
            return
        handle = _kernel.OpenEventW(0x100000, False, "Global\\ClawEnhancedSleep.Awake-" + str(session.value))
        if handle:
            try:
                _kernel.WaitForSingleObject(handle, 10000)
            finally:
                _kernel.CloseHandle(handle)
        else:
            time.sleep(1)
    await asyncio.to_thread(wait)


async def set_game(pid):
    if type(pid) is not int or pid < 0:
        raise ValueError("游戏进程格式错误")
    return await asyncio.to_thread(_request, {"command": "set_game", "pid": pid})


async def power_action(action, *, preview=False):
    if action not in ("shutdown", "restart") or type(preview) is not bool:
        raise ValueError("不支持的电源操作")
    return await asyncio.to_thread(_request, {"command": "preview_exit" if preview else "power_action", "action": action})
