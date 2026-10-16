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
import base64
import ctypes
from ctypes import wintypes
import importlib.util
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import hashlib
import secrets
import shutil
import struct
import sys

ROOT = Path(__file__).resolve().parent
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
DATA = HOME / "data"
ENDPOINT = HOME / "agent.json"
LEGACY_DATA = Path(os.environ["USERPROFILE"]) / "homebrew" / "settings" / "claw-button-router"
# Clash Verge stays in the Decky plugin (it needs Decky's aiohttp), so its files stay there too.
LEGACY_SKIP = ("clash-ui.json", "verge-session.json", "subscription-backups", "verge-webview-")
MUTEX_NAME = "Local\\ClawToolboxAgent"
# The Game Bar widget is an app container: it cannot read this folder, so the agent also leaves
# its endpoint in the widget package's own LocalState, where the widget page reads it.
WIDGET_STATE = Path(os.environ["LOCALAPPDATA"]) / "Packages" / "ClawToolbox.GameBarWidget_ba3r68zr466za" / "LocalState"
WS_GUID = b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
MAX_LINE = 8 * 1024 * 1024

# Everything a front end may call. Anything else (private methods, close) is refused.
METHODS = {
    "get_state", "refresh", "save_settings", "frontend_ready", "set_steam_context", "action_result",
    "manage_compatibility",
    "get_power_state", "save_power", "restore_power", "set_ecore",
    "get_rumble_state", "set_rumble_strength", "restore_rumble", "test_rumble",
    "get_gamepad_state", "set_gamepad_mode", "set_stick_deadzone", "set_stick_swap", "set_trigger_range",
    "set_quick_trigger", "set_back_key", "set_key_map", "set_desktop_key_map", "set_light", "restore_gamepad",
    "get_input_layer_state", "set_input_layer_enabled", "get_input_layout", "save_input_layout",
    "get_input_layer_catalog", "test_input_layer", "set_desktop_stick_deadzone",
    "get_magpie_state", "get_magpie_targets", "get_magpie_catalog", "apply_magpie", "stop_magpie",
    "set_magpie_plain", "toggle_magpie", "magpie_auto_start", "set_magpie_pause",
    "get_game_picker_path", "get_game_picker_locations", "inspect_game_file",
    "play_navigation_haptic", "set_navigation_context",
    # Game Bar widget
    "widget_state", "set_brightness", "set_volume", "set_mute", "set_refresh_rate", "set_fps_limit",
    "close_game", "power_action", "toggle_keyboard", "screenshot",
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
    """JSON lines over TCP (Decky plugin, tests)."""
    def __init__(self, reader, writer):
        self.reader, self.writer = reader, writer
        self.lock = asyncio.Lock()
        self.frontends = set()
        self.name = "?"

    async def recv(self):
        line = await self.reader.readline()
        return line.decode("utf-8") if line else None

    async def send(self, message):
        data = (json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8")
        async with self.lock:
            self.writer.write(data)
            await self.writer.drain()


class WebSocketConnection(Connection):
    """The same JSON messages as WebSocket text frames (the Game Bar widget page)."""
    async def handshake(self):
        request = (await asyncio.wait_for(self.reader.readuntil(b"\r\n\r\n"), 5)).decode("latin-1")
        headers = {}
        for line in request.split("\r\n")[1:]:
            name, _, value = line.partition(":")
            headers[name.strip().lower()] = value.strip()
        key = headers.get("sec-websocket-key")
        if headers.get("upgrade", "").lower() != "websocket" or not key:
            raise ValueError("not a websocket request")
        accept = base64.b64encode(hashlib.sha1(key.encode() + WS_GUID).digest()).decode()
        self.writer.write(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                           "Sec-WebSocket-Accept: " + accept + "\r\n\r\n").encode())
        await self.writer.drain()

    async def recv(self):
        message = b""
        while True:
            head = await self.reader.readexactly(2)
            final, opcode, masked, length = head[0] & 0x80, head[0] & 0x0F, head[1] & 0x80, head[1] & 0x7F
            if length == 126:
                length = struct.unpack(">H", await self.reader.readexactly(2))[0]
            elif length == 127:
                length = struct.unpack(">Q", await self.reader.readexactly(8))[0]
            if length > MAX_LINE:
                raise ValueError("frame too large")
            mask = await self.reader.readexactly(4) if masked else b"\0\0\0\0"
            data = bytes(b ^ mask[i % 4] for i, b in enumerate(await self.reader.readexactly(length)))
            if opcode == 8:
                return None
            if opcode == 9:
                await self._frame(10, data)
                continue
            if opcode in (0, 1):
                message += data
                if final:
                    return message.decode("utf-8")

    async def _frame(self, opcode, data):
        length = len(data)
        if length < 126:
            size = bytes([length])
        elif length < 65536:
            size = bytes([126]) + struct.pack(">H", length)
        else:
            size = bytes([127]) + struct.pack(">Q", length)
        async with self.lock:
            self.writer.write(bytes([0x80 | opcode]) + size + data)
            await self.writer.drain()

    async def send(self, message):
        await self._frame(1, json.dumps(message, ensure_ascii=False).encode("utf-8"))


class Agent:
    def __init__(self, service_factory, endpoint=ENDPOINT, token=None):
        self.version = version()
        self.endpoint = Path(endpoint)
        self.token = token or secrets.token_hex(32)
        self.connections = set()
        self.stopped = asyncio.Event()
        self.server = None
        self.ws_server = None
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
        self.ws_server = await asyncio.start_server(self.handle_ws, "127.0.0.1", 0, limit=MAX_LINE)
        ws_port = self.ws_server.sockets[0].getsockname()[1]
        record = {"port": port, "wsPort": ws_port, "token": self.token, "pid": os.getpid(), "version": self.version}
        self.endpoint.parent.mkdir(parents=True, exist_ok=True)
        temp = self.endpoint.with_suffix(".tmp")
        temp.write_text(json.dumps(record), encoding="utf-8")
        os.replace(temp, self.endpoint)
        self.publish_widget_endpoint(record)
        logging.info("Agent %s listening on 127.0.0.1:%s (WebSocket %s)", self.version, port, ws_port)
        await self.service.start()
        return port

    def publish_widget_endpoint(self, record, folder=None):
        folder = Path(folder) if folder else WIDGET_STATE
        if not folder.is_dir():
            return  # widget not installed
        try:
            temp = folder / "agent.tmp"
            temp.write_text(json.dumps({"wsPort": record["wsPort"], "token": record["token"]}), encoding="utf-8")
            os.replace(temp, folder / "agent.json")
        except OSError:
            logging.exception("Could not publish the endpoint for the Game Bar widget")

    async def handle(self, reader, writer):
        await self.serve(Connection(reader, writer))

    async def handle_ws(self, reader, writer):
        connection = WebSocketConnection(reader, writer)
        try:
            await connection.handshake()
        except (asyncio.TimeoutError, asyncio.IncompleteReadError, asyncio.LimitOverrunError, ValueError,
                ConnectionError, OSError):
            writer.close()
            return
        await self.serve(connection)

    async def serve(self, connection):
        writer = connection.writer
        try:
            hello = json.loads(await asyncio.wait_for(connection.recv(), 5) or "null")
            if not isinstance(hello, dict) or not secrets.compare_digest(str(hello.get("hello", "")), self.token):
                return
            connection.name = str(hello.get("client", "?"))[:32]
            await connection.send({"hello": "ok", "version": self.version, "pid": os.getpid()})
            self.connections.add(connection)
            logging.info("Front end connected: %s", connection.name)
            while not self.stopped.is_set():
                text = await connection.recv()
                if text is None:
                    break
                try:
                    request = json.loads(text)
                except ValueError:
                    continue
                if isinstance(request, dict):
                    # Concurrent: a slow call (compatibility repair) never delays a haptic pulse.
                    task = asyncio.create_task(self.answer(connection, request))
                    self.tasks.add(task)
                    task.add_done_callback(self.tasks.discard)
        except (asyncio.TimeoutError, asyncio.IncompleteReadError, ValueError, UnicodeError, ConnectionError, OSError):
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
        for server in (self.server, self.ws_server):
            if server:
                server.close()
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
