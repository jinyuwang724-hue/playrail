"""Software input layer: the half of the mixed mapping the pad firmware cannot do (docs/input/gamepad-input-layer.md).

There is no switch for the user: the layer runs whenever any stored layout (global or per game) binds a
button, and only while the pad is in gamepad (XInput) mode.

The engine (bin/ClawInput-<version>.exe, native/InputLayer) hides the real pad with HidHide, reads it at
~1 kHz, translates it by the active layout and writes a ViGEm Xbox 360 pad plus keyboard / mouse
input. This module owns everything around it: the stored layouts (one global, optional per game),
their validation and compilation to the engine form, the on / off switch, conflicts with ClawTweaks
and Steam Input, and the firmware side of "every key belongs to one layer" (M1 / M2 become F22 / F23
for the hook, keys the layout uses go back to themselves in the firmware, all put back when off).
"""
import asyncio
import ctypes
from ctypes import wintypes
import json
import logging
import os
from pathlib import Path
import re
import subprocess
import time

SOURCES = ("A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Back", "Start", "Guide", "LS", "RS",
           "Up", "Down", "Left", "Right", "M1", "M2")
PAD_BUTTONS = {"A": 0x1000, "B": 0x2000, "X": 0x4000, "Y": 0x8000, "LB": 0x0100, "RB": 0x0200, "Back": 0x0020,
               "Start": 0x0010, "Guide": 0x0400, "LS": 0x0040, "RS": 0x0080, "Up": 0x0001, "Down": 0x0002,
               "Left": 0x0004, "Right": 0x0008}
PAD_AXES = {"LT": 0, "RT": 1, "LStickUp": 2, "LStickDown": 3, "LStickLeft": 4, "LStickRight": 5,
            "RStickUp": 6, "RStickDown": 7, "RStickLeft": 8, "RStickRight": 9}
MOUSE_BUTTONS = {"Left": 1, "Right": 2, "Middle": 3, "Back": 4, "Forward": 5}
WHEEL = {"Up": 1, "Down": 2, "Left": 3, "Right": 4}
TRIGGERS = ("regular", "long", "double", "start", "release", "chord")

# Keyboard: name -> (virtual-key code, extended key). Names follow Steam's keyboard page.
KEYS = {"Esc": (0x1B, False), "Tab": (0x09, False), "CapsLock": (0x14, False), "Space": (0x20, False),
        "Enter": (0x0D, False), "Backspace": (0x08, False), "LShift": (0xA0, False), "RShift": (0xA1, False),
        "LCtrl": (0xA2, False), "RCtrl": (0xA3, True), "LAlt": (0xA4, False), "RAlt": (0xA5, True),
        "LWin": (0x5B, True), "RWin": (0x5C, True), "Menu": (0x5D, True),
        "Insert": (0x2D, True), "Delete": (0x2E, True), "Home": (0x24, True), "End": (0x23, True),
        "PageUp": (0x21, True), "PageDown": (0x22, True), "Up": (0x26, True), "Down": (0x28, True),
        "Left": (0x25, True), "Right": (0x27, True), "PrintScreen": (0x2C, True), "ScrollLock": (0x91, False),
        "Pause": (0x13, False), "NumLock": (0x90, True), "NumMultiply": (0x6A, False), "NumAdd": (0x6B, False),
        "NumSubtract": (0x6D, False), "NumDecimal": (0x6E, False), "NumDivide": (0x6F, True),
        "NumEnter": (0x0D, True), "Minus": (0xBD, False), "Equals": (0xBB, False), "LeftBracket": (0xDB, False),
        "RightBracket": (0xDD, False), "Backslash": (0xDC, False), "Semicolon": (0xBA, False),
        "Quote": (0xDE, False), "Comma": (0xBC, False), "Period": (0xBE, False), "Slash": (0xBF, False),
        "Grave": (0xC0, False), "VolumeUp": (0xAF, False), "VolumeDown": (0xAE, False),
        "VolumeMute": (0xAD, False), "MediaPlayPause": (0xB3, False), "MediaStop": (0xB2, False),
        "MediaNext": (0xB0, False), "MediaPrev": (0xB1, False)}
KEYS.update({chr(c): (c, False) for c in range(ord("A"), ord("Z") + 1)})
KEYS.update({str(d): (0x30 + d, False) for d in range(10)})
KEYS.update({"F%d" % n: (0x6F + n, False) for n in range(1, 25)})
KEYS.update({"Num%d" % d: (0x60 + d, False) for d in range(10)})
KEY_ALIASES = {"Ctrl": "LCtrl", "Shift": "LShift", "Alt": "LAlt", "Win": "LWin", "Escape": "Esc", "Return": "Enter"}
# System commands. Screenshot = Game Bar's own shortcut (saved to Videos\Captures);
# steamScreenshot = Steam's in-game screenshot key (F12 unless changed in Steam, see steam_screenshot_keys).
SYSTEM = {"screenshot": ["LWin", "LAlt", "PrintScreen"], "steamScreenshot": "steam", "keyboard": None,
          "volumeUp": ["VolumeUp"], "volumeDown": ["VolumeDown"], "mute": ["VolumeMute"],
          "gamebar": None, "library": None, "controlCenter": None, "steamMainMenu": None,
          "steamQuickAccess": None, "steamLibrary": None, "magpieToggle": None}
STEAM_KEY_MODIFIERS = {"SHIFT": "LShift", "CTRL": "LCtrl", "CONTROL": "LCtrl", "ALT": "LAlt"}


def steam_screenshot_keys():
    """Steam's screenshot hotkey. Steam only writes it to localconfig.vdf once it is changed
    ("InGameOverlayScreenshotHotKey" "KEY_F12", modifiers before the key); otherwise it is F12."""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            root = Path(winreg.QueryValueEx(key, "SteamPath")[0])
        configs = sorted(root.glob("userdata/*/config/localconfig.vdf"), key=lambda p: p.stat().st_mtime, reverse=True)
        if configs:
            text = configs[0].read_text(encoding="utf-8", errors="replace")
            match = re.search(r'"InGameOverlayScreenshotHotKey"\s+"([^"]+)"', text, re.IGNORECASE)
            if match:
                keys = []
                for token in re.split(r"[\s+]+", match.group(1).strip()):
                    token = token.upper()
                    name = token[4:] if token.startswith("KEY_") else STEAM_KEY_MODIFIERS.get(token, "")
                    name = next((k for k in KEYS if k.upper() == name.upper()), None) if name else None
                    if name is None:
                        return ["F12"]
                    keys.append(name)
                if keys:
                    return keys
    except Exception as exc:
        logging.info("steam screenshot key: %s", exc)
    return ["F12"]
STICK_MODES = ("stick", "none")
SECOND_BATCH = {"mouse": "摇杆当鼠标", "scroll": "摇杆当滚轮", "dpad": "摇杆当十字键", "flick": "快速转向摇杆",
                "radial": "径向菜单", "mouseRegion": "鼠标区域", "touchMenu": "触摸菜单", "hotbar": "热键栏菜单"}
DEFAULTS = {"longPressMs": 300, "doublePressMs": 250, "startDelayMs": 0, "endDelayMs": 0, "repeatHz": 0,
            "toggle": False, "interruptible": True}
RANGES = {"longPressMs": (50, 5000), "doublePressMs": (50, 2000), "startDelayMs": (0, 5000),
          "endDelayMs": (0, 5000), "repeatHz": (0, 60)}
# HidHide only stops new opens of the pad (checked on the device 2026-10-16).
OPEN_BEFORE_NOTE = "开启前就在运行的程序（Steam、已经打开的游戏）仍能直接读到真手柄；游戏要重新启动后才只看到虚拟手柄"
GAME_NAME = re.compile(r"^[^\\/:*?\"<>|]{1,120}\.exe$", re.IGNORECASE)

# Firmware side: back keys become F22 / F23 (what the engine's keyboard hook catches).
BACK_KEY_CODES = {"m1": 0x9D, "m2": 0x9C}  # KEY_F22, KEY_F23 in the pad's code table
FIRMWARE_KEYS = ("A", "B", "X", "Y", "LB", "RB", "LT", "RT", "LS", "RS", "Up", "Down", "Left", "Right")


def _number(value, low, high, label):
    if type(value) not in (int, float) or value != value or not low <= value <= high:
        raise ValueError("%s 要在 %s 到 %s 之间" % (label, low, high))
    return int(value) if float(value).is_integer() else float(value)


def key_name(name):
    if not isinstance(name, str):
        raise ValueError("键名要是字符串")
    name = KEY_ALIASES.get(name, name)
    if name not in KEYS:
        raise ValueError("不认识的键盘键 %r" % (name,))
    return name


def validate_command(command):
    if not isinstance(command, dict):
        raise ValueError("指令格式错误")
    kind = command.get("type")
    if kind == "none":
        return {"type": "none"}
    if kind == "gamepad":
        button = command.get("button")
        if button not in PAD_BUTTONS and button not in PAD_AXES:
            raise ValueError("不认识的手柄键 %r" % (button,))
        return {"type": "gamepad", "button": button}
    if kind == "keyboard":
        keys = command.get("keys")
        if not isinstance(keys, list) or not 1 <= len(keys) <= 4:
            raise ValueError("键盘指令要 1 到 4 个键")
        keys = [key_name(k) for k in keys]
        if len(set(keys)) != len(keys):
            raise ValueError("键盘指令里有重复的键")
        return {"type": "keyboard", "keys": keys}
    if kind == "mouse":
        if "wheel" in command:
            if command["wheel"] not in WHEEL:
                raise ValueError("滚轮方向只能是 " + " / ".join(WHEEL))
            return {"type": "mouse", "wheel": command["wheel"]}
        if command.get("button") not in MOUSE_BUTTONS:
            raise ValueError("鼠标键只能是 " + " / ".join(MOUSE_BUTTONS))
        return {"type": "mouse", "button": command["button"]}
    if kind == "system":
        if command.get("action") not in SYSTEM:
            raise ValueError("系统动作只能是 " + " / ".join(SYSTEM))
        return {"type": "system", "action": command["action"]}
    if kind in ("actionSet", "camera"):
        raise ValueError("操作集 / 视角指令第二批才做")
    raise ValueError("不认识的指令类型 %r" % (kind,))


def validate_activator(item, source):
    if not isinstance(item, dict):
        raise ValueError("触发方式格式错误")
    trigger = item.get("trigger")
    if trigger not in TRIGGERS:
        raise ValueError("触发方式只能是 " + " / ".join(TRIGGERS))
    for reserved, label in (("extraCommands", "额外指令"), ("haptic", "触感"), ("cycle", "指令循环"), ("invert", "反转输入")):
        if item.get(reserved) not in (None, False, []):
            raise ValueError(label + "第二批才做")
    result = {"trigger": trigger}
    for field, default in DEFAULTS.items():
        value = item.get(field, default)
        if field in RANGES:
            value = _number(value, *RANGES[field], field)
        elif type(value) is not bool:
            raise ValueError("%s 要是 true / false" % field)
        result[field] = value
    if trigger == "chord":
        chord = item.get("chordWith")
        if chord not in SOURCES or chord == source:
            raise ValueError("组合键要指定另一个键（chordWith）")
        result["chordWith"] = chord
    result["command"] = validate_command(item.get("command"))
    return result


def validate_layout(layout):
    """Stored / UI form, with every default filled in. Raises ValueError with a message for the UI."""
    if not isinstance(layout, dict):
        raise ValueError("布局格式错误")
    if layout.get("actionSets") not in (None, []):
        raise ValueError("操作集第二批才做")
    name = layout.get("name", "")
    if not isinstance(name, str) or len(name) > 60:
        raise ValueError("布局名称最多 60 个字")
    buttons = layout.get("buttons", {})
    if not isinstance(buttons, dict):
        raise ValueError("buttons 要是对象")
    out_buttons = {}
    for source, items in buttons.items():
        if source not in SOURCES:
            raise ValueError("不认识的键 %r" % (source,))
        if not isinstance(items, list) or len(items) > 8:
            raise ValueError("%s 的触发方式要是列表（最多 8 个）" % source)
        out_buttons[source] = [validate_activator(item, source) for item in items]
    triggers = layout.get("triggers", {})
    if not isinstance(triggers, dict):
        raise ValueError("triggers 要是对象")
    out_triggers = {}
    for side in ("LT", "RT"):
        value = triggers.get(side, {})
        if not isinstance(value, dict):
            raise ValueError("扳机设置格式错误")
        out_triggers[side] = {"threshold": _number(value.get("threshold", 50), 1, 99, side + " 按下阈值 %")}
    sticks = layout.get("sticks", {})
    if not isinstance(sticks, dict):
        raise ValueError("sticks 要是对象")
    out_sticks = {}
    for side in ("left", "right"):
        value = sticks.get(side, {})
        if not isinstance(value, dict):
            raise ValueError("摇杆设置格式错误")
        mode = value.get("mode", "stick")
        if mode in SECOND_BATCH:
            raise ValueError(SECOND_BATCH[mode] + "第二批才做")
        if mode not in STICK_MODES:
            raise ValueError("摇杆模式只能是 " + " / ".join(STICK_MODES))
        output = value.get("output", side)
        if output not in ("left", "right"):
            raise ValueError("摇杆输出只能是 left / right")
        out_sticks[side] = {"mode": mode, "output": output}
    settings = layout.get("settings", {})
    if not isinstance(settings, dict):
        raise ValueError("settings 要是对象")
    tap = _number(settings.get("tapMs", 40), 10, 500, "短按输出时长 ms")
    return {"version": 1, "name": name, "buttons": out_buttons, "triggers": out_triggers, "sticks": out_sticks,
            "settings": {"tapMs": tap}}


def compile_command(command):
    kind = command["type"]
    if kind == "gamepad":
        button = command["button"]
        if button in PAD_BUTTONS:
            return {"kind": "pad", "pad": PAD_BUTTONS[button]}
        return {"kind": "axis", "value": PAD_AXES[button]}
    if kind == "keyboard":
        return {"kind": "keys", "keys": [KEYS[k][0] for k in command["keys"]],
                "extended": [KEYS[k][1] for k in command["keys"]]}
    if kind == "mouse":
        if "wheel" in command:
            return {"kind": "wheel", "value": WHEEL[command["wheel"]]}
        return {"kind": "mouse", "value": MOUSE_BUTTONS[command["button"]]}
    if kind == "system":
        keys = SYSTEM[command["action"]]
        if keys is None:
            return {"kind": "action", "action": command["action"]}
        if keys == "steam":
            keys = steam_screenshot_keys()
        return compile_command({"type": "keyboard", "keys": keys})
    return {"kind": "none"}


def compile_layout(layout):
    """Engine form (native/InputLayer/LayoutJson.cs)."""
    sticks = layout["sticks"]
    out = {"name": layout["name"], "tapMs": layout["settings"]["tapMs"],
           "ltThreshold": round(layout["triggers"]["LT"]["threshold"] * 255 / 100),
           "rtThreshold": round(layout["triggers"]["RT"]["threshold"] * 255 / 100),
           "leftStick": sticks["left"]["output"] if sticks["left"]["mode"] == "stick" else "none",
           "rightStick": sticks["right"]["output"] if sticks["right"]["mode"] == "stick" else "none",
           "buttons": {}}
    for source, items in layout["buttons"].items():
        out["buttons"][source] = [{
            "type": item["trigger"], "longMs": item["longPressMs"], "doubleMs": item["doublePressMs"],
            "startDelayMs": item["startDelayMs"], "endDelayMs": item["endDelayMs"], "repeatHz": item["repeatHz"],
            "toggle": item["toggle"], "interruptible": item["interruptible"], "chord": item.get("chordWith"),
            "command": compile_command(item["command"])} for item in items]
    return out


def default_layout():
    return validate_layout({"name": "默认"})


def catalog():
    """Everything the gamepad page needs to build its pickers."""
    return {
        "sources": list(SOURCES),
        "triggers": list(TRIGGERS),
        "defaults": dict(DEFAULTS),
        "ranges": {k: list(v) for k, v in RANGES.items()},
        "commands": {
            "gamepad": list(PAD_BUTTONS) + list(PAD_AXES),
            "keyboard": sorted(KEYS), "keyboardAliases": dict(KEY_ALIASES),
            "mouse": {"button": list(MOUSE_BUTTONS), "wheel": list(WHEEL)},
            "system": list(SYSTEM),
        },
        "sticks": {"modes": list(STICK_MODES), "outputs": ["left", "right"], "secondBatch": SECOND_BATCH},
        "triggerThreshold": {"default": 50, "range": [1, 99]},
        "tapMs": {"default": 40, "range": [10, 500]},
        "secondBatch": ["sticks.mouse", "sticks.scroll", "radial menus", "actionSets", "extraCommands",
                        "haptic", "cycle", "invert"],
    }


def layout_sources(layout):
    """Sources the layout handles itself (anything listed, including disabled ones)."""
    return set(layout["buttons"])


# ---- conflicts -------------------------------------------------------------------------------------

class _ProcessEntry(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD), ("pid", wintypes.DWORD),
                ("heap", ctypes.c_size_t), ("module", wintypes.DWORD), ("threads", wintypes.DWORD),
                ("parent", wintypes.DWORD), ("priority", wintypes.LONG), ("flags", wintypes.DWORD),
                ("exe", wintypes.WCHAR * 260)]


def running_processes():
    """Lower-case exe names of every process."""
    k = ctypes.windll.kernel32
    k.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    snap = k.CreateToolhelp32Snapshot(2, 0)
    names = set()
    if not snap or snap == wintypes.HANDLE(-1).value:
        return names
    try:
        entry = _ProcessEntry(); entry.size = ctypes.sizeof(_ProcessEntry)
        ok = k.Process32FirstW(wintypes.HANDLE(snap), ctypes.byref(entry))
        while ok:
            names.add(entry.exe.lower())
            ok = k.Process32NextW(wintypes.HANDLE(snap), ctypes.byref(entry))
    finally:
        k.CloseHandle(wintypes.HANDLE(snap))
    return names


def steam_xbox_support():
    """Steam's 'Xbox configuration support' (Steam Input for Xbox pads), True / False / None (unknown)."""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            steam = Path(winreg.QueryValueEx(key, "SteamPath")[0])
    except OSError:
        return None
    for path in [steam / "config" / "config.vdf"] + sorted((steam / "userdata").glob("*/config/localconfig.vdf")):
        try:
            match = re.search(r'"SteamController_XBoxSupport"\s+"(\d+)"', path.read_text(encoding="utf-8", errors="replace"))
        except OSError:
            continue
        if match:
            return match.group(1) != "0"
    return None


def steam_apps():
    """steam.exe and its web helper(s): Steam (Big Picture, its navigation) keeps reading the real pad."""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            steam = Path(winreg.QueryValueEx(key, "SteamPath")[0])
    except OSError:
        return []
    found = [steam / "steam.exe"] + sorted((steam / "bin" / "cef").glob("*/steamwebhelper.exe"))
    # SteamPath is stored lower-case ("c:/software"); HidHide gets the path as it is on disk.
    return [os.path.realpath(path) for path in found if path.is_file()]


def conflicts(engine_status, processes=None, xbox_support=None):
    processes = running_processes() if processes is None else processes
    hidhide = (engine_status or {}).get("hidhide") or {}
    white = [p.lower() for p in hidhide.get("whitelist") or []]
    ours = {p.lower() for p in hidhide.get("apps") or []}
    hidden = {d.lower() for d in hidhide.get("hidden") or []}
    tweaks = sorted(processes & {"xboxgamingbarhelper.exe", "ctw_center.exe"})
    tweaks_sees = any(p.endswith("\\xboxgamingbarhelper.exe") for p in white)
    steam = "steam.exe" in processes
    steam_sees = any(p.endswith("\\steam.exe") for p in white)
    others = [d for d in hidhide.get("blacklist") or [] if d.lower() not in hidden]
    virtual = (engine_status or {}).get("otherVirtualPads", 0)
    warnings = []
    if tweaks:
        warnings.append("ClawTweaks 在运行" + ("，它的后台在 HidHide 白名单里，仍能直接读真手柄" if tweaks_sees else "")
                        + "；它也会改 HidHide / ViGEm，可能和输入层互相打架")
    if virtual:
        warnings.append("还有 %d 个别的虚拟 Xbox 手柄（ClawTweaks / 别的映射工具？），游戏会看到多个手柄" % virtual)
    if steam and xbox_support:
        warnings.append("Steam 开着「Xbox 配置支持」：游戏开了 Steam 输入时，Steam 会把我们的虚拟手柄再映射一遍")
    if others:
        warnings.append("HidHide 里还有别人隐藏的设备，开启隐藏后它们也会被藏起来：" + "、".join(others[:4]))
    return {"clawTweaks": {"running": bool(tweaks), "processes": tweaks, "helperSeesPad": tweaks_sees},
            "steamInput": {"steamRunning": steam, "xboxSupport": xbox_support, "steamSeesPad": steam_sees},
            "otherVirtualPads": virtual, "otherHiddenDevices": others, "warnings": warnings}


# ---- storage -----------------------------------------------------------------------------------------

class Layouts:
    """layouts/global.json and layouts/games/<exe>.json."""

    def __init__(self, folder):
        self.folder = Path(folder)
        self.games_folder = self.folder / "games"

    @staticmethod
    def game_name(game):
        if not isinstance(game, str) or not GAME_NAME.match(game) or game.strip() != game:
            raise ValueError("游戏要用 exe 文件名，例如 eldenring.exe")
        return game.lower()

    def _path(self, game):
        return self.folder / "global.json" if game is None else self.games_folder / (self.game_name(game) + ".json")

    def get(self, game=None):
        try:
            return validate_layout(json.loads(self._path(game).read_text(encoding="utf-8")))
        except FileNotFoundError:
            return None
        except (ValueError, OSError) as error:
            logging.warning("Input layout %s unreadable: %s", game or "global", error)
            return None

    def save(self, layout, game=None):
        path = self._path(game)
        if layout is None:
            if game is None:
                raise ValueError("全局布局不能删除，只能改")
            path.unlink(missing_ok=True)
            return None
        layout = validate_layout(layout)
        path.parent.mkdir(parents=True, exist_ok=True)
        temp = path.with_suffix(".tmp")
        temp.write_text(json.dumps(layout, ensure_ascii=False, indent=1), encoding="utf-8")
        os.replace(temp, path)
        return layout

    def games(self):
        return sorted(p.stem for p in self.games_folder.glob("*.exe.json")) if self.games_folder.is_dir() else []

    def all(self):
        result = {"global": self.get() or default_layout(), "games": {}}
        for game in self.games():
            layout = self.get(game)
            if layout:
                result["games"][game] = layout
        return result


# ---- the switch --------------------------------------------------------------------------------------

def layouts_bind_buttons(layouts):
    """The rule: the layer runs while any layout binds a button."""
    return any(layout["buttons"] for layout in [layouts["global"]] + list(layouts["games"].values()))


class InputLayer:
    """Runs by the rule (layouts bind buttons, pad in gamepad mode); set_enabled() overrides it for debugging."""
    REQUEST_TIMEOUT = 20
    RETRY_DELAY = 5
    RETRIES = 3          # automatic restarts after an engine crash ...
    RETRY_WINDOW = 600   # ... per 10 minutes

    def __init__(self, plugin_root, data_dir, emit, gamepad=None, not_games=(), python=None, log=None,
                 spawn=None, processes=running_processes, xbox_support=steam_xbox_support, extra_apps=steam_apps):
        self._root = Path(plugin_root)
        self._data = Path(data_dir) / "input-layer"
        self._data.mkdir(parents=True, exist_ok=True)
        self._emit = emit
        self._gamepad = gamepad
        self._not_games = sorted(set(not_games) | {"clawlibrary.exe"})
        self._python = python
        self._extra_apps = extra_apps
        self._override = None    # set_enabled(): True / False beats the rule until set back to None
        self._pad_mode = None    # xinput / dinput / desktop, from the gamepad's mode watcher
        self._crashes = []
        self._osd = None
        self._log = log or logging.info
        self._spawn = spawn or self._spawn_engine
        self._processes = processes
        self._xbox_support = xbox_support
        self.layouts = Layouts(self._data / "layouts")
        self._settings_path = self._data / "settings.json"
        self._firmware_path = self._data / "firmware.json"
        self._settings = {"pollHz": 1000}
        self._process = None
        self._reader = None
        self._ids = 0
        self._waiting = {}
        self._lock = asyncio.Lock()
        self._engine = {}
        self._phase = "off"
        self._error = None
        self._conflicts = None
        self._conflicts_at = 0.0
        self._xbox = None
        self._xbox_at = 0.0
        self._closing = False
        try:
            saved = json.loads(self._settings_path.read_text(encoding="utf-8"))
            if type(saved.get("pollHz")) is int and 250 <= saved["pollHz"] <= 1000:
                self._settings["pollHz"] = saved["pollHz"]
        except (OSError, ValueError, AttributeError):
            pass

    @property
    def exe(self):
        version = json.loads((self._root / "plugin.json").read_text(encoding="utf-8"))["version"]
        enhanced = self._root / "bin" / ("ClawInput-" + version + "-enhanced-sleep.exe")
        if enhanced.is_file():
            return enhanced
        return self._root / "bin" / ("ClawInput-" + version + ".exe")

    def wanted(self):
        if self._pad_mode not in (None, "xinput"):
            return False  # desktop / DInput mode: no XInput pad to read, nothing hidden
        if self._override is not None:
            return self._override
        return layouts_bind_buttons(self.layouts.all())

    async def start(self, pad_mode=None):
        self._pad_mode = pad_mode
        await self._restore_leftovers("agent-start", firmware=not self.wanted())
        await self.reconcile("agent-start")

    async def reconcile(self, why):
        """Start or stop the engine to match the rule. Never raises: a failure lands in the state."""
        async with self._lock:
            want = self.wanted()
            running = self._phase == "active" and self._process is not None
            try:
                if want and not running:
                    self._log("Input layer on (%s)" % why)
                    await self._enable()
                elif not want and (running or self._process is not None or self._phase == "error"):
                    self._log("Input layer off (%s)" % why)
                    self._phase = "stopping"
                    await self._publish()
                    self._phase = "off"
                    self._error = None
                    await self._disable()
            except Exception as error:
                self._log("Input layer %s failed: %s" % (why, error))
        await self._publish()

    async def set_pad_mode(self, mode):
        if mode == self._pad_mode:
            return
        self._pad_mode = mode
        await self.reconcile("pad mode " + str(mode))

    async def show_mode_osd(self, mode):
        """The mode notice (ClawInput --osd), replacing one that is still fading."""
        if self._osd and self._osd.returncode is None:
            try:
                self._osd.kill()
            except OSError:
                pass
        try:
            self._osd = await asyncio.create_subprocess_exec(
                str(self.exe), "--osd", "desktop" if mode == "desktop" else "gamepad",
                stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW)
        except OSError as error:
            self._log("Mode notice failed: %s" % error)

    async def _restore_leftovers(self, why, firmware=True):
        """A hiding or firmware takeover left by a crashed engine / agent goes back first."""
        if self.exe.is_file():
            try:
                process = await asyncio.create_subprocess_exec(
                    str(self.exe), "--restore", str(self._data), stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
                out, _ = await asyncio.wait_for(process.communicate(), 15)
                reply = json.loads(out.decode("utf-8", "replace").strip().splitlines()[-1] or "{}")
                if reply.get("restored"):
                    self._log("Input layer: undid a HidHide hiding left behind (%s)" % why)
            except (OSError, ValueError, IndexError, asyncio.TimeoutError) as error:
                self._log("Input layer restore check failed: %s" % error)
        if firmware and self._firmware_path.exists() and self._process is None:
            await self._release_firmware()

    # ---- engine process ---------------------------------------------------------------------------

    async def _spawn_engine(self):
        return await asyncio.create_subprocess_exec(
            str(self.exe), str(self._data), stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW, limit=4 * 1024 * 1024)

    async def _start_engine(self):
        if self._process and self._process.returncode is None:
            return
        if self._spawn == self._spawn_engine and not self.exe.is_file():
            raise RuntimeError("输入层文件缺失，请重新安装工具箱")
        self._process = await self._spawn()
        line = await asyncio.wait_for(self._process.stdout.readline(), 40)
        ready = json.loads(line or b"{}")
        if ready.get("type") != "ready":
            await self._stop_engine()
            raise RuntimeError(ready.get("error") or "输入层没能获得管理员权限")
        self._reader = asyncio.create_task(self._read())

    async def _read(self):
        process = self._process
        try:
            while True:
                line = await process.stdout.readline()
                if not line:
                    break
                try:
                    message = json.loads(line.decode("utf-8-sig"))
                except ValueError:
                    continue
                if message.get("type") == "system_action":
                    await self._emit("input_system_action", message)
                    continue
                if "id" in message and message["id"] in self._waiting:
                    future = self._waiting.pop(message["id"])
                    if not future.done():
                        future.set_result(message)
                if isinstance(message.get("status"), dict):
                    self._engine = message["status"]
                    if message.get("type") == "status":
                        await self._publish()
        finally:
            for future in self._waiting.values():
                if not future.done():
                    future.set_exception(RuntimeError("输入层已退出"))
            self._waiting.clear()
            if process is self._process and not self._closing and self._phase in ("active", "starting"):
                # Unexpected end: the bridge / guardian already unhid the pad; put the firmware back too.
                self._process = None
                self._phase, self._error = "error", "输入层意外退出，已解除隐藏"
                self._engine = {}
                asyncio.create_task(self._after_crash())

    async def _after_crash(self):
        await self._restore_leftovers("engine-exit")
        await self._publish()
        # No switch for the user to flip back: start again, a few times at most.
        now = time.monotonic()
        self._crashes = [t for t in self._crashes if now - t < self.RETRY_WINDOW] + [now]
        if len(self._crashes) <= self.RETRIES and not self._closing:
            await asyncio.sleep(self.RETRY_DELAY)
            if not self._closing and self._phase == "error":
                await self.reconcile("restart after crash")

    async def _call(self, command, timeout=None, **args):
        self._ids += 1
        ident = self._ids
        future = asyncio.get_running_loop().create_future()
        self._waiting[ident] = future
        self._process.stdin.write((json.dumps({"id": ident, "command": command, **args}) + "\n").encode("utf-8"))
        await self._process.stdin.drain()
        try:
            reply = await asyncio.wait_for(future, timeout or self.REQUEST_TIMEOUT)
        finally:
            self._waiting.pop(ident, None)
        if not reply.get("ok"):
            raise RuntimeError(reply.get("error") or "输入层操作失败")
        return reply

    async def _stop_engine(self):
        process, self._process = self._process, None
        if process and process.returncode is None:
            try:
                process.stdin.close()
                await asyncio.wait_for(process.wait(), 10)
            except (asyncio.TimeoutError, OSError):
                process.kill()
        if self._reader and not self._reader.done():
            try:
                await asyncio.wait_for(self._reader, 2)
            except (asyncio.TimeoutError, asyncio.CancelledError):
                self._reader.cancel()
        self._reader = None

    def _configure_args(self):
        layouts = self.layouts.all()
        return {"layouts": {"global": compile_layout(layouts["global"]),
                            "games": {game: compile_layout(layout) for game, layout in layouts["games"].items()}},
                "notGames": self._not_games,
                "whitelist": ([self._python] if self._python else []) + list(self._extra_apps() if self._extra_apps else []),
                "pollHz": self._settings["pollHz"]}

    # ---- firmware: every key belongs to one layer -------------------------------------------------

    def _firmware_record(self):
        try:
            return json.loads(self._firmware_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {"items": {}}

    def _write_firmware_record(self, record):
        if not record["items"]:
            self._firmware_path.unlink(missing_ok=True)
            return
        temp = self._firmware_path.with_suffix(".tmp")
        temp.write_text(json.dumps(record), encoding="utf-8")
        os.replace(temp, self._firmware_path)

    def _wanted_firmware(self):
        used = set()
        layouts = self.layouts.all()
        for layout in [layouts["global"]] + list(layouts["games"].values()):
            used |= layout_sources(layout)
        return used

    async def _take_firmware(self):
        """Back keys the layouts use -> F22 / F23; front keys the layouts use -> themselves in the firmware."""
        if self._gamepad is None:
            return
        used = self._wanted_firmware()
        record = self._firmware_record()
        items = record["items"]
        state = await self._gamepad.read()
        if not state.get("available"):
            self._error = "读不到手柄固件（%s），M1 / M2 暂时用不了" % state.get("error")
            return
        managed = set(state.get("managed") or [])
        own = {"m1": [BACK_KEY_CODES["m1"]], "m2": [BACK_KEY_CODES["m2"]]}
        for back, source in (("m1", "M1"), ("m2", "M2")):
            if source in used and back not in items:
                items[back] = {"codes": state["backKeys"][back]["codes"], "managed": back in managed}
                self._write_firmware_record(record)
                await self._gamepad.set_back_key(back, own[back])
        own_codes = {"Up": 0x01, "Down": 0x02, "Left": 0x03, "Right": 0x04, "LB": 0x05, "RB": 0x06, "LS": 0x07,
                     "RS": 0x08, "A": 0x09, "B": 0x0A, "X": 0x0B, "Y": 0x0C, "LT": 0x13, "RT": 0x14}
        for source in FIRMWARE_KEYS:
            current = (state.get("keys") or {}).get(source, {}).get("codes")
            name = "key." + source
            if source in used and name not in items and current and current != [own_codes[source]]:
                items[name] = {"codes": current, "managed": name in managed}
                self._write_firmware_record(record)
                await self._gamepad.set_key_map(source, [own_codes[source]])
        await self._release_firmware(keep=used)

    async def _release_firmware(self, keep=()):
        """Put back what we changed, except for keys the layouts still use."""
        if self._gamepad is None:
            return
        record = self._firmware_record()
        for name in list(record["items"]):
            source = {"m1": "M1", "m2": "M2"}.get(name) or name[4:]
            if source in keep:
                continue
            item = record["items"][name]
            try:
                if not item["managed"]:
                    await self._gamepad.restore(name)  # the bytes from before anyone in the toolbox took it
                elif name in ("m1", "m2"):
                    await self._gamepad.set_back_key(name, item["codes"])
                else:
                    await self._gamepad.set_key_map(source, item["codes"])
                del record["items"][name]
                self._write_firmware_record(record)
            except Exception as error:
                self._log("Input layer: could not put back %s: %s" % (name, error))

    # ---- switch --------------------------------------------------------------------------------------

    async def _enable(self):
        self._phase, self._error = "starting", None
        await self._publish()
        try:
            await self._start_engine()
            await self._call("configure", **self._configure_args())
            await self._take_firmware()
            reply = await self._call("enable")
            self._engine = reply.get("status") or {}
            self._phase = "active"
        except Exception as error:
            self._phase, self._error = "error", str(error)
            await self._disable()
            self._phase = "error"
            raise RuntimeError(str(error))
        finally:
            await self._publish()

    async def _disable(self):
        if self._process and self._process.returncode is None:
            try:
                await self._call("stop", timeout=15)
            except Exception as error:
                self._log("Input layer stop: %s" % error)
        await self._stop_engine()
        await self._restore_leftovers("disable")
        self._engine = {}
        if self._phase != "error":
            self._phase = "off"

    async def set_enabled(self, enabled):
        """Debugging only (the UI has no switch): true / false overrides the rule, null goes back to it.
        Not saved: the next agent start follows the rule again."""
        if enabled is not None and type(enabled) is not bool:
            raise ValueError("enabled 要是 true / false / null")
        self._override = enabled
        await self.reconcile("override %s" % enabled)
        return await self.state()

    # ---- layouts -------------------------------------------------------------------------------------

    async def get_layout(self, game=None):
        stored = self.layouts.get(game)
        source = ("game" if game else "global") if stored else ("global" if game else "default")
        layout = stored or (self.layouts.get() if game else None) or default_layout()
        return {"game": Layouts.game_name(game) if game else None, "source": source, "layout": layout,
                "games": self.layouts.games()}

    async def save_layout(self, layout, game=None):
        async with self._lock:
            saved = self.layouts.save(layout, game)
            if self._phase == "active" and self._process:
                await self._call("configure", **self._configure_args())
                await self._take_firmware()
        await self.reconcile("layout saved")  # first bound button starts the layer, last one removed stops it
        result = await self.get_layout(game)
        result["saved"] = saved is not None
        return result

    async def test(self, steps, layout_for=None, tail_ms=600):
        """Scripted button timeline through the real engine (virtual pad + SendInput), for checks on the device."""
        if self._phase != "active" or not self._process:
            raise RuntimeError("先打开输入层")
        if not isinstance(steps, list) or not 1 <= len(steps) <= 64:
            raise ValueError("测试步骤要 1 到 64 步")
        for step in steps:
            if (not isinstance(step, dict) or type(step.get("at")) not in (int, float) or not 0 <= step["at"] <= 10000
                    or not isinstance(step.get("buttons"), list) or any(b not in SOURCES for b in step["buttons"])):
                raise ValueError("测试步骤格式：{at: 毫秒, buttons: [键名…]}")
        if layout_for is not None:
            Layouts.game_name(layout_for)
        await self._call("test", steps=steps, layoutFor=layout_for, tailMs=tail_ms)
        length = max(step["at"] for step in steps) + tail_ms
        await asyncio.sleep(length / 1000 + 0.1)
        for _ in range(50):
            reply = await self._call("test_result")
            if reply.get("finished"):
                return {"timeline": reply.get("timeline") or [], "status": reply.get("status")}
            await asyncio.sleep(0.1)
        raise RuntimeError("测试没有结束")

    # ---- state -------------------------------------------------------------------------------------

    async def state(self):
        now = time.monotonic()
        if self._conflicts is None or now - self._conflicts_at > 2:
            try:
                processes = await asyncio.to_thread(self._processes)
                if self._xbox_at == 0 or now - self._xbox_at > 60:
                    self._xbox, self._xbox_at = await asyncio.to_thread(self._xbox_support), now
            except Exception:
                processes = set()
            self._conflicts = conflicts(self._engine, processes, self._xbox)
            self._conflicts_at = now
        engine = self._engine or {}
        notes = [OPEN_BEFORE_NOTE] if self._phase == "active" else []
        return {"owner": "toolbox" if self._phase == "active" else "off", "auto": self._override is None,
                "wanted": self.wanted(), "override": self._override, "padMode": self._pad_mode,
                "phase": self._phase, "error": self._error or engine.get("error"), "notes": notes,
                "pollHz": self._settings["pollHz"], "engine": engine or None,
                "latency": engine.get("latency"), "layout": engine.get("layout"), "foreground": engine.get("foreground"),
                "conflicts": self._conflicts, "firmware": self._firmware_record()["items"],
                "layouts": {"global": self.layouts.get() is not None, "games": self.layouts.games()}}

    async def _publish(self):
        try:
            await self._emit("input_layer", await self.state())
        except Exception:
            logging.exception("input layer publish failed")

    async def close(self):
        self._closing = True
        async with self._lock:
            if self._process:
                self._phase = "off"
                await self._disable()
