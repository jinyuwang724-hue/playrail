"""Power limit (PL1/PL2) and CPU frequency cap, replacing 体感助手's TDP page.

The elevated ClawPower helper does the hardware writes; this module owns the
settings. Two plug-in presets (charger / battery) switch with the power source;
manual mode uses one value for both and the presets are not applied while it is
on. Smart mode moves the limit by itself from the game's real frame rate (see
SmartGovernor). The original register and power plan values are backed up before
the first write and can be put back with restore().
"""
import asyncio
import ctypes
from ctypes import wintypes as W
import json
import logging
import os
from pathlib import Path
import subprocess
import time

# Intel rates the Arc G3 Extreme for 8-35 W.
TDP_MIN, TDP_MAX = 8, 35
BOOST_MAX = 15
TICK = 1.0
TARGETS = (0, 30, 40, 45, 60, 90, 120)
FREQ_CHOICES = (0, 1500, 2000, 2250, 2500, 2750, 3000, 3500, 4000, 4500, 5000)
DEFAULTS = {
    "enabled": False, "mode": "smart", "boost": 1, "quick": [15, 20, 30],
    "ac": {"tdp": 35, "freq": 2750}, "dc": {"tdp": 25, "freq": 2750}, "manual": {"tdp": 20, "freq": 0},
    "smart": {"target": 60, "acMax": 35, "dcMax": 25, "floor": 8},
    # The OSD also keeps games such as 鸣潮 3.7 from crashing under RTSS (see ClawPower.cs Osd).
    "osd": {"enabled": True, "level": 2, "size": "medium", "rows": 1, "smallUnits": False},
    "fan": {"mode": "firmware", "curves": None, "full": False},  # curves: FAN_PRESETS until edited
}
# The toolbox drives the fan itself from the CPU package temperature: each mode is a curve of
# (°C, duty %) points, interpolated in between. Measured on the Claw 8 EX (2026-10-12):
# 20-40 % all sit at the ~2800 rpm floor, 50 % ~3060, 60 % ~3680, 70 % ~4300, 80 % ~4930,
# 100 % ~6100 rpm. The user wants it no louder than 4700 rpm, which is 76 %; only the
# full-speed switch goes past it.
FAN_CAP = 76
# Six duty points (%) per mode at FAN_TEMPS °C, linear in between. 均衡 is the user's curve
# (20 % ≤50 °C, 30 % 60 °C, 50 % 70 °C, 76 % ≥85 °C); 静音 is it 5 °C later, 性能 5 °C earlier.
# The user can edit every mode's points; these are the defaults.
FAN_TEMPS = (50, 60, 70, 75, 80, 90)
FAN_PRESETS = {"quiet": [20, 25, 40, 50, 59, 76], "balanced": [20, 30, 50, 59, 67, 76],
               "performance": [25, 40, 59, 67, 76, 76]}
# Hysteresis: speed up only after the curve has asked for more for FAN_RISE s straight, slow
# down only after it has asked for less for FAN_FALL s, so a short spike (loading screen,
# shader compile) does not make the fan surge and drop again. At FAN_HOT °C it reacts at once.
FAN_RISE, FAN_FALL, FAN_HOT = 6.0, 10.0, 95
FAN_MODES = ("firmware",) + tuple(FAN_PRESETS)


def _int(value, low, high, name):
    if type(value) is not int or not low <= value <= high:
        raise ValueError(f"{name}超出范围")
    return value


def _profile(value, name):
    if not isinstance(value, dict):
        raise ValueError(f"{name}格式错误")
    freq = value.get("freq")
    if freq not in FREQ_CHOICES:
        raise ValueError(f"{name}频率无效")
    return {"tdp": _int(value.get("tdp"), TDP_MIN, TDP_MAX, f"{name}功耗"), "freq": freq}


def validate(value):
    if not isinstance(value, dict) or type(value.get("enabled")) is not bool or value.get("mode") not in ("auto", "manual", "smart"):
        raise ValueError("功耗设置格式错误")
    smart = value.get("smart", DEFAULTS["smart"])
    if not isinstance(smart, dict) or smart.get("target") not in TARGETS:
        raise ValueError("目标帧率无效")
    floor = _int(smart.get("floor"), TDP_MIN, 20, "智能下限")
    smart = {"target": smart["target"], "floor": floor,
             "acMax": _int(smart.get("acMax"), floor, TDP_MAX, "插电上限"),
             "dcMax": _int(smart.get("dcMax"), floor, TDP_MAX, "电池上限")}
    osd = {**DEFAULTS["osd"], **(value.get("osd") or {})}  # 0.14.0 had per-item switches instead
    if (type(osd["enabled"]) is not bool or osd["level"] not in (1, 2, 3, 4) or osd["size"] not in ("small", "medium", "large")
            or osd["rows"] not in (1, 2) or type(osd["smallUnits"]) is not bool):
        raise ValueError("OSD 设置格式错误")
    osd = {"enabled": osd["enabled"], "level": osd["level"], "size": osd["size"], "rows": osd["rows"], "smallUnits": osd["smallUnits"]}
    quick = value.get("quick", DEFAULTS["quick"])
    if not isinstance(quick, list) or len(quick) != 3:
        raise ValueError("快捷档位格式错误")
    return {"enabled": value["enabled"], "mode": value["mode"], "boost": _int(value.get("boost"), 0, BOOST_MAX, "短时加成"),
            "quick": [_int(q, TDP_MIN, TDP_MAX, "快捷档位") for q in quick],
            "ac": _profile(value.get("ac"), "插电"), "dc": _profile(value.get("dc"), "电池"),
            "manual": _profile(value.get("manual"), "手动"), "smart": smart, "osd": osd, "fan": _fan(value.get("fan"))}


def _fan(value):
    fan = {**DEFAULTS["fan"], **(value or {})}
    if fan["mode"] == "custom":  # 0.16.9 had one custom curve instead of editable modes
        fan["mode"] = "balanced"
    if fan["curves"] is not None and not isinstance(fan["curves"], dict):
        raise ValueError("风扇设置格式错误")
    curves = {**FAN_PRESETS, **(fan["curves"] or {})}
    if (fan["mode"] not in FAN_MODES or type(fan["full"]) is not bool or set(curves) != set(FAN_PRESETS)
            or any(not isinstance(c, list) or len(c) != 6 or any(type(d) is not int or not 0 <= d <= 100 for d in c)
                   for c in curves.values())):
        raise ValueError("风扇设置格式错误")
    return {"mode": fan["mode"], "curves": {m: [min(d, FAN_CAP) for d in curves[m]] for m in FAN_PRESETS},
            "full": fan["full"]}


def fan_points(fan):
    return [[t, d] for t, d in zip(FAN_TEMPS, fan["curves"][fan["mode"]])]


def fan_duty_at(points, temp):
    """Duty on a (°C, %) curve, linear between points and flat beyond the ends."""
    if temp <= points[0][0]:
        return points[0][1]
    for (t0, d0), (t1, d1) in zip(points, points[1:]):
        if temp <= t1:
            return round(d0 + (d1 - d0) * (temp - t0) / (t1 - t0))
    return points[-1][1]


class FanGovernor:
    def __init__(self):
        self.history = []  # (time, duty the curve asks for)
        self.duty = None

    def reset(self):
        self.history, self.duty = [], None

    def update(self, points, temp, now):
        want = fan_duty_at(points, temp)
        self.history = [(t, w) for t, w in self.history if now - t < FAN_FALL] + [(now, want)]
        if self.duty is None or temp >= FAN_HOT:
            self.duty = max(want, self.duty or 0) if temp >= FAN_HOT else want
            return self.duty
        span = now - self.history[0][0]
        rise = min(w for t, w in self.history if now - t < FAN_RISE)
        if rise > self.duty and span >= FAN_RISE - 1:
            self.duty = rise
        elif span >= FAN_FALL - 1:
            fall = max(w for _, w in self.history)
            if fall < self.duty:
                self.duty = fall
        return self.duty


class PowerStatus(ctypes.Structure):
    _fields_ = [("ac", W.BYTE), ("battery", W.BYTE), ("percent", W.BYTE), ("saver", W.BYTE),
                ("life", W.DWORD), ("full", W.DWORD)]


def on_charger():
    status = PowerStatus()
    if not ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(status)):
        return True
    return status.ac != 0  # 1 online, 255 unknown counts as charger like Windows does


def battery_percent():
    status = PowerStatus()
    if not ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(status)):
        return None
    percent = status.percent & 0xFF
    return None if percent > 100 else percent


class LightStats:
    """CPU load from GetSystemTimes and memory from GlobalMemoryStatusEx; no counters."""
    class _Memory(ctypes.Structure):
        _fields_ = [("length", W.DWORD), ("load", W.DWORD)] + [(n, ctypes.c_ulonglong) for n in
                    ("total", "avail", "totalPage", "availPage", "totalVirtual", "availVirtual", "availExtended")]

    def __init__(self):
        self.last = None

    def read(self):
        k, result = ctypes.windll.kernel32, {}
        idle, kernel, user = W.FILETIME(), W.FILETIME(), W.FILETIME()
        if k.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user)):
            value = lambda t: (t.dwHighDateTime << 32) | t.dwLowDateTime
            now = (value(idle), value(kernel) + value(user))  # kernel time includes idle
            if self.last and now[1] > self.last[1]:
                busy = 1 - (now[0] - self.last[0]) / (now[1] - self.last[1])
                result["cpuUsage"] = round(max(0.0, min(1.0, busy)) * 100, 1)
            self.last = now
        memory = self._Memory(); memory.length = ctypes.sizeof(self._Memory)
        if k.GlobalMemoryStatusEx(ctypes.byref(memory)):
            result["ramUsedGB"] = round((memory.total - memory.avail) / 2**30, 1)
        return result


OSD_ZOOM = {"small": 2, "medium": 3, "large": 4}
# RTSS's Raster3D renderer (Implementation=2 in every profile here) draws single-byte
# Latin text only: CJK and ℃/° came out as boxes or nothing, whatever the face. The degree
# sign is therefore a superscript "o" (RTSS hypertext <S1>), all ASCII. The profiles keep
# their original face (Cascadia Mono; 0.16.2 had switched them to Microsoft YaHei UI).
OSD_FONT = "Cascadia Mono"
DEGREE = "<S1>o<S>C"
# RTSS zoom 3 with an 8-pixel font; frequencies keep one decimal so level 4 fits the width: level 4 spans the 1920-pixel screen.
# Zoom 7 overflowed by almost half; the user asked for ~1.25x the zoom-3/7-pixel size.
OSD_LEVEL_ZOOM = {1: 3, 2: 3, 3: 3, 4: 3}
OSD_FONT_HEIGHT = -8
# RTSS hypertext: <Cn=RRGGBB> defines palette colour n, <Cn>..<C> uses it; <S0=50> makes
# preset 0 a half-size superscript, used for units so the numbers stay readable.
# Catppuccin Mocha: pastel accents for the group names, white for the numbers and grey
# for time and secondary text. Values otherwise fall back to the RTSS profile colour (orange).
OSD_PALETTE = ("<C0=9399B2><C1=A6E3A1><C2=89B4FA><C3=CBA6F7><C4=FAB387><C5=F9E2AF><C6=89DCEB>"
               "<C7=FFFFFF><C8=94E2D5><S0=50><S1=60>")
# Plain-ASCII fallback, off. (It was on while a 鸣潮 crash was wrongly blamed on RTSS
# hypertext; the real cause was the game's -krqlv=uhd launch option.)
PLAIN_OSD = False
_TAG = __import__("re").compile(r"<[^>]*>")


SMALL_UNITS = [False]


def _u(unit):
    if unit == DEGREE or not SMALL_UNITS[0]:
        return unit
    return f"<S0>{unit}<S>"


def _num(value, digits=0):
    return None if value is None else (f"{value:.{digits}f}" if digits else str(round(value)))


def _item(label, value, unit=""):
    """A reading inside a labelled group: the group name says which hardware it is and the
    small unit says what the number is (%, GHz, °C, W), so items carry no label of their own
    (a label per item pushed level 4 past the screen edge). Kept only where a unit alone
    would be ambiguous."""
    return (f"<C0>{label}<C>" if label else "") + f"{value}{_u(unit) if unit else ''}"


class BatteryRate:
    """Charge or discharge power from RemainingCapacity (mWh) over the last minutes, for
    firmware that reports the rate as unknown (this Claw does)."""
    WINDOW = 180.0

    def __init__(self, clock=time.monotonic):
        self.clock, self.samples, self.charger = clock, [], None

    def update(self, remaining, charger):
        if remaining is None:
            return None
        now = self.clock()
        if charger != self.charger:
            self.samples, self.charger = [], charger
        if not self.samples or self.samples[-1][1] != remaining or now - self.samples[-1][0] > 5:
            self.samples.append((now, remaining))
        self.samples = [s for s in self.samples if now - s[0] <= self.WINDOW]
        (t0, r0), (t1, r1) = self.samples[0], self.samples[-1]
        if t1 - t0 < 15 or r1 == r0:
            return None
        return (r1 - r0) / 1000 / ((t1 - t0) / 3600)  # W, negative while discharging

def _clock(minutes):
    """2h17min / 45min."""
    hours, rest = int(minutes // 60), int(minutes % 60)
    return f"{hours}h{rest:02d}min" if hours else f"{rest}min"


def _battery(d, detailed, level=3):
    """Power and battery in one group at the right end: charge (+) or discharge (-) power,
    the TDP that is set, the charge level and the time left (to empty, or FULL in … while
    charging). ASCII only: the RTSS font shows CJK text as boxes."""
    watts, charger = d.get("batteryW") or 0, d.get("charger")
    parts = []
    if detailed:
        if charger and watts > 0.3:
            parts.append(f"<C1>+{watts:.1f}{_u('W')}<C>")
        elif charger:
            parts.append("<C1>FULL<C>" if d["battery"] >= 99 else "<C1>AC<C>")
        elif watts < -0.3:
            parts.append(f"<C4>-{abs(watts):.1f}{_u('W')}<C>")
    elif charger and d["battery"] >= 99 and watts <= 0.3:
        parts.append("<C1>FULL<C>")
    parts.append(f"{d['battery']}{_u('%')}")
    if charger and d.get("chargeMinutes") and watts > 0.3:
        parts.append(f"<C0>FULL in<C> {_clock(d['chargeMinutes'])}")
    elif not charger and d.get("minutes"):
        parts.append(_clock(d["minutes"]))
    return ("<C6>BAT<C> " if detailed else "") + " ".join(parts)

def osd_groups(level, d):
    """OSD groups for level 1-4 (简洁/标准/详细/完整). Levels 1-2 stay compact; 3-4 label every
    reading with what it is. The frame rate is RTSS's own count (frames presented, generated
    ones included); the game's real rendered rate from PresentMon is added when frame
    generation makes the two differ. "{GRAPH}" is RTSS's frame time graph (see Osd.Show)."""
    groups = []
    detailed = level >= 3
    if level >= 2 and d.get("time"):
        groups.append(f"<C0>{d['time']}<C>")
    rate = d.get("rtssFps")
    fps = f"<C1>{_num(rate) if rate is not None else '--'}<C>" + ("" if detailed else _u(" FPS"))
    if level >= 2 and d.get("frameTime"):
        fps += f" {d['frameTime']:.1f}{_u('ms')}"
    if level >= 4 and d.get("graph"):
        fps += "<C1>{GRAPH}<C>"  # the graph takes the text colour around it: FPS green
    if detailed and d.get("fps01"):
        fps += " " + _item("1%L ", _num(d["fps01"]))
    if d.get("fps") and d.get("presented") and d["presented"] > d["fps"] * 1.3:
        fps += " " + _item("REAL ", _num(d["fps"]))
    groups.append(("<C1>FPS<C> " if detailed else "") + fps)
    if detailed:
        cpu = [x for x in (d.get("cpuUsage") is not None and _item("", _num(d["cpuUsage"]), "%"),
                           d.get("cpuMhz") and _item("", f"{d['cpuMhz'] / 1000:.1f}", "GHz"),
                           d.get("cpuTemp") is not None and _item("", d["cpuTemp"], DEGREE),
                           d.get("cpuPower") is not None and _item("", f"{d['cpuPower']:.1f}", "W")) if x]
        if cpu:
            groups.append("<C2>CPU<C> " + " ".join(cpu))
        gpu = [x for x in (d.get("gpuUsage") is not None and _item("", _num(d["gpuUsage"]), "%"),
                           d.get("gpuMhz") and _item("", f"{d['gpuMhz'] / 1000:.1f}", "GHz"),
                           d.get("gpuTemp") and _item("", _num(d["gpuTemp"]), DEGREE),
                           d.get("gpuPower") is not None and _item("", f"{d['gpuPower']:.1f}", "W")) if x]
        if gpu:
            groups.append("<C3>GPU<C> " + " ".join(gpu))
        if d.get("limit"):
            groups.append("<C4>TDP<C> " + _item("", _num(d["limit"]), "W")
                          + (" " + _item("<C0>TGT<C> ", d["target"], "FPS") if level >= 4 and d.get("target") else ""))
        if d.get("fanRpm"):
            groups.append("<C8>FAN<C> " + _item("", d["fanRpm"], "rpm"))
        if d.get("ramUsed") is not None:
            groups.append(_item("<C5>RAM<C> ", f"{d['ramUsed']:.1f}" + (f"/{d['ramTotal']:.0f}" if level >= 4 and d.get("ramTotal") else ""), "GB"))
    elif level == 2:
        compact = [x for x in (d.get("package") is not None and f"<C4>{d['package']:.1f}<C>{_u('W')}",
                               d.get("cpuTemp") is not None and f"{d['cpuTemp']}{_u(DEGREE)}") if x]
        if compact:
            groups.append(" ".join(compact))
    if d.get("battery") is not None:
        groups.append(_battery(d, detailed, level))
    return groups

def osd_text(level, d, size="medium", rows=1, small_units=True):
    SMALL_UNITS[0] = small_units
    groups = osd_groups(level, d)

    if not groups:
        return ""
    # A large font cannot fit the detailed levels on one 1920-pixel line: wrap in two.
    if (size == "large" or rows == 2) and level >= 3 and len(groups) > 3:
        half = (len(groups) + 1) // 2
        lines = ["  ".join(groups[:half]), "  ".join(groups[half:])]
    else:
        # One space between groups (the coloured group names already separate them) keeps
        # level 4 inside the screen width at the 8-pixel font.
        lines = [(" " if level >= 3 else "  ").join(groups)]
    text = "\n".join(lines)
    if PLAIN_OSD:
        text = _TAG.sub("", text)
        for cn, en in (("内存", "RAM "), ("显存", "VRAM "), ("1%低", "1%L "), ("真实", "real "), ("补帧", "LS "),
                       ("目标", "target "), ("风扇", "FAN"), ("°C", "C"), ("°", "C")):
            text = text.replace(cn, en)
        return text
    # Every <C> (back to default) returns to the value colour instead of the profile colour.
    return OSD_PALETTE + "<C7>" + text.replace("<C>", "<C7>")


class ProcessEntry(ctypes.Structure):
    _fields_ = [("size", W.DWORD), ("usage", W.DWORD), ("pid", W.DWORD), ("heap", ctypes.c_size_t),
                ("module", W.DWORD), ("threads", W.DWORD), ("parent", W.DWORD), ("priority", W.LONG),
                ("flags", W.DWORD), ("exe", W.WCHAR * 260)]


def process_names():
    """pid -> exe name for every process. Works for elevated ones too, without a child process."""
    k = ctypes.windll.kernel32
    k.CreateToolhelp32Snapshot.restype = W.HANDLE
    snap = k.CreateToolhelp32Snapshot(2, 0)
    names = {}
    if not snap or snap == W.HANDLE(-1).value:
        return names
    try:
        entry = ProcessEntry(); entry.size = ctypes.sizeof(ProcessEntry)
        ok = k.Process32FirstW(W.HANDLE(snap), ctypes.byref(entry))
        while ok:
            names[entry.pid] = entry.exe.lower()
            ok = k.Process32NextW(W.HANDLE(snap), ctypes.byref(entry))
    finally:
        k.CloseHandle(W.HANDLE(snap))
    return names


def motion_assistant_running():
    return "motionassistant.exe" in process_names().values()


# Foreground windows that are never a game: Steam's own UI (the in-game overlay
# keeps the game in front), the shell, and this toolbox's helpers.
NOT_GAMES = {"steam.exe", "steamwebhelper.exe", "gameoverlayui.exe", "gameoverlayui64.exe", "explorer.exe",
             "searchhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe",
             "applicationframehost.exe", "lockapp.exe", "taskmgr.exe", "claude.exe", "msedge.exe",
             "losslessscaling.exe", "magpie.exe", "motionassistant.exe", "rtss.exe", "clash-verge.exe",
             "sunshine.exe", "pluginloader_noconsole.exe", "pluginloader.exe", "gamingmode.exe"}


def foreground_game():
    """(pid, exe) of the foreground window when it looks like a game, else None."""
    u = ctypes.windll.user32
    u.GetForegroundWindow.restype = W.HWND
    hwnd = u.GetForegroundWindow()
    if not hwnd:
        return None
    pid = W.DWORD()
    u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if not pid.value or pid.value == os.getpid():
        return None
    name = process_names().get(pid.value, "")
    if not name or name in NOT_GAMES or name.startswith("claw"):
        return None
    return pid.value, name


class SmartGovernor:
    """Finds the lowest power limit that still holds the target frame rate.

    Frame rate is the game's own rendered frames (PresentMon Application FPS), so
    XeSS frame generation or Lossless Scaling never makes a game look faster than
    it is. Watts are only added while the chip actually uses the whole limit
    ("bound"); a game held back by its CPU thread, an engine cap or a static
    visual-novel screen does not draw the limit, so it is lowered toward what it
    really draws instead. When two extra watts stop buying frames the governor
    remembers that ceiling for a while.
    """
    STEP_EVERY = 2.0
    HOLD = 60.0

    def __init__(self, clock=time.monotonic):
        self.clock = clock
        self.reset(None, 35)

    def reset(self, game, start):
        self.game, self.limit = game, start
        self.fps = self.draw = None
        self.last_step = self.clock()
        self.probe = None          # (limit before raising, fps before raising)
        self.ceiling = None        # (limit, until)
        self.reason = "等待帧率数据"

    def update(self, game, app_fps, draw, target, floor, cap, start=None):
        now = self.clock()
        if game != self.game:
            self.reset(game, cap if start is None else start)
        if self.ceiling and now >= self.ceiling[1]:
            self.ceiling = None
        alpha = .5
        if app_fps is not None:
            self.fps = app_fps if self.fps is None else self.fps + alpha * (app_fps - self.fps)
        if draw is not None:
            self.draw = draw if self.draw is None else self.draw + alpha * (draw - self.draw)
        self.limit = max(floor, min(cap, self.limit))
        if now - self.last_step < self.STEP_EVERY or self.draw is None:
            return self.limit
        top = min(cap, self.ceiling[0]) if self.ceiling else cap
        bound = self.draw >= self.limit - 1.5
        fps = self.fps
        step = 0
        if self.probe and fps is not None:
            before_limit, before_fps = self.probe
            if self.limit - before_limit >= 2:
                self.probe = None
                if before_fps and fps < before_fps * 1.03:
                    self.ceiling = (before_limit, now + self.HOLD)
                    self.limit = max(floor, before_limit)
                    self.last_step = now
                    self.reason = f"再加功耗帧数也不涨，停在 {self.limit} W"
                    return self.limit
        if fps is None or fps < 3:
            # No frames to judge (menus, desktop, a static scene): follow the real draw.
            if not bound and self.limit > floor:
                step = -2 if self.draw < self.limit - 5 else -1
                self.reason = "画面负载低，按实际耗电降低"
            elif bound and fps is None and self.limit < top:
                step = 1
                self.reason = "功耗用满，适当提高"
        elif target and fps >= target * 1.07:
            step = -2 if fps >= target * 1.3 else -1
            self.reason = f"帧率高于目标 {target}，降低功耗"
        elif target and fps < target * 0.95:
            if bound and self.limit < top:
                step = 2 if fps < target * 0.8 else 1
                self.reason = f"帧率低于目标 {target}，提高功耗"
            elif not bound and self.limit > floor:
                step = -1
                self.reason = "瓶颈不在功耗，多给也没用"
            else:
                self.reason = f"已到上限 {self.limit} W" if self.limit >= top else "保持"
        elif not target:
            if bound and self.limit < top:
                step = 2
                self.reason = "不限帧率：尝试更高功耗"
            elif not bound and self.limit > floor:
                step = -1
                self.reason = "不限帧率：按实际耗电降低"
        else:
            self.reason = f"帧率达到目标 {target}"
        if step:
            if step < 0:
                self.probe = None
            elif self.probe is None:
                self.probe = (self.limit, fps)
            self.limit = max(floor, min(top, self.limit + step))
            self.last_step = now
        return self.limit


class PowerControl:
    def __init__(self, root, data, emit):
        self.root, self.data, self.emit = Path(root), Path(data), emit
        version = json.loads((self.root / "plugin.json").read_text(encoding="utf-8"))["version"]
        self.exe = self.root / "bin" / f"ClawPower-{version}.exe"
        self.path, self.backup_path = self.data / "power.json", self.data / "power-backup.json"
        self.settings = json.loads(json.dumps(DEFAULTS))
        self.error = ""
        try:
            self.settings = validate({**DEFAULTS, **json.loads(self.path.read_text(encoding="utf-8"))})
        except FileNotFoundError:
            pass
        except (OSError, ValueError) as e:
            self.error = f"功耗设置无法读取，已暂停：{e}"
            self.settings["enabled"] = False
        self.process = None
        self.lock = asyncio.Lock()
        self.ids = 0
        self.actual = None
        self.charger = on_charger()
        self.reapplied = 0
        self.last_apply = None
        self.other_tool = False
        self.task = None
        self.closing = False
        self.written = None
        self.temperature = None
        self.temperature_at = 0.0
        self.package_at = 0.0
        self.frames_at = 0.0
        self.fan_state = {}
        self.gpu_state = {}
        self.battery_rate = BatteryRate()
        self.fan_error = ""
        self.fan_governor = FanGovernor()
        self.fan_written = None  # flat duty last written to the EC tables
        self.fan_backup_path = self.data / "fan-backup.json"
        self.stats = {}
        self.cpu = LightStats()
        self.last_frames = None
        self.osd_zoom = None
        self.osd_font = None
        self.osd_height = None
        self.osd = {}
        self.osd_text = ""
        self.governor = SmartGovernor()
        self.smart = {}

    # ---- helper -------------------------------------------------------------
    async def _start(self):
        if self.process and self.process.returncode is None:
            return
        if not self.exe.is_file():
            raise RuntimeError("功耗后台文件缺失，请重新安装插件")
        self.process = await asyncio.create_subprocess_exec(
            str(self.exe), cwd=str(self.exe.parent), stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
        ready = json.loads(await asyncio.wait_for(self.process.stdout.readline(), 40) or b"{}")
        if not ready.get("ready"):
            await self._stop()
            raise RuntimeError(ready.get("error") or "功耗后台未能获得管理员权限")

    async def _call(self, command, **args):
        async with self.lock:
            await self._start()
            self.ids += 1
            request = {"id": self.ids, "command": command, **args}
            try:
                self.process.stdin.write((json.dumps(request) + "\n").encode("utf-8"))
                await self.process.stdin.drain()
                while True:
                    line = await asyncio.wait_for(self.process.stdout.readline(), 15)
                    if not line:
                        raise RuntimeError("功耗后台已退出")
                    reply = json.loads(line.decode("utf-8-sig"))
                    if reply.get("id") == self.ids:
                        break
            except (asyncio.TimeoutError, RuntimeError, OSError, ValueError):
                await self._stop()
                raise RuntimeError("功耗后台没有响应，稍后自动重试")
            if not reply.get("ok"):
                raise RuntimeError(reply.get("error") or "功耗操作失败")
            if command in ("fan_read", "fan_curve", "fan_restore"):
                return reply.get("fan") or {}
            if command in ("thermal", "osd", "stats", "osd_zoom", "osd_height", "osd_font", "rtss_app", "gpu"):
                return reply.get(command) or {}
            if command == "frames":
                frames = reply.get("frames") or {}
                if frames.get("msrError"):
                    raise RuntimeError(frames["msrError"])
                self.actual = {**(self.actual or {}), **{k: frames[k] for k in ("pl1", "pl2", "package") if k in frames}}
                if "package" in frames:
                    self.package_at = time.monotonic()
                self.last_frames = frames
                self.frames_at = time.monotonic()
                return frames
            power = reply.get("power") or {}
            if power.get("msrError"):
                raise RuntimeError(power["msrError"])
            if "package" in power:
                self.package_at = time.monotonic()
            elif (self.actual or {}).get("package") is not None and time.monotonic() - self.package_at < 10:
                # A read without sampling has no package power; keep the last recent one.
                power = {**power, "package": self.actual["package"]}
            self.actual = power
            return power

    async def _stop(self):
        process, self.process = self.process, None
        if process and process.returncode is None:
            try:
                process.stdin.close()
                await asyncio.wait_for(process.wait(), 6)
            except (asyncio.TimeoutError, OSError):
                process.kill()

    # ---- policy -------------------------------------------------------------
    def target(self):
        s = self.settings
        if s["mode"] == "smart":
            smart = s["smart"]
            cap = smart["acMax"] if self.charger else smart["dcMax"]
            limit = self.governor.limit = max(smart["floor"], min(cap, self.governor.limit))
        else:
            limit = (s["manual"] if s["mode"] == "manual" else s["ac" if self.charger else "dc"])["tdp"]
        return limit, limit + s["boost"]

    def freq_target(self):
        s = self.settings
        if s["mode"] == "manual":
            return s["manual"]["freq"], s["manual"]["freq"]
        return s["ac"]["freq"], s["dc"]["freq"]

    async def _backup(self, power):
        if self.backup_path.exists():
            return
        freq = power.get("freq") or {}
        record = {"raw": power.get("raw"), "pl1": power.get("pl1"), "pl2": power.get("pl2"), "freq": freq,
                  "savedAt": time.strftime("%Y-%m-%d %H:%M:%S")}
        if not record["raw"] or not freq.get("scheme"):
            raise RuntimeError("无法读取原始功耗设置，未做任何修改")
        temp = self.backup_path.with_suffix(".tmp")
        temp.write_text(json.dumps(record, ensure_ascii=False, indent=1), encoding="utf-8")
        os.replace(temp, self.backup_path)

    async def _apply(self, force=False):
        power = self.actual if (self.actual and not force) else await self._call("read")
        await self._backup(power)
        pl1, pl2 = self.target()
        drift = abs(power.get("pl1", 0) - pl1) > .2 or abs(power.get("pl2", 0) - pl2) > .2 \
            or power.get("pl1Enabled") is False or power.get("pl2Enabled") is False
        if force or drift:
            # Count only changes made by someone else, not our own new target.
            if not force and self.written and (abs(power.get("pl1", 0) - self.written[0]) > .2
                                               or abs(power.get("pl2", 0) - self.written[1]) > .2):
                self.reapplied += 1
            power = await self._call("set_tdp", pl1=pl1, pl2=pl2)
            self.written = (pl1, pl2)
        ac, dc = self.freq_target()
        freq = power.get("freq") or {}
        if any(freq.get(k) not in (None, v) for k, v in (("ac", ac), ("dc", dc), ("ac1", ac), ("dc1", dc))):
            power = await self._call("set_freq", scheme=freq.get("scheme"), ac=ac, dc=dc, ac1=ac, dc1=dc)
        self.last_apply = time.time()
        if abs(power.get("pl1", 0) - pl1) > .2 or abs(power.get("pl2", 0) - pl2) > .2:
            raise RuntimeError("功耗写入后回读不一致，可能被其他软件或固件改回")

    async def _smart_tick(self):
        s = self.settings["smart"]
        cap = s["acMax"] if self.charger else s["dcMax"]
        game = await asyncio.to_thread(foreground_game)
        frames = await self._call("frames", pid=game[0] if game else 0)
        chains = [c for c in frames.get("swapChains") or [] if c.get("presentedFps", 0) > 0]
        # The game's main swap chain is the one presenting the most frames.
        chain = max(chains, key=lambda c: c.get("presentedFps", 0)) if chains else None
        app_fps = chain.get("appFps") if chain else (0.0 if game else None)
        limit = self.governor.update(game[1] if game else None, app_fps, frames.get("package"),
                                     s["target"], s["floor"], cap)
        self.smart = {"game": game[1] if game else None, "fps": chain.get("appFps") if chain else None,
                      "presentedFps": chain.get("presentedFps") if chain else None,
                      "gpuBusy": chain.get("gpuBusy") if chain else None, "limit": limit,
                      "reason": self.governor.reason, "framesError": frames.get("framesError")}

    async def _loop(self):
        tick = 0
        while not self.closing:
            try:
                await asyncio.sleep(TICK)
                tick += 1
                osd_on = self.settings["osd"]["enabled"]
                await self._fan_tick(tick)
                if not self.settings["enabled"]:
                    if osd_on:
                        await self._osd_tick(tick, None)
                    continue
                charger = on_charger()
                changed = charger != self.charger
                self.charger = charger
                if tick % 10 == 1:
                    self.other_tool = await asyncio.to_thread(motion_assistant_running)
                smart = self.settings["mode"] == "smart"
                if smart:
                    await self._smart_tick()
                if osd_on:
                    await self._osd_tick(tick, self.smart if smart else None)
                if not smart:
                    if tick % 3 and not changed:
                        continue
                    await self._call("read")
                await self._apply(force=changed and not smart)
                if self.error:
                    self.error = ""
                if not smart or tick % 2 == 0:
                    await self.publish()
            except asyncio.CancelledError:
                raise
            except Exception as e:
                logging.warning("Power control: %s", e)
                if str(e) != self.error:
                    self.error = str(e)
                    await self.publish()

    async def _osd_tick(self, tick, smart):
        """Writes the OSD; reuses the smart tick's frame data when it ran."""
        osd = self.settings["osd"]
        game = await asyncio.to_thread(foreground_game)
        if smart is None:
            # Real rendered rate, 1% low and GPU telemetry from PresentMon, plus package power.
            await self._call("frames", pid=game[0] if game else 0)
        rtss = await self._call("rtss_app", pid=game[0]) if game else {}
        frames = self.last_frames or {}
        chains = [c for c in frames.get("swapChains") or [] if c.get("presentedFps", 0) > 0]
        chain = max(chains, key=lambda c: c.get("presentedFps", 0)) if chains else {}
        if tick % 2 == 1 or self.temperature is None:
            try:
                if time.monotonic() - self.temperature_at > 0.9:  # else the fan tick just read it
                    self.temperature = (await self._call("thermal")).get("package")
                    self.temperature_at = time.monotonic()
            except RuntimeError:
                self.temperature = None
            if osd["level"] >= 2:  # level 2 shows the battery time too
                try:
                    self.stats = await self._call("stats")
                except RuntimeError:
                    self.stats = await asyncio.to_thread(self.cpu.read)
                try:
                    gpu = await self._call("gpu")
                    self.gpu_state = {k: gpu[k] for k in ("gpuMhz", "gpuTemp", "gpuPower") if k in gpu}
                except RuntimeError:
                    self.gpu_state = {}
        if self.osd_zoom != OSD_LEVEL_ZOOM[osd["level"]]:
            await self._apply_zoom()
        if self.osd_font != OSD_FONT:
            await self._apply_font()
        if self.osd_height != OSD_FONT_HEIGHT:
            self.osd_height = OSD_FONT_HEIGHT
            try:
                result = await self._call("osd_height", height=OSD_FONT_HEIGHT)
                backup = self.data / "osd-height-backup.json"
                if not backup.exists() and result.get("previous"):
                    backup.write_text(json.dumps(result["previous"], ensure_ascii=False, indent=1), encoding="utf-8")
            except RuntimeError as e:
                self.osd = {**self.osd, "error": f"字号设置失败：{e}"}
        s = dict(self.stats) if osd["level"] >= 2 else {}
        charger = on_charger()
        remaining, full = s.get("batteryRemaining"), s.get("batteryFull")
        estimate = self.battery_rate.update(remaining, charger)
        if not s.get("batteryW") and estimate is not None:
            s["batteryW"] = round(estimate, 1)
        watts = s.get("batteryW") or 0
        if remaining and watts < -0.3 and not s.get("batteryMinutes"):
            s["batteryMinutes"] = round(remaining / 1000 / -watts * 60)
        if remaining and full and watts > 0.3 and not s.get("chargeMinutes"):
            s["chargeMinutes"] = round(max(0, full - remaining) / 1000 / watts * 60)
        smart_on = self.settings["enabled"] and self.settings["mode"] == "smart"
        data = {"time": time.strftime("%H:%M"), "rtssFps": rtss.get("fps"), "frameTime": rtss.get("frameTime"),
                "fps": chain.get("appFps"), "presented": chain.get("presentedFps"),
                "fps01": frames.get("appFps01"), "package": (self.actual or {}).get("package"),
                "limit": (self.actual or {}).get("pl1"),
                "target": self.settings["smart"]["target"] if smart_on else None,
                "cpuUsage": s.get("cpuUsage"), "cpuMhz": s.get("cpuMhz"), "cpuTemp": self.temperature,
                "gpuUsage": s.get("gpuUsage"), "gpuMhz": frames.get("gpuMhz"), "gpuTemp": frames.get("gpuTemp"),
                "gpuPower": frames.get("gpuPower"), "ramUsed": s.get("ramUsedGB"), "gpuMem": s.get("gpuMemMB"),
                "battery": battery_percent(), "charger": on_charger(), "batteryW": s.get("batteryW"),
                "minutes": s.get("batteryMinutes"), "chargeMinutes": s.get("chargeMinutes"), "fanRpm": self.fan_state.get("rpm1"), "fanDuty": self.fan_state.get("duty1"),
                "cpuPower": frames.get("cpuPower"), "ramTotal": s.get("ramTotalGB"), "graph": osd["level"] >= 3, **self.gpu_state}
        text = osd_text(osd["level"], data, osd["size"], osd["rows"], osd["smallUnits"])
        self.osd = await self._call("osd", text=text)
        self.osd_text = text

    async def _apply_font(self):
        """A font with ℃ in every RTSS profile; the faces found first go to osd-font-backup.json."""
        self.osd_font = OSD_FONT  # once per run, also when it fails
        try:
            result = await self._call("osd_font", face=OSD_FONT)
            backup = self.data / "osd-font-backup.json"
            if not backup.exists() and result.get("previous"):
                backup.write_text(json.dumps(result["previous"], ensure_ascii=False, indent=1), encoding="utf-8")
        except RuntimeError as e:
            self.osd = {**self.osd, "error": f"字体设置失败：{e}"}

    async def _apply_zoom(self):
        """RTSS font zoom for the chosen size, in every RTSS profile. The values found the
        first time are kept in osd-zoom-backup.json."""
        zoom = OSD_LEVEL_ZOOM[self.settings["osd"]["level"]]
        try:
            result = await self._call("osd_zoom", zoom=zoom)
            backup = self.data / "osd-zoom-backup.json"
            if not backup.exists() and result.get("previous"):
                backup.write_text(json.dumps(result["previous"], ensure_ascii=False, indent=1), encoding="utf-8")
            self.osd_zoom = zoom
        except RuntimeError as e:
            self.osd_zoom = zoom  # do not retry every second; the error is shown once
            self.osd = {**self.osd, "error": f"字号设置失败：{e}"}

    async def _fan_tick(self, tick):
        """Runs the toolbox curve once a second with hysteresis, keeps it in place (the EC can
        be reset by sleep or MSI tools) and reads fan speed for the OSD. Firmware mode with a
        short OSD needs no helper."""
        fan = self.settings["fan"]
        managed = fan["mode"] != "firmware"
        wants_rpm = self.settings["osd"]["enabled"] and self.settings["osd"]["level"] >= 3
        if not managed and not wants_rpm:
            return
        try:
            if managed and not fan["full"]:
                self.temperature = (await self._call("thermal")).get("package")
                self.temperature_at = time.monotonic()
                if self.temperature is not None:
                    duty = self.fan_governor.update(fan_points(fan), self.temperature, time.monotonic())
                    if duty != self.fan_written:
                        await self._apply_fan()
            if tick % 3 == 0 or not self.fan_state:
                state = await self._call("fan_read")
                self.fan_state = state
                if managed and tick % 9 == 0:
                    duties = [100] * 6 if fan["full"] else [self.fan_written] * 6
                    if (not state.get("software") or state.get("cpu") != duties or state.get("gpu") != duties
                            or state.get("fullSpeed") != fan["full"]):
                        await self._apply_fan()
            self.fan_error = ""
        except RuntimeError as e:
            self.fan_error = str(e)

    async def _apply_fan(self):
        fan = self.settings["fan"]
        if fan["mode"] == "firmware":
            self.fan_governor.reset()
            self.fan_written = None
            backup = None
            if self.fan_backup_path.exists():
                backup = json.loads(self.fan_backup_path.read_text(encoding="utf-8"))
            self.fan_state = await self._call("fan_restore", **({"cpu": backup["cpu"], "gpu": backup["gpu"]} if backup else {}))
            return
        if not self.fan_backup_path.exists():
            state = await self._call("fan_read")
            if state.get("software"):
                raise RuntimeError("风扇当前不是固件控制，未备份原曲线，请先在其他软件里恢复自动")
            temp = self.fan_backup_path.with_suffix(".tmp")
            temp.write_text(json.dumps({"cpu": state["cpu"], "gpu": state["gpu"], "cpuTemps": state.get("cpuTemps"),
                                        "savedAt": time.strftime("%Y-%m-%d %H:%M:%S")}, ensure_ascii=False), encoding="utf-8")
            os.replace(temp, self.fan_backup_path)
        if fan["full"]:
            duties = [100] * 6
        else:
            if self.fan_governor.duty is None:
                if self.temperature is None or time.monotonic() - self.temperature_at > 5:
                    self.temperature = (await self._call("thermal")).get("package")
                    self.temperature_at = time.monotonic()
                self.fan_governor.update(fan_points(fan), self.temperature or 0, time.monotonic())
            # One flat duty on all six EC points: the toolbox, not the EC, follows the temperature.
            duties = [self.fan_governor.duty] * 6
        self.fan_state = await self._call("fan_curve", cpu=duties, gpu=duties, full=fan["full"])
        self.fan_written = duties[0] if not fan["full"] else None

    async def _fan_fallback(self):
        """When the toolbox stops, leave the EC its own curve built from the chosen mode (capped
        the same way) so the fan still follows the temperature without the toolbox."""
        fan = self.settings["fan"]
        if fan["mode"] == "firmware" or fan["full"] or not self.process or self.process.returncode is not None:
            return
        try:
            axis = (self.fan_state or {}).get("cpuTemps") or [47, 50, 57, 64, 71, 78]
            duties = [fan_duty_at(fan_points(fan), t) for t in axis]
            duties[-1] = max(duties[-1], FAN_CAP)
            await self._call("fan_curve", cpu=duties, gpu=duties, full=False)
        except RuntimeError as e:
            logging.warning("Fan fallback curve: %s", e)

    async def _osd_off(self):
        if self.process and self.process.returncode is None:
            try:
                await self._call("osd", text="")
            except RuntimeError:
                pass
        self.osd = {}
        self.osd_text = ""

    def start(self):
        if self.task is None:
            self.task = asyncio.create_task(self._loop())

    def _live_fps(self):
        if time.monotonic() - self.frames_at > 5:
            return None
        chains = [c for c in (self.last_frames or {}).get("swapChains") or [] if c.get("presentedFps", 0) > 0]
        return max(chains, key=lambda c: c.get("presentedFps", 0)).get("appFps") if chains else None

    def view(self):
        pl1, pl2 = self.target()
        a = self.actual or {}
        return {"settings": self.settings, "charger": self.charger, "target": {"pl1": pl1, "pl2": pl2},
                "actual": {k: a.get(k) for k in ("pl1", "pl2", "package", "locked", "tdpBase", "freq")} if a else None,
                "backup": self.backup_path.exists(), "otherTool": self.other_tool, "reapplied": self.reapplied,
                "smart": self.smart if self.settings["mode"] == "smart" else None, "targets": list(TARGETS),
                "fan": {**self.fan_state, "error": self.fan_error, "presets": FAN_PRESETS, "cap": FAN_CAP,
                        "axis": list(FAN_TEMPS), "target": self.fan_governor.duty},
                # Read-only readings for the toolbox home; a temperature older than 15 s is not shown.
                "live": {"battery": battery_percent(),
                         "cpuTemp": self.temperature if time.monotonic() - self.temperature_at < 15 else None,
                         # Real rendered frame rate from the OSD or smart tick, in any mode.
                         "fps": self._live_fps()},
                "osd": {"rtss": self.osd.get("rtss"), "text": self.osd_text, "error": self.osd.get("error")},
                "error": self.error, "limits": {"tdpMin": TDP_MIN, "tdpMax": TDP_MAX, "boostMax": BOOST_MAX,
                                                 "freq": list(FREQ_CHOICES)}}

    async def publish(self):
        try:
            await self.emit(self.view())
        except Exception:
            logging.exception("Power status emit failed")

    # ---- api ----------------------------------------------------------------
    async def get(self, sample=False):
        self.charger = on_charger()
        self.other_tool = await asyncio.to_thread(motion_assistant_running)
        # Not managing power: keep no elevated helper or driver handle around at all.
        if not self.settings["enabled"]:
            return self.view()
        try:
            await self._call("read", sample=bool(sample))
        except Exception as e:
            self.error = str(e)
        try:
            self.temperature = (await self._call("thermal")).get("package")
            self.temperature_at = time.monotonic()
        except Exception:
            pass  # The toolbox home then shows no temperature.
        return self.view()

    async def save(self, value):
        next_settings = validate(value)
        previous = self.settings
        self.settings = next_settings
        temp = self.path.with_suffix(".tmp")
        temp.write_text(json.dumps(next_settings, ensure_ascii=False), encoding="utf-8")
        os.replace(temp, self.path)
        self.charger = on_charger()
        if next_settings["enabled"]:
            try:
                await self._apply(force=True)
                self.error = ""
            except Exception as e:
                self.error = str(e)
        else:
            self.error = ""
            self.smart = {}
        if next_settings["fan"] != previous["fan"]:
            self.fan_governor.reset()
            try:
                await self._apply_fan()
                self.fan_error = ""
            except RuntimeError as e:
                self.fan_error = str(e)
        if not next_settings["osd"]["enabled"]:
            await self._osd_off()
            if not next_settings["enabled"] and next_settings["fan"]["mode"] == "firmware":
                await self._stop()  # nothing left that needs the elevated helper
        return self.view()

    async def restore(self):
        if not self.backup_path.exists():
            raise RuntimeError("没有原始值备份：插件尚未修改过功耗")
        record = json.loads(self.backup_path.read_text(encoding="utf-8"))
        self.settings = {**self.settings, "enabled": False}
        temp = self.path.with_suffix(".tmp")
        temp.write_text(json.dumps(self.settings, ensure_ascii=False), encoding="utf-8")
        os.replace(temp, self.path)
        await self._call("restore_tdp", raw=record["raw"])
        freq = record.get("freq") or {}
        current = (self.actual or {}).get("freq") or {}
        if current.get("scheme") == freq.get("scheme"):
            await self._call("set_freq", scheme=freq["scheme"],
                             **{k: freq[k] for k in ("ac", "dc", "ac1", "dc1") if freq.get(k) is not None})
        self.error = ""
        if not self.settings["osd"]["enabled"]:
            await self._stop()
        return self.view()

    async def close(self):
        self.closing = True
        if self.task:
            self.task.cancel()
            try:
                await self.task
            except asyncio.CancelledError:
                pass
        await self._fan_fallback()
        await self._stop()
