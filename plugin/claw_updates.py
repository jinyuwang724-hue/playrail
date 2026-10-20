"""Driver, BIOS and pad-firmware updates straight from MSI and Intel, without MSI Center M or Intel's
Driver & Support Assistant.

Finding updates:
- Intel: the catalogue Intel's own assistant downloads (dsadata.intel.com/data/zh-CN, a zip holding
  software-configurations.json). Each package lists the hardware IDs it is for; a package applies when
  one of this machine's devices matches and its driver is older than the package.
- MSI: the msi.com support API for this model (BIOS, drivers, pad firmware). msi.com refuses anything
  that is not a real browser, so it is read through a headless Edge. MSI's API has no hardware IDs, so
  each driver title is tied to the device it updates here (taken from MSI Live Update's own data).
Installing: the elevated helper downloads from the vendor's server, checks size and hash, unpacks and
runs the vendor's installer silently (UpdateJob in native/ClawUpdate.cs). Several updates run one after
another; the agent keeps the queue going even when no page is open."""
import asyncio
import html
import io
import json
import logging
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.request
import zipfile

INTEL_CATALOG = "https://dsadata.intel.com/data/zh-CN"
MSI_PRODUCT = "Claw-8-EX-AI-Plus-CG3EMX"
MSI_API = "https://www.msi.com/api/v1/product/support/panel?product={}&type={}"
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0"

# MSI driver title → the hardware ID of the device it updates on this Claw 8 EX (SKU 1T91), and whether
# its install.bat takes /s. From MSI Live Update's StoredUpdates.json on this machine (2026-10-20).
# Titles not listed (Realtek audio extensions, sensor kit, Studio Effects, card reader) have no device
# to read a version from and are not offered.
MSI_DRIVERS = {
    "Intel Graphics Driver": ("PCI\\VEN_8086&DEV_B080", "/s"),
    "Intel Bluetooth Driver": ("PCI\\VEN_8086&DEV_E476", ""),
    "Intel Wireless LAN Driver": ("PCI\\VEN_8086&DEV_E440", "/s"),
    "Intel NPU Driver": ("PCI\\VEN_8086&DEV_B03E", "/s"),
    "Intel Chipset Driver": ("PCI\\VEN_8086&DEV_E422", "/s"),
    "Intel Management Engine Driver": ("PCI\\VEN_8086&DEV_E470", "/s"),
    "Intel Serial IO Driver": ("PCI\\VEN_8086&DEV_E450", ""),
    "Intel Platform Monitoring Technology Driver": ("PCI\\VEN_8086&DEV_B07D", "/s"),
    "Intel Smart Sound Technology driver": ("PCI\\VEN_8086&DEV_E428", "/s"),
    "Intel Dynamic Tuning Driver": ("SWC\\VID8086_DTT_0001", "/s"),
    "Intel Game Assist Driver": ("SWC\\VID8086_DTT_GA_1.0", "/s"),
    "Intel PPM Provision Driver": ("SWC\\VID8086_PPM_1008_1.0", "/s"),
    "Intel IPF Driver": ("ACPI\\VEN_INTC&DEV_10D9", ""),
    "Intel Trusted Execution Technology Driver": ("ACPI\\VEN_INTC&DEV_1025", ""),
    "Intel HID Event Filter": ("ACPI\\VEN_INTC&DEV_10CC", ""),
    "DTS Driver": ("SWC\\VEN_DTSI&AID_DTSI2", "/s"),
    "IntelliGo APO Driver": ("SWC\\VEN_IGO&CID_APO", ""),
    "Goodix FingerPrinter Driver": ("USB\\VID_27C6&PID_6090", ""),
}
# Silent switches of Intel's installers, by file name.
INTEL_SWITCHES = (("gfx_win_", "-s"), ("WiFi-", "-q"), ("BT-", "-q"), ("npu_win_", "-s"))
CHINESE = {"Graphics": "显卡驱动", "Network": "网络驱动", "Wireless": "无线网卡驱动", "Bluetooth": "蓝牙驱动", "Chipset": "芯片组驱动"}


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


# ---------------- Intel ----------------

def intel_catalog(url=INTEL_CATALOG):
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": UA}), timeout=60) as r:
        data = r.read()
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        return json.loads(z.read("software-configurations.json").decode("utf-8-sig"))


def intel_updates(catalog, drivers):
    best = {}
    for sw in catalog:
        if sw.get("IsBeta"):
            continue
        files = [f for f in sw.get("Files") or [] if f.get("Url", "").lower().endswith(".exe")
                 and any(o.startswith("windows-11") and o.endswith("-64") for o in f.get("OperatingSystems") or [])]
        if not files:
            continue
        for comp in sw.get("Components") or []:
            if comp.get("DetectionType") != "Default":
                continue
            # Some components carry no version of their own (the NPU driver): the package's version counts.
            version = comp.get("Version") or (sw.get("Version") or "").split(" ")[0]
            for value in comp.get("DetectionValues") or []:
                # "VEN_8086&DEV_E440&SUBSYS_*8086": * stands for any characters
                pattern = re.compile(".*".join(re.escape(part) for part in value.upper().split("*")))
                for d in drivers:
                    hw = d["hwid"].upper()
                    if not pattern.search(hw) or not newer(version, d["version"]):
                        continue
                    key = hw.split("&REV_")[0]
                    if key in best and not newer(version, best[key]["latest"]):
                        continue
                    f = files[0]
                    name = f["Url"].rsplit("/", 1)[-1]
                    switch = next((s for p, s in INTEL_SWITCHES if name.startswith(p)), "")
                    options = (sw.get("InstallOptions") or "").strip()
                    best[key] = {
                        "id": f"intel:{sw['Id']}", "vendor": "intel", "hwid": key,
                        "title": f"{_clean(d['name'])} {CHINESE.get(comp.get('Category'), '驱动')}",
                        "current": d["version"], "latest": version, "date": (sw.get("DisplayReleaseDate") or "")[:10],
                        "url": f["Url"], "size": f.get("Size"), "sha1": (f.get("Hash") or "") if len(f.get("Hash") or "") == 40 else "",
                        "kind": "exe", "args": " ".join(x for x in (switch, options) if x), "silent": bool(switch),
                    }
    return list(best.values())


def _clean(name):
    return re.sub(r"\s+", " ", (name or "").replace("®", "").replace("™", "").replace("(R)", "").replace("(TM)", "")).strip()


# ---------------- MSI ----------------

def edge_path():
    for base in (os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramFiles")):
        if base and (Path(base) / "Microsoft" / "Edge" / "Application" / "msedge.exe").exists():
            return Path(base) / "Microsoft" / "Edge" / "Application" / "msedge.exe"
    raise RuntimeError("找不到 Edge，没法读微星官网")


def msi_fetch(kind, profile):
    """msi.com answers only real browsers, so the JSON is read as a page by a headless Edge."""
    out = subprocess.run([str(edge_path()), "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                          f"--user-data-dir={profile}", f"--user-agent={UA}", "--dump-dom", MSI_API.format(MSI_PRODUCT, kind)],
                         capture_output=True, timeout=60, creationflags=NO_WINDOW)
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
            title, latest, url = r.get("download_title") or "", r.get("download_version") or "", r["download_url"]
            sha = re.search(r"[0-9a-fA-F]{64}", r.get("download_sha256") or "")
            item = {"id": f"msi:{r.get('download_id')}", "vendor": "msi", "latest": latest, "date": r.get("download_release") or "",
                    "url": url, "size": r.get("download_size"), "sha256": sha.group(0) if sha else "", "kind": "zip", "silent": True}
            if kind == "bios":
                if bios and newer(latest, bios):
                    found.append({**item, "title": "BIOS", "current": bios, "run": "FLASHWIN.bat", "args": "",
                                  "confirm": True, "needsPower": True, "note": "更新完会自动重启"})
            elif kind == "firmware":
                if pad_firmware and newer(latest, pad_firmware):
                    exe = url.rsplit("/", 1)[-1].replace(".zip", ".exe")
                    found.append({**item, "title": "手柄固件", "current": f"{int(pad_firmware[:2])}.{pad_firmware[2:]}",
                                  "run": exe, "args": "-s", "confirm": True, "needsPower": True, "note": "手柄设置会恢复默认"})
            elif title in MSI_DRIVERS:
                hwid, switch = MSI_DRIVERS[title]
                d = _device(drivers, hwid)
                if d and newer(latest, d["version"]):
                    found.append({**item, "hwid": hwid.upper(), "title": title.replace("Driver", "驱动").replace("driver", "驱动"),
                                  "current": d["version"], "run": "install.bat", "args": switch})
    return found


# ---------------- the update list and the install queue ----------------

class Updates:
    def __init__(self, call, home, battery, firmware):
        """call: the helper. battery(): (percent, on charger). firmware(): the pad's firmware as the pad reports it ('0419')."""
        self.call, self.battery, self.firmware = call, battery, firmware
        self.profile = Path(home) / "edge-msi"
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
        if self.check_task is None or self.check_task.done():
            self.check_task = asyncio.create_task(self._check())
        return self.view()

    async def _check(self):
        self.state = {**self.state, "state": "checking"}
        errors = []
        try:
            drivers = await self.call("pnp_drivers")
        except RuntimeError as e:
            self.state = {"state": "error", "error": f"读不到本机驱动：{e}"}
            return
        found = []
        try:
            found += intel_updates(await asyncio.to_thread(intel_catalog), drivers)
        except Exception as e:  # network, catalogue format
            errors.append(f"Intel：{e}")
            logging.warning("Intel catalogue: %s", e)
        try:
            rows = {}
            for kind in ("bios", "driver", "firmware"):
                rows[kind] = await asyncio.to_thread(msi_fetch, kind, self.profile)
            system = await self.call("system_read")
            bios = ((system.get("device") or {}).get("bios") or "")
            found += msi_updates(rows, drivers, bios, await self.firmware())
        except Exception as e:
            errors.append(f"微星：{e}")
            logging.warning("MSI catalogue: %s", e)
        # MSI and Intel can both offer a driver for the same device: keep the newer one (MSI's on a tie).
        by_device, rest = {}, []
        for item in found:
            key = item.get("hwid")
            if not key:
                rest.append(item)
            elif key not in by_device or newer(item["latest"], by_device[key]["latest"]) or (
                    item["vendor"] == "msi" and not newer(by_device[key]["latest"], item["latest"])):
                by_device[key] = item
        self.items = rest + sorted(by_device.values(), key=lambda i: i["title"])
        self.state = {"state": "done" if not errors else "partial", "checked": time.strftime("%H:%M"), "errors": errors}

    async def install(self, ids, dry_run=False):
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
        self.queue += [i for i in ids if i not in self.queue and i != self.active]
        self.results.update({i: None for i in ids})
        if self.active is None:
            await self._next()
        if self.watch_task is None or self.watch_task.done():
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
                self.job = await self.call("update_start", job=i, **request)
                self.active = i
                logging.info("Update %s started: %s", i, item["url"])
                return
            except RuntimeError as e:
                self.results[i] = f"failed: {e}"
        self.active = None

    async def _watch(self):
        """Polls the helper's job; starts the next queued update when one ends; re-checks at the end."""
        while self.active:
            await asyncio.sleep(1.5)
            try:
                self.job = await self.call("update_status")
            except RuntimeError:
                continue
            if self.job.get("state") in ("done", "failed"):
                code = self.job.get("exitCode")
                # Installers report 0 for done, 3010 / 1641 for done with a restart pending.
                ok = self.job["state"] == "done" and code in (0, 3010, 1641)
                self.results[self.active] = "done" if ok else f"failed: {self.job.get('error') or f'安装程序返回 {code}'}"
                if ok and code in (3010, 1641):
                    self.results[self.active] = "restart"
                if ok and self.job.get("dryRun"):
                    self.results[self.active] = "tested"
                logging.info("Update %s ended: %s", self.active, self.results[self.active])
                await self._next()
        await self._check()
