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
import uuid

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
_settings_page_spec = importlib.util.spec_from_file_location("claw_toolbox_settings", PLUGIN_ROOT / "claw_settings.py")
_settings_page_module = importlib.util.module_from_spec(_settings_page_spec)
_settings_page_spec.loader.exec_module(_settings_page_module)
_system_spec = importlib.util.spec_from_file_location("claw_toolbox_system", PLUGIN_ROOT / "claw_system.py")
_system_module = importlib.util.module_from_spec(_system_spec)
_system_spec.loader.exec_module(_system_module)
_freeze_spec = importlib.util.spec_from_file_location("claw_toolbox_game_freeze", PLUGIN_ROOT / "claw_game_freeze.py")
_freeze_module = importlib.util.module_from_spec(_freeze_spec)
_freeze_spec.loader.exec_module(_freeze_module)
_qam_scaling_spec = importlib.util.spec_from_file_location("claw_toolbox_qam_scaling", PLUGIN_ROOT / "claw_qam_scaling.py")
_qam_scaling_module = importlib.util.module_from_spec(_qam_scaling_spec)
_qam_scaling_spec.loader.exec_module(_qam_scaling_module)
_rumble_spec = importlib.util.spec_from_file_location("claw_toolbox_rumble", PLUGIN_ROOT / "claw_rumble.py")
_rumble_module = importlib.util.module_from_spec(_rumble_spec)
_rumble_spec.loader.exec_module(_rumble_module)
_gamepad_fw_spec = importlib.util.spec_from_file_location("claw_toolbox_gamepad_fw", PLUGIN_ROOT / "claw_gamepad_fw.py")
_gamepad_fw_module = importlib.util.module_from_spec(_gamepad_fw_spec)
_gamepad_fw_spec.loader.exec_module(_gamepad_fw_module)
_input_spec = importlib.util.spec_from_file_location("claw_toolbox_input_layer", PLUGIN_ROOT / "claw_input_layer.py")
_input_module = importlib.util.module_from_spec(_input_spec)
_input_spec.loader.exec_module(_input_module)
_buttons_spec = importlib.util.spec_from_file_location("claw_toolbox_button_mappings", PLUGIN_ROOT / "claw_button_mappings.py")
_buttons_module = importlib.util.module_from_spec(_buttons_spec)
_buttons_spec.loader.exec_module(_buttons_module)
_captures_spec = importlib.util.spec_from_file_location("claw_toolbox_captures", PLUGIN_ROOT / "claw_captures.py")
_captures_module = importlib.util.module_from_spec(_captures_spec)
_captures_spec.loader.exec_module(_captures_module)
_local_games_spec = importlib.util.spec_from_file_location("claw_toolbox_local_games", PLUGIN_ROOT / "claw_local_games.py")
_local_games_module = importlib.util.module_from_spec(_local_games_spec)
_local_games_spec.loader.exec_module(_local_games_module)
_game_settings_spec = importlib.util.spec_from_file_location("claw_toolbox_game_settings", PLUGIN_ROOT / "claw_game_settings.py")
_game_settings_module = importlib.util.module_from_spec(_game_settings_spec)
_game_settings_spec.loader.exec_module(_game_settings_module)
_lossless_spec = importlib.util.spec_from_file_location("claw_toolbox_lossless", PLUGIN_ROOT / "claw_lossless.py")
_lossless_module = importlib.util.module_from_spec(_lossless_spec)
_lossless_spec.loader.exec_module(_lossless_module)

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
            "hapticsEnabled": value.get("hapticsEnabled", True),
            "buttonMappings": _buttons_module.validate_maps(value.get("buttonMappings", {}), _input_module.validate_command, general=True),
            "steamBindings": _buttons_module.validate_maps(value.get("steamBindings", {}), _input_module.validate_command)}


def _steam_game_running():
    """A Steam game is running (Steam's own RunningAppID), so Steam must not be restarted now."""
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            return int(winreg.QueryValueEx(key, "RunningAppID")[0]) != 0
    except (OSError, ValueError, TypeError):
        return False


async def start_router(executable, data, config):
    """Allow an exiting owner to release its mutex; never accept an exited child."""
    for attempt in range(12):
        process = await asyncio.create_subprocess_exec(
            str(executable), str(data), str(config), "--owner-pid=" + str(os.getpid()),
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            await asyncio.wait_for(asyncio.shield(process.wait()), .15)
        except asyncio.TimeoutError:
            return process
        detail = (await process.stdout.read()).decode("utf-8", errors="replace").strip()
        if process.returncode == 2 and attempt < 11:
            logging.info("Previous button listener is still exiting; retrying startup")
            await asyncio.sleep(.25)
            continue
        raise RuntimeError("按键监听启动失败（退出码 %s）：%s" % (process.returncode, detail[:512]))


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
        self._lossless = _game_settings_module.LosslessConfig(self._data)
        self._lossless_editor = _lossless_module.SettingsEditor(self._data, _game_settings_module.LS_SETTINGS)
        self._lossless_lock = asyncio.Lock()
        self._overlay_frames = _frames_module.OverlayFrames()
        self._power = _power_module.PowerControl(PLUGIN_ROOT, self._data, self._emit_power)
        self._freeze = _freeze_module.GameFreeze(PLUGIN_ROOT, self._data)
        self._qam_scaling = _qam_scaling_module.Scaling(self._magpie, self._lossless, self._lossless_editor,
            self._power._call, _game_settings_module.lossless_start, _lossless_module.parse_hotkey, _input_module.compile_command)
        self._system = _system_module.SystemControl(
            self._power._call, self._data, PLUGIN_ROOT,
            lambda: (_power_module.battery_percent(), _power_module.on_charger()))
        self._settings_page = _settings_page_module.SettingsControl(self._power._call)
        self._rumble = _rumble_module.RumbleControl(self._data, log=logging.info)
        self._gamepad = _gamepad_fw_module.GamepadControl(
            self._data, _gamepad_fw_module.GamepadFirmware(rumble=_rumble_module), log=logging.info)
        # The agent's own Python keeps seeing the real pad (navigation haptics match it by vendor / product id).
        self._input = _input_module.InputLayer(PLUGIN_ROOT, self._data, self._input_emit, gamepad=self._gamepad,
                                               not_games=_power_module.NOT_GAMES, python=sys.executable,
                                               log=logging.info)
        self._input_task = None
        # Game Bar screenshots: which window each new capture came from (data\captures.jsonl, read by ClawLibrary).
        self._captures = _captures_module.CaptureLog(self._data, log=logging.info)
        # Steam cloud screenshots (Big Picture → 媒体): listed by the Steam front end, copied to data\steam-cloud.
        self._steam_cloud = _captures_module.SteamCloud(self._data, log=logging.info)
        self._cloud_waiters = {}
        # ClawLibrary 添加游戏: local games, their SteamGridDB art, and writing them into Steam (data\\local-games).
        self._local_games = _local_games_module.LocalGames(self._data, log=logging.info, game_running=_steam_game_running)
        self._local_games_task = None
        self._local_games_error = ""
        # Pad mode switches (set_gamepad_mode, or the firmware switching by itself if it can): notice first, then the firmware listener.
        self._gamepad.watcher.listeners.insert(0, self._pad_mode_changed)
        self._settings_path = self._data / "settings.json"
        self._task = None
        self._recent = []
        self._maintenance_waiters = {}
        self._mapping_tasks = set()
        self._mapping_menu_waiters = {}
        self._permission_waiters = {}
        self._mapping_lock = asyncio.Lock()
        self._last_mapping = None
        self._toolbox_last_press = None
        self._compatibility = _compatibility_module.Compatibility(PLUGIN_ROOT, self._data, self._maintenance)
        self._compatibility_task = None
        self._compatibility.game_running = _steam_game_running
        self._library_haptic_id = 0
        self._compatibility_mode = None

    async def start(self):
        await self._freeze.start()
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
        self._process = await start_router(
            str(PLUGIN_ROOT / "bin" / ("ClawRouter-" + version + ".exe")), str(self._data), str(config),
        )
        self._started.set()
        self._reader = asyncio.create_task(self._read_output())
        self._task = asyncio.create_task(self._read_errors())
        self._schedule_compatibility()
        self._power.start()
        self._rumble.start()
        self._gamepad.start()
        self._captures.start()
        self._input_task = asyncio.create_task(self._start_input())
        await self._configure()  # general mappings also work before any Steam frontend connects

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

    async def _input_emit(self, kind, value):
        if kind == "input_system_action":
            self._schedule_mapping({"scope": "input", "command": {"type": "system", "action": value.get("action")}})
        else:
            await self._emit(kind, value)

    def _schedule_mapping(self, event):
        if self._stopping or len(self._mapping_tasks) >= 4:
            return
        if (event.get("source") == "Claw" and event.get("scope") in ("general", "steam")
                and (event.get("command") or {}).get("action") in ("library", "controlCenter")):
            now = time.monotonic()
            if self._toolbox_last_press is not None and now - self._toolbox_last_press < .35:
                return
            self._toolbox_last_press = now
        task = asyncio.create_task(self._execute_mapping(event))
        self._mapping_tasks.add(task)
        task.add_done_callback(self._mapping_tasks.discard)

    async def _allow_gamebar(self):
        ident = uuid.uuid4().hex
        future = asyncio.get_running_loop().create_future()
        self._permission_waiters[ident] = future
        try:
            await self._send({"command": "allow_gamebar", "id": ident})
            if not await asyncio.wait_for(future, 2):
                raise ValueError("游戏模式使用快捷栏")
        finally:
            self._permission_waiters.pop(ident, None)

    async def _execute_system_mapping(self, action, event=None):
        if action == "gamebar" and _buttons_module.desktop_quick_settings_hold(event):
            context = await asyncio.to_thread(_buttons_module.toolbox_context)
            if context.get("xboxFse") is True:
                command = {"type": "keyboard", "keys": ["LWin", "Tab"]}
                await self._power._call("mapped_input", mapping=_input_module.compile_command(command))
                return "fse-task-view"
        if action in ("library", "controlCenter"):
            mode = "BigPicture" if self._status.get("bigPictureOpen") else self._status.get("mode")
            context = await asyncio.to_thread(_buttons_module.toolbox_context)
            route = _buttons_module.toolbox_route(action, event or {}, mode, context)
            await asyncio.to_thread(_buttons_module.activate_toolbox, route, context)
            return route
        elif action in ("steamMainMenu", "steamQuickAccess"):
            if not self._ready or self._status.get("mode") != "BigPicture":
                await asyncio.to_thread(os.startfile, "steam://open/gamepadui")
                deadline = time.monotonic() + 10
                while not self._stopping and time.monotonic() < deadline:
                    if self._ready and self._status.get("mode") == "BigPicture":
                        break
                    await asyncio.sleep(.1)
            if not self._ready:
                raise RuntimeError("Steam 按键前端尚未连接，请启动 Steam 和 Decky")
            ident = uuid.uuid4().hex
            future = asyncio.get_running_loop().create_future()
            self._mapping_menu_waiters[ident] = future
            try:
                await self._emit("router_event", {"type": "action", "id": ident,
                    "action": "MainMenu" if action == "steamMainMenu" else "QuickAccess",
                    "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
                result = await asyncio.wait_for(future, 6)
                if not result["ok"]:
                    raise RuntimeError(result.get("reason") or "Steam 菜单没有响应")
            finally:
                self._mapping_menu_waiters.pop(ident, None)
        elif action == "steamLibrary":
            await asyncio.to_thread(os.startfile, "steam://open/games")
        elif action == "magpieToggle":
            await self._magpie.toggle()
        elif action == "keyboard":
            await self._power.system_set("keyboard")
        else:
            if action in ("gamebar", "screenshot"):
                await self._allow_gamebar()
            if action == "screenshot":
                await asyncio.to_thread(self._captures.note_request)
            command = {"type": "keyboard", "keys": ["LWin", "G"]} if action == "gamebar" else {"type": "system", "action": action}
            await self._power._call("mapped_input", mapping=_input_module.compile_command(command))

    async def _execute_mapping(self, event):
        async with self._mapping_lock:
            result = {"source": event.get("source"), "scope": event.get("scope"), "ok": False}
            try:
                command = _input_module.validate_command(event.get("command"))
                if command["type"] == "system":
                    route = await self._execute_system_mapping(command["action"], event)
                    if route is not None:
                        result["windowRoute"] = route
                elif command["type"] != "none":
                    if command["type"] == "gamepad":
                        raise ValueError("专用按键目前支持系统动作和键盘鼠标指令")
                    if command["type"] == "keyboard" and "G" in command["keys"] and any(k in command["keys"] for k in ("LWin", "RWin")):
                        await self._allow_gamebar()
                    await self._power._call("mapped_input", mapping=_input_module.compile_command(command))
                result["ok"] = True
            except Exception as error:
                result["error"] = str(error)
                logging.warning("Special button mapping: %s", error)
            self._last_mapping = result
            await self._emit("button_mapping_result", result)
            return result

    async def test_button_mapping(self, source, steam=False):
        if type(steam) is not bool or source not in _buttons_module.SOURCES:
            raise ValueError("专用按键测试参数无效")
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出")
        return await self._execute_mapping({"source": source, "scope": "steam" if steam else "general",
            "command": _buttons_module.selected(self._settings, source, steam)})

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
                if kind == "mapping_permission":
                    waiter = self._permission_waiters.get(event.get("id"))
                    if waiter is not None and not waiter.done():
                        waiter.set_result(event.get("ok", True))
                    continue
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
                elif kind == "mapped_action":
                    self._schedule_mapping(event)
                    continue
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
                "haptics": self._haptic_state(), "compatibility": self._compatibility.view(), "buttonMappingResult": self._last_mapping}

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

    async def set_screensaver_power(self, active):
        return await self._power.set_screensaver_power(active)

    async def save_power(self, settings):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._power.save(settings)

    async def fan_test(self, action, config=None):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._power.fan_test(action, config)

    async def set_ecore(self, enabled):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._power.set_ecore(enabled)

    async def restore_ecore(self):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._power.restore_ecore()

    # ---- Game Bar widget ----------------------------------------------------------
    async def widget_state(self):
        """Everything the widget's single page shows, in one call. Answered from what the agent
        already keeps up to date, so a press in the widget never waits on hardware reads."""
        system = await self._power.system_cached()
        return {"power": self._power.view(), "system": system, "gameFreeze": self._freeze_view(system.get("game")),
                "xboxFse": _buttons_module.xbox_fse_active(),
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

    # ---- 显示与声音 (ClawLibrary control centre) ---------------------------------------
    async def get_display_state(self):
        return await self._power.display()

    async def set_resolution(self, width, height):
        return await self._power.display("resolution_set", width=self._number(width, 640, 7680),
                                         height=self._number(height, 480, 4320))

    async def set_vrr(self, enabled):
        if type(enabled) is not bool:
            raise ValueError("可变刷新率开关格式错误")
        return await self._power.display("vrr_set", enabled=enabled)

    async def set_hdr(self, enabled):
        if type(enabled) is not bool:
            raise ValueError("HDR 开关格式错误")
        return await self._power.display("hdr_set", enabled=enabled)

    async def set_audio_output(self, output):
        if type(output) is not str or not output or len(output) > 400:
            raise ValueError("输出设备格式错误")
        return await self._power.display("audio_output_set", output=output)

    async def set_mic_mute(self, muted):
        if type(muted) is not bool:
            raise ValueError("麦克风开关格式错误")
        return await self._power.display("mic_mute_set", muted=muted)

    async def set_display_scale(self, percent):
        return await self._power.display("scale_set", percent=self._number(percent, 100, 500))

    async def set_auto_brightness(self, enabled):
        if type(enabled) is not bool:
            raise ValueError("自动亮度开关格式错误")
        return await self._power.display("auto_brightness_set", enabled=enabled)

    async def set_gpu_option(self, name, value):
        """Intel 显卡的全局 3D 设置：lowLatency = off / on / boost，frameSync = app / vsyncOff / vsync / smooth / speed / capped，xessFg = bool。"""
        allowed = {"lowLatency": ("off", "on", "boost"), "frameSync": ("app", "vsyncOff", "vsync", "smooth", "speed", "capped")}
        if name in allowed:
            if value not in allowed[name]:
                raise ValueError("显卡选项的值不对")
        elif name == "xessFg":
            if type(value) is not bool:
                raise ValueError("XeSS 插帧开关格式错误")
        else:
            raise ValueError("没有这个显卡选项")
        return await self._power.display("gpu_set", name=name, value=value)

    # ---- 系统 (ClawLibrary control centre) ------------------------------------------------
    @property
    def restart_requested(self):
        return self._system.restart_requested

    async def get_system_state(self):
        return await self._system.view(self._gamepad.read)

    async def _system_write(self, call, *args):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        await call(*args)
        return await self._system.view()

    async def set_power_button(self, action):
        return await self._system_write(self._system.set_power_button, action)

    async def set_sleep_animation(self, enabled):
        return await self._system_write(self._system.set_sleep_animation, enabled)

    async def set_wake_animation(self, enabled):
        return await self._system_write(self._system.set_wake_animation, enabled)

    async def set_enhanced_sleep(self, enabled):
        return await self._system_write(self._system.set_enhanced_sleep, enabled)

    async def set_sleep_games(self, enabled):
        return await self._system_write(self._system.set_sleep_games, enabled)

    async def set_sleep_fingerprint(self, enabled):
        return await self._system_write(self._system.set_sleep_fingerprint, enabled)

    async def set_hibernate_after(self, seconds):
        return await self._system_write(self._system.set_hibernate_after, seconds)

    async def set_display_off(self, seconds):
        return await self._system_write(self._system.set_display_off, seconds)

    # ---- 设置 page (ClawLibrary) --------------------------------------------------------
    async def get_settings_state(self):
        if self._system.lookup_task is None:  # widget version (and the Windows Update count) for 关于
            self._system.lookup_task = asyncio.create_task(self._system._lookups())
        return {**await self._settings_page.view(), "autostart": self._system.autostart if self._system.autostart is not None
                else await asyncio.to_thread(self._system._autostart), "haptics": self._settings.get("hapticsEnabled", True), "steamKeys": {k: self._settings.get(k) for k in ("enabled", "qamAction", "msiAction")},
                "compatibility": self._compatibility.view(), "backups": self._system._backups(), "widget": self._system.widget_version, "version": json.loads((PLUGIN_ROOT / "plugin.json").read_text(encoding="utf-8"))["version"]}

    async def set_home_app(self, aumid):
        return await asyncio.to_thread(_settings_page_module.set_home_app, aumid)

    async def set_startup_item(self, item, enabled):
        return await _settings_page_module.set_startup(self._power._call, item, enabled)

    async def set_vendor_app(self, app, disabled):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        logging.info("Vendor app %s %s from the library settings", app, "disabled" if disabled else "enabled")
        return await self._settings_page.set_vendor(app, disabled)

    async def set_haptics_enabled(self, enabled):
        if type(enabled) is not bool:
            raise ValueError("开关格式错误")
        return await self.save_settings({**self._settings, "hapticsEnabled": enabled})

    async def restart_agent(self):
        await asyncio.to_thread(self._system.restart)
        return {"restarting": True}

    async def install_windows_updates(self, ids):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._system.install_windows(ids)

    async def set_steam_keys(self, enabled, qam, msi):
        """Steam 大屏幕里快设键（qamAction）和 Claw 键（msiAction）接管成左右侧栏：MainMenu = 左侧主菜单，QuickAccess = 右侧快捷栏。
        改了哪个键，就去掉它在 steamBindings 里的自定义覆盖（和 Decky 插件面板的做法一样）。"""
        if type(enabled) is not bool or qam not in ALLOWED_ACTIONS or msi not in ALLOWED_ACTIONS:
            raise ValueError("按键设置格式错误")
        bindings = dict(self._settings.get("steamBindings") or {})
        if qam != self._settings.get("qamAction"):
            bindings.pop("QS", None)
        if msi != self._settings.get("msiAction"):
            bindings.pop("Claw", None)
        return await self.save_settings({**self._settings, "enabled": enabled, "qamAction": qam, "msiAction": msi, "steamBindings": bindings})

    async def library_haptic(self, pattern):
        """游戏库里的导航震动：和 Steam 大屏幕同一套震动样式，只是不要求 Steam 在前台（游戏在跑时照样不震）。"""
        if pattern not in HAPTIC_PATTERNS:
            raise ValueError("不认识的震动样式")
        if self._stopping:
            return {"handled": True, "path": "stopping"}
        duration, side, scale = HAPTIC_PATTERNS[pattern]
        self._library_haptic_id += 1
        vendor, product = _haptic_module._HARDWARE
        haptics = self._haptics
        gate = lambda: haptics._output is None or haptics._output.can_recover()
        request = {"requestId": 900_000_000 + self._library_haptic_id, "vendorId": vendor, "productId": product,
                   "durationMs": duration, "side": 2 if side is None else side, "magnitude": 0.75 * scale}
        return await asyncio.to_thread(self._haptics.request, request, gate=gate)

    async def check_driver_updates(self):
        return self._system.vendor.check()

    async def install_updates(self, ids, dry_run=False):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._system.vendor.install(ids, dry_run)

    async def set_charge_limit(self, percent):
        return await self._system_write(self._system.set_charge_limit, percent)

    async def set_wake_lock(self, locked):
        return await self._system_write(self._system.set_wake_lock, locked)

    async def set_autostart(self, enabled):
        return await self._system_write(self._system.set_autostart, enabled)

    async def check_updates(self):
        return self._system.check_updates()

    async def backup_settings(self):
        return await asyncio.to_thread(self._system.backup)

    async def restore_settings(self, name):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await asyncio.to_thread(self._system.restore, name)

    async def reset_settings(self):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await asyncio.to_thread(self._system.reset)

    async def export_logs(self):
        return await asyncio.to_thread(self._system.export_logs)

    async def set_fps_limit(self, fps):
        game = (self._power.sys_cache or {}).get("game") or {}
        return await self._power.system_set("fps_limit_set", fps=self._number(fps, 0, 360), exe=game.get("exe") or "")

    async def close_game(self, pid):
        return await self._power.close_game(pid)

    def _freeze_view(self, target):
        session = getattr(self._power, "sessions", None)
        if session is not None:
            game = session.current
            recognized = bool(target and game and target["identity"] == f"{game['pid']}:{game['created']}")
        else:
            recognized = bool(target and target["identity"] == getattr(self._power, "sleep_game_identity", None))
        return {**self._freeze.view(), "canFreeze": recognized}

    async def freeze_game(self, identity):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        target = self._power.close_target.sample()
        if not isinstance(identity, str) or not target or target.get("identity") != identity or not self._freeze_view(target)["canFreeze"]:
            raise RuntimeError("当前窗口不是已识别的游戏，或游戏已经切换")
        with self._power.close_target.hold(target["pid"]):
            return await self._freeze.freeze(target)

    async def resume_frozen_game(self):
        return await self._freeze.resume()

    async def show_desktop(self):
        await asyncio.to_thread(_system_module.show_desktop)
        return {"ok": True}

    async def open_task_manager(self):
        await asyncio.to_thread(os.startfile, str(Path(os.environ["WINDIR"]) / "System32" / "Taskmgr.exe"))
        return {"ok": True}

    async def open_gaming_home(self):
        await asyncio.to_thread(_settings_page_module.open_gaming_home)
        return {"ok": True}

    async def leave_xbox_fse(self):
        if _buttons_module.xbox_fse_active() is not True:
            return {"ok": True}
        command = {"type": "keyboard", "keys": ["LWin", "F11"]}
        await self._power._call("mapped_input", mapping=_input_module.compile_command(command))
        for _ in range(12):
            await asyncio.sleep(.2)
            if _buttons_module.xbox_fse_active() is False:
                return {"ok": True}
        raise RuntimeError("Windows 尚未退出全屏体验")

    async def qam_scaling_state(self, engine):
        session = getattr(self._power, "sessions", None)
        game = (getattr(session, "current", None) or (self._power.sys_cache or {}).get("game") or {})
        return await self._qam_scaling.view(engine, game.get("path", ""))

    async def qam_scaling_action(self, engine, action):
        if self._stopping:
            raise RuntimeError("工具箱后台正在退出，请稍候")
        return await self._qam_scaling.action(engine, action)

    async def power_action(self, action):
        if action not in ("sleep", "shutdown", "restart", "lock"):
            raise ValueError("不支持的电源操作")
        if action in ("shutdown", "restart"):
            animation = _system_module._animation
            state = await animation.view()
            if state.get("available") and state.get("suspendEnabled"):
                result = await animation.power_action(action)
                if result.get("handled"):
                    return result
        return await self._power.system_set("power_action", action=action)

    async def preview_power_animation(self):
        """Preview uses a fixed non-destructive pipe command, never the OS fallback."""
        return await _system_module._animation.power_action("restart", preview=True)

    async def toggle_keyboard(self):
        logging.info("Touch keyboard toggled from the Game Bar widget")
        return await self._power.system_set("keyboard")

    async def screenshot(self):
        await asyncio.to_thread(self._captures.note_request)
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

    # ---- software input layer (docs/input/gamepad-input-layer.md) ----

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

    async def steam_cloud_screenshots(self, request_id, items):
        """The Steam front end's list of uploaded screenshots (on its own schedule, or answering
        refresh_steam_cloud_screenshots). Downloads continue in the background."""
        if not isinstance(items, list) or len(items) > 20000:
            raise ValueError("截图列表格式错误")
        waiter = self._cloud_waiters.get(request_id) if isinstance(request_id, str) else None
        if waiter is not None:
            if not waiter.done():
                waiter.set_result(items)
            return {"handled": True}
        return await asyncio.to_thread(self._steam_cloud.update, items)

    async def refresh_steam_cloud_screenshots(self):
        """ClawLibrary's 截图 page opened: ask Steam for the current list and give the downloads a few
        seconds. Without a Steam front end the copies already here are all there is."""
        if self._ready:
            ident = uuid.uuid4().hex
            future = asyncio.get_running_loop().create_future()
            self._cloud_waiters[ident] = future
            try:
                await self._emit("router_event", {"type": "cloud_screenshots", "id": ident})
                items = await asyncio.wait_for(future, 5)
                return await asyncio.to_thread(self._steam_cloud.update, items, 3.5)
            except asyncio.TimeoutError:
                pass
            finally:
                self._cloud_waiters.pop(ident, None)
        return {"offline": True, **await asyncio.to_thread(self._steam_cloud.counts)}

    # ---------------- ClawLibrary 添加游戏 ----------------

    # ---------------- 游戏库：游戏设置页 ----------------

    def _game_item(self, item):
        if not isinstance(item, dict) or not str(item.get("id", "")).isdigit():
            raise ValueError("游戏格式错误")
        appid = int(item["id"])
        if not 0 < appid <= 0xFFFFFFFF:
            raise ValueError("游戏编号格式错误")
        return appid, str(item.get("platform") or ""), str(item.get("title") or "")[:200]

    def _game_exe(self, appid, platform, folder):
        """The game's exe: a non-Steam shortcut's own target; for a Steam game the likeliest exe in its folder."""
        gs, lg = _game_settings_module, _local_games_module
        if appid >= 0x80000000:
            user = lg.steam_user(lg.steam_root())
            if user:
                for _, _, entry in lg.shortcut_entries(lg.read_shortcuts(user)):
                    if (lg.Vdf.get(entry, "appid") or 0) & 0xFFFFFFFF == appid:
                        return lg.unquote(lg.Vdf.get(entry, "Exe") or "") or None
            for g in self._local_games._load():
                if g.get("id") == str(appid):
                    return g.get("exe")
            return None
        return gs.steam_game_exe(folder) if folder else None

    async def game_settings_get(self, item):
        appid, platform, title = self._game_item(item)
        gs, cdp = _game_settings_module, _local_games_module.cdp

        def read():
            props = None
            if cdp.available():
                try:
                    props = cdp.app_properties(appid)
                except cdp.CdpError as e:
                    logging.info("game settings: %s", e)
            folder = (props or {}).get("folder") or ""
            exe = self._game_exe(appid, platform, folder)
            # Magpie: a Steam game by its install folder (its exe may differ from our guess), others by exe
            magpie_key = ("dir:" + folder) if appid < 0x80000000 and folder else exe
            ls = self._lossless.view()
            return {"steam": props, "shortcut": appid >= 0x80000000, "exe": exe, "folder": folder,
                    "magpie": {"available": self._magpie.view().get("available", False), "key": magpie_key,
                               **(self._magpie.game_info(magpie_key) if magpie_key else
                                  {"enabled": False, "auto": True, "preset": "无", "presets": self._magpie.presets()})},
                    # 手柄档案: the input layer's per-game layout (by exe name; none = the global one)
                    "pad": {"exe": os.path.basename(exe) if exe else None,
                            "custom": bool(exe) and os.path.basename(exe).lower() in self._input.layouts.games()},
                    "lossless": {**ls, "installed": ls["configured"],
                                 "enabled": ls["configured"] and gs.lossless_enabled(exe) if exe else False}}
        return await asyncio.to_thread(read)

    async def game_settings_set(self, item, key, value=None):
        appid, platform, title = self._game_item(item)
        gs, cdp = _game_settings_module, _local_games_module.cdp
        if key in ("magpie", "magpieAuto", "magpiePreset"):
            target = item.get("magpieKey")
            if not isinstance(target, str) or not target:
                raise ValueError("找不到这款游戏的程序")
            if key == "magpie": return self._magpie.set_game(target, enabled=bool(value))
            if key == "magpieAuto": return self._magpie.set_game(target, auto=bool(value))
            if not isinstance(value, str): raise ValueError("超分预设格式错误")
            return self._magpie.set_game(target, preset=value)
        if key == "padCustom":
            exe = item.get("exe")
            if not isinstance(exe, str) or not exe.lower().endswith(".exe"):
                raise ValueError("找不到这款游戏的程序")
            game = os.path.basename(exe)
            if value:
                # Start from the global layout; the gamepad page then edits this game's copy
                current = await self._input.get_layout(None)
                return await self._input.save_layout(current["layout"], game)
            return await self._input.save_layout(None, game)
        if key == "lossless":
            async with self._lossless_lock:
                return await self._set_lossless_game(item, title, value)
        if key not in ("launchOptions", "overlay", "cloud", "favorite", "hidden", "verify"):
            raise ValueError("不认识的游戏设置")
        if key == "launchOptions" and (not isinstance(value, str) or len(value) > 2000):
            raise ValueError("启动选项太长")
        if not await asyncio.to_thread(cdp.available):
            raise RuntimeError("Steam 没开")
        await asyncio.to_thread(cdp.set_app_property, appid, key, value)
        return {"ok": True}

    async def _set_lossless_game(self, item, title, value):
        gs = _game_settings_module
        if type(value) is not bool:
            raise ValueError("小黄鸭开关格式错误")
        exe = item.get("exe")
        if not isinstance(exe, str) or not exe.lower().endswith(".exe"):
            raise ValueError("找不到这款游戏的程序")
        if not await asyncio.to_thread(self._lossless.exe):
            raise RuntimeError("请先在设置 › 小黄鸭中完成配置")
        editor = await asyncio.to_thread(self._lossless_editor.read)
        matching = [p for p in editor["profiles"] if p["path"] and gs._same_exe(p["path"], exe)]
        # A game's on/off switch must retain the scaling/FG settings edited on the full page.
        updates = [dict(index=p["index"], values={"AutoScale": value}) for p in matching]
        if not matching:
            if not value: return {"enabled": False}
            updates.append(dict(index=None, values={"Title": title, "Path": exe, "AutoScale": True}))
        result = await self._save_lossless_settings(dict(revision=editor["revision"], globalValues={}, profiles=updates, remove=[]), start_if_stopped=value)
        if result["warning"]: raise RuntimeError(result["warning"])
        return {"enabled": bool(value)}

    async def get_lossless_config(self, refresh=False):
        if type(refresh) is not bool: raise ValueError("检测参数格式错误")
        return await asyncio.to_thread(self._lossless.view, refresh)

    async def get_lossless_settings(self):
        def read():
            state = self._lossless.view()
            return {**state, "editor": self._lossless_editor.read() if state["configured"] else None}
        return await asyncio.to_thread(read)

    async def _stop_lossless(self, exe):
        gs = _game_settings_module
        processes = await asyncio.to_thread(gs.lossless_processes)
        matching = [p for p in processes if p["path"] and gs._same_exe(p["path"], exe)]
        if any(not p["path"] for p in processes):
            raise RuntimeError("无法确认小黄鸭后台路径，请先退出小黄鸭后再保存")
        for process in matching:
            # Check identity immediately before passing the PID to the existing elevated helper.
            if process not in await asyncio.to_thread(gs.lossless_processes): continue
            await self._power._call("kill", pid=process["pid"])
        for _ in range(50):
            if not await asyncio.to_thread(self._lossless.pids, exe): return
            await asyncio.sleep(.1)
        raise RuntimeError("小黄鸭后台仍在运行，本次未写入设置")

    async def save_lossless_settings(self, value):
        async with self._lossless_lock:
            return await self._save_lossless_settings(value)

    async def _save_lossless_settings(self, value, start_if_stopped=False):
        """Called only while holding _lossless_lock, including per-game on/off updates."""
        exe = await asyncio.to_thread(self._lossless.exe)
        if not exe: raise RuntimeError("没有找到小黄鸭，请重新选择程序")
        # Validate every field and the file revision before touching a running application.
        prepared = await asyncio.to_thread(self._lossless_editor.prepare, value)
        running = bool(await asyncio.to_thread(self._lossless.pids, exe))
        if running: await self._stop_lossless(exe)
        try:
            editor = await asyncio.to_thread(self._lossless_editor.commit, prepared, exe)
        except Exception:
            if running:
                try: await asyncio.to_thread(_game_settings_module.lossless_start, exe)
                except Exception: logging.exception("Could not restore Lossless Scaling after rejected save")
            raise
        warning = ""
        if running or start_if_stopped:
            try: await asyncio.to_thread(_game_settings_module.lossless_start, exe)
            except Exception as e: warning = "设置已保存，后台未能恢复：" + str(e)
        return {**await asyncio.to_thread(self._lossless.view), "editor": editor, "warning": warning}

    async def set_lossless_running(self, enabled):
        if type(enabled) is not bool: raise ValueError("后台开关格式错误")
        async with self._lossless_lock:
            exe = await asyncio.to_thread(self._lossless.exe)
            if not exe: raise RuntimeError("没有找到小黄鸭，请重新选择程序")
            running = bool(await asyncio.to_thread(self._lossless.pids, exe))
            if enabled and not running: await asyncio.to_thread(_game_settings_module.lossless_start, exe)
            elif not enabled and running: await self._stop_lossless(exe)
            return await asyncio.to_thread(self._lossless.view)

    async def configure_lossless(self, mode, exe=None):
        async with self._lossless_lock:
            return await asyncio.to_thread(self._lossless.configure, mode, exe)

    async def clear_lossless_config(self):
        async with self._lossless_lock:
            return await asyncio.to_thread(self._lossless.clear)

    async def open_lossless(self):
        exe = await asyncio.to_thread(self._lossless.exe)
        if not exe:
            raise RuntimeError("请先配置有效的小黄鸭程序")
        await asyncio.to_thread(os.startfile, exe)
        return {"ok": True}

    async def get_magpie_prefs(self):
        return await self._magpie.get_prefs()

    async def set_magpie_prefs(self, value):
        return await self._magpie.set_prefs(value)

    async def save_magpie_preset(self, value):
        return self._magpie.save_preset(value)

    async def delete_magpie_preset(self, name):
        return self._magpie.delete_preset(name)

    async def game_art_get(self, item):
        """A Steam game's current library images (custom grid ones first, else Steam's own cache)."""
        appid, platform, title = self._game_item(item)
        lg = _local_games_module
        def read():
            root = lg.steam_root()
            user = lg.steam_user(root)
            art = lg.LocalGames._grid_art(user, str(appid), None) if user else {}
            cache = root / "appcache" / "librarycache" if root else None
            if cache and cache.is_dir():
                folder = cache / str(appid)
                names = {"p": ["library_600x900.jpg", "library_capsule.jpg"], "hero": ["library_hero.jpg"],
                         "logo": ["logo.png"], "header": ["header.jpg", "library_header.jpg"]}
                for kind, files in names.items():
                    if kind in art: continue
                    for f in files:
                        for cand in (folder.rglob(f) if folder.is_dir() else []):
                            art[kind] = str(cand); break
                        if kind in art: break
                        flat = cache / f"{appid}_{f}"
                        if flat.is_file(): art[kind] = str(flat); break
            return {"art": art}
        return await asyncio.to_thread(read)

    async def game_art_save(self, item, art):
        """Set a Steam game's library images from the cover page (urls are downloaded, file: copied)."""
        appid, platform, title = self._game_item(item)
        if not isinstance(art, dict): raise ValueError("封面格式错误")
        cdp = _local_games_module.cdp
        def save():
            if not cdp.available(): raise RuntimeError("Steam 没开")
            files = self._local_games._art_files("steam-" + str(appid), {k: v for k, v in art.items() if k in cdp.ASSET_TYPES})
            for kind, url in art.items():
                if kind not in cdp.ASSET_TYPES: continue
                if url == "": cdp.set_artwork(appid, kind, None)
                elif kind in files: cdp.set_artwork(appid, kind, files[kind])
            return {"saved": sorted(files)}
        return await asyncio.to_thread(save)

    async def local_games_state(self):
        state = await asyncio.to_thread(self._local_games.state)
        state["busy"] = bool(self._local_games_task and not self._local_games_task.done())
        state["error"] = self._local_games_error
        return state

    async def local_games_search(self, name):
        if not isinstance(name, str) or len(name) > 260:
            raise ValueError("名称格式错误")
        return await asyncio.to_thread(self._local_games.search, name)

    async def local_games_candidates(self, match, kind=None, previews=True):
        """previews=False: remote preview urls (ClawLibrary downloads them itself, tile by tile)."""
        return await asyncio.to_thread(self._local_games.candidates, match, kind, previews is not False)

    async def local_games_official(self, appid):
        if not isinstance(appid, str) or not appid.isdigit():
            raise ValueError("appid 格式错误")
        return await asyncio.to_thread(self._local_games.official, appid)

    async def local_games_save(self, game):
        saved = await asyncio.to_thread(self._local_games.save, game)
        # Straight into Steam as well (live, or into its files when Steam is closed); never restarts Steam on its own.
        # A failure leaves the game pending for 「同步到 Steam」.
        if saved and saved.get("steam") and not (self._local_games_task and not self._local_games_task.done()):
            self._local_games_run(self._local_games.sync, False)
        return saved

    async def local_games_remove(self, ident):
        if not isinstance(ident, str) or not ident.isdigit():
            raise ValueError("游戏编号格式错误")
        return await asyncio.to_thread(self._local_games.remove, ident)

    async def set_steamgriddb_key(self, key):
        if not isinstance(key, str) or len(key) > 100:
            raise ValueError("密钥格式不对")
        return await asyncio.to_thread(self._local_games.set_key, key)

    def _local_games_run(self, work, *args):
        """Long jobs (many games' art, writing into Steam) run in the background; local_games_state shows progress."""
        if self._local_games_task and not self._local_games_task.done():
            raise RuntimeError("正在处理")
        self._local_games_error = ""

        async def run():
            try:
                await asyncio.to_thread(work, *args)
            except (RuntimeError, ValueError, OSError) as error:
                self._local_games_error = str(error) or type(error).__name__
                logging.info("local games job failed: %s", type(error).__name__)
            except Exception:
                logging.exception("local games job failed")
                self._local_games_error = "出错"
        self._local_games_task = asyncio.create_task(run())
        return {"started": True}

    async def local_games_add_many(self, items, collections=None, new_collections=None):
        if not isinstance(items, list) or len(items) > 500 or not all(isinstance(i, dict) for i in items):
            raise ValueError("列表格式错误")
        return self._local_games_run(self._local_games.add_many, items, collections, new_collections)

    async def local_games_fill_art(self):
        return self._local_games_run(self._local_games.fill_missing)

    async def local_games_sync(self):
        return self._local_games_run(self._local_games.sync)

    async def hide_steam_cloud_screenshot(self, ident):
        if not isinstance(ident, str) or not ident.isdigit() or len(ident) > 24:
            raise ValueError("截图编号格式错误")
        return await asyncio.to_thread(self._steam_cloud.hide, ident)

    async def action_result(self, result):
        if (not isinstance(result, dict) or type(result.get("ok")) is not bool
                or not isinstance(result.get("id"), str) or len(result["id"]) != 32):
            raise ValueError("结果格式错误")
        waiter = self._mapping_menu_waiters.get(result["id"])
        if waiter is not None:
            if not waiter.done():
                waiter.set_result(result)
            return {"handled": True}
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
        await self._qam_scaling.close()
        try:
            await self._freeze.resume()
        except (OSError, ValueError, RuntimeError):
            logging.exception("Game freeze shutdown recovery failed")
        tasks = list(self._mapping_tasks)
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
        self._overlay_frames.close()
        await asyncio.to_thread(self._captures.close)
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
