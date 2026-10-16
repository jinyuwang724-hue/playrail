"""MSI Claw firmware rumble strength (0-100 % per motor), stored in the pad's ROM.

Protocol from Linux hid-msi (drivers/hid/hid-msi.c): 64-byte output report
0x0f 00 00 3c <cmd> <data>; replies arrive as input report 0x10 00 00 3c.
Read profile 0x04 -> 0x05, write profile data 0x21 -> ack 0x06, sync to ROM 0x22.
Rumble lives at profile 1, addresses 0x0022 (left) and 0x0023 (right). The
firmware applies it to every XInput/DInput rumble, in games and in Steam alike.
"""
import asyncio
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import threading
import time

_HARDWARE = (0x0DB0, 0x1901)
_ADDRESSES = (0x0022, 0x0023)
_PACKET = 64
_OUTPUT_ID, _INPUT_ID, _TAIL = 0x0F, 0x10, 0x3C
_READ, _READ_ACK, _ACK, _WRITE, _SYNC = 0x04, 0x05, 0x06, 0x21, 0x22


def supported_firmware(version):
    """bcdDevice gates from hid-msi claw_features_supported()."""
    major, minor = (version >> 8) & 0xFF, version & 0xFF
    return (major == 1 and minor >= 0x66) or (major == 2 and minor >= 0x17) or major >= 3


def packet(cmd, data=b""):
    frame = bytearray(_PACKET)
    frame[:5] = bytes((_OUTPUT_ID, 0, 0, _TAIL, cmd))
    frame[5:5 + len(data)] = data
    return bytes(frame)


def rumble_field(address, value=0):
    return bytes((0x01, address >> 8, address & 0xFF, 0x01, value))


def parse_reply(frame):
    """Return (cmd, address, value); address/value only for profile replies."""
    if len(frame) < 10 or frame[0] != _INPUT_ID or frame[3] != _TAIL:
        return None
    if frame[4] == _READ_ACK:
        return _READ_ACK, (frame[6] << 8) | frame[7], frame[9]
    return frame[4], None, None


class _Guid(ctypes.Structure):
    _fields_ = [("a", wintypes.DWORD), ("b", wintypes.WORD), ("c", wintypes.WORD), ("d", ctypes.c_ubyte * 8)]


class _InterfaceData(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("guid", _Guid), ("flags", wintypes.DWORD), ("reserved", ctypes.c_void_p)]


class _Attributes(ctypes.Structure):
    _fields_ = [("size", wintypes.ULONG), ("vendor", wintypes.USHORT),
                ("product", wintypes.USHORT), ("version", wintypes.USHORT)]


class _Caps(ctypes.Structure):
    _fields_ = [("usage", wintypes.USHORT), ("usage_page", wintypes.USHORT),
                ("input_length", wintypes.USHORT), ("output_length", wintypes.USHORT),
                ("feature_length", wintypes.USHORT), ("reserved", wintypes.USHORT * 17),
                ("rest", wintypes.USHORT * 10)]


class _Overlapped(ctypes.Structure):
    _fields_ = [("internal", ctypes.c_void_p), ("internal_high", ctypes.c_void_p),
                ("offset", wintypes.DWORD), ("offset_high", wintypes.DWORD), ("event", wintypes.HANDLE)]


_INVALID = ctypes.c_void_p(-1).value
CHANNEL_LOCK = threading.Lock()


class HidChannel:
    """One open config collection. Opened per operation, never held while idle."""

    def __init__(self, products=(_HARDWARE[1],)):
        # The pad re-enumerates with another product id per mode (0x1901 XInput, 0x1902 DInput, 0x1903 desktop).
        self._products = tuple(products)
        self._k = ctypes.WinDLL("kernel32", use_last_error=True)
        self._hid = ctypes.WinDLL("hid", use_last_error=True)
        self._setup = ctypes.WinDLL("setupapi", use_last_error=True)
        k, h, s = self._k, self._hid, self._setup
        k.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        k.CreateFileW.restype = wintypes.HANDLE
        k.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
        k.CreateEventW.restype = wintypes.HANDLE
        k.WriteFile.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
                                ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(_Overlapped)]
        k.ReadFile.argtypes = k.WriteFile.argtypes
        k.GetOverlappedResult.argtypes = [wintypes.HANDLE, ctypes.POINTER(_Overlapped),
                                          ctypes.POINTER(wintypes.DWORD), wintypes.BOOL]
        k.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        k.WaitForSingleObject.restype = wintypes.DWORD
        k.CancelIoEx.argtypes = [wintypes.HANDLE, ctypes.POINTER(_Overlapped)]
        k.CloseHandle.argtypes = [wintypes.HANDLE]
        h.HidD_GetHidGuid.argtypes = [ctypes.POINTER(_Guid)]
        h.HidD_GetAttributes.argtypes = [wintypes.HANDLE, ctypes.POINTER(_Attributes)]
        h.HidD_GetPreparsedData.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
        h.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
        h.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(_Caps)]
        h.HidP_GetCaps.restype = ctypes.c_long
        s.SetupDiGetClassDevsW.argtypes = [ctypes.POINTER(_Guid), wintypes.LPCWSTR, wintypes.HWND, wintypes.DWORD]
        s.SetupDiGetClassDevsW.restype = wintypes.HANDLE
        s.SetupDiEnumDeviceInterfaces.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.POINTER(_Guid),
                                                  wintypes.DWORD, ctypes.POINTER(_InterfaceData)]
        s.SetupDiGetDeviceInterfaceDetailW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_InterfaceData),
                                                       ctypes.c_void_p, wintypes.DWORD,
                                                       ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
        s.SetupDiDestroyDeviceInfoList.argtypes = [wintypes.HANDLE]
        self._handle = None
        self.version = None
        self.product = None

    def _paths(self):
        guid = _Guid()
        self._hid.HidD_GetHidGuid(ctypes.byref(guid))
        info = self._setup.SetupDiGetClassDevsW(ctypes.byref(guid), None, None, 0x12)  # PRESENT|INTERFACE
        if info == _INVALID:
            return []
        paths = []
        try:
            index = 0
            while True:
                data = _InterfaceData(size=ctypes.sizeof(_InterfaceData))
                if not self._setup.SetupDiEnumDeviceInterfaces(info, None, ctypes.byref(guid), index, ctypes.byref(data)):
                    break
                index += 1
                needed = wintypes.DWORD()
                self._setup.SetupDiGetDeviceInterfaceDetailW(info, ctypes.byref(data), None, 0, ctypes.byref(needed), None)
                buffer = ctypes.create_string_buffer(needed.value)
                ctypes.cast(buffer, ctypes.POINTER(wintypes.DWORD))[0] = 8 if ctypes.sizeof(ctypes.c_void_p) == 8 else 6
                if self._setup.SetupDiGetDeviceInterfaceDetailW(info, ctypes.byref(data), buffer, needed,
                                                                None, None):
                    path = ctypes.wstring_at(ctypes.addressof(buffer) + 4)
                    if any("vid_0db0&pid_%04x" % product in path.lower() for product in self._products):
                        paths.append(path)
        finally:
            self._setup.SetupDiDestroyDeviceInfoList(info)
        return paths

    def open(self):
        matches = []
        for path in self._paths():
            handle = self._k.CreateFileW(path, 0xC0000000, 3, None, 3, 0x40000000, None)  # R/W, shared, overlapped
            if handle in (None, _INVALID):
                continue
            keep = False
            try:
                attributes = _Attributes(size=ctypes.sizeof(_Attributes))
                caps, parsed = _Caps(), ctypes.c_void_p()
                if (self._hid.HidD_GetAttributes(handle, ctypes.byref(attributes))
                        and attributes.vendor == _HARDWARE[0] and attributes.product in self._products
                        and self._hid.HidD_GetPreparsedData(handle, ctypes.byref(parsed))):
                    try:
                        ok = self._hid.HidP_GetCaps(parsed, ctypes.byref(caps)) == 0x00110000
                    finally:
                        self._hid.HidD_FreePreparsedData(parsed)
                    # The vendor config collection: 64-byte in/out reports, vendor usage page.
                    if (ok and caps.usage_page >= 0xFF00 and caps.output_length == _PACKET
                            and caps.input_length == _PACKET):
                        matches.append((handle, attributes.version, attributes.product))
                        keep = True
            finally:
                if not keep:
                    self._k.CloseHandle(handle)
        if len(matches) != 1:
            for handle, _, _ in matches:
                self._k.CloseHandle(handle)
            raise OSError("没有找到唯一的 Claw 手柄配置接口")
        self._handle, self.version, self.product = matches[0]
        return self

    def close(self):
        if self._handle is not None:
            self._k.CloseHandle(self._handle)
            self._handle = None

    def _io(self, function, buffer, timeout_ms):
        overlapped = _Overlapped(event=self._k.CreateEventW(None, True, False, None))
        try:
            done = wintypes.DWORD()
            if not function(self._handle, buffer, len(buffer), None, ctypes.byref(overlapped)):
                if ctypes.get_last_error() != 997:  # ERROR_IO_PENDING
                    raise OSError("手柄通信失败 %d" % ctypes.get_last_error())
                if self._k.WaitForSingleObject(overlapped.event, timeout_ms) != 0:
                    self._k.CancelIoEx(self._handle, ctypes.byref(overlapped))
                    self._k.GetOverlappedResult(self._handle, ctypes.byref(overlapped), ctypes.byref(done), True)
                    return None
            if not self._k.GetOverlappedResult(self._handle, ctypes.byref(overlapped), ctypes.byref(done), True):
                raise OSError("手柄通信失败 %d" % ctypes.get_last_error())
            return done.value
        finally:
            self._k.CloseHandle(overlapped.event)

    def send(self, frame):
        buffer = ctypes.create_string_buffer(frame, len(frame))
        if self._io(self._k.WriteFile, buffer, 500) != len(frame):
            raise OSError("手柄没有接收命令")

    def receive(self, timeout_ms):
        buffer = ctypes.create_string_buffer(_PACKET)
        size = self._io(self._k.ReadFile, buffer, timeout_ms)
        return None if size is None else buffer.raw[:size]


class ClawRumble:
    """Read and write the firmware rumble ceiling. Writes only when it differs."""

    def __init__(self, channel_factory=HidChannel, clock=time.monotonic, lock=None):
        self._factory = channel_factory
        self._clock = clock
        # Shared with claw_gamepad_fw: every open handle sees every reply, so one request at a time.
        self._lock = lock or CHANNEL_LOCK

    def _await(self, channel, accept, timeout=0.3):
        deadline = self._clock() + timeout
        while (left := deadline - self._clock()) > 0:
            frame = channel.receive(max(1, int(left * 1000)))
            reply = frame and parse_reply(frame)
            if reply and accept(reply):
                return reply
        raise TimeoutError("手柄没有回应")

    def _read(self, channel, address):
        channel.send(packet(_READ, rumble_field(address)))
        return self._await(channel, lambda r: r[0] == _READ_ACK and r[1] == address)[2]

    def _open(self):
        channel = self._factory().open()
        if not supported_firmware(channel.version):
            channel.close()
            raise OSError("手柄固件版本不支持调节震动强度")
        return channel

    def read(self):
        with self._lock:
            channel = self._open()
            try:
                return [self._read(channel, address) for address in _ADDRESSES]
            finally:
                channel.close()

    def write(self, left, right):
        values = [left, right]
        if any(type(v) is not int or not 0 <= v <= 100 for v in values):
            raise ValueError("震动强度需在 0 到 100 之间")
        with self._lock:
            channel = self._open()
            try:
                before = [self._read(channel, address) for address in _ADDRESSES]
                for address, old, new in zip(_ADDRESSES, before, values):
                    if old == new:
                        continue
                    channel.send(packet(_WRITE, rumble_field(address, new)))
                    self._await(channel, lambda r: r[0] == _ACK)
                    # The MCU acks a ROM sync only after the USB transfer; drain it.
                    channel.send(packet(_SYNC))
                    try:
                        self._await(channel, lambda r: r[0] == _ACK, 0.15)
                    except TimeoutError:
                        pass
                after = [self._read(channel, address) for address in _ADDRESSES]
            finally:
                channel.close()
        if after != values:
            raise OSError("手柄没有保存新的震动强度")
        return {"before": before, "after": after, "written": before != values}


class RumbleControl:
    """Plugin-side owner: keeps the chosen strength in the pad.

    ClawTweaks rewrites its own ceiling (100 %) when its helper starts, so the
    chosen value is re-checked a few times after load and on demand; the pad
    ROM is written only when the value read back differs.
    """
    RECHECK_DELAYS = (3, 30, 120)

    def __init__(self, data_dir, rumble=None, log=None):
        self._path = Path(data_dir) / "rumble.json"
        self._rumble = rumble or ClawRumble()
        self._log = log or (lambda *args: None)
        self._config = {"strength": None, "original": None}
        self._current = None
        self._error = None
        self._task = None
        try:
            loaded = json.loads(self._path.read_text(encoding="utf-8"))
            if self._valid_strength(loaded.get("strength")) and self._valid_pair(loaded.get("original")):
                self._config = {"strength": loaded["strength"], "original": loaded["original"]}
        except (OSError, ValueError, AttributeError):
            pass

    @staticmethod
    def _valid_strength(value):
        # One value for both motors, or [left, right].
        return value is None or (type(value) is int and 0 <= value <= 100) or RumbleControl._valid_pair(value)

    def _pair(self):
        strength = self._config["strength"]
        return None if strength is None else (list(strength) if isinstance(strength, list) else [strength, strength])

    @staticmethod
    def _valid_pair(value):
        return value is None or (isinstance(value, list) and len(value) == 2
                                 and all(type(v) is int and 0 <= v <= 100 for v in value))

    def _save(self):
        temp = self._path.with_suffix(".tmp")
        temp.write_text(json.dumps(self._config), encoding="utf-8")
        os.replace(temp, self._path)

    def view(self):
        current, pair = self._current, self._pair()
        shown = pair or current
        return {"strength": round(sum(shown) / 2) if shown else None,
                "left": shown[0] if shown else None, "right": shown[1] if shown else None,
                "managed": pair is not None,
                "current": current, "original": self._config["original"], "error": self._error}

    async def _run(self, function, *args):
        return await asyncio.get_running_loop().run_in_executor(None, function, *args)

    async def refresh(self):
        try:
            self._current = await self._run(self._rumble.read)
            self._error = None
        except Exception as error:
            self._error = str(error)
        return self.view()

    async def enforce(self, reason):
        pair = self._pair()
        if pair is None:
            return self.view()
        try:
            result = await self._run(self._rumble.write, *pair)
            self._current, self._error = result["after"], None
            if result["written"]:
                self._log("Claw rumble %s: %s -> %s" % (reason, result["before"], result["after"]))
        except Exception as error:
            self._error = str(error)
        return self.view()

    async def set_strength(self, strength, right=None):
        """set_strength(both) or set_strength(left, right)."""
        for value in (strength, strength if right is None else right):
            if type(value) is not int or not 0 <= value <= 100:
                raise ValueError("震动强度需在 0 到 100 之间")
        if right is not None and right != strength:
            strength = [strength, right]
        if self._config["original"] is None:
            # Back up the pad's own value before the first write.
            self._current = await self._run(self._rumble.read)
            self._config["original"] = list(self._current)
        self._config["strength"] = strength
        self._save()
        return await self.enforce("set")

    async def restore(self):
        original = self._config["original"]
        self._config["strength"] = None
        self._save()
        if original:
            try:
                result = await self._run(self._rumble.write, *original)
                self._current, self._error = result["after"], None
            except Exception as error:
                self._error = str(error)
        return self.view()

    def start(self):
        async def recheck():
            for delay in self.RECHECK_DELAYS:
                await asyncio.sleep(delay)
                await self.enforce("startup")
        self._task = asyncio.create_task(recheck())

    async def close(self):
        if self._task and not self._task.done():
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
