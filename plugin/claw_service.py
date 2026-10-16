"""Toolbox service: everything the toolbox does on the device, independent of Decky and Steam.

Runs inside the standalone agent (claw_agent.py). Front ends (the Decky plugin today) reach it
through the agent's local connection. Native helpers own hardware events; the router restores
ClawTweaks whenever no Steam front end is ready."""
import asyncio
import ctypes
from ctypes import wintypes
import json
import logging
import math
import ntpath
import os
from pathlib import Path
import subprocess
import sys
import importlib.util
import time

PLUGIN_ROOT = Path(__file__).resolve().parent
ALLOWED_ACTIONS = {"MainMenu", "QuickAccess", "MagpieToggle", "None"}
_haptic_spec = importlib.util.spec_from_file_location("claw_toolbox_haptics", PLUGIN_ROOT / "claw_navigation_haptics.py")
_haptic_module = importlib.util.module_from_spec(_haptic_spec)
_haptic_spec.loader.exec_module(_haptic_module)
_compatibility_spec = importlib.util.spec_from_file_location("claw_toolbox_compatibility", PLUGIN_ROOT / "claw_compatibility.py")
_compatibility_module = importlib.util.module_from_spec(_compatibility_spec)
_compatibility_spec.loader.exec_module(_compatibility_module)
_magpie_spec = importlib.util.spec_from_file_location("claw_toolbox_magpie", PLUGIN_ROOT / "claw_magpie.py")
_magpie_module = importlib.util.module_from_spec(_magpie_spec)
_magpie_spec.loader.exec_module(_magpie_module)
_frames_spec = importlib.util.spec_from_file_location("claw_toolbox_overlay_frames", PLUGIN_ROOT / "claw_overlay_frames.py")
_frames_module = importlib.util.module_from_spec(_frames_spec)
_frames_spec.loader.exec_module(_frames_module)
_power_spec = importlib.util.spec_from_file_location("claw_toolbox_power", PLUGIN_ROOT / "claw_power.py")
_power_module = importlib.util.module_from_spec(_power_spec)
_power_spec.loader.exec_module(_power_module)
_rumble_spec = importlib.util.spec_from_file_location("claw_toolbox_rumble", PLUGIN_ROOT / "claw_rumble.py")
_rumble_module = importlib.util.module_from_spec(_rumble_spec)
_rumble_spec.loader.exec_module(_rumble_module)
_gamepad_fw_spec = importlib.util.spec_from_file_location("claw_toolbox_gamepad_fw", PLUGIN_ROOT / "claw_gamepad_fw.py")
_gamepad_fw_module = importlib.util.module_from_spec(_gamepad_fw_spec)
_gamepad_fw_spec.loader.exec_module(_gamepad_fw_module)
_input_spec = importlib.util.spec_from_file_location("claw_toolbox_input_layer", PLUGIN_ROOT / "claw_input_layer.py")
_input_module = importlib.util.module_from_spec(_input_spec)
_input_spec.loader.exec_module(_input_module)

# ControllerHapticsService.PlayPattern in Playhub 2.1.0 executes the first step.
HAPTIC_PATTERNS = {
    "moveUp": (16, None, .86), "moveDown": (16, None, .86),
    "moveLeft": (16, None, .86), "moveRight": (16, None, .86),
    "tabPrevious": (18, None, 1), "tabNext": (18, None, 1),
    "sliderDecrease": (12, None, .68), "sliderIncrease": (12, None, .68),
    "toggleOn": (13, 0, .42), "toggleOff": (13, 1, .42),
    "confirm": (21, 2, 1), "back": (18, 0, .88),
    "options": (11, 0, .48), "menu": (11, 0, .48),
    "dropdown": (12, 2, .40), "letter": (19, 2, .90),
}


def navigation_request(value):
    if not isinstance(value, dict):
        raise ValueError("反馈请求格式错误")
    if value.get("stop") is True:
        return {"stop": True, "requestId": value.get("requestId")}
    pattern, side, strength = value.get("pattern"), value.get("side"), value.get("magnitude")
    if (not isinstance(pattern, str) or len(pattern) > 48 or type(side) is not int or side not in (0, 1, 2)
            or type(strength) not in (int, float) or not math.isfinite(strength) or not 0 <= strength <= 1):
        raise ValueError("反馈参数格式错误")
    duration, fixed_side, scale = HAPTIC_PATTERNS.get(pattern, (16, None, .82))
    return {"vendorId": value.get("vendorId"), "productId": value.get("productId"),
            "requestId": value.get("requestId"), "durationMs": duration,
            "side": side if fixed_side is None else fixed_side, "magnitude": strength * scale}


def validate_settings(value):
    if not isinstance(value, dict) or type(value.get("enabled")) is not bool:
        raise ValueError("设置格式错误")
    if (type(value.get("msiAction")) is not str or type(value.get("qamAction")) is not str
            or value["msiAction"] not in ALLOWED_ACTIONS or value["qamAction"] not in ALLOWED_ACTIONS):
        raise ValueError("不支持的按键动作")
    if type(value.get("hapticsEnabled", True)) is not bool:
        raise ValueError("反馈开关格式错误")
    return {**{key: value[key] for key in ("enabled", "msiAction", "qamAction")},
            "hapticsEnabled": value.get("hapticsEnabled", True)}


class Service:
    def __init__(self, data, emit):
        """data: settings folder. emit(event, value): coroutine that pushes to every front end."""
        self._emit = emit
        self._process = None
        self._reader = None
        self._write_lock = asyncio.Lock()
        self._stopping = False
        self._ready = False
        self._frontend_id = None
        self._steam_context_revision = -1
        self._navigation_active = False
        self._navigation_revision = -1
        self._started = asyncio.Event()
        self._status = {"mode": "Unknown", "phase": "正在启动", "enabled": False}
        self._settings = {"enabled": False, "msiAction": "QuickAccess", "qamAction": "MainMenu", "hapticsEnabled": True}
        self._haptics = _haptic_module.ClawNavigationHaptics()
        self._haptic_status = {"phase": "等待导航", "lastPath": None}
        self._data = Path(data)
        self._data.mkdir(parents=True, exist_ok=True)
        self._magpie = _magpie_module.Magpie(PLUGIN_ROOT, self._data, self._emit_magpie)
        self._overlay_frames = _frames_module.OverlayFrames()
        self._power = _power_module.PowerControl(PLUGIN_ROOT, self._data, self._emit_power)
        self._rumble = _rumble_module.RumbleControl(self._data, log=logging.info)
        self._gamepad = _gamepad_fw_module.GamepadControl(
            self._data, _gamepad_fw_module.GamepadFirmware(rumble=_rumble_module), log=logging.info)
        # The agent's own Python keeps seeing the real pad (navigation haptics match it by vendor / product id).
        self._input = _input_module.InputLayer(PLUGIN_ROOT, self._data, self._emit, gamepad=self._gamepad,
                                               not_games=_power_module.NOT_GAMES, python=sys.executable,
                                               log=logging.info)
        self._input_task = None
        # Pad mode switches (set_gamepad_mode, or the firmware switching by itself if it can): notice first, then the firmware listener.
        self._gamepad.watcher.listeners.insert(0, self._pad_mode_changed)
        self._settings_path = self._data / "settings.json"
        self._task = None
        self._recent = []
        self._maintenance_waiters = {}
        self._compatibility = _compatibility_module.Compatibility(PLUGIN_ROOT, self._data, self._maintenance)
        self._compatibility_task = None
        self._compatibility_mode = None

    async def start(self):
        try:
            self._settings = validate_settings(json.loads(self._settings_path.read_text(encoding="utf-8")))
        except FileNotFoundError:
            pass
        except (ValueError, OSError):
            self._status["error"] = "已有设置无法读取，已暂停"
        if self._settings.get("hapticsEnabled", True):
            logging.info("Claw residual feedback recovery: %s", self._haptics.recover())
        config = Path(os.environ["APPDATA"]) / "GamingMode"
        version = json.loads((PLUGIN_ROOT / "plugin.json").read_text(encoding="utf-8"))["version"]
        self._process = await asyncio.create_subprocess_exec(
            str(PLUGIN_ROOT / "bin" / ("ClawRouter-" + version + ".exe")), str(self._data), str(config),
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self._started.set()
        self._reader = asyncio.create_task(self._read_output())
        self._task = asyncio.create_task(self._read_errors())
        self._schedule_compatibility()
        self._power.start()
        self._rumble.start()
        self._gamepad.start()
        self._input_task = asyncio.create_task(self._start_input())

    def _schedule_compatibility(self):
        if self._stopping or (self._compatibility_task and not self._compatibility_task.done()):
            return
        self._compatibility_task = asyncio.create_task(self._compatibility.check())

    async def _maintenance(self, begin, token):
        import uuid
        token = uuid.uuid4().hex if begin else token
        future = asyncio.get_running_loop().create_future()
        self._maintenance_waiters[token] = future
        try:
            await self._send({"command": "begin_maintenance" if begin else "end_maintenance", "id": token})
            result = await asyncio.wait_for(future, 22)
            if not result.get("ok"):
                raise RuntimeError(result.get("error") or "ClawTweaks 维护交接未完成")
            return token
        finally:
            self._maintenance_waiters.pop(token, None)

    async def _send(self, command):
        if not self._started.is_set():
            await asyncio.wait_for(self._started.wait(), 6)
        async with self._write_lock:
            if self._process is None or self._process.returncode is not None:
                raise RuntimeError("按键后台未运行")
            self._process.stdin.write((json.dumps(command, ensure_ascii=False) + "\n").encode("utf-8"))
            await self._process.stdin.drain()

    async def _configure(self, reset_steam_context=False):
        await self._send({"command": "configure", "ready": self._ready, "resetSteamContext": reset_steam_context, **self._settings})

    async def _read_output(self):
        try:
            while True:
                line = await self._process.stdout.readline()
                if not line:
                    break
                try:
                    event = json.loads(line.decode("utf-8-sig"))
                except (ValueError, UnicodeError):
                    continue
                kind = event.get("type")
                if kind == "maintenance":
                    waiter = getattr(self, "_maintenance_waiters", {}).get(event.get("id"))
                    if waiter is not None and not waiter.done():
                        waiter.set_result(event)
                    continue
                if kind == "status":
                    self._status = event
                    if event.get("error"):
                        logging.error("Native status: %s", event["error"])
                    # A native failure pauses the persisted setting too, preventing repeated stop attempts.
                    if (event.get("configured") is True and event.get("enabled") is False
                            and self._settings["enabled"] and self._ready and not self._stopping):
                        self._settings["enabled"] = False
                        self._save()
                    if hasattr(self, "_compatibility") and event.get("mode") != self._compatibility_mode:
                        self._compatibility_mode = event.get("mode")
                        self._schedule_compatibility()
                elif kind == "button":
                    self._recent = (self._recent + [event])[-8:]
                await self._emit("router_event", event)
            if not self._stopping:
                self._status = {"mode": "Unknown", "phase": "按键后台已退出", "enabled": False,
                                "error": "重启工具箱后台可以读取恢复记录并恢复 ClawTweaks"}
                await self._emit("router_event", {"type": "status", **self._status})
        except asyncio.CancelledError:
            raise
        except Exception:
            logging.exception("Claw router bridge failed")
            self._ready = False
            try:
                await self._configure()
            except Exception:
                pass

    async def _read_errors(self):
        while self._process:
            line = await self._process.stderr.readline()
            if not line:
                return
            logging.error("Claw router: %s", line.decode("utf-8", errors="replace").strip())

    def _save(self):
        temp = self._settings_path.with_suffix(".tmp")
        temp.write_text(json.dumps(self._settings, ensure_ascii=False), encoding="utf-8")
        os.replace(temp, self._settings_path)

    async def get_state(self):
        return {"settings": self._settings, "status": self._status, "recent": self._recent,
                "haptics": self._haptic_state(), "compatibility": self._compatibility.view()}

    async def manage_compatibility(self, action, feature=None, enabled=None):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._compatibility.check(action, feature, enabled)

    async def _emit_magpie(self, value):
        await self._emit("magpie_status", value)

    async def _emit_power(self, value):
        await self._emit("power_status", value)

    async def get_power_state(self, sample=False):
        return await self._power.get(sample)

    async def save_power(self, settings):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._power.save(settings)

    async def set_ecore(self, enabled):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._power.set_ecore(enabled)

    # ---- Game Bar widget ----------------------------------------------------------
    async def widget_state(self):
        """Everything the widget's single page shows, in one call. Answered from what the agent
        already keeps up to date, so a press in the widget never waits on hardware reads."""
        return {"power": self._power.view(), "system": await self._power.system_cached(),
                "rumble": self._rumble.view(), "magpie": self._magpie.view()}

    async def set_brightness(self, value):
        return await self._power.system_set("brightness_set", value=self._number(value, 0, 100))

    async def set_volume(self, value):
        return await self._power.system_set("volume_set", value=self._number(value, 0, 100))

    async def set_mute(self, muted):
        if type(muted) is not bool:
            raise ValueError("静音开关格式错误")
        return await self._power.system_set("mute_set", muted=muted)

    async def set_refresh_rate(self, hz):
        return await self._power.system_set("refresh_set", hz=self._number(hz, 24, 500))

    async def set_fps_limit(self, fps):
        game = (self._power.sys_cache or {}).get("game") or {}
        return await self._power.system_set("fps_limit_set", fps=self._number(fps, 0, 360), exe=game.get("exe") or "")

    async def close_game(self, pid):
        return await self._power.close_game(pid)

    async def power_action(self, action):
        if action not in ("sleep", "shutdown", "restart", "lock"):
            raise ValueError("不支持的电源操作")
        return await self._power.system_set("power_action", action=action)

    async def toggle_keyboard(self):
        logging.info("Touch keyboard toggled from the Game Bar widget")
        return await self._power.system_set("keyboard")

    async def screenshot(self):
        return await self._power.system_set("screenshot")

    @staticmethod
    def _number(value, low, high):
        if type(value) not in (int, float) or not low <= value <= high:
            raise ValueError("数值超出范围")
        return int(round(value))

    async def restore_power(self):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._power.restore()

    async def get_rumble_state(self):
        return await self._rumble.refresh()

    async def set_rumble_strength(self, strength, right=None):
        # set_rumble_strength(both) or set_rumble_strength(left, right).
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._rumble.set_strength(strength, right)

    async def restore_rumble(self):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._rumble.restore()

    async def get_gamepad_state(self):
        # Pad mode, button mapping, M1/M2, dead zones, triggers, lights as the firmware has them now.
        return await self._gamepad.read()

    async def _gamepad_write(self, call, *args):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        await call(*args)
        return await self._gamepad.read()

    async def set_gamepad_mode(self, mode, notify=True):
        # notify=False: a temporary switch (the game library in front), no mode notice.
        return await self._gamepad_write(self._gamepad.set_mode, mode, notify)

    async def set_stick_deadzone(self, side, deadzone, outer):
        return await self._gamepad_write(self._gamepad.set_stick_deadzone, side, deadzone, outer)

    async def set_desktop_stick_deadzone(self, side, deadzone, outer):
        return await self._gamepad_write(self._gamepad.set_desktop_stick_deadzone, side, deadzone, outer)

    async def set_stick_swap(self, enabled):
        return await self._gamepad_write(self._gamepad.set_stick_swap, enabled)

    async def set_trigger_range(self, side, start, end):
        return await self._gamepad_write(self._gamepad.set_trigger_range, side, start, end)

    async def set_quick_trigger(self, side, enabled):
        return await self._gamepad_write(self._gamepad.set_quick_trigger, side, enabled)

    async def set_back_key(self, key, codes):
        return await self._gamepad_write(self._gamepad.set_back_key, key, codes)

    async def set_key_map(self, source, codes):
        return await self._gamepad_write(self._gamepad.set_key_map, source, codes)

    async def set_desktop_key_map(self, source, codes):
        return await self._gamepad_write(self._gamepad.set_desktop_key_map, source, codes)

    async def set_light(self, effect, color=None, brightness=100, speed=1, direction="cw", frames=None):
        return await self._gamepad_write(self._gamepad.set_light, effect, color, brightness, speed, direction, frames)

    async def restore_gamepad(self, what="all"):
        return await self._gamepad_write(self._gamepad.restore, what)

    # ---- software input layer (docs/gamepad-input-layer.md) ----

    async def _start_input(self):
        mode = await self._gamepad.watcher.poll()  # the mode before the layer decides anything
        self._gamepad.watcher.start()
        await self._input.start(pad_mode=mode)

    async def _pad_mode_changed(self, mode, previous, notify=True):
        if notify:
            await self._input.show_mode_osd(mode)
        await self._emit("gamepad_mode", {"mode": mode, "previous": previous, "notify": notify,
                                          "at": self._gamepad.watcher.changed_at})
        # Desktop mode pauses the layer (nothing hidden, no virtual pad), gamepad mode resumes it.
        asyncio.create_task(self._input.set_pad_mode(mode))

    async def get_input_layer_state(self):
        return await self._input.state()

    async def set_input_layer_enabled(self, enabled):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._input.set_enabled(enabled)

    async def get_input_layout(self, game=None):
        return await self._input.get_layout(game)

    async def save_input_layout(self, layout, game=None):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._input.save_layout(layout, game)

    async def get_input_layer_catalog(self):
        return _input_module.catalog()

    async def test_input_layer(self, steps, layout_for=None):
        return await self._input.test(steps, layout_for)

    async def test_rumble(self):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await asyncio.get_running_loop().run_in_executor(None, self._haptics.test_pulse)

    async def get_magpie_state(self):
        return await self._magpie.get()

    async def get_magpie_targets(self):
        return await self._magpie.targets()

    async def get_magpie_catalog(self):
        return await self._magpie.catalog()

    async def apply_magpie(self, settings):
        return await self._magpie.apply(settings)

    async def stop_magpie(self):
        return await self._magpie.stop()

    async def set_magpie_plain(self, rects):
        return await self._magpie.set_plain(rects)

    async def toggle_magpie(self):
        return await self._magpie.toggle()

    async def magpie_auto_start(self):
        return await self._magpie.auto_start()

    async def set_magpie_pause(self, source, visible):
        if source == "overlay" and type(visible) is bool and not self._stopping:
            try:
                self._overlay_frames.set_visible(visible)
            except Exception:
                logging.exception("Overlay frame pump could not change state")
        return await self._magpie.pause(source, visible)

    async def get_game_picker_path(self, drive=None, preferred_path=None):
        if drive is not None:
            if (not isinstance(drive, str) or len(drive) != 2 or
                    not "A" <= drive[0].upper() <= "Z" or drive[1] != ":"):
                raise ValueError("请选择有效的浏览磁盘")

            def start_path():
                root = Path(drive + "/")
                if not root.is_dir():
                    raise ValueError("所选磁盘暂不可用，请确认 SD 卡已插入并刷新磁盘列表")
                if (isinstance(preferred_path, str) and len(preferred_path) <= 32768 and
                        not any(ord(char) < 32 for char in preferred_path) and
                        ntpath.isabs(preferred_path) and ntpath.splitdrive(preferred_path)[0].upper() == drive.upper()):
                    try:
                        if Path(preferred_path).is_dir():
                            return Path(preferred_path).as_posix()
                    except OSError:
                        pass
                return drive.upper() + ":/."

            # A different card can reuse a drive letter; fall back if its old folder is gone.
            return await asyncio.to_thread(start_path)
        for candidate in (Path("C:/Game"), Path("C:/Games"), Path.home()):
            if candidate.is_dir():
                return candidate.as_posix()
        return Path.home().as_posix()

    async def get_game_picker_locations(self):
        # Only enumerate Windows drive letters; do not scan folders or launch files.
        def read_drives():
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.GetLogicalDrives.argtypes = []
            kernel.GetLogicalDrives.restype = wintypes.DWORD
            kernel.GetDriveTypeW.argtypes = [wintypes.LPCWSTR]
            kernel.GetDriveTypeW.restype = wintypes.UINT
            mask = kernel.GetLogicalDrives()
            if not mask:
                raise RuntimeError("无法读取磁盘列表，请稍后刷新")
            kinds = {2: "可移动磁盘", 3: "本地磁盘", 4: "网络磁盘", 6: "内存磁盘"}
            drives = []
            for index in range(26):
                if not mask & (1 << index):
                    continue
                letter = chr(ord("A") + index)
                kind = kernel.GetDriveTypeW(letter + ":\\")
                if kind in kinds:
                    # Decky strips a trailing slash. '/.' keeps a drive root absolute.
                    drives.append({"id": letter + ":", "label": f"{letter} 盘（{kinds[kind]}）",
                                   "path": letter + ":/."})
            if not drives:
                raise RuntimeError("没有可浏览的磁盘，请插入 SD 卡后刷新")
            return drives

        drives = await asyncio.to_thread(read_drives)
        default_path = await self.get_game_picker_path()
        default_drive = ntpath.splitdrive(default_path)[0].upper()
        if not any(item["id"] == default_drive for item in drives):
            default_drive, default_path = drives[0]["id"], drives[0]["path"]
        return {"drives": drives, "defaultDrive": default_drive, "defaultPath": default_path}

    async def inspect_game_file(self, path):
        if (not isinstance(path, str) or not 1 <= len(path) <= 32768 or
                any(ord(char) < 32 for char in path) or not ntpath.isabs(path)):
            raise ValueError("请选择有效的游戏启动文件")
        try:
            target = Path(path).resolve(strict=True)
            if not target.is_file() or target.suffix.lower() not in (".exe", ".lnk"):
                raise ValueError()
        except (OSError, ValueError, RuntimeError):
            raise ValueError("启动文件不存在，或不是 .exe／.lnk 文件，请重新选择") from None
        return {"path": str(target), "name": target.stem, "directory": str(target.parent),
                "extension": target.suffix.lower()}

    def _haptic_state(self):
        enabled = self._settings.get("hapticsEnabled", True)
        return {**self._haptic_status, "enabled": enabled,
                "navigationActive": self._navigation_active,
                "phase": (self._haptic_status["phase"] if self._navigation_active else "等待 Steam 导航")
                if enabled else "已关闭"}

    async def play_navigation_haptic(self, request):
        try:
            if not isinstance(request, dict):
                raise ValueError("反馈请求格式错误")
            if request.get("stop") is not True:
                if self._stopping or not self._ready or not self._settings.get("hapticsEnabled", True):
                    return {"handled": True, "path": "disabled"}
                if (request.get("frontendId") != self._frontend_id or not self._navigation_active
                        or request.get("revision") != self._navigation_revision):
                    return {"handled": True, "path": "navigation-inactive"}
            deadline = request.get("expiresAtMs")
            if request.get("stop") is not True:
                now = time.time() * 1000
                if type(deadline) is not int or deadline < now or deadline > now + 250:
                    return {"handled": True, "path": "expired"}
            result = self._haptics.request(navigation_request(request))
        except ValueError:
            return {"handled": False, "path": "invalid-request"}
        path = result.get("path")
        phase = {"claw-xinput": "正常", "steam-ui-only": "等待 Steam 导航",
                 "ambiguous-or-missing-claw": "未识别内置手柄",
                 "xinput-error": "手柄输出暂不可用", "xinput-unavailable": "手柄输出暂不可用"}.get(path)
        if phase and phase != self._haptic_status["phase"]:
            self._haptic_status = {"phase": phase, "lastPath": path}
            await self._emit("haptic_status", self._haptic_state())
        return result

    async def set_navigation_context(self, frontend_id, revision, active):
        if type(revision) is not int or revision < 0 or type(active) is not bool:
            raise ValueError("导航状态格式错误")
        if frontend_id != self._frontend_id or not self._ready or self._stopping:
            return {"handled": False, "path": "stale-frontend"}
        if revision <= self._navigation_revision:
            return {"handled": True, "path": "stale-context"}
        self._navigation_revision = revision
        self._navigation_active = active and self._settings.get("hapticsEnabled", True)
        recovered = False
        if not self._navigation_active:
            self._haptics.stop()
            if self._settings.get("hapticsEnabled", True):
                recovered = self._haptics.recover()
        logging.info("Claw navigation context: active=%s revision=%s recovered=%s",
                     self._navigation_active, revision, recovered)
        return {"handled": True, "path": "navigation-active" if self._navigation_active else "navigation-stopped",
                "recovered": recovered}

    async def refresh(self):
        await self._send({"command": "status"})
        return await self.get_state()

    async def frontend_ready(self, ready, frontend_id=None):
        if type(ready) is not bool:
            raise ValueError("状态格式错误")
        if not isinstance(frontend_id, str) or not 1 <= len(frontend_id) <= 128:
            raise ValueError("前端标识格式错误")
        if not ready and self._frontend_id != frontend_id:
            return await self.get_state()
        if ready:
            self._frontend_id = frontend_id
            self._steam_context_revision = -1
            self._navigation_active = False
            self._navigation_revision = -1
            self._haptics.stop()
            if self._settings.get("hapticsEnabled", True):
                self._haptics.recover()
        self._ready = ready
        if not ready:
            self._navigation_active = False
            self._haptics.stop()
        await self._configure(reset_steam_context=True)
        return await self.get_state()

    async def frontend_gone(self, frontend_ids):
        """A front end's connection closed (Steam exited, Decky reloaded or crashed) without
        reporting itself unready. Release the buttons it held and stop Steam overlay pumping."""
        if self._frontend_id is None or self._frontend_id not in frontend_ids:
            return
        try:
            await self.frontend_ready(False, self._frontend_id)
        except Exception:
            logging.exception("Releasing a closed front end failed")
        try:
            await self.set_magpie_pause("overlay", False)
        except Exception:
            logging.exception("Resuming Magpie after a closed front end failed")

    async def set_steam_context(self, frontend_id, revision, active):
        if type(revision) is not int or not 0 <= revision <= 9007199254740991 or type(active) is not bool:
            raise ValueError("Steam 状态格式错误")
        if frontend_id != self._frontend_id or not self._ready or self._stopping:
            return {"handled": False, "path": "stale-frontend"}
        if revision <= self._steam_context_revision:
            return {"handled": False, "path": "stale-context"}
        self._steam_context_revision = revision
        await self._send({"command": "steam_context", "active": active})
        return {"handled": True, "path": "big-picture" if active else "steam-closed"}

    async def save_settings(self, settings):
        next_settings = validate_settings(settings)
        if next_settings == self._settings:
            return await self.get_state()
        self._settings = next_settings
        if not self._settings["hapticsEnabled"]:
            self._navigation_active = False
            self._haptics.stop()
        self._save()
        await self._configure()
        return await self.get_state()

    async def action_result(self, result):
        if (not isinstance(result, dict) or type(result.get("ok")) is not bool
                or not isinstance(result.get("id"), str) or len(result["id"]) != 32):
            raise ValueError("结果格式错误")
        path = result.get("path")
        reason = result.get("reason")
        raw_timings = result.get("timings")
        timings = {key: value for key, value in raw_timings.items()
                   if key in ("nativeMs", "bridgeMs", "menuMs") and type(value) in (int, float)
                   and math.isfinite(value) and 0 <= value <= 10000} if isinstance(raw_timings, dict) else {}
        await self._send({"command": "result", "id": result["id"], "ok": result["ok"],
                          "path": path if path in ("home", "overlay", "playhub-window") else None,
                          "reason": reason[:256] if isinstance(reason, str) else None, "timings": timings})

    async def close(self):
        self._stopping = True
        self._overlay_frames.close()
        await self._power.close()
        await self._rumble.close()
        if self._input_task and not self._input_task.done():
            await self._input_task
        await self._input.close()  # unhides the pad and puts M1 / M2 back while the firmware link is still open
        await self._gamepad.close()
        await self._magpie.close()
        if self._compatibility_task:
            await self._compatibility_task
        await self._compatibility.close()
        self._haptics.close()
        if self._process and self._process.returncode is None:
            try:
                await self._send({"command": "stop"})
                # Closing stdin also triggers native cleanup if Application.Exit races startup.
                self._process.stdin.close()
                await asyncio.wait_for(self._process.wait(), 15)
            except asyncio.TimeoutError:
                # Never kill a helper in the middle of restoring the original owner.
                logging.error("Native cleanup is still running; restore journal retained")
        for task in (self._reader, self._task):
            if task and not task.done():
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass
