"""Agent-owned fixed overlay. Stdin EOF closes it; it never receives RPC credentials."""
import asyncio
import ctypes
import json
import math
import subprocess
import time
from collections import deque


_SETTINGS = ("enabled", "level", "position", "scale", "size")
_READINGS = frozenset((
    "battery", "batteryW", "batteryWSource", "charger", "cpuCoreMhz", "cpuCores",
    "cpuMhz", "cpuPower", "cpuTemp", "cpuUsage", "displayed", "displayHzHistory",
    "fanRpm", "fps", "fps01", "fpsKind", "fpsLabel", "framePoints", "frameTime",
    "frameTimeKind", "frameTimes", "gpuMem", "gpuMhz", "gpuPower", "gpuTemp",
    "gpuUsage", "graphEnd", "graphEpoch", "minutes", "presented", "ramUsed",
    "upscalerName", "upscalerText", "vramMhz",
))
_FPS = frozenset(("displayed", "fps", "fpsKind", "presented"))
_BAR = _FPS | frozenset(("battery", "batteryW", "batteryWSource", "charger", "cpuPower",
    "cpuUsage", "fpsLabel", "framePoints", "frameTimeKind", "frameTimes", "gpuMem",
    "gpuPower", "gpuUsage", "graphEnd", "graphEpoch", "minutes", "ramUsed"))
_AVERAGED_KEYS = ("cpuPower", "gpuPower", "batteryW", "cpuUsage", "gpuUsage")


class FrameCadence:
    """Keep game cadence; back off when a foreground window has stopped presenting."""
    def __init__(self, clock=time.monotonic, foreground=None):
        if foreground is None:
            foreground = ctypes.windll.user32.GetForegroundWindow
            foreground.restype = ctypes.c_void_p
        self.clock, self.foreground = clock, foreground
        self.window = None
        self.next_poll = 0.0
        self.marker = None
        self.last_frame = 0.0

    def due(self):
        window = self.foreground()
        if window != self.window:
            self.reset()
            self.window = window
        return self.clock() >= self.next_poll

    def finish(self, sample):
        now = self.clock()
        active = not sample.get("frameHeld") and any(
            sample.get(key) for key in ("fps", "presented", "displayed")
        )
        end, count = sample.get("graphEnd"), sample.get("frameCount")
        if active and (end is not None or count is not None):
            marker = sample.get("graphEpoch"), end, count
            if marker != self.marker:
                self.marker, self.last_frame = marker, now
            active = now - self.last_frame < .3
        # The caller already waits 50 ms between ticks.
        self.next_poll = 0.0 if active else now + .25

    def reset(self):
        self.window, self.next_poll = None, 0.0
        self.marker, self.last_frame = None, 0.0


class HardwareDisplay:
    INTERVAL = 2.0
    POWER_WINDOW = 8.0
    RUNTIME_WINDOW = 180.0
    RUNTIME_MINIMUM = 15.0

    def __init__(self):
        self.reset()

    def reset(self):
        self.samples = deque()
        self.context = self.last_sample = None
        self.next_update = 0.0
        self.values = {}

    def update(self, sample, now):
        sample_id = sample.get("sampleId")
        if sample_id is None:
            self.reset()
            return {}
        context = sample.get("charger"), sample.get("batteryWSource")
        if context != self.context or (self.samples and now - self.samples[-1][0] > 3):
            self.reset()
            self.context = context
        if sample_id == self.last_sample:
            return self.values
        self.last_sample = sample_id
        readings = {}
        for key in _AVERAGED_KEYS:
            value = sample.get(key)
            if type(value) in (int, float) and math.isfinite(value):
                readings[key] = value
        self.samples.append((now, readings))
        while self.samples[0][0] < now - self.RUNTIME_WINDOW:
            self.samples.popleft()
        if now < self.next_update:
            return self.values
        self.next_update = now + self.INTERVAL
        recent = [values for at, values in self.samples if at >= now - self.POWER_WINDOW]
        self.values = {}
        for key in _AVERAGED_KEYS:
            values = [values[key] for values in recent if key in values]
            self.values[key] = round(sum(values) / len(values), 1) if key in readings else None

        minutes = None
        remaining = sample.get("batteryRemaining")
        if not context[0] and type(remaining) in (int, float) and math.isfinite(remaining) and remaining > 0:
            discharge = [(at, -values["batteryW"]) for at, values in self.samples if values.get("batteryW", 0) < 0]
            if readings.get("batteryW", 0) < 0 and discharge and now - discharge[0][0] >= self.RUNTIME_MINIMUM:
                watts = sum(value for _, value in discharge) / len(discharge)
                if watts > 0.3:
                    minutes = round(remaining * 0.06 / watts)  # mWh to Wh, then hours to minutes
        self.values["minutes"] = minutes
        return self.values


class FixedOverlay:
    def __init__(self, root):
        self.exe = root / "bin/ClawPerformanceOverlay.exe"
        self.process = None
        self.lock = asyncio.Lock()
        self.last_payload = None
        self.revision = None
        self.last_sent = 0.0
        self.hardware_display = HardwareDisplay()
        # Quick panel owns visibility while its lease is active.
        self.suppressed = False

    async def update(self, state):
        async with self.lock:
            self.revision = None
            await self._update(state["settings"], lambda: state)

    async def refresh(self, settings, revision, view):
        async with self.lock:
            await self._update(settings, view, revision)

    async def _update(self, settings, view, revision=None):
        if self.suppressed or not settings["enabled"] or settings["renderer"] != "gamebar":
            await self._close()
            return
        if not self.process or self.process.returncode is not None:
            if not self.exe.is_file():
                return
            self.process = await asyncio.create_subprocess_exec(str(self.exe), stdin=asyncio.subprocess.PIPE,
                stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW)
            self.last_payload = self.revision = None
        now = time.monotonic()
        payload = None
        if revision is None or revision != self.revision:
            level = settings.get("level")
            keys = _FPS if level == 1 else _BAR if level == 2 else _READINGS
            state = view()
            sample = state.get("sample", {})
            if level != 1:
                if state.get("hardwareHeld"):
                    self.hardware_display.reset()
                    sample = {**sample, **dict.fromkeys((*_AVERAGED_KEYS, "minutes"))}
                else:
                    sample = {**sample, **self.hardware_display.update(sample, now)}
            elif self.hardware_display.samples:
                self.hardware_display.reset()
            payload = {"settings": {key: settings[key] for key in _SETTINGS if key in settings},
                       "sample": {key: value for key, value in sample.items() if key in keys}}
            self.revision = revision
        if payload is None or payload == self.last_payload:
            if now - self.last_sent < 5:
                return
            payload = None
        try:
            packet = {"heartbeat": True} if payload is None else payload
            if payload is not None and self.last_payload is not None:
                previous, current = self.last_payload["sample"], payload["sample"]
                packet = {"delta": {key: value for key, value in current.items()
                                    if key not in previous or value != previous[key]}}
                removed = [key for key in previous if key not in current]
                if removed:
                    packet["remove"] = removed
                if payload["settings"] != self.last_payload["settings"]:
                    packet["settings"] = payload["settings"]
            self.process.stdin.write((json.dumps(packet, ensure_ascii=True, separators=(",", ":")) + "\n").encode("utf-8"))
            await asyncio.wait_for(self.process.stdin.drain(), 1)
            self.last_sent = now
            if payload is not None:
                self.last_payload = payload
        except (BrokenPipeError, ConnectionError, asyncio.TimeoutError):
            await self._close()

    async def close(self, timeout=2):
        async with self.lock:
            await self._close(timeout)

    async def _close(self, timeout=2):
        process, self.process = self.process, None
        self.last_payload = self.revision = None
        if self.hardware_display.samples:
            self.hardware_display.reset()
        if not process:
            return
        if process.stdin:
            process.stdin.close()
        try:
            await asyncio.wait_for(process.wait(), timeout)
        except asyncio.TimeoutError:
            process.kill()
            await process.wait()
