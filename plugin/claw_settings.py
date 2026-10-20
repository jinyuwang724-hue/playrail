"""The library's 设置 page: what lives in Windows rather than in the toolbox's own files.

- The full-screen experience home app (HKCU GamingConfiguration\\GamingHomeApp, what Settings writes).
- Start-up programs: Run entries and Startup-folder shortcuts, switched on and off the way Task
  Manager does it (Explorer\\StartupApproved). Machine-wide ones need the elevated helper.
- Vendor software the toolbox replaces (MSI Center M, ClawTweaks, Intel's assistant): the helper
  disables their services and tasks and closes them; nothing is uninstalled."""
import asyncio
import os
from pathlib import Path
import time
import winreg

OWN_HOME = "ClawToolbox.Library_ba3r68zr466za!App"
XBOX_HOME = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App"
GAMING = r"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration"
APPROVED = r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved"
# (location id, hive, Run key or Startup folder, StartupApproved sub-key)
LOCATIONS = (
    ("user", winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run", "Run"),
    ("machine", winreg.HKEY_LOCAL_MACHINE, r"Software\Microsoft\Windows\CurrentVersion\Run", "Run"),
    ("machine32", winreg.HKEY_LOCAL_MACHINE, r"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32"),
    ("userfolder", winreg.HKEY_CURRENT_USER, Path(os.environ.get("APPDATA", "")) / r"Microsoft\Windows\Start Menu\Programs\Startup", "StartupFolder"),
    ("machinefolder", winreg.HKEY_LOCAL_MACHINE, Path(os.environ.get("PROGRAMDATA", "")) / r"Microsoft\Windows\Start Menu\Programs\Startup", "StartupFolder"),
)
FRIENDLY = {
    "OneDrive": "OneDrive", "LosslessScaling": "Lossless Scaling", "nefarius_HidHide_Updater": "HidHide 更新检查",
    "DeckyLoader": "Decky（工具箱的 Steam 插件）", "SecurityHealth": "Windows 安全中心图标", "RtkAudUService": "Realtek 音频服务",
    "Intel Endurance Gaming": "Intel Endurance Gaming", "Intel® Graphics Software": "Intel 显卡控制中心",
}
# Entries the toolbox itself needs: shown, but not switchable from here.
PROTECTED = {"DeckyLoader": "工具箱需要", "Intel Endurance Gaming": "功耗的 Auto 模式要用"}


def _friendly(name):
    if name.startswith("MicrosoftEdgeAutoLaunch_"):
        return "Microsoft Edge 预启动"
    return FRIENDLY.get(name, name.removesuffix(".lnk"))


def _read_gaming(name):
    for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        try:
            with winreg.OpenKey(hive, GAMING) as key:
                return winreg.QueryValueEx(key, name)[0]
        except OSError:
            continue
    return None


def home_app():
    """全屏体验：开机是否进入（StartupToGamingHome）和主页应用（GamingHomeApp）。"""
    current = _read_gaming("GamingHomeApp")
    startup = _read_gaming("StartupToGamingHome")
    choices = [{"id": OWN_HOME, "name": "Claw 游戏库"}, {"id": XBOX_HOME, "name": "Xbox"}]
    if current and current not in (OWN_HOME, XBOX_HOME):
        family = current.split("!")[0].split("_")[0]
        choices.append({"id": current, "name": family.rsplit(".", 1)[-1]})
    return {"current": current, "startup": startup != 0, "choices": choices}


def set_home_app(aumid):
    """aumid = "off"：开机不进全屏体验（主页设置不动）；其余：开机进全屏体验，主页用它。
    Steam 大屏幕模式也是把主页设成 Claw 游戏库，由游戏库被当主页打开时转去 Steam 大屏幕（替代 AnyFSE）。"""
    if aumid != "off" and aumid not in [c["id"] for c in home_app()["choices"]]:
        raise ValueError("不认识的主页应用")
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, GAMING) as key:
        winreg.SetValueEx(key, "StartupToGamingHome", 0, winreg.REG_DWORD, 0 if aumid == "off" else 1)
        if aumid != "off":
            winreg.SetValueEx(key, "GamingHomeApp", 0, winreg.REG_SZ, aumid)
    return home_app()


def _approved(hive, sub, name):
    try:
        with winreg.OpenKey(hive, APPROVED + "\\" + sub) as key:
            data = winreg.QueryValueEx(key, name)[0]
            return not (isinstance(data, bytes) and data and data[0] & 1)  # odd first byte = off
    except OSError:
        return True


def startup_items():
    items = []
    for loc, hive, where, sub in LOCATIONS:
        names = []
        if isinstance(where, Path):
            try:
                names = [(f.name, str(f)) for f in where.iterdir() if f.suffix.lower() in (".lnk", ".exe", ".bat", ".cmd", ".url")]
            except OSError:
                pass
        else:
            try:
                with winreg.OpenKey(hive, where) as key:
                    for i in range(winreg.QueryInfoKey(key)[1]):
                        name, value, _ = winreg.EnumValue(key, i)
                        names.append((name, str(value)))
            except OSError:
                pass
        for name, command in names:
            items.append({"id": f"{loc}|{name}", "name": _friendly(name), "command": command,
                          "enabled": _approved(hive, sub, name), "machine": hive == winreg.HKEY_LOCAL_MACHINE,
                          "protected": PROTECTED.get(name)})
    return items


async def set_startup(call, item_id, enabled):
    if type(item_id) is not str or "|" not in item_id or type(enabled) is not bool:
        raise ValueError("自启项格式错误")
    loc, name = item_id.split("|", 1)
    match = next((l for l in LOCATIONS if l[0] == loc), None)
    if match is None or not any(i["id"] == item_id for i in startup_items()):
        raise ValueError("这个自启项已经不在了")
    if name in PROTECTED:
        raise ValueError(f"这一项{PROTECTED[name]}，不能在这里关")
    _, hive, _, sub = match
    if hive == winreg.HKEY_LOCAL_MACHINE:
        await call("startup_approve", key=sub, name=name, enabled=enabled)
    else:
        data = bytearray(12)
        data[0] = 2 if enabled else 3
        if not enabled:
            data[4:12] = int((time.time() + 11644473600) * 10_000_000).to_bytes(8, "little")
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, APPROVED + "\\" + sub) as key:
            winreg.SetValueEx(key, name, 0, winreg.REG_BINARY, bytes(data))
    return startup_items()


class SettingsControl:
    def __init__(self, call):
        self.call = call
        self.vendors, self.vendors_at, self.vendor_task = None, 0.0, None

    async def view(self):
        # Reading the vendors' services and tasks starts a few sc / schtasks processes: kept 15 s,
        # refreshed behind the page so it never waits on them.
        if (self.vendors is None or time.monotonic() - self.vendors_at > 15) and (self.vendor_task is None or self.vendor_task.done()):
            self.vendor_task = asyncio.create_task(self._read_vendors())
        if self.vendors is None:
            await asyncio.wait({self.vendor_task}, timeout=3)
        return {"home": await asyncio.to_thread(home_app), "startup": await asyncio.to_thread(startup_items),
                "vendors": self.vendors}

    async def _read_vendors(self):
        try:
            self.vendors = await self.call("vendor_apps")
        except RuntimeError:
            pass
        self.vendors_at = time.monotonic()

    async def set_vendor(self, app, disabled):
        if type(app) is not str or type(disabled) is not bool:
            raise ValueError("开关格式错误")
        self.vendors = await self.call("vendor_app_set", app=app, disabled=disabled)
        self.vendors_at = time.monotonic()
        return self.vendors
