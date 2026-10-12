"""Standalone toolbox agent. Started at sign-in by the "\\ClawToolbox\\Agent" scheduled task,
so power, fan, OSD, rumble and the button router keep working without Steam or Decky.

Front ends connect over 127.0.0.1 with the token in %LOCALAPPDATA%\\ClawToolbox\\agent.json
(readable only by this user) and speak JSON lines:
  -> {"hello": token, "client": "decky"}          <- {"hello": "ok", "version": ..., "pid": ...}
  -> {"id": 1, "method": "get_power_state", "args": [false]}
  <- {"id": 1, "result": ...}  or  {"id": 1, "error": "message"}
  <- {"event": "power_status", "value": ...}       (pushed to every front end)
"""
import asyncio
import ctypes
from ctypes import wintypes
import importlib.util
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import secrets
import shutil
import sys

ROOT = Path(__file__).resolve().parent
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
DATA = HOME / "data"
ENDPOINT = HOME / "agent.json"
LEGACY_DATA = Path(os.environ["USERPROFILE"]) / "homebrew" / "settings" / "claw-button-router"
# Clash Verge stays in the Decky plugin (it needs Decky's aiohttp), so its files stay there too.
LEGACY_SKIP = ("clash-ui.json", "verge-session.json", "subscription-backups", "verge-webview-")
MUTEX_NAME = "Local\\ClawToolboxAgent"
MAX_LINE = 8 * 1024 * 1024

# Everything a front end may call. Anything else (private methods, close) is refused.
METHODS = {
    "get_state", "refresh", "save_settings", "frontend_ready", "set_steam_context", "action_result",
    "manage_compatibility",
    "get_power_state", "save_power", "restore_power",
    "get_rumble_state", "set_rumble_strength", "restore_rumble", "test_rumble",
    "get_magpie_state", "get_magpie_targets", "get_magpie_catalog", "apply_magpie", "stop_magpie",
    "set_magpie_plain", "toggle_magpie", "magpie_auto_start", "set_magpie_pause",
    "get_game_picker_path", "get_game_picker_locations", "inspect_game_file",
    "play_navigation_haptic", "set_navigation_context",
}


def version():
    return json.loads((ROOT / "plugin.json").read_text(encoding="utf-8"))["version"]


def migrate(legacy=LEGACY_DATA, data=DATA):
    """First start only: copy the Decky plugin's settings so nothing is lost. Copy, not move,
    so going back to an older plugin still finds its files."""
    marker = data / ".migrated"
    if marker.exists():
        return False
    data.mkdir(parents=True, exist_ok=True)
    copied = False
    if legacy.is_dir():
        for item in legacy.iterdir():
            if any(item.name == skip or (skip.endswith("-") and item.name.startswith(skip)) for skip in LEGACY_SKIP):
                continue
            target = data / item.name
            if target.exists():
                continue
            if item.is_dir():
                shutil.copytree(item, target)
            else:
                shutil.copy2(item, target)
            copied = True
    marker.write_text(str(legacy), encoding="utf-8")
    return copied


class SingleInstance:
    """Named mutex. A newer agent started while an older one is still restoring waits for it."""
    WAIT_OBJECT_0, WAIT_ABANDONED = 0, 0x80

    def __init__(self, name=MUTEX_NAME):
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateMutexW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR]
        kernel.CreateMutexW.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.WaitForSingleObject.restype = wintypes.DWORD
        kernel.ReleaseMutex.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self._kernel = kernel
        self._handle = kernel.CreateMutexW(None, False, name)
        if not self._handle:
            raise ctypes.WinError(ctypes.get_last_error())

    def acquire(self, timeout_ms):
        return self._kernel.WaitForSingleObject(self._handle, timeout_ms) in (self.WAIT_OBJECT_0, self.WAIT_ABANDONED)

    def release(self):
        self._kernel.ReleaseMutex(self._handle)
        self._kernel.CloseHandle(self._handle)


class Connection:
    def __init__(self, reader, writer):
        self.reader, self.writer = reader, writer
        self.lock = asyncio.Lock()
        self.frontends = set()
        self.name = "?"

    async def send(self, message):
        data = (json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8")
        async with self.lock:
            self.writer.write(data)
            await self.writer.drain()


class Agent:
    def __init__(self, service_factory, endpoint=ENDPOINT, token=None):
        self.version = version()
        self.endpoint = Path(endpoint)
        self.token = token or secrets.token_hex(32)
        self.connections = set()
        self.stopped = asyncio.Event()
        self.server = None
        self.tasks = set()
        self.closed = False
        self.service = service_factory(self.emit)

    async def emit(self, event, value):
        message = {"event": event, "value": value}
        for connection in list(self.connections):
            try:
                await connection.send(message)
            except (ConnectionError, OSError, RuntimeError):
                pass

    async def start(self, port=0):
        self.server = await asyncio.start_server(self.handle, "127.0.0.1", port, limit=MAX_LINE)
        port = self.server.sockets[0].getsockname()[1]
        self.endpoint.parent.mkdir(parents=True, exist_ok=True)
        temp = self.endpoint.with_suffix(".tmp")
        temp.write_text(json.dumps({"port": port, "token": self.token, "pid": os.getpid(),
                                    "version": self.version}), encoding="utf-8")
        os.replace(temp, self.endpoint)
        logging.info("Agent %s listening on 127.0.0.1:%s", self.version, port)
        await self.service.start()
        return port

    async def handle(self, reader, writer):
        connection = Connection(reader, writer)
        try:
            hello = json.loads(await asyncio.wait_for(reader.readline(), 5))
            if not isinstance(hello, dict) or not secrets.compare_digest(str(hello.get("hello", "")), self.token):
                return
            connection.name = str(hello.get("client", "?"))[:32]
            await connection.send({"hello": "ok", "version": self.version, "pid": os.getpid()})
            self.connections.add(connection)
            logging.info("Front end connected: %s", connection.name)
            while not self.stopped.is_set():
                line = await reader.readline()
                if not line:
                    break
                try:
                    request = json.loads(line.decode("utf-8"))
                except (ValueError, UnicodeError):
                    continue
                if isinstance(request, dict):
                    # Concurrent: a slow call (compatibility repair) never delays a haptic pulse.
                    task = asyncio.create_task(self.answer(connection, request))
                    self.tasks.add(task)
                    task.add_done_callback(self.tasks.discard)
        except (asyncio.TimeoutError, ValueError, UnicodeError, ConnectionError, OSError):
            pass
        finally:
            if connection in self.connections:
                self.connections.discard(connection)
                logging.info("Front end disconnected: %s", connection.name)
                if connection.frontends:
                    await self.service.frontend_gone(connection.frontends)
            writer.close()

    async def answer(self, connection, request):
        ident, method, args = request.get("id"), request.get("method"), request.get("args", [])
        try:
            if not isinstance(args, list):
                raise ValueError("参数格式错误")
            if method == "agent_info":
                result = {"version": self.version, "pid": os.getpid(), "clients": [c.name for c in self.connections]}
            elif method == "shutdown":
                self.stopped.set()
                result = {"stopping": True}
            elif method in METHODS:
                result = await getattr(self.service, method)(*args)
                if method == "frontend_ready" and len(args) >= 2 and isinstance(args[1], str):
                    (connection.frontends.add if args[0] is True else connection.frontends.discard)(args[1])
            else:
                raise ValueError("不支持的后台调用")
            reply = {"id": ident, "result": result}
        except (RuntimeError, ValueError, TypeError, OSError) as error:
            reply = {"id": ident, "error": str(error) or type(error).__name__}
        except Exception as error:
            logging.exception("Call %s failed", method)
            reply = {"id": ident, "error": "工具箱后台出错：" + type(error).__name__}
        try:
            await connection.send(reply)
        except (ConnectionError, OSError, RuntimeError):
            pass

    async def close(self):
        if self.closed:
            return
        self.closed = True
        self.stopped.set()
        if self.server:
            self.server.close()
        try:
            if json.loads(self.endpoint.read_text(encoding="utf-8")).get("pid") == os.getpid():
                self.endpoint.unlink()
        except (OSError, ValueError):
            pass
        for connection in list(self.connections):
            connection.writer.close()
        if self.server:
            try:
                await asyncio.wait_for(self.server.wait_closed(), 5)
            except asyncio.TimeoutError:
                pass
        await self.service.close()


def load_service():
    spec = importlib.util.spec_from_file_location("claw_toolbox_service", ROOT / "claw_service.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


async def run():
    service_module = load_service()
    agent = Agent(lambda emit: service_module.Service(DATA, emit))
    try:
        await agent.start()
        await agent.stopped.wait()
    finally:
        logging.info("Agent stopping")
        await agent.close()
        logging.info("Agent stopped")


def main():
    HOME.mkdir(parents=True, exist_ok=True)
    (HOME / "logs").mkdir(exist_ok=True)
    handler = RotatingFileHandler(HOME / "logs" / "agent.log", maxBytes=1 << 20, backupCount=3, encoding="utf-8")
    logging.basicConfig(level=logging.INFO, handlers=[handler], format="%(asctime)s %(levelname)s %(message)s")
    instance = SingleInstance()
    if not instance.acquire(30000):
        logging.info("Another agent is running; exiting")
        return 0
    try:
        if migrate():
            logging.info("Copied settings from %s", LEGACY_DATA)
        asyncio.run(run())
    except Exception:
        logging.exception("Agent failed")
        return 1
    finally:
        instance.release()
    return 0


if __name__ == "__main__":
    sys.exit(main())
