"""Own a source-built Magpie child, including a verified elevation pipe bridge."""
import asyncio
import ctypes
import copy
from ctypes import wintypes as W
from contextlib import contextmanager
import json
import hashlib
import logging
import math
import os
from pathlib import Path
import shutil
import subprocess
import time

VERSION = "0.12.1-claw"
SCALING_CLASS = "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22"
CAPTURES = {"graphics": 0, "desktop": 1, "gdi": 2, "dwm": 3}
DEFAULTS = {"fill": True, "sharpen": True, "strength": .5, "capture": "graphics", "cropX": 0, "cropY": 0,
            "pipeline": [], "maxFrameRate": 60, "game3D": False, "adjustCursor": True,
            # The original profile's remaining options (Game Bar Magpie page)
            "captureTitleBar": False, "disableDirectFlip": False, "simulateExclusiveFullscreen": False,
            "cursorScaling": 1.0, "cursorInterpolation": 0, "autoHideCursorDelay": 0, "toolbar": 0,
            "duplicateFrameDetection": 1, "multiMonitorUsage": 0}
BOOLS = ("fill", "sharpen", "game3D", "adjustCursor", "captureTitleBar", "disableDirectFlip", "simulateExclusiveFullscreen")
# Settings a game's own entry may change from the Game Bar page; everything else stays as the library set it.
QAM_KEYS = ("capture", "maxFrameRate", "game3D", "adjustCursor", "cropX", "cropY", "captureTitleBar", "disableDirectFlip",
            "simulateExclusiveFullscreen", "cursorScaling", "cursorInterpolation", "autoHideCursorDelay", "toolbar",
            "duplicateFrameDetection", "multiMonitorUsage")
try: PLUGIN_VERSION = json.loads((Path(__file__).parent / "plugin.json").read_text(encoding="utf-8"))["version"]
except (OSError, ValueError, KeyError): PLUGIN_VERSION = "0"
CATALOG = json.loads((Path(__file__).parent / "bin/magpie/claw-catalog.json").read_text(encoding="utf-8"))


def validate_pipeline(pipeline):
    if not isinstance(pipeline, list) or len(pipeline) > 12:
        raise ValueError("效果链最多包含 12 个效果")
    known = {e["name"]: e for e in CATALOG["effects"]}
    result = []
    for stage in pipeline:
        if not isinstance(stage, dict) or set(stage) - {"name", "parameters", "scalingType", "scale"}:
            raise ValueError("效果配置格式错误")
        name = stage.get("name")
        if not isinstance(name, str) or name not in known: raise ValueError("请选择有效的 Magpie 效果")
        params = stage.get("parameters", {})
        if not isinstance(params, dict): raise ValueError("效果参数格式错误")
        info = {p["name"]: p for p in known[name]["parameters"]}
        for key, value in params.items():
            if key not in info or type(value) not in (int, float) or not math.isfinite(value):
                raise ValueError("效果参数格式错误")
            p = info[key]
            if not p["min"] - 1e-7 <= value <= p["max"] + 1e-7 or (p["integer"] and value != int(value)):
                raise ValueError(f"{p['label']} 超出有效范围")
        scaling = stage.get("scalingType", 1 if known[name]["canScale"] else 0)
        scale = stage.get("scale", [1, 1])
        if type(scaling) is not int or scaling not in range(4) or not isinstance(scale, list) or len(scale) != 2:
            raise ValueError("效果缩放尺寸格式错误")
        if any(type(v) not in (int, float) or not math.isfinite(v) or not 0 < v <= 16384 for v in scale):
            raise ValueError("效果缩放尺寸超出有效范围")
        result.append({"name": name, "parameters": dict(params), "scalingType": scaling, "scale": list(scale)})
    return result


def validate(value):
    # Settings saved before an option existed take its default.
    if isinstance(value, dict) and set(value) <= set(DEFAULTS) | {"target"} and "target" in value:
        value = {**{k: v for k, v in DEFAULTS.items() if k not in value}, **value}
    if not isinstance(value, dict) or set(value) != set(DEFAULTS) | {"target"}:
        raise ValueError("画面设置格式错误")
    if any(type(value[k]) is not bool for k in BOOLS):
        raise ValueError("画面开关格式错误")
    if type(value["cursorScaling"]) not in (int, float) or not 0 <= value["cursorScaling"] <= 10:
        raise ValueError("光标缩放需在 0–10 倍之间")
    if type(value["autoHideCursorDelay"]) not in (int, float) or not (value["autoHideCursorDelay"] == 0 or .1 <= value["autoHideCursorDelay"] <= 5):
        raise ValueError("隐藏光标延迟需为关闭或 0.1–5 秒")
    for key, high in (("cursorInterpolation", 1), ("toolbar", 2), ("duplicateFrameDetection", 2), ("multiMonitorUsage", 2)):
        if type(value[key]) is not int or not 0 <= value[key] <= high:
            raise ValueError("画面设置格式错误")
    if type(value["capture"]) is not str or value["capture"] not in CAPTURES:
        raise ValueError("请选择有效的捕获方式")
    if type(value["strength"]) not in (int, float) or not math.isfinite(value["strength"]) or not 0 <= value["strength"] <= 1:
        raise ValueError("锐度需在 0–100% 之间")
    if type(value["target"]) is not int or not 0 <= value["target"] < 2**53:
        raise ValueError("请选择有效的游戏窗口")
    for key in ("cropX", "cropY"):
        if type(value[key]) is not int or not 0 <= value[key] <= 400:
            raise ValueError("每边裁剪需在 0–400 像素之间")
    if type(value["maxFrameRate"]) is not int or value["maxFrameRate"] != 0 and not 15 <= value["maxFrameRate"] <= 240:
        raise ValueError("处理帧率需为不限制，或 15–240 FPS")
    return {**value, "pipeline": validate_pipeline(value["pipeline"])}


def calculate_crop(width, height, screen_width, screen_height, settings):
    x, y = settings["cropX"], settings["cropY"]
    remaining_w, remaining_h = width - 2 * x, height - 2 * y
    if min(remaining_w, remaining_h, screen_width, screen_height) < 64:
        raise ValueError("裁剪后画面过小，请降低黑边修正数值")
    if settings["fill"]:
        scale = max(screen_width / remaining_w, screen_height / remaining_h)
        x += (remaining_w - screen_width / scale) / 2
        y += (remaining_h - screen_height / scale) / 2
    # Magpie rounds source edges to whole physical pixels. Symmetric rounding
    # keeps distortion below one pixel; its Fit stage preserves the final ratio.
    return {"left": round(x), "right": round(x), "top": round(y), "bottom": round(y)}


class Rect(ctypes.Structure):
    _fields_ = [(k, W.LONG) for k in ("left", "top", "right", "bottom")]


class MonitorInfo(ctypes.Structure):
    _fields_ = [("size", W.DWORD), ("monitor", Rect), ("work", Rect), ("flags", W.DWORD)]


class Windows:
    EXCLUDED = {"steam.exe", "steamwebhelper.exe", "gameoverlayui.exe", "explorer.exe", "applicationframehost.exe",
                "msedge.exe", "chrome.exe", "codex.exe", "chatgpt.exe", "magpie.exe", "gamingmode.exe", "playhub.exe",
                "xboxgamingbar.exe", "motionassistant.exe", "clash-verge.exe", "startmenuexperiencehost.exe", "searchhost.exe",
                "textinputhost.exe", "systemsettings.exe", "shellexperiencehost.exe", "lockapp.exe", "snippingtool.exe",
                "xboxgamingbarhelper.exe", "magpielauncher.exe", "codex-computer-use.exe"}

    def __init__(self):
        self.u = ctypes.WinDLL("user32", use_last_error=True)
        self.k = ctypes.WinDLL("kernel32", use_last_error=True)
        self.callback = ctypes.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
        definitions = {
            "IsWindow": ([W.HWND], W.BOOL), "IsWindowVisible": ([W.HWND], W.BOOL), "IsIconic": ([W.HWND], W.BOOL),
            "GetForegroundWindow": ([], W.HWND), "GetClientRect": ([W.HWND, ctypes.POINTER(Rect)], W.BOOL),
            "GetWindowTextW": ([W.HWND, W.LPWSTR, ctypes.c_int], ctypes.c_int),
            "GetClassNameW": ([W.HWND, W.LPWSTR, ctypes.c_int], ctypes.c_int),
            "GetWindowThreadProcessId": ([W.HWND, ctypes.POINTER(W.DWORD)], W.DWORD),
            "MonitorFromWindow": ([W.HWND, W.DWORD], W.HANDLE),
            "GetMonitorInfoW": ([W.HANDLE, ctypes.POINTER(MonitorInfo)], W.BOOL),
            "EnumWindows": ([self.callback, W.LPARAM], W.BOOL),
            "FindWindowW": ([W.LPCWSTR, W.LPCWSTR], W.HWND),
            "FindWindowExW": ([W.HWND, W.HWND, W.LPCWSTR, W.LPCWSTR], W.HWND),
            "GetPropW": ([W.HWND, W.LPCWSTR], W.HANDLE),
            "PostMessageW": ([W.HWND, W.UINT, W.WPARAM, W.LPARAM], W.BOOL),
            "RegisterWindowMessageW": ([W.LPCWSTR], W.UINT),
            "SetThreadDpiAwarenessContext": ([W.HANDLE], W.HANDLE),
            "GetAncestor": ([W.HWND, W.UINT], W.HWND),
            "keybd_event": ([ctypes.c_ubyte, ctypes.c_ubyte, W.DWORD, ctypes.c_size_t], None),
        }
        for name, (args, result) in definitions.items():
            fn = getattr(self.u, name); fn.argtypes, fn.restype = args, result
        self.k.OpenProcess.argtypes, self.k.OpenProcess.restype = [W.DWORD, W.BOOL, W.DWORD], W.HANDLE
        self.k.CloseHandle.argtypes = [W.HANDLE]
        self.k.QueryFullProcessImageNameW.argtypes = [W.HANDLE, W.DWORD, W.LPWSTR, ctypes.POINTER(W.DWORD)]
        self.k.QueryFullProcessImageNameW.restype = W.BOOL
        self.a = ctypes.WinDLL("advapi32", use_last_error=True)
        self.a.OpenProcessToken.argtypes, self.a.OpenProcessToken.restype = [W.HANDLE, W.DWORD, ctypes.POINTER(W.HANDLE)], W.BOOL
        self.a.GetTokenInformation.argtypes = [W.HANDLE, ctypes.c_int, W.LPVOID, W.DWORD, ctypes.POINTER(W.DWORD)]
        self.a.GetTokenInformation.restype = W.BOOL
        self.dwm = ctypes.WinDLL("dwmapi", use_last_error=True)
        self.dwm.DwmGetWindowAttribute.argtypes = [W.HWND, W.DWORD, W.LPVOID, W.DWORD]
        self.dwm.DwmGetWindowAttribute.restype = W.LONG

    @contextmanager
    def pixels(self):
        old = self.u.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4))
        try:
            yield
        finally:
            if old: self.u.SetThreadDpiAwarenessContext(old)

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

    def elevated(self, pid):
        process = self.k.OpenProcess(0x1000, False, pid)
        token, result, length = W.HANDLE(), W.DWORD(), W.DWORD()
        try:
            if not process or not self.a.OpenProcessToken(process, 8, ctypes.byref(token)):
                raise RuntimeError("无法读取游戏权限，请重新选择游戏窗口")
            if not self.a.GetTokenInformation(token, 20, ctypes.byref(result), ctypes.sizeof(result), ctypes.byref(length)):
                raise RuntimeError("无法读取游戏权限")
            return bool(result.value)
        finally:
            if token: self.k.CloseHandle(token)
            if process: self.k.CloseHandle(process)

    def describe(self, hwnd):
        if not self.u.IsWindow(hwnd) or not self.u.IsWindowVisible(hwnd) or self.u.IsIconic(hwnd): return None
        cloaked = W.DWORD()
        if self.dwm.DwmGetWindowAttribute(hwnd, 14, ctypes.byref(cloaked), ctypes.sizeof(cloaked)) == 0 and cloaked.value: return None
        with self.pixels():
            rect, monitor = Rect(), MonitorInfo(); monitor.size = ctypes.sizeof(monitor)
            if not self.u.GetClientRect(hwnd, ctypes.byref(rect)): return None
            if not self.u.GetMonitorInfoW(self.u.MonitorFromWindow(hwnd, 2), ctypes.byref(monitor)): return None
        width, height = rect.right - rect.left, rect.bottom - rect.top
        if width < 200 or height < 120: return None
        title, cls = ctypes.create_unicode_buffer(1024), ctypes.create_unicode_buffer(256)
        self.u.GetWindowTextW(hwnd, title, len(title)); self.u.GetClassNameW(hwnd, cls, len(cls))
        pid = self.pid(hwnd); path = self.path(pid)
        if not title.value or not path or pid == os.getpid() or Path(path).name.lower() in self.EXCLUDED: return None
        normalized = os.path.normcase(os.path.abspath(path))
        system = os.environ.get("WINDIR", "C:\\Windows")
        if any(normalized.startswith(os.path.normcase(os.path.join(system, p)) + os.sep) for p in ("SystemApps", "System32", "SysWOW64")): return None
        return {"hwnd": hwnd, "pid": pid, "path": path, "title": title.value, "className": cls.value,
                "width": width, "height": height, "screenWidth": monitor.monitor.right - monitor.monitor.left,
                "screenHeight": monitor.monitor.bottom - monitor.monitor.top,
                "foreground": hwnd == self.u.GetForegroundWindow()}

    def targets(self):
        results = []
        def visit(hwnd, _):
            try:
                item = self.describe(hwnd)
                if item: results.append(item)
            except Exception:
                logging.exception("Magpie window inspection failed")
            return True
        self.u.EnumWindows(self.callback(visit), 0)
        return sorted(results, key=lambda v: (not v["foreground"], -v["width"] * v["height"]))[:40]

    def foreground_root(self):
        # Some engines (CatSystem2) keep focus on a hidden owned window. A visible
        # owned dialog (such as a quit prompt) still counts as leaving the game.
        hwnd = self.u.GetForegroundWindow()
        if not hwnd or self.u.IsWindowVisible(hwnd): return hwnd or 0
        return self.u.GetAncestor(hwnd, 3) or hwnd

    def toggle_fullscreen(self, hwnd):
        # Alt+Enter, the standard D3D/DXGI windowed-mode toggle. Only sent while
        # this exact game window is in the foreground with no Steam menu open.
        if self.foreground_root() != hwnd: return False
        for vk, scan, flags in ((0x12, 0x38, 0), (0x0D, 0x1C, 0), (0x0D, 0x1C, 2), (0x12, 0x38, 2)):
            self.u.keybd_event(vk, scan, flags, 0); time.sleep(.03)
        return True

    def hotkey(self, pid=None):
        hwnd = self.u.FindWindowExW(ctypes.c_void_p(-3), None, "Magpie_Hotkey", None)
        return hwnd if hwnd and (pid is None or self.pid(hwnd) == pid) else 0

    def post(self, hwnd, message, wparam=0):
        if not self.u.PostMessageW(hwnd, message, wparam, 0):
            raise RuntimeError("Magpie 控制请求失败，请检查程序权限")

    def output(self, pid):
        hwnd = self.u.FindWindowW(SCALING_CLASS, None)
        if not hwnd or self.pid(hwnd) != pid: return None
        def prop(key): return self.u.GetPropW(hwnd, "Magpie." + key) or 0
        def number(key): return ctypes.c_int32(prop(key)).value
        return {"hwnd": hwnd, "visible": bool(self.u.IsWindowVisible(hwnd)), "target": prop("SrcHWND"),
                "source": [number(k) for k in ("SrcLeft", "SrcTop", "SrcRight", "SrcBottom")],
                "dest": [number(k) for k in ("DestLeft", "DestTop", "DestRight", "DestBottom")]}


class Magpie:
    def __init__(self, root, data, emit, windows=None):
        self.bundle = Path(root) / "bin/magpie"
        self.runtime = Path(data) / ("magpie-runtime-" + VERSION)
        self.prefs_path = Path(data) / "magpie.json"
        self.games_path = Path(data) / "magpie-games.json"
        # The library's game settings: per game whether Magpie starts by itself (自动缩放) and which 超分预设
        self.meta_path = Path(data) / "magpie-game-meta.json"
        self.presets_path = Path(data) / "magpie-presets.json"
        self.custom_presets = {}
        try:
            saved = json.loads(self.presets_path.read_text(encoding="utf-8"))
            if isinstance(saved, dict):
                for name, effects in list(saved.items())[:64]:
                    try:
                        self.custom_presets[self.preset_name(name)] = validate_pipeline(effects)
                    except (ValueError, TypeError):
                        logging.warning("Ignoring invalid Magpie preset %r", name)
        except (OSError, ValueError):
            pass
        try: self.meta = json.loads(self.meta_path.read_text(encoding="utf-8"))
        except (OSError, ValueError): self.meta = {}
        if not isinstance(self.meta, dict): self.meta = {}
        self.emit, self.windows = emit, windows or Windows()
        self.lock = asyncio.Lock(); self.process = self.watcher = self.auto_task = None
        self.switched = 0; self.switched_at = 0; self.touch = None; self.running = None; self.wake = asyncio.Event(); self.overlay_changed = 0; self.plain = []
        # Remembered games and each game's own effect settings (automatic per-game
        # configuration). Older files were a plain list of paths.
        self.games = {}
        try:
            saved_games = json.loads(self.games_path.read_text(encoding="utf-8"))
            if isinstance(saved_games, list): saved_games = {g: None for g in saved_games}
            if isinstance(saved_games, dict):
                for key, prefs in saved_games.items():
                    if not isinstance(key, str) or len(key) >= 32768: continue
                    try: self.games[key] = {k: v for k, v in validate({**DEFAULTS, **prefs, "target": 0}).items() if k in DEFAULTS} if isinstance(prefs, dict) else None
                    except (ValueError, TypeError): self.games[key] = None
        except FileNotFoundError: pass
        except (ValueError, OSError): logging.warning("Magpie game list could not be loaded")
        self.blockers = set(); self.closed = False; self.source = None; self.started_at = 0
        self.requested = False; self.output_seen = False; self.geometry = None; self.engine_pid = 0
        self.prefs = dict(DEFAULTS)  # 设置页 › Magpie defaults; self.current = what the running scaling uses
        try:
            saved = validate({**DEFAULTS, **json.loads(self.prefs_path.read_text(encoding="utf-8")), "target": 0})
            self.prefs.update({k: saved[k] for k in DEFAULTS})
        except FileNotFoundError: pass
        except (ValueError, OSError, TypeError): logging.warning("Magpie preferences could not be loaded")
        self.current = dict(self.prefs)
        self.state = {"enabled": False, "active": False, "target": 0, "phase": "尚未应用到游戏", "error": "",
                      "engineVersion": VERSION, "preferences": dict(self.prefs), "source": [], "dest": [], "crop": {}}

    @staticmethod
    def game_key(path): return os.path.normcase(os.path.abspath(path)) if path else ""

    def match_key(self, path):
        """The remembered entry for a game's exe: the exe itself, or a whole install folder ("dir:<folder>",
        how the library turns it on for a Steam game before its exe is known)."""
        key = self.game_key(path)
        if not key: return None
        if key in self.games: return key
        return next((k for k in self.games if k.startswith("dir:") and key.startswith(k[4:].rstrip("\\") + os.sep)), None)

    def presets(self):
        """超分预设 names: 无 (only fill + sharpen) and Magpie's own presets from the catalog."""
        return ["无"] + [p["name"] for p in self.preset_catalog()]

    @staticmethod
    def preset_name(name):
        if not isinstance(name, str) or not name.strip() or len(name.strip()) > 80 or any(ord(c) < 32 for c in name) or name.strip() == "无":
            raise ValueError("预设名称需为 1–80 个字符，且不能叫「无」")
        return name.strip()

    def preset_catalog(self):
        presets = {p["name"]: {**p, "builtin": True, "modified": False} for p in CATALOG["presets"]}
        for name, effects in self.custom_presets.items():
            presets[name] = {"name": name, "effects": effects, "builtin": name in presets, "modified": True}
        return copy.deepcopy(list(presets.values()))

    def _update_preset_games(self, old, new, effects):
        for key, meta in self.meta.items():
            if meta.get("preset") != old:
                continue
            meta["preset"] = new
            if key in self.games:
                self.games[key] = {**(self.games[key] or self.prefs), "pipeline": copy.deepcopy(effects)}
        self._save_games()

    def _save_presets(self):
        temp = self.presets_path.with_suffix(".tmp")
        temp.write_text(json.dumps(self.custom_presets, ensure_ascii=False), encoding="utf-8")
        os.replace(temp, self.presets_path)

    def save_preset(self, value):
        if not isinstance(value, dict) or set(value) != {"name", "originalName", "effects"}:
            raise ValueError("预设格式错误")
        name = self.preset_name(value["name"])
        old = value["originalName"]
        names = self.presets()[1:]
        if old is not None and (not isinstance(old, str) or old not in names):
            raise ValueError("原预设已不存在，请重新打开")
        if name != old and name in names:
            raise ValueError("已经有同名预设")
        if old in {p["name"] for p in CATALOG["presets"]} and old != name:
            raise ValueError("内置预设请先复制再改名")
        if name not in self.custom_presets and old not in self.custom_presets and len(self.custom_presets) >= 64:
            raise ValueError("最多保存 64 个自定义预设")
        effects = validate_pipeline(value["effects"])
        previous = copy.deepcopy(self.custom_presets)
        if old != name:
            self.custom_presets.pop(old, None)
        self.custom_presets[name] = effects
        try:
            self._save_presets()
        except OSError:
            self.custom_presets = previous
            raise
        if old is not None:
            self._update_preset_games(old, name, effects)
        return {"presets": self.preset_catalog()}

    def delete_preset(self, name):
        if not isinstance(name, str) or name not in self.custom_presets:
            raise ValueError("这个预设没有可删除的自定义设置")
        previous = self.custom_presets.pop(name)
        try:
            self._save_presets()
        except OSError:
            self.custom_presets[name] = previous
            raise
        builtin = next((p for p in CATALOG["presets"] if p["name"] == name), None)
        self._update_preset_games(name, name if builtin else "无", builtin["effects"] if builtin else [])
        return {"presets": self.preset_catalog()}

    def norm_key(self, key):
        if not isinstance(key, str) or not key or len(key) > 4096: raise ValueError("游戏路径格式错误")
        return "dir:" + os.path.normcase(os.path.abspath(key[4:])) if key.startswith("dir:") else self.game_key(key)

    def _save_games(self):
        temp = self.games_path.with_suffix(".tmp")
        temp.write_text(json.dumps(self.games, ensure_ascii=False, sort_keys=True), encoding="utf-8")
        os.replace(temp, self.games_path)
        temp = self.meta_path.with_suffix(".tmp")
        temp.write_text(json.dumps(self.meta, ensure_ascii=False, sort_keys=True), encoding="utf-8")
        os.replace(temp, self.meta_path)

    def set_game(self, key, enabled=None, auto=None, preset=None):
        """The library's game settings page: Magpie on / off for a game (an exe, or a Steam install folder
        "dir:<folder>"), whether it starts by itself when the game comes to the front (自动缩放), and its 超分预设
        (the game's own settings = the global ones with that preset's effect chain)."""
        key = self.norm_key(key)
        meta = dict(self.meta.get(key) or {"auto": True, "preset": "无"})
        if auto is not None: meta["auto"] = bool(auto)
        if preset is not None:
            if preset not in self.presets(): raise ValueError("没有这个超分预设")
            meta["preset"] = preset
        if enabled is None: enabled = key in self.games
        if enabled:
            chain = next((p["effects"] for p in self.preset_catalog() if p["name"] == meta["preset"]), [])
            prefs = {**self.prefs, "pipeline": validate_pipeline([{k: e[k] for k in ("name", "parameters", "scalingType", "scale") if k in e} for e in chain])}
            self.games[key] = prefs
            self.meta[key] = meta
        else:
            self.games.pop(key, None)
            self.meta[key] = meta
        self._save_games()
        return self.game_info(key)

    def game_info(self, key):
        key = self.norm_key(key)
        meta = self.meta.get(key) or {"auto": True, "preset": "无"}
        enabled = key in self.games if key.startswith("dir:") else self.match_key(key) is not None
        return {"enabled": enabled, "auto": bool(meta.get("auto", True)), "preset": meta.get("preset", "无"), "presets": self.presets()}

    def game_enabled(self, key):
        if not isinstance(key, str) or not key: return False
        return self.game_info(key)["enabled"]

    def auto_for(self, path):
        k = self.match_key(path)
        return k is not None and (self.meta.get(k) or {}).get("auto", True) is not False

    async def get_prefs(self):
        return {"preferences": dict(self.prefs), "captures": list(CAPTURES), "presets": self.presets()}

    async def set_prefs(self, value):
        """设置页 › Magpie: the defaults every game starts from (fill, sharpen, capture, frame rate, …)."""
        if not isinstance(value, dict): raise ValueError("画面设置格式错误")
        prefs = validate({**self.prefs, **{k: v for k, v in value.items() if k in DEFAULTS}, "target": 0})
        self.prefs.update({k: prefs[k] for k in DEFAULTS})
        self.save()
        # Games keep their own preset's chain on top of the new defaults
        for key in list(self.games):
            if self.games[key] is not None:
                self.games[key] = {**self.prefs, "pipeline": self.games[key].get("pipeline", [])}
        self._save_games()
        return await self.get_prefs()

    def remember(self, path, enabled, prefs=None):
        key = self.game_key(path)
        if not key: return
        if enabled: self.games[key] = dict(prefs) if prefs else self.games.get(key)
        elif key in self.games: self.games.pop(key)
        else: return
        temp = self.games_path.with_suffix(".tmp")
        temp.write_text(json.dumps(self.games, ensure_ascii=False, sort_keys=True), encoding="utf-8")
        os.replace(temp, self.games_path)

    def qam_view(self, path):
        """The Game Bar page: the running game's own settings (or the defaults when no game is known)."""
        key = self.match_key(path) if path else None
        prefs = {**DEFAULTS, **(self.games.get(key) or self.prefs)} if key else {**DEFAULTS, **self.prefs}
        meta = (self.meta.get(key) or {}) if key else {}
        return {"game": key or "", "values": {k: prefs[k] for k in QAM_KEYS}, "presets": self.presets(),
                "preset": meta.get("preset", "无"), "auto": bool(key and self.auto_for(path))}

    async def qam_set(self, path, patch):
        """One change from the Game Bar page, saved to the running game's own entry at once; a running
        scaling of that game picks it up (Magpie restarts its output when the game is back in front)."""
        if not path: raise ValueError("没有正在运行的游戏")
        if not isinstance(patch, dict) or len(patch) != 1: raise ValueError("画面设置格式错误")
        (name, value), = patch.items()
        key = self.match_key(path) or self.game_key(path)
        meta = dict(self.meta.get(key) or {"auto": False, "preset": "无"})
        prefs = {**DEFAULTS, **(self.games.get(key) or self.prefs)}
        if name == "auto":
            if type(value) is not bool: raise ValueError("自动缩放格式错误")
            meta["auto"] = value
        elif name == "preset":
            if value not in self.presets(): raise ValueError("没有这个超分预设")
            chain = next((p["effects"] for p in self.preset_catalog() if p["name"] == value), [])
            prefs["pipeline"] = validate_pipeline([{k: e[k] for k in ("name", "parameters", "scalingType", "scale") if k in e} for e in chain])
            meta["preset"] = value
        elif name in QAM_KEYS:
            prefs = {k: v for k, v in validate({**prefs, name: value, "target": 0}).items() if k in DEFAULTS}
        else:
            raise ValueError("画面设置格式错误")
        self.games[key] = {k: prefs[k] for k in DEFAULTS}
        self.meta[key] = meta
        self._save_games()
        source = self.source or {}
        if self.state["enabled"] and source and self.match_key(source.get("path")) == key and name != "auto":
            await self.apply({**self.games[key], "target": source["hwnd"]}, persist=False)
        return self.qam_view(path)

    def game_prefs(self, path):
        """This game's saved settings, or the last used ones."""
        return dict(self.games.get(self.match_key(path) or "") or self.prefs)

    def view(self):
        source = self.source or {}
        return {**self.state, "available": (self.bundle / "Magpie.exe").is_file(), "paused": self.menu_blocks(),
                "remembered": self.game_key(source.get("path")) in self.games if source else False}

    async def publish(self, **patch):
        before = self.view(); self.state.update(patch)
        if before == self.view(): return
        try: await self.emit(self.view())
        except Exception: logging.exception("Magpie status delivery failed")

    async def get(self): return self.view()

    async def targets(self):
        return [{**t, "remembered": self.match_key(t["path"]) is not None,
                 "preferences": self.game_prefs(t["path"]) if self.match_key(t["path"]) is not None else None}
                for t in self.windows.targets()]

    async def auto_start(self):
        """A Steam game started: wait briefly for a remembered game's window to take focus."""
        if self.closed or not self.games or (self.auto_task and not self.auto_task.done()): return self.view()
        self.auto_task = asyncio.create_task(self._auto_start())
        return self.view()

    async def _auto_start(self):
        try:
            for _ in range(90):  # bounded: only after a launch, never an idle scan
                await asyncio.sleep(1)
                if self.closed or self.state["enabled"]: return
                hwnd = self.windows.u.GetForegroundWindow()
                root = self.windows.foreground_root() if hasattr(self.windows, "foreground_root") else hwnd
                item = self.windows.describe(root) if root else None
                if item and self.match_key(item["path"]) is not None and self.auto_for(item["path"]):
                    # The game's own settings (its 超分预设): not new defaults, and its entry stays as the library saved it
                    await self.apply({**self.game_prefs(item["path"]), "target": item["hwnd"]}, persist=False)
                    return
        except asyncio.CancelledError: raise
        except Exception as error:
            logging.exception("Magpie auto start failed")
            await self.publish(error=str(error), phase="自动铺满未能启动，可在画面模块中手动应用")

    async def catalog(self): return {**copy.deepcopy(CATALOG), "presets": self.preset_catalog()}

    def save(self):
        temp = self.prefs_path.with_suffix(".tmp")
        temp.write_text(json.dumps(self.prefs, ensure_ascii=False), encoding="utf-8")
        os.replace(temp, self.prefs_path)

    async def apply(self, settings, persist=True):
        settings = validate(settings)
        if not settings["fill"] and not settings["sharpen"] and not settings["pipeline"]: raise ValueError("请开启画面效果，或使用恢复原画面")
        async with self.lock:
            if self.closed: raise RuntimeError("插件正在重载，请稍候")
            source = self.windows.describe(settings["target"])
            if not source: raise ValueError("游戏窗口已关闭、最小化或不可用，请刷新后重新选择")
            crop = calculate_crop(source["width"], source["height"], source["screenWidth"], source["screenHeight"], settings)
            if self.state["enabled"] and self.source and source["pid"] == self.source["pid"] and source["hwnd"] == self.source["hwnd"] and settings == {**self.current, "target": self.source["hwnd"]}:
                return self.view()
            await self.stop_runtime()
            if self.windows.hotkey(): raise RuntimeError("另一个 Magpie 实例正在运行，请退出它后再应用")
            if not (self.bundle / "Magpie.exe").is_file(): raise RuntimeError("Magpie 后台未安装，请重新安装工具箱")
            runtime_exe = self.runtime / "Magpie.exe"
            files = ("Magpie.exe", "MagpieLauncher.exe", "claw-catalog.json")
            if any((self.bundle / name).is_file() and (not (self.runtime / name).is_file() or
                   hashlib.sha256((self.runtime / name).read_bytes()).digest() != hashlib.sha256((self.bundle / name).read_bytes()).digest()) for name in files):
                await asyncio.to_thread(shutil.copytree, self.bundle, self.runtime, dirs_exist_ok=True, ignore=shutil.ignore_patterns("magpie-source.zip"))
            self.source = source; self.geometry = self.signature(source); self.switched = 0
            self.current = {k: settings[k] for k in DEFAULTS}
            if persist:
                self.prefs = dict(self.current); self.save()
                # Remember the game so its next launch fills the screen without opening the panel.
                self.remember(source["path"], True, self.prefs)
            # Minimal portable startup settings; rendering parameters travel over
            # the explicit pipe, never through a hotkey or a default game profile.
            self.write_config({"language": "zh-CN", "showNotifyIcon": False,
                "autoCheckForUpdates": False, "alwaysRunAsAdmin": False,
                "profiles": [{"scalingMode": -1}]})
            await self.publish(enabled=True, active=False, target=source["hwnd"], targetTitle=source["title"], error="",
                               preferences=dict(self.current), crop=crop, source=[], dest=[], phase="已准备，关闭菜单并返回所选游戏后生效")
            if not self.watcher or self.watcher.done(): self.watcher = asyncio.create_task(self.watch())
            return self.view()

    @staticmethod
    def signature(source):
        return tuple(source[k] for k in ("width", "height", "screenWidth", "screenHeight"))

    def write_config(self, config):
        path = self.runtime / "config/config.json"; path.parent.mkdir(parents=True, exist_ok=True)
        temp = path.with_suffix(".tmp"); temp.write_text(json.dumps(config, ensure_ascii=False), encoding="utf-8"); os.replace(temp, path)

    async def stop_runtime(self):
        process, self.process = self.process, None
        self.requested = self.output_seen = False; self.engine_pid = 0
        await self.stop_touch()
        if not process or process.returncode is not None: return
        try:
            if process.stdin: process.stdin.close()
            await asyncio.wait_for(process.wait(), 10)
        except (asyncio.TimeoutError, RuntimeError, OSError):
            # Only our subprocess; it owns GPU output, no firmware or restore journal.
            if process.returncode is None:
                process.terminate(); await process.wait()

    async def start_touch(self, elevated=False, parent=0):
        # Maps taps on Magpie's output back into the game window; it only shows
        # itself while Magpie's output is visible and exits when stdin closes.
        # Versioned like the router so a reinstall never overwrites a running bridge.
        exe = self.bundle.parent / f"ClawTouchBridge-{PLUGIN_VERSION}.exe"
        if (self.touch and self.touch.returncode is None) or not exe.is_file(): return
        if elevated:
            # An elevated game (Steam run as administrator) ignores input posted from
            # this lower integrity level, so the bridge must run elevated as well, the
            # same way the Magpie engine does. It has no stdin then; it watches the
            # Magpie launcher and exits with it.
            ok = await asyncio.to_thread(lambda: ctypes.windll.shell32.ShellExecuteW(
                None, "runas", str(exe), f"--parent {int(parent)}", str(exe.parent), 0) > 32)
            if not ok: logging.warning("Elevated touch bridge could not start")
            return
        try:
            self.touch = await asyncio.create_subprocess_exec(str(exe), stdin=asyncio.subprocess.PIPE,
                stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except OSError:
            logging.exception("Touch bridge could not start"); self.touch = None

    async def stop_touch(self):
        touch, self.touch = self.touch, None
        if not touch or touch.returncode is not None: return
        try:
            if touch.stdin: touch.stdin.close()
            await asyncio.wait_for(touch.wait(), 3)
        except (asyncio.TimeoutError, RuntimeError, OSError):
            if touch.returncode is None: touch.terminate(); await touch.wait()

    async def stop(self):
        async with self.lock:
            await self.stop_runtime()
            # An explicit restore also means "not for this game" on later launches.
            if self.source: self.remember(self.source["path"], False)
            await self.publish(enabled=False, active=False, error="", phase="已恢复原游戏画面", source=[], dest=[], enginePid=0, engineElevated=False)
            return self.view()

    async def set_plain(self, rects):
        """Frame regions (Steam side menus) Magpie renders without the effect chain."""
        if (not isinstance(rects, list) or len(rects) > 4 or any(
                not isinstance(r, list) or len(r) != 4 or any(type(v) not in (int, float) or not math.isfinite(v) or not 0 <= v <= 1 for v in r)
                or r[2] <= r[0] or r[3] <= r[1] for r in rects)):
            raise ValueError("菜单区域格式错误")
        async with self.lock:
            rects = [[float(v) for v in r] for r in rects]
            if rects == self.plain: return self.view()
            self.plain = rects
            if self.requested and self.process and self.process.returncode is None:
                await self.command("plain", rects=rects)
            return self.view()

    async def toggle(self):
        """Mapped button: upscale + fill the game being played, or restore it."""
        if self.state["enabled"]: return await self.stop()
        targets = await self.targets()
        root = self.foreground()
        # The focused game first; from a Steam screen, the remembered or only game.
        game = (next((t for t in targets if t["hwnd"] == root), None) or next((t for t in targets if t["remembered"]), None)
                or (targets[0] if len(targets) == 1 else None))
        if not game: raise ValueError("没有找到正在运行的游戏窗口，请先回到游戏再按")
        return await self.apply({**self.game_prefs(game["path"]), "fill": True, "target": game["hwnd"]})

    async def pause(self, source, visible):
        if source not in ("panel", "overlay", "focus") or type(visible) is not bool: raise ValueError("菜单状态格式错误")
        async with self.lock:
            if visible: self.blockers.add(source)
            else: self.blockers.discard(source)
            if source == "overlay": self.overlay_changed = time.monotonic()
            logging.info("Magpie menu %s=%s blockers=%s", source, visible, sorted(self.blockers))
            self.wake.set()  # react to menu changes without waiting for the next tick
            if self.state["enabled"] and self.menu_blocks():
                await self.suspend()
                await self.publish(active=False, phase="菜单已打开，返回游戏后恢复")
            return self.view()

    async def watch(self):
        try:
            while not self.closed and self.state["enabled"]:
                async with self.lock:
                    if self.closed or not self.state["enabled"]: return
                    await self.tick()
                try: await asyncio.wait_for(self.wake.wait(), .25)
                except asyncio.TimeoutError: pass
                self.wake.clear()
        except asyncio.CancelledError: raise
        except Exception as error:
            logging.exception("Magpie control failed")
            async with self.lock:
                await self.stop_runtime()
                await self.publish(enabled=False, active=False, error=str(error), phase="画面处理未生效，已释放后台")

    def menu_blocks(self):
        # No Steam menu stops scaling any more: the in-game overlay is drawn into the
        # game's frames, and the side menus of an upscaled game come from the main
        # Big Picture window, which is transparent around them and sits above
        # Magpie's output while focused. Scaling stays on underneath either way.
        return False

    def foreground(self):
        return self.windows.foreground_root() if hasattr(self.windows, "foreground_root") else self.windows.u.GetForegroundWindow()

    def game_fullscreen(self, current):
        return bool(self.current["fill"] and current["width"] >= current["screenWidth"] and current["height"] >= current["screenHeight"])

    async def tick(self):
        hwnd = self.source["hwnd"]
        if not self.windows.u.IsWindow(hwnd) or self.windows.pid(hwnd) != self.source["pid"]:
            await self.stop_runtime(); await self.publish(enabled=False, active=False, phase="游戏窗口已关闭，已恢复画面"); return
        current = self.windows.describe(hwnd)
        # Once scaling, keep it through focus changes (task bar, other windows):
        # Magpie drops its topmost order by itself and resumes on return. Only a
        # menu that covers the game, or the window going away, stops the output.
        if self.menu_blocks() or not current or (not self.requested and self.foreground() != hwnd):
            if self.requested:
                logging.info("Magpie suspended: menu=%s window=%s foreground=%s", self.menu_blocks(), bool(current), self.foreground())
            await self.suspend(); await self.publish(active=False, phase="等待关闭菜单并返回所选游戏"); return
        if self.signature(current) != self.geometry:
            await self.suspend()
            crop = calculate_crop(current["width"], current["height"], current["screenWidth"], current["screenHeight"], self.current)
            self.geometry = self.signature(current); await self.publish(crop=crop)
        # Fill covers the screen at Magpie's output stage instead of cropping the
        # capture. Steam's side menus are drawn into the game frame; the frontend
        # lays them out inside the part of the frame that stays visible after the
        # cover crop, so the view never changes while they are open.
        cover = self.current["fill"]
        crop = calculate_crop(current["width"], current["height"], current["screenWidth"], current["screenHeight"],
                              {**self.current, "fill": False})
        if self.requested and self.running and self.running[0] != crop: await self.suspend()
        if self.requested and self.running and self.running[1] != cover:
            await self.command("view", cover=cover); self.running = (crop, cover)
        if self.process and self.process.returncode is not None:
            raise RuntimeError("Magpie 后台意外退出，请重新应用")
        if not self.process:
            if self.windows.hotkey(): raise RuntimeError("另一个 Magpie 实例正在运行，请退出它后再应用")
            higher = self.windows.elevated(current["pid"]) and not self.windows.elevated(os.getpid())
            self.process = await asyncio.create_subprocess_exec(str(self.runtime / "MagpieLauncher.exe"),
                "elevated" if higher else "normal", cwd=str(self.runtime), limit=2**21,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            self.sequence = 0
            self.started_at = time.monotonic(); self.requested = self.output_seen = False
            await self.start_touch(elevated=higher, parent=self.process.pid)
            await self.publish(active=False, phase="Magpie 正在准备游戏画面…")
        if not self.requested:
            if self.foreground() != hwnd or self.menu_blocks(): return
            if self.game_fullscreen(current):
                # The game's own fullscreen bakes its black bars into the frame,
                # so fill cannot crop them. Switch it to windowed once per apply.
                if self.switched != hwnd:
                    self.switched, self.switched_at = hwnd, time.monotonic()
                    if hasattr(self.windows, "toggle_fullscreen") and await asyncio.to_thread(self.windows.toggle_fullscreen, hwnd):
                        await self.publish(active=False, phase="正在把游戏切换为窗口模式，以便铺满屏幕…"); return
                elif time.monotonic() - self.switched_at < 3: return
            effects = self.current["pipeline"] or [{"name": "Bicubic", "scalingType": 1,
                "parameters": {"paramB": 0, "paramC": .5}}]
            if self.current["sharpen"]:
                effects = [*effects, {"name": "FSR\\FSR_RCAS", "parameters": {"sharpness": self.current["strength"]}}]
            reply = await self.command("start", target=hwnd, capture=CAPTURES[self.current["capture"]],
                effects=effects, fitOutput=bool(self.current["pipeline"]), cover=cover, maxFrameRate=self.current["maxFrameRate"],
                game3D=self.current["game3D"], adjustCursor=self.current["adjustCursor"],
                captureTitleBar=self.current["captureTitleBar"], disableDirectFlip=self.current["disableDirectFlip"],
                simulateExclusiveFullscreen=self.current["simulateExclusiveFullscreen"],
                cursorScaling=self.current["cursorScaling"], cursorInterpolation=self.current["cursorInterpolation"],
                **({"autoHideCursorDelay": self.current["autoHideCursorDelay"]} if self.current["autoHideCursorDelay"] else {}),
                toolbar=self.current["toolbar"], duplicateFrameDetection=self.current["duplicateFrameDetection"],
                multiMonitorUsage=self.current["multiMonitorUsage"],
                crop=[crop[k] for k in ("left", "top", "right", "bottom")])
            if not reply["ok"]:
                if "pid" not in reply: raise RuntimeError("Magpie 启动失败：" + reply.get("error", "权限启动失败"))
                # Focus may change during engine initialization. Try only after
                # returning to the exact selected game; a rejected start is not success.
                if self.foreground() != hwnd: return
                if not self.windows.u.IsWindow(hwnd):
                    await self.stop_runtime(); await self.publish(enabled=False, active=False, phase="游戏窗口已关闭，已恢复画面"); return
                raise RuntimeError("Magpie 拒绝捕获此窗口，请切换为窗口模式后重新应用")
            self.requested = True; self.started_at = time.monotonic(); self.running = (crop, cover)
            if self.plain: await self.command("plain", rects=self.plain)
        reply = await self.command("state")
        code = reply.get("errorCode", 0)
        if code:
            messages = {5: "无法捕获该游戏窗口", 6: "请将游戏切换为窗口模式", 7: "游戏权限高于画面后台",
                        8: "裁剪区域无效", 10: "渲染初始化失败", 11: "捕获失败，可尝试 GDI 兼容方式", 12: "显卡同步初始化失败"}
            raise RuntimeError(f"Magpie：{messages.get(code, '画面处理失败')}（{code}）")
        output = self.windows.output(self.engine_pid)
        if output and output["visible"]:
            if output["target"] != hwnd: raise RuntimeError("Magpie 的源窗口不匹配，已停止处理")
            self.output_seen = True
            phase = ("画面效果已生效；游戏仍是自带全屏，黑边无法裁掉，请在游戏设置中改为窗口模式"
                     if self.game_fullscreen(current) else "Magpie 画面效果已生效")
            await self.publish(active=True, source=output["source"], dest=output["dest"], phase=phase)
        elif time.monotonic() - self.started_at > 12:
            raise RuntimeError("Magpie 未产生可见输出。请使用窗口模式，或尝试 GDI 兼容捕获")

    async def command(self, command, **payload):
        if not self.process or self.process.returncode is not None: raise RuntimeError("Magpie 后台已退出")
        self.sequence += 1
        request = {"id": self.sequence, "command": command, **payload}
        self.process.stdin.write((json.dumps(request) + "\n").encode("utf-8"))
        await self.process.stdin.drain()
        try: raw = await asyncio.wait_for(self.process.stdout.readline(), 45)
        except asyncio.TimeoutError: raise RuntimeError("Magpie 控制接口无响应，请重新应用") from None
        if not raw: raise RuntimeError("Magpie 后台启动失败，请查看后台日志")
        reply = json.loads(raw)
        if reply.get("id") != self.sequence: raise RuntimeError("Magpie 控制响应不匹配")
        if reply.get("pid"):
            self.engine_pid = reply["pid"]
            await self.publish(enginePid=self.engine_pid, engineElevated=reply.get("elevated", False))
        return reply

    async def suspend(self):
        if self.process and self.process.returncode is None and self.requested:
            await self.command("stop")
        self.requested = self.output_seen = False; self.running = None

    async def close(self):
        self.closed = True
        for task in (self.auto_task, self.watcher):
            if task and not task.done():
                task.cancel()
                try: await task
                except asyncio.CancelledError: pass
        async with self.lock: await self.stop_runtime()
