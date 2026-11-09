"""Front-end side of the toolbox agent connection (see claw_agent.py for the protocol).

Keeps one connection open, starts the agent through its scheduled task when it is not
running, and reconnects after the agent restarts."""
import asyncio
import json
import logging
import os
from pathlib import Path
import subprocess

HOME = Path(os.environ.get("LOCALAPPDATA", ".")) / "ClawToolbox"
TASK = "\\ClawToolbox\\Agent"
MAX_LINE = 8 * 1024 * 1024


class AgentUnavailable(RuntimeError):
    pass


def start_agent(home=HOME):
    """Run the sign-in task now. If it is missing (not installed through install.ps1), start the
    deployed agent directly, detached so Decky's exit does not take it along."""
    try:
        if subprocess.run(["schtasks", "/run", "/tn", TASK], capture_output=True,
                          creationflags=subprocess.CREATE_NO_WINDOW, timeout=10).returncode == 0:
            return "task"
    except (OSError, subprocess.SubprocessError):
        pass
    current = json.loads((home / "current.json").read_text(encoding="utf-8"))
    subprocess.Popen([current["python"], current["agent"]], cwd=str(Path(current["agent"]).parent),
                     creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
                     | subprocess.CREATE_BREAKAWAY_FROM_JOB | subprocess.CREATE_NO_WINDOW,
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)
    return "direct"


class AgentClient:
    def __init__(self, name, on_event, on_connected=None, on_disconnected=None, home=HOME, starter=start_agent):
        self._name = name
        self._on_event = on_event
        self._on_connected = on_connected
        self._on_disconnected = on_disconnected
        self._home = Path(home)
        self._starter = starter
        self._writer = None
        self._lock = asyncio.Lock()
        self._pending = {}
        self._sequence = 0
        self._connected = asyncio.Event()
        self._closed = False
        self._task = None
        self._handler = None
        self.info = None

    def start(self):
        self._task = asyncio.create_task(self._run())

    async def _open(self):
        endpoint = json.loads((self._home / "agent.json").read_text(encoding="utf-8"))
        reader, writer = await asyncio.wait_for(
            asyncio.open_connection("127.0.0.1", endpoint["port"], limit=MAX_LINE), 3)
        writer.write((json.dumps({"hello": endpoint["token"], "client": self._name}) + "\n").encode("utf-8"))
        await writer.drain()
        reply = json.loads(await asyncio.wait_for(reader.readline(), 5))
        if reply.get("hello") != "ok":
            writer.close()
            raise ConnectionError("agent refused")
        return reader, writer, reply

    async def _run(self):
        started_at = None
        while not self._closed:
            try:
                reader, writer, info = await self._open()
            except (OSError, ValueError, KeyError, asyncio.TimeoutError, ConnectionError):
                loop = asyncio.get_running_loop()
                if started_at is None or loop.time() - started_at > 20:
                    started_at = loop.time()
                    try:
                        logging.info("Toolbox agent not reachable; starting it (%s)",
                                     await asyncio.to_thread(self._starter, self._home))
                    except Exception:
                        logging.exception("Toolbox agent could not be started")
                await asyncio.sleep(.5)
                continue
            started_at = None
            self._writer, self.info = writer, info
            self._connected.set()
            logging.info("Connected to toolbox agent %s (pid %s)", info.get("version"), info.get("pid"))
            if self._on_connected:
                # Its own task: the handler makes calls whose replies this loop has to read.
                self._handler = asyncio.create_task(self._connected_handler(info))
            try:
                while True:
                    line = await reader.readline()
                    if not line:
                        break
                    try:
                        message = json.loads(line.decode("utf-8"))
                    except (ValueError, UnicodeError):
                        continue
                    if "event" in message:
                        try:
                            await self._on_event(message["event"], message.get("value"))
                        except Exception:
                            logging.exception("Agent event handler failed")
                        continue
                    waiter = self._pending.pop(message.get("id"), None)
                    if waiter and not waiter.done():
                        if "error" in message:
                            waiter.set_exception(RuntimeError(message["error"]))
                        else:
                            waiter.set_result(message.get("result"))
            except (ConnectionError, OSError):
                pass
            finally:
                self._connected.clear()
                self._writer = None
                writer.close()
                for waiter in self._pending.values():
                    if not waiter.done():
                        waiter.set_exception(AgentUnavailable("工具箱后台连接中断，请重试"))
                self._pending.clear()
            if not self._closed:
                logging.warning("Toolbox agent connection closed")
                if self._on_disconnected:
                    try:
                        await self._on_disconnected()
                    except Exception:
                        logging.exception("Agent disconnect handler failed")

    async def _connected_handler(self, info):
        try:
            await self._on_connected(info)
        except Exception:
            logging.exception("Agent connect handler failed")

    async def call(self, method, *args, wait=8):
        if not self._connected.is_set():
            try:
                await asyncio.wait_for(self._connected.wait(), wait)
            except asyncio.TimeoutError:
                raise AgentUnavailable("工具箱后台未运行，正在启动，请稍后重试") from None
        self._sequence += 1
        ident = self._sequence
        future = asyncio.get_running_loop().create_future()
        self._pending[ident] = future
        try:
            async with self._lock:
                if self._writer is None:
                    raise AgentUnavailable("工具箱后台连接中断，请重试")
                self._writer.write((json.dumps({"id": ident, "method": method, "args": list(args)},
                                               ensure_ascii=False) + "\n").encode("utf-8"))
                await self._writer.drain()
        except (ConnectionError, OSError):
            self._pending.pop(ident, None)
            raise AgentUnavailable("工具箱后台连接中断，请重试") from None
        return await future

    @property
    def connected(self):
        return self._connected.is_set()

    async def close(self):
        self._closed = True
        if self._writer is not None:
            self._writer.close()
        if self._task:
            self._task.cancel()
            try:
                await self._task
            except (asyncio.CancelledError, Exception):
                pass
