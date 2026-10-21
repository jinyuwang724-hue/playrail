"""Power limit (PL1/PL2) and CPU frequency cap, replacing 体感助手's TDP page.

The elevated ClawPower helper does the hardware writes; this module owns the
settings. Two plug-in presets (charger / battery) switch with the power source;
manual mode uses one value for both and the presets are not applied while it is
on. AutoTDP uses FPS feedback. Legacy Intel sessions are only kept for cleanup.
The original
register and power plan values are backed up before
the first write and can be put back with restore().
"""
import asyncio
import ctypes
from ctypes import wintypes as W
import json
import math
import importlib.util
import logging
import os
from pathlib import Path
import subprocess
import time
import winreg

# Intel rates the Arc G3 Extreme for 8-35 W.
TDP_MIN, TDP_MAX = 8, 35
BOOST_MAX = 15
TICK = 1.0
# Every cap a saved setting may hold (older versions offered up to 5 GHz).
FREQ_CHOICES = (0, 1500, 2000, 2250, 2500, 2750, 3000, 3250, 3500, 3750, 4000, 4250, 4500, 4750, 5000)
# Offered in the panel: fine steps where handheld power limits actually land, only below this
# CPU's own maximum turbo (read from MSR 0x1AD; 0 = no cap is the maximum itself).
FREQ_OFFERED = (1500, 2000, 2250, 2500, 2750, 3000, 3250, 3500, 4000, 4500)


def freq_offered(cpu_max, selected=()):
    """Choices for this CPU: below its max turbo, plus any value already saved."""
    offered = [f for f in FREQ_OFFERED if not cpu_max or f < cpu_max]
    return [0] + sorted(set(offered) | {f for f in selected if f})


def max_turbo_mhz(cpu):
    """Highest single-core turbo from MSR 0x1AD (one ratio per byte, 100 MHz each)."""
    try:
        ratios = bytes.fromhex(cpu["turboRatios"])
    except (KeyError, TypeError, ValueError):
        return None
    top = max(ratios, default=0)
    return top * 100 if 10 <= top <= 80 else None
DEFAULTS = {
    "enabled": False, "mode": "autotdp", "boost": 1, "quick": [15, 20, 30],
    "ac": {"tdp": 35, "freq": 2750}, "dc": {"tdp": 25, "freq": 2750}, "manual": {"tdp": 20, "freq": 0},
    "autotdp": {"target": 60, "floor": 12, "acMax": 35, "dcMax": 25},
    # The OSD also keeps games such as 鸣潮 3.7 from crashing under RTSS (see ClawPower.cs Osd).
    "osd": {"enabled": True, "level": 2, "size": "medium", "rows": 1, "smallUnits": False},
    "fan": {"mode": "balanced", "curves": None, "full": False, "control": "custom"},
}
# The toolbox follows temperature and writes a flat duty to both fan tables.
# Autonomous custom EC curves were not verified on the CG3EM; see docs/fan-ec-integration.md.
# Measured on the Claw 8 EX (2026-10-12):
# 20-40 % all sit at the ~2800 rpm floor, 50 % ~3060, 60 % ~3680, 70 % ~4300, 80 % ~4930,
# 100 % ~6100 rpm. The user wants 静音 and 均衡 no louder than 4700 rpm, which is 76 %;
# 性能 may go past it.
FAN_CAP = 76
FAN_CAPS = {"quiet": FAN_CAP, "balanced": FAN_CAP, "performance": 100}
# Avoid audible oscillation: the curve follows a smoothed package temperature (it jumps 65→86 °C within a
# second even at 10 W); raises wait FAN_RISE, drops FAN_FALL; only a really hot reading goes straight up.
FAN_RISE, FAN_FALL, FAN_HOT, FAN_SMOOTH = 4.0, 10.0, 92, 8.0
# Six duty points (%) per mode at FAN_TEMPS °C, linear in between. 均衡 is the user's curve
# (20 % ≤50 °C, 30 % 60 °C, 50 % 70 °C, 76 % ≥80 °C); 静音 is it 5 °C later; 性能 is more
# aggressive and reaches full speed at 90 °C. The user can edit every mode's points.
FAN_TEMPS = (50, 60, 70, 75, 80, 90)
FAN_PRESETS = {"quiet": [20, 25, 40, 50, 63, 76], "balanced": [20, 30, 50, 63, 76, 76],
               "performance": [30, 45, 63, 76, 88, 100]}
FAN_MODES = tuple(FAN_PRESETS)


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
    if not isinstance(value, dict) or type(value.get("enabled")) is not bool or value.get("mode") not in ("auto", "manual", "smart", "autotdp"):
        raise ValueError("功耗设置格式错误")
    # Old clients/saved Intel selections migrate to AutoTDP; they can no longer enable Intel policy.
    mode = "autotdp" if value["mode"] == "smart" else value["mode"]
    auto = value.get("autotdp", DEFAULTS["autotdp"])
    if not isinstance(auto, dict):
        raise ValueError("Auto 功耗设置格式错误")
    auto = {**DEFAULTS["autotdp"], **auto}
    auto = {"target": _int(auto["target"], 20, 240, "目标帧率"),
            **{k: _int(auto[k], TDP_MIN, TDP_MAX, "Auto 功耗") for k in ("floor", "acMax", "dcMax")}}
    if auto["floor"] > min(auto["acMax"], auto["dcMax"]):
        raise ValueError("Auto 功耗下限不能高于插电或电池上限")
    osd = {**DEFAULTS["osd"], **(value.get("osd") or {})}  # 0.14.0 had per-item switches instead
    if (type(osd["enabled"]) is not bool or osd["level"] not in (1, 2, 3, 4) or osd["size"] not in ("small", "medium", "large")
            or osd["rows"] not in (1, 2) or type(osd["smallUnits"]) is not bool):
        raise ValueError("OSD 设置格式错误")
    osd = {"enabled": osd["enabled"], "level": osd["level"], "size": osd["size"], "rows": osd["rows"], "smallUnits": osd["smallUnits"]}
    quick = value.get("quick", DEFAULTS["quick"])
    if not isinstance(quick, list) or len(quick) != 3:
        raise ValueError("快捷档位格式错误")
    return {"enabled": value["enabled"], "mode": mode, "boost": _int(value.get("boost"), 0, BOOST_MAX, "短时加成"),
            "quick": [_int(q, TDP_MIN, TDP_MAX, "快捷档位") for q in quick],
            "ac": _profile(value.get("ac"), "插电"), "dc": _profile(value.get("dc"), "电池"),
            "manual": _profile(value.get("manual"), "手动"), "autotdp": auto,
            "osd": osd, "fan": _fan(value.get("fan"))}


def _fan(value):
    fan = {**DEFAULTS["fan"], **(value or {})}
    if fan["mode"] in ("custom", "firmware"):  # old selections migrate to the balanced preset
        fan["mode"] = "balanced"
    if fan["curves"] is not None and not isinstance(fan["curves"], dict):
        raise ValueError("风扇设置格式错误")
    curves = {**FAN_PRESETS, **(fan["curves"] or {})}
    if (fan["mode"] not in FAN_MODES or fan["control"] not in ("custom", "auto") or type(fan["full"]) is not bool or set(curves) != set(FAN_PRESETS)
            or any(not isinstance(c, list) or len(c) != 6 or any(type(d) is not int or not 0 <= d <= 100 for d in c)
                   for c in curves.values())):
        raise ValueError("风扇设置格式错误")
    return {"mode": fan["mode"], "curves": {m: [max(1, min(d, FAN_CAPS[m])) for d in curves[m]] for m in FAN_PRESETS},
            "full": fan["full"], "control": fan["control"]}


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
    """Same rules as the fan service's FanGovernor (native/ClawFanService.cs)."""
    def __init__(self):
        self.history = []  # (time, duty the curve asks for)
        self.duty = None
        self.smooth = self.smooth_at = None

    def reset(self):
        self.history, self.duty = [], None
        self.smooth = self.smooth_at = None

    def update(self, points, temp, now):
        dt = max(0.0, now - self.smooth_at) if self.smooth_at is not None else 0.0
        self.smooth = temp if self.smooth is None else self.smooth + (temp - self.smooth) * (1 - math.exp(-dt / FAN_SMOOTH))
        self.smooth_at = now
        want = fan_duty_at(points, self.smooth)
        self.history = [(t, w) for t, w in self.history if now - t < FAN_FALL] + [(now, want)]
        if self.duty is None:
            self.duty = want
            return self.duty
        if temp >= FAN_HOT:
            self.duty = max(fan_duty_at(points, temp), self.duty)
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


SMALL_CORES_KEY = r"SYSTEM\CurrentControlSet\Control\Session Manager\kernel\Kgroups\00"


def small_cores_enabled():
    """体感助手's 模拟全核心E核: SmallProcessorMask present means every core is scheduled as an E-core."""
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, SMALL_CORES_KEY, 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
            winreg.QueryValueEx(key, "SmallProcessorMask")
            return True
    except FileNotFoundError:
        return False


def boot_time():
    ctypes.windll.kernel32.GetTickCount64.restype = ctypes.c_uint64
    return time.time() - ctypes.windll.kernel32.GetTickCount64() / 1000


def window_title(pid):
    """Title of the visible top-level window of a process (the game's own name, e.g. 鸣潮)."""
    u = ctypes.windll.user32
    found = []
    PROC = ctypes.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)

    def each(hwnd, _):
        owner = W.DWORD()
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and u.IsWindowVisible(hwnd):
            length = u.GetWindowTextLengthW(hwnd)
            if length:
                buffer = ctypes.create_unicode_buffer(length + 1)
                u.GetWindowTextW(hwnd, buffer, length + 1)
                found.append(buffer.value)
                return False
        return True
    u.EnumWindows(PROC(each), 0)
    return found[0] if found else None


def battery_charging():
    status = PowerStatus()
    return bool(ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(status))) and status.battery & 8 != 0


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
# keeps the game in front), the shell, consoles, Game Bar, and this toolbox's helpers.
NOT_GAMES = {"steam.exe", "steamwebhelper.exe", "gameoverlayui.exe", "gameoverlayui64.exe", "explorer.exe",
             "searchhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe",
             "applicationframehost.exe", "lockapp.exe", "taskmgr.exe", "claude.exe", "chatgpt.exe", "msedge.exe",
             "chrome.exe", "firefox.exe", "codex.exe", "msedgewebview2.exe",
             "losslessscaling.exe", "magpie.exe", "motionassistant.exe", "rtss.exe", "clash-verge.exe",
             "sunshine.exe", "pluginloader_noconsole.exe", "pluginloader.exe", "gamingmode.exe",
             "windowsterminal.exe", "openconsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
             "gamebar.exe", "gamebarftserver.exe", "xboxgamebarwidgets.exe", "clawwidget.exe"}


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


_endurance_spec = importlib.util.spec_from_file_location("claw_toolbox_endurance", Path(__file__).with_name("claw_endurance.py"))
_endurance_module = importlib.util.module_from_spec(_endurance_spec)
_endurance_spec.loader.exec_module(_endurance_module)
_auto_spec = importlib.util.spec_from_file_location("claw_toolbox_autotdp", Path(__file__).with_name("claw_autotdp.py"))
_auto_module = importlib.util.module_from_spec(_auto_spec)
_auto_spec.loader.exec_module(_auto_module)
_fan_service_spec = importlib.util.spec_from_file_location("claw_toolbox_fan_service", Path(__file__).with_name("claw_fan_service.py"))
_fan_service_module = importlib.util.module_from_spec(_fan_service_spec)
_fan_service_spec.loader.exec_module(_fan_service_module)


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
        self.fan_written = None  # flat duty and full-speed override last verified
        self.fan_lock = asyncio.Lock()
        self.fan_write_count = 0
        self.fan_last_write = None
        self.fan_test_session = None
        self.fan_test_watchdog = None
        self.fan_test_result = None
        self.fan_backup_path = self.data / "fan-backup.json"
        self.fan_service = _fan_service_module.FanServiceClient()
        self.ecore_path = self.data / "ecore.json"
        self.cpu_max = None  # MHz, read once from the CPU
        self.sys_cache = None  # widget readings (brightness, volume, refresh, frame limit, game)
        self.sys_at = 0.0
        self.sys_task = None
        self.stats = {}
        self.cpu = LightStats()
        self.last_frames = None
        self.osd_zoom = None
        self.osd_font = None
        self.osd_height = None
        self.osd = {}
        self.osd_text = ""
        self.policy_lock = asyncio.Lock()
        self.endurance = _endurance_module.IntelEndurance(self._call, self.data, None)  # recovery only
        self.last_game = None
        self.auto_governor = _auto_module.AutoTDPGovernor()
        self.auto_session = _auto_module.AutoTDPSession(self._call, self.data)
        self.auto_game = None
        self.auto_state = {}
        self.auto_logged = None
        self.auto_logged_at = 0.0
        self.frames_started = 0.0

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
            started = time.monotonic()
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
            if command in ("fan_read", "fan_curve", "fan_restore", "fan_test_curve", "fan_test_done"):
                return reply.get("fan") or {}
            if command == "fan_probe":
                return reply.get("fanProbe") or {}
            if command in ("thermal", "osd", "stats", "osd_zoom", "osd_height", "osd_font", "rtss_app", "gpu"):
                return reply.get(command) or {}
            if command in ("sys_read", "brightness_set", "volume_set", "mute_set", "refresh_set", "fps_limit_set",
                           "kill", "power_action", "keyboard", "screenshot", "mapped_input"):
                return reply.get("sys") or {}
            if command in ("display_read", "resolution_set", "vrr_set", "hdr_set", "audio_output_set", "mic_mute_set",
                           "scale_set", "auto_brightness_set", "gpu_set"):
                return reply.get("display") or {}
            if command in ("system_read", "power_button_set", "hibernate_after_set", "display_off_set", "wake_set", "charge_limit_set", "startup_approve"):
                return reply.get("system") or {}
            if command in ("update_start", "update_status", "wu_start", "wu_status"):
                return reply.get("update") or {}
            if command in ("vendor_apps", "vendor_app_set"):
                return reply.get("apps") or []
            if command == "pnp_drivers":
                return reply.get("drivers") or []
            if command in ("endurance_read", "endurance_set", "frame_pacing_set"):
                return reply.get("endurance") or {}
            if command == "cpu_info":
                return reply.get("cpu") or {}
            if command == "ecore_set":
                return reply.get("ecore") or {}
            if command == "frames":
                frames = reply.get("frames") or {}
                if frames.get("msrError"):
                    raise RuntimeError(frames["msrError"])
                self.actual = {**(self.actual or {}), **{k: frames[k] for k in ("pl1", "pl2", "package") if k in frames}}
                if "package" in frames:
                    self.package_at = time.monotonic()
                self.last_frames = frames
                self.frames_at = time.monotonic()
                self.frames_started = started
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
        if s["mode"] == "autotdp":
            cfg = s["autotdp"]
            ceiling = cfg["acMax" if self.charger else "dcMax"]
            limit = max(cfg["floor"], min(ceiling, self.auto_governor.limit or ceiling))
            return limit, limit + s["boost"]
        limit = (s["manual"] if s["mode"] == "manual" else s["ac" if self.charger else "dc"])["tdp"]
        return limit, limit + s["boost"]

    def freq_target(self):
        s = self.settings
        if s["mode"] == "autotdp":
            return 0, 0
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
        if self.settings["mode"] != "autotdp":
            await self._release_auto()
        await self.endurance.release()
        if self.settings["mode"] == "autotdp":
            self._configure_auto()
            await self._backup(await self._call("read"))
            await self.auto_session.acquire()
        else:
            state = await self.endurance.read()
            if state.get("available") and state.get("control") != "off":
                await self._call("endurance_set", control="off", profile=state["profile"])
        power = self.actual if (self.actual and not force) else await self._call("read")
        await self._backup(power)
        pl1, pl2 = self.target()
        drift = abs(power.get("pl1", 0) - pl1) > .2 or abs(power.get("pl2", 0) - pl2) > .2 \
            or power.get("pl1Enabled") is False or power.get("pl2Enabled") is False
        if force or drift:
            if self.settings["mode"] == "autotdp" and not force and self.written and (
                    abs(power.get("pl1", 0) - self.written[0]) > .2 or abs(power.get("pl2", 0) - self.written[1]) > .2):
                raise RuntimeError("功耗被其他软件或固件改写，Auto 已暂停；请排除冲突后重新选择模式")
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
        self.written = (pl1, pl2)
        if self.settings["mode"] == "autotdp":
            self.auto_governor.applied(pl1, time.monotonic())
            self.auto_state = {**self.auto_governor.view(), "game": self.auto_game[1] if self.auto_game else None}

    def _configure_auto(self):
        cfg = self.settings["autotdp"]
        key = (self.auto_game, self.charger, ((self.actual or {}).get("freq") or {}).get("scheme"))
        self.auto_governor.configure(key, cfg["floor"], cfg["acMax" if self.charger else "dcMax"],
                                     cfg["target"], time.monotonic())

    async def _release_auto(self, restore_driver=False):
        await self.auto_session.release(restore_driver=restore_driver)
        self.auto_governor = _auto_module.AutoTDPGovernor()
        self.auto_game, self.auto_state, self.auto_logged = None, {}, None

    async def _auto_tick(self):
        game = await asyncio.to_thread(foreground_game)
        if game is None and self.auto_game:
            names = await asyncio.to_thread(process_names)
            if names.get(self.auto_game[0]) == self.auto_game[1]:
                game = self.auto_game  # game stays tracked while its control center is open
        self.auto_game = game
        self._configure_auto()
        await self._apply()
        if game is None:
            self.auto_governor.hold("等待游戏帧率 · 保持当前功耗")
        else:
            frames = await self._call("frames", pid=game[0])
            chains = [] if frames.get("framesError") or frames.get("pid") != game[0] else frames.get("swapChains") or []
            # Select by the game's own rendered FPS, never generated/presented FPS.
            def real_fps(chain):
                v = chain.get("appFps")
                return v if type(v) in (int, float) and 0 < v <= 500 else 0
            chain = max(chains, key=real_fps) if chains else {}
            offset, window = frames.get("sampleOffsetMs"), frames.get("sampleWindowMs")
            if real_fps(chain) and type(offset) in (int, float) and type(window) in (int, float) and 0 <= offset <= 2000 and 0 < window <= 2000:
                end = self.frames_started - offset / 1000
                self.auto_governor.update(real_fps(chain), end - window / 1000, end, time.monotonic())
            else:
                self.auto_governor.hold("帧率采集暂不可用 · 保持当前功耗")
        if self.target() != self.written:
            await self._apply()
        self.auto_state = {**self.auto_governor.view(), "game": game[1] if game else None}
        self._log_auto()

    def _log_auto(self):
        now = time.monotonic()
        signature = (self.auto_state.get("game"), self.auto_state.get("limit"), self.auto_state.get("reason"))
        if signature == self.auto_logged and now - self.auto_logged_at < 5:
            return
        self.auto_logged, self.auto_logged_at = signature, now
        try:
            path = self.data / "autotdp-decisions.jsonl"
            if path.exists() and path.stat().st_size > 1024 * 1024:
                os.replace(path, path.with_suffix(".previous.jsonl"))
            with path.open("a", encoding="utf-8") as file:
                file.write(json.dumps({"time": time.strftime("%Y-%m-%d %H:%M:%S"), "charger": self.charger,
                    **self.auto_state, "actual": {k: (self.actual or {}).get(k) for k in ("pl1", "pl2", "package")}},
                    ensure_ascii=False) + "\n")
        except OSError:
            logging.exception("AutoTDP decision log failed")

    async def _loop(self):
        tick = 0
        while not self.closing:
            try:
                await asyncio.sleep(TICK)
                tick += 1
                osd_on = self.settings["osd"]["enabled"]
                await self._fan_tick(tick)
                self.charger = on_charger()
                if tick % 10 == 1:
                    self.other_tool = await asyncio.to_thread(motion_assistant_running)
                # Telemetry and fan operation do not depend on Intel policy availability.
                if osd_on:
                    await self._osd_tick(tick, self.auto_state if self.settings["enabled"] and self.settings["mode"] == "autotdp" else None)
                async with self.policy_lock:
                    if self.settings["enabled"]:
                        await self._call("read")
                        if self.settings["mode"] == "autotdp":
                            await self._auto_tick()
                        elif tick % 3 == 0:
                            await self._apply()
                    else:
                        await self._release_auto(restore_driver=True)
                        await self.endurance.release(restore_driver=True)
                    if self.error:
                        self.error = ""
                if tick % 2 == 0:
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
        data = {"time": time.strftime("%H:%M"), "rtssFps": rtss.get("fps"), "frameTime": rtss.get("frameTime"),
                "fps": chain.get("appFps"), "presented": chain.get("presentedFps"),
                "fps01": frames.get("appFps01"), "package": (self.actual or {}).get("package"),
                "limit": (self.actual or {}).get("pl1"),
                "target": self.settings["autotdp"]["target"] if self.settings["enabled"] and self.settings["mode"] == "autotdp" else None,
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
        """Follow the selected curve with hysteresis and repair EC resets."""
        async with self.fan_lock:
            if self.fan_service.installed:
                try:
                    self._accept_fan_service(await self.fan_service.get())
                except RuntimeError as error:
                    self.fan_error = str(error)
                return
            if self.fan_test_session is not None:
                return  # the independent watchdog owns safety; no host speed writes here
            try:
                fan = self.settings["fan"]
                if fan["control"] == "auto":
                    if tick % 3 == 0 or not self.fan_state:
                        await self._fan_release_locked()
                    return
                if not fan["full"]:
                    self.temperature = (await self._call("thermal")).get("package")
                    self.temperature_at = time.monotonic()
                    self._fan_temperature()
                    self.fan_governor.update(fan_points(fan), self.temperature, time.monotonic())
                duty = 100 if fan["full"] else self.fan_governor.duty
                if self.fan_written != (duty, fan["full"]):
                    await self._apply_fan_locked()
                if tick % 3 == 0 or not self.fan_state:
                    self.fan_state = await self._call("fan_read")
                    if (not self.fan_state.get("software") or self.fan_state.get("cpu") != [duty] * 6
                            or self.fan_state.get("gpu") != [duty] * 6 or self.fan_state.get("fullSpeed") != fan["full"]):
                        await self._apply_fan_locked(self.fan_state)
                self.fan_error = ""
            except RuntimeError as e:
                self.fan_error = str(e)
                # Never leave a fixed low duty in place after losing the temperature sensor.
                try:
                    await self._fan_release_locked()
                except RuntimeError as release:
                    self.fan_error += f"；恢复自动控制失败：{release}"

    def _accept_fan_service(self, status):
        self.settings["fan"] = _fan(status["settings"])
        self.fan_state = {k: v for k, v in status.items() if k != "settings"}
        self.fan_error = status.get("error", "")
        if type(status.get("temperature")) in (int, float):
            self.temperature = status["temperature"]
            self.temperature_at = time.monotonic()

    def _fan_temperature(self):
        if type(self.temperature) not in (int, float) or not 0 < self.temperature <= 125:
            raise RuntimeError("CPU 温度读取异常，已尝试恢复固件自动控制")

    async def fan_test(self, action, config=None):
        """Bounded diagnostic lease. Never changes saved user power/fan settings."""
        if self.fan_service.installed:
            raise RuntimeError("风扇由独立服务管理，旧的直接写入测试已停用")
        async with self.fan_lock:
            if action == "begin":
                if self.fan_test_session is not None:
                    raise RuntimeError("风扇测试已在进行")
                probe = await self._call("fan_probe")
                if "CG3EM" not in probe.get("model", ""):
                    raise RuntimeError("当前机型未通过风扇测试校验")
                state = await self._call("fan_read")
                self.fan_test_session = {"before": state, "deadline": time.monotonic() + 30,
                                         "started": time.monotonic(), "writes": 0}
                self.fan_test_result = None
                self.fan_test_watchdog = asyncio.create_task(self._fan_test_watch())
                return {"active": True, "probe": probe, "fan": state, "writes": 0}
            if action == "end":
                if self.fan_test_session is not None:
                    await self._fan_test_finish("completed")
                return {"active": False, "result": self.fan_test_result}
            session = self.fan_test_session
            if session is None:
                raise RuntimeError("风扇测试未开始或已自动恢复：" + str(self.fan_test_result))
            if time.monotonic() > session["deadline"] or time.monotonic() - session["started"] > 900:
                await self._fan_test_finish("timeout")
                raise RuntimeError("风扇测试超时，已恢复")
            if action == "apply":
                config = config or {}
                for key in ("cpu", "gpu"):
                    values = config.get(key)
                    if (not isinstance(values, list) or len(values) != 7
                            or any(type(v) is not int or not 35 <= v <= 100 for v in values)
                            or any(a > b for a, b in zip(values, values[1:]))):
                        raise ValueError("测试曲线必须为 7 个递增的 35–100 风量值")
                if type(config.get("custom")) is not bool or config.get("transport") not in ("set_ap", "legacy"):
                    raise ValueError("测试控制参数无效")
                temp = (await self._call("thermal")).get("package")
                if type(temp) not in (int, float) or not 0 < temp < 82:
                    await self._fan_test_finish("temperature_abort")
                    raise RuntimeError("温度不符合风扇测试条件，已恢复")
                self.fan_state = await self._call("fan_test_curve", **{k: config[k] for k in ("cpu", "gpu", "custom", "transport")})
                session["writes"] += 1
            elif action != "sample":
                raise ValueError("未知风扇测试操作")
            thermal = await self._call("thermal")
            temperature = thermal.get("package")
            if type(temperature) not in (int, float) or not 0 < temperature < 82:
                await self._fan_test_finish("temperature_abort")
                raise RuntimeError("风扇测试达到温度限制或温度不可用，已恢复")
            self.fan_state = await self._call("fan_read")
            session["deadline"] = time.monotonic() + 30
            return {"active": True, "fan": self.fan_state, "temperature": temperature,
                    "writes": session["writes"], "softwareWrites": self.fan_write_count}

    async def _fan_test_watch(self):
        try:
            while self.fan_test_session is not None:
                await asyncio.sleep(1)
                async with self.fan_lock:
                    session = self.fan_test_session
                    if session is None:
                        return
                    if time.monotonic() > session["deadline"] or time.monotonic() - session["started"] > 900:
                        await self._fan_test_finish("timeout")
                        return
                    try:
                        temp = (await self._call("thermal")).get("package")
                        if type(temp) not in (int, float) or not 0 < temp < 82:
                            await self._fan_test_finish("temperature_abort")
                            return
                    except RuntimeError:
                        await self._fan_test_finish("sensor_error")
                        return
        except asyncio.CancelledError:
            raise
        except Exception:
            logging.exception("Fan test watchdog recovery failed")

    async def _fan_test_finish(self, reason):
        session = self.fan_test_session
        if session is None:
            return
        before = session["before"]
        watcher = self.fan_test_watchdog
        self.fan_test_watchdog = None
        if watcher and watcher is not asyncio.current_task():
            watcher.cancel()
        # Restore all seven values touched by diagnostics, including the disputed first byte.
        restored = False
        try:
            await self._call("fan_test_curve", cpu=before["cpuTable"][:7], gpu=before["gpuTable"][:7],
                             custom=bool(before["software"]), transport="set_ap")
            await self._call("fan_test_done")
            restored = True
        except RuntimeError:
            await self._fan_release_locked()
            await self._call("fan_test_done")
            raise
        finally:
            self.fan_test_session = None
            self.fan_test_result = {"reason": reason, "writes": session["writes"], "restored": restored}
            self.fan_written = None
            self.fan_governor.reset()
            self.temperature_at = 0.0  # resume against a fresh sensor reading, never a pre-test cached temperature
        # Reapply the user's chosen software curve, also restoring a saved full-speed override.
        try:
            await self._apply_fan_locked()
        except RuntimeError as error:
            self.fan_error = str(error)
            await self._fan_release_locked()
            raise

    async def _apply_fan(self):
        async with self.fan_lock:
            await self._apply_fan_locked()

    async def _apply_fan_locked(self, state=None, full=None):
        if self.fan_service.installed:
            self._accept_fan_service(await self.fan_service.update(self.settings["fan"]))
            return
        fan = self.settings["fan"]
        if fan["control"] == "auto":
            await self._fan_release_locked()
            return
        state = state if state is not None else await self._call("fan_read")
        if not self.fan_backup_path.exists():
            if state.get("software"):
                raise RuntimeError("风扇当前不是固件控制，未备份原曲线，请先在其他软件里恢复自动")
            temp = self.fan_backup_path.with_suffix(".tmp")
            temp.write_text(json.dumps({"cpu": state["cpu"], "gpu": state["gpu"], "cpuTemps": state.get("cpuTemps"),
                                        "savedAt": time.strftime("%Y-%m-%d %H:%M:%S")}, ensure_ascii=False), encoding="utf-8")
            os.replace(temp, self.fan_backup_path)
        full = fan["full"] if full is None else full
        if not full and self.fan_governor.duty is None:
            if self.temperature is None or time.monotonic() - self.temperature_at > 5:
                self.temperature = (await self._call("thermal")).get("package")
                self.temperature_at = time.monotonic()
            self._fan_temperature()
            self.fan_governor.update(fan_points(fan), self.temperature, time.monotonic())
        duty = 100 if full else self.fan_governor.duty
        duties = [duty] * 6
        applied = await self._call("fan_curve", cpu=duties, gpu=duties, full=full)
        if (applied.get("cpu") != duties or applied.get("gpu") != duties or not applied.get("software")
                or applied.get("fullSpeed") != full):
            raise RuntimeError("风扇写入后回读不一致")
        self.fan_state, self.fan_written = applied, (duty, full)
        self.fan_write_count += 1
        self.fan_last_write = time.time()

    async def _fan_release_locked(self):
        backup = json.loads(self.fan_backup_path.read_text(encoding="utf-8")) if self.fan_backup_path.exists() else {}
        self.fan_state = await self._call("fan_restore", **{k: backup[k] for k in ("cpu", "gpu") if k in backup})
        self.fan_written = None
        self.fan_governor.reset()

    async def _fan_fallback(self):
        """Release custom/full-speed control on normal shutdown; the EC uses factory auto."""
        if self.fan_service.installed:
            return  # UI/agent exit must not stop the independent controller.
        if not self.process or self.process.returncode is not None:
            return
        try:
            async with self.fan_lock:
                if self.fan_test_session is not None:
                    await self._fan_test_finish("shutdown")
                await self._fan_release_locked()
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

    def ecore_view(self):
        """Registry state and whether it differs from what Windows booted with (applies after restart)."""
        enabled = small_cores_enabled()
        boot = boot_time()
        try:
            record = json.loads(self.ecore_path.read_text(encoding="utf-8"))
            if abs(record["boot"] - boot) > 120:
                raise ValueError("new boot")
        except (OSError, ValueError, KeyError, TypeError):
            record = {"boot": boot, "atBoot": enabled}
            try:
                self.ecore_path.write_text(json.dumps(record), encoding="utf-8")
            except OSError:
                pass
        return {"enabled": enabled, "pending": enabled != record["atBoot"]}

    # ---- system controls for the Game Bar widget --------------------------------
    async def system(self):
        """Brightness, volume, refresh rates, frame limit and the foreground game."""
        try:
            view = await self._call("sys_read")
        except RuntimeError as e:
            view = {"error": str(e)}
        game = await asyncio.to_thread(foreground_game)
        view["game"] = {"pid": game[0], "exe": game[1],
                        "title": await asyncio.to_thread(window_title, game[0]) or game[1].rsplit(".", 1)[0]} if game else None
        view["charging"] = battery_charging()
        self.sys_cache, self.sys_at = view, time.monotonic()
        return view

    async def system_cached(self):
        """The widget asks every few seconds; answer from the cache at once and refresh it behind."""
        if self.sys_cache is None:
            return await self.system()
        if time.monotonic() - self.sys_at > 1.0 and (self.sys_task is None or self.sys_task.done()):
            self.sys_task = asyncio.create_task(self.system())
        return self.sys_cache

    async def system_set(self, command, **args):
        # The helper answers setters with its fresh readings; the game and charging state are unchanged.
        result = await self._call(command, **args)
        self.sys_cache = {**(self.sys_cache or {}), **{k: v for k, v in result.items() if k in
                          ("brightness", "volume", "muted", "refresh", "refreshRates", "fpsLimit")}}
        self.sys_at = time.monotonic()
        return self.sys_cache

    async def display(self, command="display_read", **args):
        """The 显示与声音 page: the sys readings plus resolution, VRR, HDR, audio outputs and microphone.
        Setters answer with the whole fresh reading, so the page shows what Windows actually took."""
        result = await self._call(command, **args)
        self.sys_cache = {**(self.sys_cache or {}), **{k: v for k, v in result.items() if k in
                          ("brightness", "volume", "muted", "refresh", "refreshRates", "fpsLimit")}}
        self.sys_at = time.monotonic()
        return result

    async def close_game(self, pid):
        game = await asyncio.to_thread(foreground_game)
        if type(pid) is not int or not game or game[0] != pid:
            raise RuntimeError("要关闭的游戏已不在前台，请重新确认")
        await self._call("kill", pid=pid)
        return await self.system()

    async def set_ecore(self, enabled):
        if type(enabled) is not bool:
            raise ValueError("开关格式错误")
        self.ecore_view()  # records the boot state before the first change
        await self._call("ecore_set", enabled=enabled)
        return self.view()

    def view(self):
        pl1, pl2 = self.target()
        a = self.actual or {}
        return {"settings": self.settings, "charger": self.charger, "target": {"pl1": pl1, "pl2": pl2},
                "actual": {k: a.get(k) for k in ("pl1", "pl2", "package", "locked", "tdpBase", "freq")} if a else None,
                "backup": self.backup_path.exists(), "otherTool": self.other_tool, "reapplied": self.reapplied,
                "autotdp": self.auto_state if self.settings["mode"] == "autotdp" else None,
                "targets": [],
                "fan": {**self.fan_state, "error": self.fan_error, "presets": FAN_PRESETS, "caps": FAN_CAPS,
                        "axis": list(FAN_TEMPS),
                        "target": self.fan_state.get("target") if self.fan_service.installed else self.fan_governor.duty,
                        "execution": "service" if self.fan_service.installed else ("test" if self.fan_test_session else "software"),
                        "writeCount": self.fan_state.get("writeCount", 0) if self.fan_service.installed else self.fan_write_count,
                        "lastWrite": self.fan_state.get("lastWrite") if self.fan_service.installed else self.fan_last_write,
                        "testResult": self.fan_test_result},
                # Read-only readings for the toolbox home; a temperature older than 15 s is not shown.
                "live": {"battery": battery_percent(),
                         "cpuTemp": self.temperature if time.monotonic() - self.temperature_at < 15 else None,
                         # Real rendered frame rate from the OSD or smart tick, in any mode.
                         "fps": self._live_fps()},
                "osd": {"rtss": self.osd.get("rtss"), "text": self.osd_text, "error": self.osd.get("error")},
                "ecore": self.ecore_view(),
                "error": self.error, "limits": {"tdpMin": TDP_MIN, "tdpMax": TDP_MAX, "boostMax": BOOST_MAX,
                                                 "freq": freq_offered(self.cpu_max, [self.settings[k]["freq"] for k in ("ac", "dc", "manual")]),
                                                 "cpuMax": self.cpu_max or None}}

    async def publish(self):
        try:
            await self.emit(self.view())
        except Exception:
            logging.exception("Power status emit failed")

    # ---- api ----------------------------------------------------------------
    async def get(self, sample=False):
        if self.fan_service.installed:
            async with self.fan_lock:
                try:
                    self._accept_fan_service(await self.fan_service.get())
                except RuntimeError as error:
                    self.fan_error = str(error)
        self.charger = on_charger()
        self.other_tool = await asyncio.to_thread(motion_assistant_running)
        if not self.settings["enabled"]:
            return self.view()
        if self.cpu_max is None:
            try:
                self.cpu_max = max_turbo_mhz(await self._call("cpu_info")) or 0
            except Exception as e:
                logging.warning("CPU frequency range: %s", e)
                self.cpu_max = 0
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

    def _persist_settings(self):
        temp = self.path.with_suffix(".tmp")
        temp.write_text(json.dumps(self.settings, ensure_ascii=False), encoding="utf-8")
        os.replace(temp, self.path)

    async def save(self, value):
        next_settings = validate(value)
        if self.fan_test_session and next_settings["fan"] != self.settings["fan"]:
            raise RuntimeError("风扇测试期间请先结束测试，再修改风扇设置")
        async with self.policy_lock:
            async with self.fan_lock:
                previous = json.loads(json.dumps(self.settings))
                # Older frontends only cycle mode/full. Those explicit actions resume
                # custom control after the user has selected factory Auto.
                if next_settings["fan"]["mode"] != previous["fan"]["mode"] or (next_settings["fan"]["full"] and not previous["fan"]["full"]):
                    next_settings["fan"]["control"] = "custom"
                fan_changed = next_settings["fan"] != previous["fan"]
                service_applied = False
                self.settings = next_settings
                self.charger = on_charger()
                try:
                    if next_settings["enabled"]:
                        await self._apply(force=True)
                    else:
                        await self._release_auto(restore_driver=True)
                        await self.endurance.release(restore_driver=True)
                    if fan_changed:
                        self.fan_governor.reset()
                        await self._apply_fan_locked()
                        service_applied = self.fan_service.installed
                        self.fan_error = ""
                    self._persist_settings()
                    self.error = ""
                except Exception as error:
                    self.settings = previous
                    self.error = str(error)
                    if fan_changed:
                        self.fan_error = str(error)
                    try:
                        if service_applied:
                            self._accept_fan_service(await self.fan_service.update(previous["fan"]))
                        elif fan_changed and not self.fan_service.installed:
                            self.fan_governor.reset()
                            await self._apply_fan_locked()
                        if previous["enabled"]:
                            await self._apply(force=True)
                        else:
                            await self._release_auto(restore_driver=True)
                            await self.endurance.release(restore_driver=True)
                    except Exception:
                        logging.exception("Power/fan settings rollback incomplete")
                    raise
        if not next_settings["osd"]["enabled"]:
            await self._osd_off()
        return self.view()

    async def restore(self):
        async with self.policy_lock:
            return await self._restore_locked()

    async def _restore_locked(self):
        await self._release_auto(restore_driver=True)
        await self.endurance.release(restore_driver=True)
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
        return self.view()

    async def close(self):
        self.closing = True
        if self.task:
            self.task.cancel()
            try:
                await self.task
            except asyncio.CancelledError:
                pass
        try:
            await self._release_auto(restore_driver=True)
            await self.endurance.release(restore_driver=True)
        except Exception:
            logging.exception("Power policy release failed; recovery journal retained")
        await self._fan_fallback()
        await self._stop()
