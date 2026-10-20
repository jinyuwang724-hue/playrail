"""The control centre's 系统 page: battery and power-plan settings, wake devices, versions,
start-up, and the toolbox's own settings (backup, restore, defaults, log export).

Hardware and power-plan reads and writes go through the elevated ClawPower helper (call);
the slow lookups (Windows Update, the widget package) run once in the background and are cached,
so opening the page never waits on them."""
import asyncio
import json
import os
from pathlib import Path
import subprocess
import time
import winreg
import zipfile

TASK = "\\ClawToolbox\\Agent"
HOME = Path(os.environ.get("LOCALAPPDATA", ".")) / "ClawToolbox"
BACKUPS = Path(os.environ.get("USERPROFILE", ".")) / "Documents" / "ClawToolbox 备份"
# What "the toolbox's settings" means for backup, restore and defaults. Hardware restore journals
# (power-backup, fan-backup, restore.json, osd-*-backup, gamepad.json originals, hidhide-journal)
# are never touched: they are what puts the machine back the way it was.
CONFIG = ("settings.json", "power.json", "magpie.json", "magpie-games.json", "rumble.json", "scaling.json",
          "input-layer/settings.json", "input-layer/layouts")
HIBERNATE_STEPS = (0, 1800, 3600, 10800)
DISPLAY_STEPS = (180, 300, 600, 1800)
UPDATE_CACHE = 6 * 3600
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def _run(args, timeout=20):
    return subprocess.run(args, capture_output=True, creationflags=NO_WINDOW, timeout=timeout)


def _powershell(script, timeout=180):
    out = _run(["powershell", "-NoProfile", "-NonInteractive", "-Command",
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script], timeout)
    if out.returncode != 0:
        raise RuntimeError(out.stderr.decode("utf-8", "replace").strip()[:200] or "PowerShell 失败")
    return out.stdout.decode("utf-8", "replace").strip()


def _config_files(data):
    for name in CONFIG:
        path = data / name
        if path.is_dir():
            yield from (p for p in path.rglob("*.json") if p.is_file())
        elif path.is_file():
            yield path


class SystemControl:
    def __init__(self, call, data, root, battery):
        """call: the helper's command call. battery(): (percent, on charger) from the power module."""
        self.call, self.data, self.root, self.battery = call, Path(data), Path(root), battery
        self.wake_path = self.data / "wake-restore.json"
        self.updates = {"state": "idle"}  # idle / checking / done / error
        self.updates_at = 0.0
        self.widget_version = None
        self.lookup_task = None
        self.firmware = None
        self.restart_requested = False
        self.samples = []  # (time, remaining mWh) for the charge / discharge rate when the firmware gives none

    # ---------------- reading ----------------

    async def view(self, gamepad_firmware=None):
        system = await self.call("system_read")
        try:
            stats = await self.call("stats")
        except RuntimeError:
            stats = {}
        percent, charger = self.battery()
        live = {"percent": percent, "charger": charger, "watts": stats.get("batteryW"),
                "minutes": stats.get("batteryMinutes"), "chargeMinutes": stats.get("chargeMinutes")}
        remaining, full = stats.get("batteryRemaining"), stats.get("batteryFull")
        if not live["watts"] and remaining:
            rate = self._rate(remaining, charger)
            if rate is not None:
                live["watts"] = round(rate, 1)
                if rate < -0.3:
                    live["minutes"] = round(remaining / 1000 / -rate * 60)
                elif rate > 0.3 and full:
                    live["chargeMinutes"] = round(max(0, full - remaining) / 1000 / rate * 60)
        battery = {**(system.get("battery") or {}), **live}
        wake_restore = self._wake_restore()
        if gamepad_firmware and not self.firmware:
            try:
                self.firmware = (await gamepad_firmware()).get("firmware")
            except Exception:  # the pad may be busy or in desktop mode; try again next time
                pass
        if self.lookup_task is None or (self.lookup_task.done() and time.monotonic() - self.updates_at > UPDATE_CACHE):
            self.lookup_task = asyncio.create_task(self._lookups())
        version = json.loads((self.root / "plugin.json").read_text(encoding="utf-8"))["version"]
        return {
            "battery": battery, "batteryError": system.get("batteryError"),
            "chargeLimit": system.get("chargeLimit"), "chargeLimitError": system.get("chargeLimitError"),
            "power": system.get("power"), "powerError": system.get("powerError"),
            "wakeArmed": system.get("wakeArmed"), "wakeLocked": system.get("wakeArmed") == [],
            "wakeRestore": wake_restore, "wakeError": system.get("wakeError"),
            "device": system.get("device") or {},
            "versions": {"backend": version, "widget": self.widget_version, "padFirmware": self.firmware},
            "updates": self.updates,
            "autostart": await asyncio.to_thread(self._autostart),
            "homeApp": self._home_app(),
            "backups": self._backups(),
        }

    def _rate(self, remaining, charger):
        now = time.monotonic()
        if self.samples and self.samples[-1][2] != charger:
            self.samples = []
        if not self.samples or self.samples[-1][1] != remaining:
            self.samples.append((now, remaining, charger))
        self.samples = [s for s in self.samples if now - s[0] <= 180]
        (t0, r0, _), (t1, r1, _) = self.samples[0], self.samples[-1]
        return None if t1 - t0 < 15 or r1 == r0 else (r1 - r0) / 1000 / ((t1 - t0) / 3600)

    async def _lookups(self):
        """Windows Update and the widget's package version: both take seconds, so off the page's path."""
        self.updates = {**self.updates, "state": "checking"}
        try:
            self.widget_version = await asyncio.to_thread(_powershell,
                "(Get-AppxPackage ClawToolbox.GameBarWidget).Version", 60) or None
        except Exception:
            self.widget_version = None
        try:
            count = await asyncio.to_thread(_powershell,
                "$s=(New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher();"
                "$s.Online=$true;($s.Search(\"IsInstalled=0 and IsHidden=0 and Type='Software'\")).Updates.Count")
            self.updates = {"state": "done", "count": int(count), "checked": time.strftime("%H:%M")}
        except Exception as e:
            self.updates = {"state": "error", "error": str(e)[:120] or "检查失败"}
        self.updates_at = time.monotonic()

    def check_updates(self):
        if self.lookup_task is None or self.lookup_task.done():
            self.lookup_task = asyncio.create_task(self._lookups())
        return self.updates

    @staticmethod
    def _autostart():
        try:
            out = _run(["schtasks", "/query", "/tn", TASK, "/xml"], 10)
        except (OSError, subprocess.SubprocessError):
            return None
        if out.returncode != 0:
            return None
        xml = out.stdout.decode("utf-16", "replace") if out.stdout[:2] in (b"\xff\xfe", b"\xfe\xff") else out.stdout.decode("utf-8", "replace")
        settings = xml.split("<Settings>", 1)[-1].split("</Settings>", 1)[0]
        return "<Enabled>false</Enabled>" not in settings

    @staticmethod
    def _home_app():
        for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
            try:
                with winreg.OpenKey(hive, r"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration") as key:
                    return winreg.QueryValueEx(key, "GamingHomeApp")[0]
            except OSError:
                continue
        return None

    def _wake_restore(self):
        try:
            return json.loads(self.wake_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return []

    @staticmethod
    def _backups():
        try:
            files = sorted(BACKUPS.glob("claw-settings-*.zip"), key=lambda p: p.stat().st_mtime, reverse=True)
        except OSError:
            return []
        return [{"name": p.name, "time": time.strftime("%Y-%m-%d %H:%M", time.localtime(p.stat().st_mtime))} for p in files[:20]]

    # ---------------- power plan ----------------

    async def set_power_button(self, action):
        if action not in ("sleep", "hibernate", "shutdown"):
            raise ValueError("不支持的电源键动作")
        await self.call("power_button_set", action=action)

    async def set_hibernate_after(self, seconds):
        if seconds not in HIBERNATE_STEPS:
            raise ValueError("不支持的休眠时间")
        await self.call("hibernate_after_set", seconds=seconds)

    async def set_display_off(self, seconds):
        if seconds not in DISPLAY_STEPS:
            raise ValueError("不支持的关屏时间")
        await self.call("display_off_set", seconds=seconds)

    async def set_wake_lock(self, locked):
        if type(locked) is not bool:
            raise ValueError("唤醒开关格式错误")
        if locked:
            result = await self.call("wake_set", lock=True, restore=[])
            names = sorted(set(self._wake_restore()) | set(result.get("changed") or []))
            self.wake_path.write_text(json.dumps(names, ensure_ascii=False), encoding="utf-8")
        else:
            names = self._wake_restore()
            if not names:
                raise RuntimeError("没有记录被关掉的唤醒设备，需要在设备管理器里逐个打开")
            await self.call("wake_set", lock=False, restore=names)
            self.wake_path.unlink(missing_ok=True)

    # ---------------- start-up ----------------

    async def set_autostart(self, enabled):
        if type(enabled) is not bool:
            raise ValueError("开关格式错误")
        out = await asyncio.to_thread(_run, ["schtasks", "/change", "/tn", TASK, "/enable" if enabled else "/disable"])
        if out.returncode != 0:
            raise RuntimeError(out.stderr.decode("mbcs", "replace").strip() or "改不了登录任务")

    # ---------------- settings: backup, restore, defaults, logs ----------------

    def backup(self, tag=""):
        BACKUPS.mkdir(parents=True, exist_ok=True)
        path = BACKUPS / f"claw-settings-{time.strftime('%Y%m%d-%H%M%S')}{tag}.zip"
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
            for file in _config_files(self.data):
                z.write(file, file.relative_to(self.data).as_posix())
        return {"name": path.name, "path": str(path)}

    def restore(self, name):
        path = BACKUPS / name
        if type(name) is not str or "/" in name or "\\" in name or not name.startswith("claw-settings-") or not path.is_file():
            raise ValueError("找不到这份备份")
        allowed = set(CONFIG)
        with zipfile.ZipFile(path) as z:
            members = [m for m in z.namelist() if not m.endswith("/")]
            for m in members:
                if ".." in m.split("/") or not (m in allowed or any(m.startswith(c + "/") for c in allowed)):
                    raise ValueError("备份文件里有不认识的内容，没有还原")
            self.backup("-还原前")
            for m in members:
                target = self.data / m
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(z.read(m))
        self.restart()
        return {"restored": name}

    def reset(self):
        saved = self.backup("-恢复默认前")
        for file in list(_config_files(self.data)):
            file.unlink()
        self.restart()
        return {"backup": saved["name"]}

    def restart(self):
        """The agent restarts so every part reloads its settings. The new copy is started right away,
        detached; it waits on the single-instance mutex until this one has put the hardware back and let go.
        (The sign-in task cannot do it: while this copy still runs it refuses to start a second one.)"""
        current = json.loads((HOME / "current.json").read_text(encoding="utf-8"))
        subprocess.Popen([current["python"], current["agent"]], cwd=str(Path(current["agent"]).parent),
                         creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
                         | subprocess.CREATE_BREAKAWAY_FROM_JOB | NO_WINDOW,
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True)
        self.restart_requested = True

    def export_logs(self):
        desktop = Path(os.environ.get("USERPROFILE", ".")) / "Desktop"
        path = desktop / f"工具箱日志-{time.strftime('%Y%m%d-%H%M%S')}.zip"
        library = Path(os.environ.get("LOCALAPPDATA", ".")) / "Packages"
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
            for file in (HOME / "logs").glob("*.log*"):
                z.write(file, "agent/" + file.name)
            for file in library.glob("ClawToolbox.Library_*/LocalCache/logs/*.log"):
                z.write(file, "library/" + file.name)
            for file in self.data.glob("events.jsonl"):
                z.write(file, "agent/" + file.name)
        return {"path": str(path), "name": path.name}
