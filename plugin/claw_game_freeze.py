"""One game at a time. The worker resumes it when the Agent's pipe closes."""
import asyncio
import json
import logging
import subprocess


class GameFreeze:
    def __init__(self, root, data):
        self.exe = root / "bin" / "ClawGameFreeze.exe"
        self.data = data
        self.journal = data / "game-freeze.json"
        self.lock = asyncio.Lock()
        self.worker = self.target = None

    def view(self):
        return {"available": self.exe.is_file(), "frozen": self.target,
                "recoveryPending": not self.target and self.journal.is_file()}

    async def _launch(self, *arguments):
        if not self.exe.is_file():
            raise RuntimeError("游戏冻结组件尚未安装")
        return await asyncio.create_subprocess_exec(str(self.exe), str(self.data), *arguments,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)

    async def _recover(self):
        if not self.journal.is_file():
            return
        process = await self._launch()
        output, _ = await process.communicate()
        if process.returncode:
            detail = json.loads(output).get("error") if output else None
            raise RuntimeError(detail or "游戏恢复尚未完成")

    async def start(self):
        try:
            async with self.lock:
                await self._recover()
        except (OSError, ValueError, RuntimeError):
            logging.exception("Game freeze startup recovery failed")

    async def freeze(self, target):
        async with self.lock:
            if self.worker:
                raise RuntimeError("请先继续已经冻结的游戏")
            await self._recover()
            pid, created = target["identity"].split(":", 1)
            process = await self._launch(pid, created)
            try:
                line = await asyncio.wait_for(process.stdout.readline(), 6)
                result = json.loads(line) if line else {}
                if result.get("ok") is not True:
                    raise RuntimeError(result.get("error") or "游戏冻结失败")
            except BaseException:
                process.stdin.close()
                await process.wait()
                await self._recover()
                raise
            self.worker = process
            self.target = {key: target[key] for key in ("pid", "identity", "title")}
            self.target["processes"] = result["processes"]
            asyncio.create_task(self._watch(process))
            return self.view()

    async def _watch(self, process):
        await process.wait()
        async with self.lock:
            if self.worker is not process:
                return
            self.worker = self.target = None
            try:
                await self._recover()
            except (OSError, ValueError, RuntimeError):
                logging.exception("Game freeze worker recovery failed")

    async def resume(self):
        async with self.lock:
            if self.worker:
                self.worker.stdin.close()
                await self.worker.wait()
                self.worker = self.target = None
            await self._recover()
            return self.view()
