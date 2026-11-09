"""Model-matched driver, BIOS and controller updates from MSI China.

The elevated helper installs signed INF packages through Windows PnPUtil.
BIOS and controller firmware use their own installers.
"""
import asyncio
import html
import json
import logging
import os
from pathlib import Path
import re
import subprocess
import time
from urllib.parse import quote, unquote, urlsplit, urlunsplit

MSI_PRODUCT = "Claw-8-EX-AI-Plus-CG3EMX"
MSI_API = "https://www.msi.cn/api/v1/product/support/panel?product={}&type={}"
MSI_DOWNLOAD = "download.msi.cn"
MSI_CATALOG = Path(__file__).with_name("claw_msi_cn_catalog.json")
MSI_KINDS = ("bios", "driver", "firmware")
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0"

# Representative hardware IDs on MSI Claw 8 EX (MS-1T91).
MSI_DRIVERS = {
    "Intel Graphics Driver": r"PCI\VEN_8086&DEV_B080",
    "Intel Bluetooth Driver": r"PCI\VEN_8086&DEV_E476",
    "Intel Wireless LAN Driver": r"PCI\VEN_8086&DEV_E440",
    "Intel NPU Driver": r"PCI\VEN_8086&DEV_B03E",
    "Intel Chipset Driver": r"PCI\VEN_8086&DEV_E422",
    "Intel Management Engine Driver": r"PCI\VEN_8086&DEV_E470",
    "Intel Serial IO Driver": r"PCI\VEN_8086&DEV_E450",
    "Intel Platform Monitoring Technology Driver": r"PCI\VEN_8086&DEV_B07D",
    "Intel Smart Sound Technology driver": r"PCI\VEN_8086&DEV_E428",
    "Intel Dynamic Tuning Driver": r"SWC\VID8086_DTT_0001",
    "Intel Game Assist Driver": r"SWC\VID8086_DTT_GA_1.0",
    "Intel PPM Provision Driver": r"SWC\VID8086_PPM_1008_1.0",
    "Intel IPF Driver": r"ACPI\VEN_INTC&DEV_10D9",
    "Intel Trusted Execution Technology Driver": r"ACPI\VEN_INTC&DEV_1025",
    "Intel HID Event Filter": r"ACPI\VEN_INTC&DEV_10CC",
    "DTS Driver": r"SWC\VEN_DTSI&AID_DTSI2",
    "IntelliGo APO Driver": r"SWC\VEN_IGO&CID_APO",
    "Goodix FingerPrinter Driver": r"USB\VID_27C6&PID_6090",
    "Realtek Card Reader Driver": r"PCI\VEN_10EC&DEV_5264",
    "Realtek Audio Driver": r"SOUNDWIRE\SDCA_FUNCTION_10&MAN_025D&FUNC_0721&TYPE_01",
    "Integrated Sensor Solution": r"PCI\VEN_8086&DEV_E445",
}


def version_key(text):
    """32.0.101.9150 → (32, 0, 101, 9150); E1T91IMS.10D → ('E1T91IMS', 0x10D); 0419 → (4, 19)."""
    text = (text or "").strip()
    m = re.fullmatch(r"([A-Z0-9]+)\.([0-9A-F]+)", text, re.I)
    if m and not text.replace(".", "").isdigit():
        return (m.group(1).upper(), int(m.group(2), 16))
    if re.fullmatch(r"\d{4}", text):  # pad firmware as the pad reports it
        return (int(text[:2]), int(text[2:]))
    return tuple(int(p) for p in re.findall(r"\d+", text))


def newer(latest, current):
    try:
        a, b = version_key(latest), version_key(current)
        return type(a[0]) is type(b[0]) and a > b if a and b else False
    except (TypeError, ValueError):
        return False


def _device(drivers, hwid):
    hwid = hwid.upper()
    for d in drivers:
        if d["hwid"].upper().startswith(hwid):
            return d
    return None


def domestic_package(url):
    uri = urlsplit(url)
    if uri.scheme != "https" or uri.netloc not in ("download.msi.com", MSI_DOWNLOAD):
        return None
    if not uri.path.lower().endswith(".zip"):
        return None
    return urlunsplit(("https", MSI_DOWNLOAD, quote(uri.path, safe="/%()"), "", ""))


def edge_path():
    for base in (os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramFiles")):
        if base and (Path(base) / "Microsoft" / "Edge" / "Application" / "msedge.exe").exists():
            return Path(base) / "Microsoft" / "Edge" / "Application" / "msedge.exe"
    raise RuntimeError("找不到 Edge，没法读微星官网")


def msi_fetch(kind, profile):
    """The MSI China support API is read through a headless Edge."""
    out = subprocess.run([str(edge_path()), "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                          "--no-proxy-server", "--disable-background-networking",
                          f"--user-data-dir={profile}", f"--user-agent={UA}", "--dump-dom", MSI_API.format(MSI_PRODUCT, kind)],
                         capture_output=True, timeout=20, creationflags=NO_WINDOW)
    page = out.stdout.decode("utf-8", "replace")
    m = re.search(r"<pre[^>]*>(.*)</pre>", page, re.S)
    if not m:
        raise RuntimeError("微星官网没有返回数据")
    data = json.loads(html.unescape(m.group(1)))
    if (data.get("status") or {}).get("code") != 200:
        raise RuntimeError("微星官网返回错误")
    items = []
    for group, rows in ((data.get("result") or {}).get("downloads") or {}).items():
        if isinstance(rows, list):
            items += [r for r in rows if isinstance(r, dict) and r.get("download_url")]
    return items


def msi_updates(rows_by_kind, drivers, bios, pad_firmware):
    found = []
    for kind, rows in rows_by_kind.items():
        for r in rows:
            title, latest = r.get("download_title") or "", r.get("download_version") or ""
            url = domestic_package(r["download_url"])
            if not url:
                continue
            sha = re.search(r"[0-9a-fA-F]{64}", r.get("download_sha256") or "")
            item = {"id": f"msi:{r.get('download_id')}", "vendor": "msi", "latest": latest, "date": r.get("download_release") or "",
                    "url": url, "size": r.get("download_size"), "sha256": sha.group(0) if sha else "", "kind": "zip", "silent": True}
            if kind == "bios":
                if bios and newer(latest, bios):
                    found.append({**item, "title": "BIOS", "current": bios, "run": "FLASHWIN.bat", "args": "",
                                  "confirm": True, "needsPower": True, "note": "更新完会自动重启"})
            elif kind == "firmware":
                if pad_firmware and newer(latest, pad_firmware):
                    exe = unquote(urlsplit(url).path.rsplit("/", 1)[-1])[:-4] + ".exe"
                    found.append({**item, "title": "手柄固件", "current": f"{int(pad_firmware[:2])}.{pad_firmware[2:]}",
                                  "run": exe, "args": "-s", "confirm": True, "needsPower": True, "note": "手柄设置会恢复默认"})
            elif title in MSI_DRIVERS:
                hwid = MSI_DRIVERS[title]
                d = _device(drivers, hwid)
                if d and newer(latest, d["version"]):
                    found.append({**item, "hwid": hwid.upper(), "title": title.replace("Driver", "驱动").replace("driver", "驱动"),
                                  "current": d["version"], "kind": "driver"})
    return found


# ---------------- the update list and the install queue ----------------

class Updates:
    def __init__(self, call, home, battery, firmware):
        """call: the helper. battery(): (percent, on charger). firmware(): the pad's firmware as the pad reports it ('0419')."""
        self.call, self.battery, self.firmware = call, battery, firmware
        self.profile = Path(home) / "edge-msi-cn"
        self.catalog_path = Path(home) / "msi-cn-catalog.json"
        self.install_lock = asyncio.Lock()
        self.state = {"state": "idle"}
        self.items = []
        self.results = {}  # id → done / failed: …
        self.queue = []
        self.active = None
        self.job = None
        self.check_task = None
        self.watch_task = None
        self.dry_run = False

    def view(self):
        return {**self.state, "items": [{**i, "result": self.results.get(i["id"])} for i in self.items],
                "active": self.active, "job": self.job, "queued": list(self.queue)}

    def check(self):
        if not self.active and not self.install_lock.locked() and (self.check_task is None or self.check_task.done()):
            self.check_task = asyncio.create_task(self._check())
        return self.view()

    def _msi_catalog(self):
        bundled = json.loads(MSI_CATALOG.read_text(encoding="utf-8"))
        try:
            cached = json.loads(self.catalog_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            cached = {}
        if not isinstance(cached, dict):
            cached = {}
        rows, fallback = {}, False
        for kind in MSI_KINDS:
            try:
                rows[kind] = msi_fetch(kind, self.profile)
                if not rows[kind]:
                    raise RuntimeError("目录没有更新数据")
            except (OSError, ValueError, subprocess.SubprocessError, RuntimeError) as error:
                logging.warning("MSI China %s catalogue: %s", kind, error)
                rows[kind] = cached.get(kind) or bundled[kind]
                fallback = True
        temp = self.catalog_path.with_suffix(".tmp")
        try:
            temp.write_text(json.dumps(rows, ensure_ascii=False), encoding="utf-8")
            os.replace(temp, self.catalog_path)
        except OSError:
            logging.warning("Could not save MSI China catalogue", exc_info=True)
        return rows, fallback

    async def _check(self):
        self.state = {**self.state, "state": "checking"}
        try:
            drivers = await self.call("pnp_drivers")
            device = (await self.call("system_read")).get("device") or {}
            if not str(device.get("model", "")).casefold().startswith("claw 8 ex ai+ cg3em"):
                raise RuntimeError("微星中国区目录暂未适配这台设备")
            rows, fallback = await asyncio.to_thread(self._msi_catalog)
            found = msi_updates(rows, drivers, device.get("bios") or "", await self.firmware())
        except (RuntimeError, OSError, ValueError) as error:
            self.state = {"state": "error", "error": str(error)}
            return
        by_target = {}
        for item in found:
            key = item.get("hwid", item["title"])
            if key not in by_target or newer(item["latest"], by_target[key]["latest"]):
                by_target[key] = item
        self.items = sorted(by_target.values(), key=lambda item: item["title"])
        self.state = {"state": "partial" if fallback else "done", "catalogCached": fallback,
                      "checked": time.strftime("%H:%M")}

    async def install(self, ids, dry_run=False):
        async with self.install_lock:
            if self.check_task and not self.check_task.done():
                raise RuntimeError("请等驱动检查完成")
            if self.active and self.dry_run != (dry_run is True):
                raise RuntimeError("已有另一种驱动安装任务在进行")
            return await self._enqueue(ids, dry_run)

    async def _enqueue(self, ids, dry_run):
        # dry_run: download, check and unpack only (for testing the path without installing)
        if type(ids) is not list or not ids or not all(type(i) is str for i in ids):
            raise ValueError("要更新的项目格式错误")
        known = {i["id"]: i for i in self.items}
        for i in ids:
            if i not in known:
                raise ValueError("这一项已经不在更新列表里，请重新检查")
            item = known[i]
            if item.get("needsPower"):
                percent, charger = self.battery()
                if not charger or (percent or 0) < 30:
                    raise RuntimeError(f"{item['title']}要插着电、电量 30 % 以上才能更新")
        self.dry_run = dry_run is True
        queued = [i for i in dict.fromkeys(ids) if i not in self.queue and i != self.active]
        self.queue.extend(queued)
        self.results.update({i: None for i in queued})
        if self.active is None:
            await self._next()
        if self.active and (self.watch_task is None or self.watch_task.done()):
            self.watch_task = asyncio.create_task(self._watch())
        return self.view()

    async def _next(self):
        known = {i["id"]: i for i in self.items}
        while self.queue:
            i = self.queue.pop(0)
            item = known.get(i)
            if not item:
                continue
            request = {k: item[k] for k in ("url", "kind", "size", "sha1", "sha256", "run", "args") if item.get(k) is not None}
            if self.dry_run:
                request["dryRun"] = True
            try:
                if item.get("hwid") and not self.dry_run and await self._driver_installed(item):
                    self.results[i] = "done"
                    continue
                self.job = await self.call("update_start", job=i, **request)
                self.active = i
                logging.info("Update %s started: %s", i, item["url"])
                return
            except RuntimeError as e:
                self.results[i] = f"failed: {e}"
        self.active = None

    async def _driver_installed(self, item):
        device = _device(await self.call("pnp_drivers"), item["hwid"])
        return bool(device and (device["version"] == item["latest"] or newer(device["version"], item["latest"])))

    async def _result(self, item, job):
        code = job.get("exitCode")
        finished = job["state"] == "done"
        if finished and job.get("dryRun"):
            return "tested"
        if finished and code in (3010, 1641):
            return "restart"
        if item.get("hwid") and finished:
            # PnPUtil can stage an INF without binding it, or apply the target before a
            # second INF returns an error. Check the version Windows is actually using.
            try:
                if await self._driver_installed(item):
                    return "done"
            except RuntimeError as error:
                return f"failed: 无法确认驱动版本（{error}）"
            if code == 0:
                return "failed: Windows 尚未加载目标版本"
        elif finished and code == 0:
            return "done"
        return f"failed: {job.get('error') or f'安装程序返回 {code}'}"

    async def _watch(self):
        while self.active:
            await asyncio.sleep(1.5)
            async with self.install_lock:
                try:
                    self.job = await self.call("update_status")
                except RuntimeError:
                    continue
                if self.job.get("state") in ("done", "failed"):
                    item = next(item for item in self.items if item["id"] == self.active)
                    self.results[self.active] = await self._result(item, self.job)
                    logging.info("Update %s ended: %s", self.active, self.results[self.active])
                    await self._next()
        self.check()
