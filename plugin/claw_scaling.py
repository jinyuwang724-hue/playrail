"""Owned WGC/D3D11 scaler. Saved preferences never auto-start capture."""
import asyncio
import json
import logging
import math
import os
from pathlib import Path
import subprocess
import uuid

MODES = ("auto", "integer", "fit", "stretch", "fill")
FILTERS = ("linear", "nearest", "fsr", "nis")


def validate(value):
    if not isinstance(value, dict) or set(value) - {"enabled", "mode", "filter", "sharpness", "target"}:
        raise ValueError("缩放设置格式错误")
    if type(value.get("enabled")) is not bool or value.get("mode") not in MODES or value.get("filter") not in FILTERS:
        raise ValueError("请选择有效的缩放模式和过滤器")
    sharpness, target = value.get("sharpness"), value.get("target")
    if type(sharpness) not in (int, float) or not math.isfinite(sharpness) or not 0 <= sharpness <= 1:
        raise ValueError("锐化强度超出范围")
    if type(target) is not int or not 0 <= target <= 2**53 - 1 or (value["enabled"] and target == 0):
        raise ValueError("请先选择正在运行的游戏窗口")
    return dict(value)


class Scaling:
    def __init__(self, root, data, emit):
        self.executable = Path(root) / "bin/scaling/ClawScaling.exe"
        self.path = Path(data) / "scaling.json"
        self.emit = emit
        self.process = self.reader = self.errors = None
        self.waiters = {}
        self.lock = asyncio.Lock()
        self.write_lock = asyncio.Lock()
        self.blockers = set()
        self.closed = False
        self.state = {"enabled": False, "mode": "auto", "filter": "linear", "sharpness": .5,
                      "target": 0, "phase": "缩放已关闭", "active": False, "error": ""}
        try:
            saved = json.loads(self.path.read_text(encoding="utf-8"))
            saved = validate({**saved, "enabled": False, "target": 0})
            self.state.update(saved)
        except FileNotFoundError:
            pass
        except (ValueError, OSError, TypeError):
            self.state["error"] = "已有缩放设置无法读取，请重新选择"

    def view(self):
        return {**self.state, "available": self.executable.is_file()}

    async def accept(self, state):
        self.state.update({k: v for k, v in state.items() if k not in ("type", "id")})
        try:
            await self.emit(self.view())
        except Exception:
            logging.exception("Scaling frontend event delivery failed")

    async def start(self):
        if self.closed:
            raise RuntimeError("插件正在重载，请稍候")
        if self.process is not None and self.process.returncode is None:
            return
        if not self.executable.is_file():
            raise RuntimeError("缩放后台尚未安装，请重新安装插件")
        self.process = await asyncio.create_subprocess_exec(str(self.executable),
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.reader = asyncio.create_task(self.read(self.process))
        self.errors = asyncio.create_task(self.read_errors(self.process))
        # Restore only preferences, with capture disabled. Overlay ownership is already known.
        await self.request("configure", settings=self.preferences(enabled=False))
        if self.blockers:
            await self.request("pause", paused=True)

    def preferences(self, **patch):
        return {k: patch.get(k, self.state[k]) for k in ("enabled", "mode", "filter", "sharpness", "target")}

    async def read(self, process):
        try:
            while line := await process.stdout.readline():
                try:
                    event = json.loads(line.decode("utf-8-sig"))
                except (ValueError, UnicodeError):
                    continue
                if event.get("type") == "status":
                    await self.accept(event)
                elif event.get("id") in self.waiters:
                    if isinstance(event.get("state"), dict):
                        await self.accept(event["state"])
                    future = self.waiters[event["id"]]
                    if not future.done():
                        future.set_result(event)
        except Exception:
            logging.exception("Scaling output reader failed")
        finally:
            if process is self.process:
                for future in self.waiters.values():
                    if not future.done():
                        future.set_exception(RuntimeError("缩放后台已退出，画面已释放"))
                if not self.closed:
                    await self.accept({"enabled": False, "active": False, "phase": "缩放后台已退出", "error": "请重新开启缩放"})

    async def read_errors(self, process):
        while line := await process.stderr.readline():
            # Shader diagnostics contain no user credentials.
            if not self.closed:
                self.state["error"] = line.decode("utf-8", errors="replace").strip()[:512]

    async def request(self, command, **args):
        token = uuid.uuid4().hex
        future = asyncio.get_running_loop().create_future()
        self.waiters[token] = future
        try:
            async with self.write_lock:
                if not self.process or self.process.returncode is not None:
                    raise RuntimeError("缩放后台未运行")
                self.process.stdin.write((json.dumps({"command": command, "id": token, **args}) + "\n").encode())
                await self.process.stdin.drain()
            result = await asyncio.wait_for(future, 20)
            if result.get("ok") is False:
                raise RuntimeError(result.get("error") or "缩放操作失败")
            return result
        except asyncio.TimeoutError:
            # This process owns only capture/output resources; ending it releases its window.
            if self.process and self.process.returncode is None:
                self.process.kill()
                await self.process.wait()
            await self.accept({"enabled": False, "active": False, "phase": "缩放已停止", "error": "缩放后台响应超时，已释放画面"})
            raise RuntimeError("缩放后台响应超时，已释放画面") from None
        finally:
            self.waiters.pop(token, None)

    async def get(self):
        async with self.lock:
            if self.process and self.process.returncode is None:
                await self.request("status")
            return self.view()

    async def targets(self):
        async with self.lock:
            await self.start()
            return (await self.request("targets"))["targets"]

    async def set(self, settings):
        value = validate(settings)
        async with self.lock:
            if value == self.preferences():
                return self.view()
            if value["enabled"] or (self.process and self.process.returncode is None):
                await self.start()
                await self.request("configure", settings=value)
            else:
                await self.accept({**value, "phase": "缩放已关闭", "error": ""})
            saved = {k: self.state[k] for k in ("mode", "filter", "sharpness")}
            temp = self.path.with_suffix(".tmp")
            temp.write_text(json.dumps(saved), encoding="utf-8")
            os.replace(temp, self.path)
            return self.view()

    async def pause(self, source, visible):
        if source not in ("overlay", "focus", "panel") or type(visible) is not bool:
            raise ValueError("菜单状态格式错误")
        async with self.lock:
            before = bool(self.blockers)
            self.blockers.add(source) if visible else self.blockers.discard(source)
            if before != bool(self.blockers) and self.process and self.process.returncode is None:
                await self.request("pause", paused=bool(self.blockers))
            return self.view()

    async def close(self):
        self.closed = True
        async with self.lock:
            process = self.process
            if process and process.returncode is None:
                try:
                    process.stdin.close()
                except (BrokenPipeError, ConnectionResetError):
                    pass
                try:
                    await asyncio.wait_for(process.wait(), 5)
                except asyncio.TimeoutError:
                    process.kill()
                    await process.wait()
            for task in (self.reader, self.errors):
                if task:
                    await task
