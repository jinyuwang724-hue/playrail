"""Game Bar's on-demand scaling controls. The engines keep their own settings."""
import asyncio
from fnmatch import fnmatch
import logging
import math
import os
import time


class Scaling:
    def __init__(self, magpie, lossless, editor, call, start_app, parse_hotkey, compile_command):
        self.magpie, self.lossless, self.editor = magpie, lossless, editor
        self.call, self.start_app = call, start_app
        self.parse_hotkey, self.compile_command = parse_hotkey, compile_command
        self.task = None
        self.pending = ""
        self.deadline = 0
        self.error = {}
        self.lossless_source = None
        self.lossless_output = None
        self.lossless_settings = {}
        self.lossless_settings_stamp = None

    async def lossless_state(self, exe):
        return await self.call("lossless_control", exe=exe, action="read") if exe else {}

    @staticmethod
    def output_key(state):
        return (state.get("outputPid"), state.get("outputCreated"), state.get("outputWindow"))

    def settings_for_page(self):
        try:
            stat = self.editor.path.stat()
            stamp = (stat.st_mtime_ns, stat.st_size)
        except FileNotFoundError:
            stamp = ()
        if stamp != self.lossless_settings_stamp:
            self.lossless_settings = self.editor.read()
            self.lossless_settings_stamp = stamp
        return self.lossless_settings

    async def view(self, engine, game_path=""):
        if engine not in ("magpie", "lossless"):
            raise ValueError("未知的缩放工具")
        pending = self.pending == engine
        result = {"pending": pending, "blocked": bool(self.pending and not pending),
                  "seconds": max(0, math.ceil(self.deadline - time.monotonic())) if pending else 0,
                  "error": self.error.get(engine, "")}
        if engine == "magpie":
            magpie = self.magpie.view()
            return {**magpie, **result, "error": result["error"] or magpie.get("error", ""),
                    "preferences": magpie["preferences"] if magpie["enabled"] else self.magpie.prefs,
                    "targetTitle": magpie.get("targetTitle", "") if magpie["enabled"] else ""}
        exe = await asyncio.to_thread(self.lossless.exe)
        runtime = await self.lossless_state(exe)
        if not runtime.get("active") or self.output_key(runtime) != self.lossless_output:
            self.lossless_source = self.lossless_output = None
        source = self.lossless_source
        if source and self.magpie.windows.pid(source["hwnd"]) != source["pid"]:
            self.lossless_source = source = None
        editor = {}
        if exe:
            try:
                editor = await asyncio.to_thread(self.settings_for_page)
            except (OSError, ValueError) as error:
                # The original app can rewrite its XML while starting. Keep the
                # live output/countdown readable while the file is being written.
                editor = self.lossless_settings
                result["error"] = result["error"] or str(error)
        profiles = editor.get("profiles", [])
        profile = next((p for p in profiles if p["default"]), {})
        path = source["path"] if source else game_path
        if path:
            profile = next((p for p in profiles if any(
                fnmatch(path, pattern.strip()) or fnmatch(os.path.basename(path), pattern.strip())
                for pattern in p.get("path", "").split(";") if pattern.strip())), profile)
        hotkey = editor.get("globalValues", {}).get("Hotkey", "Ctrl+Alt+S")
        choices = {field["key"]: field["choices"] for group in editor.get("profileSchema", [])
                   for field in group["fields"] if field["key"] in (
                       "ScalingType", "FrameGeneration", "LSFG3Mode1", "LSFG2Mode", "LSFGSize")}
        choices["LSFG3Multiplier"] = [{"value": n, "label": f"×{n}"} for n in (1.5, 2, 2.5, 3, 4)]
        choices["LSFG3Target"] = [{"value": n, "label": f"{n} FPS" if n else "跟随屏幕"}
                                   for n in (0, 40, 48, 60, 72, 90, 120, 144, 165, 240)]
        return {**result, **runtime, "available": bool(exe), "hotkey": hotkey,
                "targetTitle": source["title"] if source else "",
                "profileName": profile.get("values", {}).get("Title") if profile and not profile["default"] else "默认档案",
                "profileIndex": profile.get("index"), "revision": editor.get("revision"),
                "preferences": profile.get("values", {}), "choices": choices,
                "profiles": [{"index": p["index"], "label": "默认档案" if p["default"] else p["title"],
                              "values": p["values"]} for p in profiles]}

    async def action(self, engine, action):
        if engine not in ("magpie", "lossless") or action not in ("start", "stop", "cancel"):
            raise ValueError("缩放操作无效")
        if action == "cancel":
            if self.pending == engine and self.deadline > time.monotonic():
                await self.close()
            return {"ok": True}
        if self.task and not self.task.done():
            raise RuntimeError("正在处理缩放，请稍候")
        self.error.pop(engine, None)
        self.pending = engine
        self.deadline = time.monotonic() + 5 if action == "start" else 0
        self.task = asyncio.create_task(self._run(engine, action))
        return {"ok": True}

    def mapping(self, text):
        key, modifiers = self.parse_hotkey(text)
        names = {"Control": "LCtrl", "Alt": "LAlt", "Shift": "LShift", "Windows": "LWin",
                 "Escape": "Esc", "Return": "Enter", "Back": "Backspace",
                 "OemPlus": "Equals", "OemMinus": "Minus", "OemComma": "Comma", "OemPeriod": "Period",
                 "OemQuestion": "Slash", "OemSemicolon": "Semicolon", "OemQuotes": "Quote",
                 "OemOpenBrackets": "LeftBracket", "OemCloseBrackets": "RightBracket",
                 "OemPipe": "Backslash", "OemTilde": "Grave",
                 "Add": "NumAdd", "Subtract": "NumSubtract", "Multiply": "NumMultiply",
                 "Divide": "NumDivide", "Decimal": "NumDecimal"}
        if key.startswith("NumPad"):
            key = "Num" + key[-1]
        elif len(key) == 2 and key[0] == "D" and key[1].isdigit():
            key = key[1]
        keys = [names[m] for m in modifiers.split() if m != "None"] + [names.get(key, key)]
        return self.compile_command({"type": "keyboard", "keys": keys})

    async def _run(self, engine, action):
        try:
            if engine == "magpie" and action == "stop":
                await self.magpie.stop()
                return
            exe = await asyncio.to_thread(self.lossless.exe)
            runtime = await self.lossless_state(exe)
            if action == "start":
                if engine == "magpie" and runtime.get("active"):
                    raise RuntimeError("小黄鸭正在缩放，请先停止")
                if engine == "lossless" and self.magpie.view()["enabled"]:
                    raise RuntimeError("Magpie 正在缩放，请先停止")
                if engine == "lossless":
                    if not exe:
                        raise RuntimeError("请先在工具箱设置中配置小黄鸭")
                    if runtime.get("active"):
                        return
                    if not runtime.get("running"):
                        await asyncio.to_thread(self.start_app, exe)
                await asyncio.sleep(max(0, self.deadline - time.monotonic()))
            if engine == "magpie":
                if (await self.lossless_state(exe)).get("active"):
                    raise RuntimeError("小黄鸭正在缩放，请先停止")
                source = self._foreground()
                await self.magpie.apply({**self.magpie.game_prefs(source["path"]), "target": source["hwnd"]}, persist=False)
                return
            await self._lossless_scale(exe, action)
        except (OSError, ValueError, RuntimeError, KeyError) as error:
            self.error[engine] = str(error)
            logging.info("Game Bar %s %s: %s", engine, action, error)
        finally:
            self.pending, self.deadline = "", 0

    def _foreground(self):
        source = self.magpie.windows.describe(self.magpie.foreground())
        if not source or os.path.basename(source["path"]).casefold() in {
            "gamebar.exe", "gamebarftserver.exe", "xboxgamebarwidgets.exe",
            "clawwidget.exe", "clawlibrary.exe", "losslessscaling.exe"
        }:
            raise RuntimeError("倒数结束时没有可缩放的前台窗口，请关闭 Game Bar 并切回游戏")
        return source

    async def _lossless_scale(self, exe, action):
        if not exe:
            return
        for attempt in range(4):
            try:
                editor = await asyncio.to_thread(self.editor.read)
                break
            except (OSError, ValueError):
                if attempt == 3:
                    raise
                await asyncio.sleep(.1)
        mapping = self.mapping(editor.get("globalValues", {}).get("Hotkey", "Ctrl+Alt+S"))
        runtime = await self.lossless_state(exe)
        start = action == "start"
        if bool(runtime.get("active")) == start:
            return
        if not runtime.get("running"):
            raise RuntimeError("小黄鸭尚未准备好，请打开原版确认后重试")
        if start and self.magpie.view()["enabled"]:
            raise RuntimeError("Magpie 正在缩放，请先停止")
        source = self._foreground() if start else None
        if source:
            mapping["foreground"] = source["hwnd"]
        await self.call("mapped_input", mapping=mapping)
        for _ in range(20):
            await asyncio.sleep(.25)
            runtime = await self.lossless_state(exe)
            confirmed = runtime.get("outputClass") == "LosslessScaling" if start else not runtime.get("active")
            if confirmed:
                self.lossless_source = source
                self.lossless_output = self.output_key(runtime) if start else None
                return
        raise RuntimeError("小黄鸭未确认缩放状态，请检查原版快捷键和窗口模式")

    async def close(self):
        if self.task and not self.task.done():
            self.task.cancel()
            await asyncio.gather(self.task, return_exceptions=True)
