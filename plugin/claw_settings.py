"""The library's 设置 page: what lives in Windows rather than in the toolbox's own files.

- The full-screen experience home app (HKCU GamingConfiguration\\GamingHomeApp, what Settings writes).
- Start-up programs: Run entries and Startup-folder shortcuts, switched on and off the way Task
  Manager does it (Explorer\\StartupApproved). Machine-wide ones need the elevated helper.
- Vendor software the toolbox replaces (MSI Center M, ClawTweaks, Intel's assistant): the helper
  disables their services and tasks and closes them; nothing is uninstalled."""
import asyncio
import json
import os
from pathlib import Path
import re
import subprocess
import threading
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

_home_cache = None
_home_cache_at = 0.0
_home_lock = threading.Lock()
_HOME_QUERY = r'''
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$catalog = [Windows.ApplicationModel.AppExtensions.AppExtensionCatalog, Windows.ApplicationModel, ContentType=WindowsRuntime]::Open('windows.gamingApp')
$homes = @($catalog.FindAll() | ForEach-Object {
    $extension = $_
    if ($extension.Package.Status.VerifyIsOK()) {
        $manifest = [xml](Get-Content -LiteralPath (Join-Path $extension.Package.InstalledLocation.Path 'AppxManifest.xml') -Raw)
        if (@($manifest.Package.Capabilities.CustomCapability.Name) -contains 'Microsoft.appCategory.gamingHome_8wekyb3d8bbwe') {
            @{ id = $extension.AppInfo.AppUserModelId; name = $extension.DisplayName }
        }
    }
})
ConvertTo-Json -InputObject $homes -Compress
'''


def _gaming_apps(refresh=False):
    """Use the system extension catalog AND the gaming-home capability, as the picker does.

    Cache discovery only; read the selected home and startup flag live on every view.
    Never synthesize a selectable home just because its AUMID is in the registry.
    """
    global _home_cache, _home_cache_at
    with _home_lock:
        if not refresh and _home_cache is not None and time.monotonic() - _home_cache_at < 15:
            return [dict(c) for c in _home_cache]
        result = subprocess.run(
            [os.path.join(os.environ.get("SystemRoot", r"C:\Windows"),
                          r"System32\WindowsPowerShell\v1.0\powershell.exe"),
             "-NoProfile", "-NonInteractive", "-Command", _HOME_QUERY],
            capture_output=True, timeout=12, creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            raise RuntimeError("无法读取系统全屏主页列表，请稍后重试")
        homes = json.loads(result.stdout.decode("utf-8-sig"))
        if not isinstance(homes, list) or any(not isinstance(c, dict) or not c.get("id") or not c.get("name") for c in homes):
            raise RuntimeError("系统全屏主页列表格式错误")
        for c in homes:
            if c["id"] == OWN_HOME:
                c["name"] = "Claw 游戏库"
            elif c["id"] == XBOX_HOME:
                c["name"] = "Xbox"
        homes.sort(key=lambda c: (c["id"] != OWN_HOME, c["name"]))
        _home_cache, _home_cache_at = homes, time.monotonic()
        return [dict(c) for c in homes]


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
    configured = _read_gaming("GamingHomeApp")
    startup = _read_gaming("StartupToGamingHome")
    try:
        choices, error = _gaming_apps(), None
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
        choices, error = [], "无法读取系统全屏主页列表，请稍后重试"
    current = configured if any(c["id"] == configured for c in choices) else None
    if configured and current is None and error is None:
        error = "已配置的主页未注册为系统全屏主页，请更新对应应用后重新选择"
    return {"current": current, "configured": configured, "startup": startup != 0,
            "choices": choices, "error": error}


def open_gaming_home():
    """Open the configured home from a manual shortcut, independent of startup."""
    home = _read_gaming("GamingHomeApp") or XBOX_HOME
    if home == OWN_HOME:
        prefs = Path(os.environ["LOCALAPPDATA"]) / "Packages" / OWN_HOME.split("!")[0] / "LocalCache" / "ui.ini"
        try:
            steam = any(line.strip().casefold() == "fsesteam=true" for line in prefs.read_text(encoding="utf-8-sig").splitlines())
        except OSError:
            steam = False
        os.startfile("steam://open/bigpicture" if steam else "claw-library://library")
    else:
        if not isinstance(home, str) or not re.fullmatch(r"[\w.-]+![\w.-]+", home, re.ASCII):
            raise RuntimeError("系统全屏体验主页配置无效")
        explorer = Path(os.environ["WINDIR"]) / "explorer.exe"
        subprocess.Popen([str(explorer), "shell:AppsFolder\\" + home])


def set_home_app(aumid):
    """aumid = "off"：开机不进全屏体验（主页设置不动）；其余：开机进全屏体验，主页用它。
    Steam 大屏幕模式也是把主页设成 Claw 游戏库，由游戏库被当主页打开时转去 Steam 大屏幕（替代 AnyFSE）。"""
    if type(aumid) is not str:
        raise ValueError("主页应用格式错误")
    if aumid != "off" and aumid not in [c["id"] for c in _gaming_apps(refresh=True)]:
        raise ValueError("这个应用尚未注册为系统全屏主页，请更新应用后重试")
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
