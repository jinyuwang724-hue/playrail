"""Restricted client of the independent machine-wide fan service.

Once installed, an unavailable service is an error; the UI agent must never
fall back to its own competing hardware writer.
"""
import asyncio
import json
import os
from pathlib import Path
import subprocess


class FanServiceClient:
    def __init__(self):
        self.marker = Path(os.environ.get("PROGRAMDATA", r"C:\ProgramData")) / "ClawToolbox/FanService/service.json"
        self.exe = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "ClawToolbox/FanService/ClawFanService.exe"
        self.revision = None
        self.lock = asyncio.Lock()

    @property
    def installed(self):
        return self.marker.is_file()

    async def _request(self, request):
        if not self.exe.is_file():
            raise RuntimeError("独立风扇服务文件缺失，请修复安装")
        try:
            process = await asyncio.create_subprocess_exec(
                str(self.exe), "--client", stdin=asyncio.subprocess.PIPE,
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW)
        except OSError as error:
            raise RuntimeError("独立风扇服务客户端无法启动，请修复安装") from error
        try:
            output, _ = await asyncio.wait_for(process.communicate((json.dumps(request) + "\n").encode()), 13)
            reply = json.loads(output.decode("utf-8-sig"))
        except asyncio.CancelledError:
            if process.returncode is None:
                process.kill()
                await process.wait()
            raise
        except (asyncio.TimeoutError, OSError, ValueError) as error:
            if process.returncode is None:
                process.kill()
                await process.wait()
            raise RuntimeError("独立风扇服务没有响应，请检查服务状态") from error
        if not reply.get("ok"):
            raise RuntimeError(reply.get("error") or "独立风扇服务操作失败")
        state = reply["fan"]
        self.revision = state["revision"]
        return state

    async def get(self):
        async with self.lock:
            return await self._request({"command": "get"})

    async def update(self, fan):
        async with self.lock:
            if self.revision is None:
                await self._request({"command": "get"})
            return await self._request({"command": "update", "revision": self.revision, "fan": fan})
