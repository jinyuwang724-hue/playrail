"""Decky front end. The toolbox itself runs in the standalone agent (claw_agent.py), which keeps
working without Steam; this plugin forwards Steam's calls to it and its events back to Steam.
Clash Verge stays here because it relies on Decky's aiohttp."""
import asyncio
import importlib.util
import json
import logging
from pathlib import Path

import decky

PLUGIN_ROOT = Path(__file__).resolve().parent


def _load(name, file):
    spec = importlib.util.spec_from_file_location(name, PLUGIN_ROOT / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_client_module = _load("claw_toolbox_agent_client", "claw_agent_client.py")
_clash_module = _load("claw_toolbox_clash", "claw_clash.py")
_subscription_module = _load("claw_toolbox_subscriptions", "claw_subscriptions.py")

# Same list as claw_agent.METHODS: the calls the Steam front end makes.
FORWARDED = (
    "get_state", "refresh", "save_settings", "frontend_ready", "set_steam_context", "action_result",
    "manage_compatibility",
    "get_power_state", "save_power", "restore_power",
    "get_rumble_state", "set_rumble_strength", "restore_rumble", "test_rumble",
    "get_magpie_state", "get_magpie_targets", "get_magpie_catalog", "apply_magpie", "stop_magpie",
    "set_magpie_plain", "toggle_magpie", "magpie_auto_start", "set_magpie_pause",
    "get_game_picker_path", "get_game_picker_locations", "inspect_game_file",
    "play_navigation_haptic", "set_navigation_context",
)
OFFLINE = {"mode": "Unknown", "phase": "工具箱后台未运行，正在重新连接", "enabled": False,
           "error": "工具箱后台未连接；按键暂由 ClawTweaks 处理"}


class Plugin:
    def __init__(self):
        self._version = json.loads((PLUGIN_ROOT / "plugin.json").read_text(encoding="utf-8"))["version"]
        self._frontend = None
        self._upgrade_requested = False
        self._client = _client_module.AgentClient("decky", decky.emit, self._connected, self._disconnected)
        data = Path(decky.DECKY_PLUGIN_SETTINGS_DIR)
        data.mkdir(parents=True, exist_ok=True)
        self._clash = _clash_module.ClashControl(PLUGIN_ROOT, data)
        self._subscriptions = _subscription_module.VergeSubscriptions(self._clash, data)

    async def _main(self):
        self._client.start()

    async def _connected(self, info):
        current = None
        try:
            current = json.loads((_client_module.HOME / "current.json").read_text(encoding="utf-8")).get("version")
        except (OSError, ValueError):
            pass
        if info.get("version") != self._version and current == self._version and not self._upgrade_requested:
            # install.ps1 deployed a new agent; the old one restores everything and exits, the task starts the new one.
            self._upgrade_requested = True
            logging.info("Agent %s is older than plugin %s; restarting it", info.get("version"), self._version)
            await self._client.call("shutdown")
            return
        if self._frontend:
            # The agent restarted while Steam was open: hand the buttons back to this front end.
            await self._client.call("frontend_ready", True, self._frontend)
            await decky.emit("router_event", {"type": "status", **(await self._client.call("get_state"))["status"]})

    async def _disconnected(self):
        await decky.emit("router_event", {"type": "status", **OFFLINE})

    async def _call(self, method, args):
        # Remember the front end before the call: if the agent is still starting, the
        # connect handler registers it once the agent is up.
        if method == "frontend_ready" and len(args) >= 2:
            if args[0] is True:
                self._frontend = args[1]
            elif args[1] == self._frontend:
                self._frontend = None
        return await self._client.call(method, *args)

    async def get_clash_state(self):
        await self._clash.refresh()
        await self._subscriptions.refresh()
        return self._subscriptions.decorate(self._clash.state)

    async def set_clash(self, action, value):
        if isinstance(action, str) and action.startswith("subscription_"):
            await self._subscriptions.perform(action[len("subscription_"):], value)
            await self._clash.refresh()
        else:
            await self._clash.apply(action, value)
        return self._subscriptions.decorate(self._clash.state)

    async def _unload(self):
        # The agent keeps running (fan, power, OSD stay on); only this front end's buttons are released.
        if self._frontend and self._client.connected:
            try:
                await asyncio.wait_for(self._client.call("frontend_ready", False, self._frontend), 15)
            except (RuntimeError, asyncio.TimeoutError):
                logging.exception("Releasing buttons on unload failed")
        await self._client.close()
        await self._subscriptions.close()
        await self._clash.close()


def _forward(name):
    async def method(self, *args):
        return await self._call(name, args)
    method.__name__ = name
    return method


for _name in FORWARDED:
    setattr(Plugin, _name, _forward(_name))
