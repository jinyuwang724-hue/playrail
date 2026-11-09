"""Lossless Scaling's native Settings.xml editor. No binaries, licence checks or copied engine.

The schema follows the installed 3.2.2 UI/Profile model. Unknown XML is retained. Writes are
optimistic (whole-file revision), backed up and atomic. The service owns stopping/reloading LS.
"""
import copy
import ctypes
from ctypes import wintypes as W
import hashlib
import io
import math
import os
from pathlib import Path
import re
import tempfile
import time
import xml.etree.ElementTree as ET
import winreg


def toggle(key, title, default=False, **extra):
    return dict(key=key, title=title, kind="toggle", default=default, **extra)


def number(key, title, default, low, high, step=1, integer=True, **extra):
    return dict(key=key, title=title, kind="number", default=default, min=low, max=high, step=step, integer=integer, **extra)


def pick(key, title, default, options, **extra):
    return dict(key=key, title=title, kind="pick", default=default,
                choices=[dict(value=v, label=n) for v, n in options], **extra)


def group(title, fields):
    return dict(title=title, fields=fields)


PROFILE_SCHEMA = [
    group("缩放", [
        pick("ScalingType", "缩放算法", "Off", [("Off", "关闭"), ("LS1", "LS1"), ("FSR", "AMD FSR"), ("NIS", "NVIDIA NIS"),
             ("SGSR", "SGSR"), ("BicubicCAS", "Bicubic CAS"), ("Anime4K", "Anime4K"), ("XBR", "xBR"), ("SharpBilinear", "锐化双线性"), ("Integer", "整数缩放"), ("Nearest", "最近邻")]),
        pick("ScalingMode", "缩放模式", "Auto", [("Auto", "自动"), ("Custom", "自定义")]),
        pick("ScalingFitMode", "画面适配", "AspectRatio", [("AspectRatio", "保持宽高比"), ("Fullscreen", "全屏拉伸")]),
        number("ScaleFactor", "缩放倍数", 1.5, 1, 10, .1, False, when={"ScalingMode": ["Custom"]}),
        toggle("ResizeBeforeScaling", "缩放前调整窗口尺寸", when={"ScalingMode": ["Custom"]}),
        pick("LS1Type", "LS1 模式", "BALANCED", [("BALANCED", "均衡"), ("PERFORMANCE", "性能")], when={"ScalingType": ["LS1"]}),
        number("LS1Sharpness", "LS1 锐化强度", 1, 0, 4, when={"ScalingType": ["LS1"]}),
        pick("FSRType", "FSR 模式", "ORIGINAL", [("ORIGINAL", "原始"), ("OPTIMIZED", "优化")], when={"ScalingType": ["FSR"]}),
        number("Sharpness", "锐化强度", 5, 0, 10, when={"ScalingType": ["FSR", "NIS", "BicubicCAS"]}),
        pick("Anime4kType", "Anime4K 质量", "S", [(s, s) for s in ("S", "M", "L", "VL", "UL")], when={"ScalingType": ["Anime4K"]}),
        toggle("WindowedMode", "窗口化输出"),
    ]),
    group("帧生成", [
        pick("FrameGeneration", "帧生成类型", "Off", [("Off", "关闭"), ("LSFG3", "LSFG 3.1"), ("LSFG2", "LSFG 2.3")]),
        pick("LSFG3Mode1", "帧生成模式", "FIXED", [("FIXED", "固定倍率"), ("ADAPTIVE", "自适应")], when={"FrameGeneration": ["LSFG3"]}),
        number("LSFG3Multiplier", "帧生成倍率", 2, 1, 20, .1, False, when={"FrameGeneration": ["LSFG3"], "LSFG3Mode1": ["FIXED"]}),
        number("LSFG3Target", "目标帧率", 0, 0, 1000, 1, False, when={"FrameGeneration": ["LSFG3"], "LSFG3Mode1": ["ADAPTIVE"]}, zeroLabel="跟随屏幕"),
        pick("LSFG2Mode", "帧生成倍率", "X2", [("X2", "×2"), ("X3", "×3"), ("X4", "×4")], when={"FrameGeneration": ["LSFG2"]}),
        number("LSFGFlowScale", "光流比例（%）", 100, 25, 100, when={"FrameGeneration": ["LSFG3", "LSFG2"]}),
        pick("LSFGSize", "性能模式", "BALANCED", [("BALANCED", "关闭"), ("PERFORMANCE", "开启")], when={"FrameGeneration": ["LSFG3", "LSFG2"]}),
    ]),
    group("捕获与渲染", [
        pick("CaptureApi", "捕获 API", "DXGI", [("DXGI", "DXGI"), ("WGC", "WGC")]),
        number("QueueTarget", "目标帧队列", 1, 0, 2),
        pick("SyncMode", "同步模式", "DEFAULT", [("DEFAULT", "默认"), ("OFF", "关闭（允许撕裂）"), ("VSYNC1", "垂直同步"), ("VSYNC2", "垂直同步 1/2"), ("VSYNC3", "垂直同步 1/3"), ("VSYNC4", "垂直同步 1/4")]),
        number("MaxFrameLatency", "最大帧延迟数", 3, 1, 16),
        toggle("GsyncSupport", "G-Sync 支持", True), toggle("HdrSupport", "HDR 支持"), toggle("DrawFps", "显示帧率", True),
    ]),
    group("光标", [toggle("ClipCursor", "限制光标在画面内", True), toggle("AdjustCursorSpeed", "调整光标速度"), toggle("HideCursor", "隐藏光标"), toggle("ScaleCursor", "缩放光标")]),
    group("显卡与显示器", [
        number("PreferredGpuId", "首选显卡", 0, 0, 31, zeroLabel="自动"),
        number("OutputDisplayId", "输出显示器", 0, 0, 31, zeroLabel="自动"),
        toggle("MultiDisplayMode", "多显示器模式"),
    ]),
    group("输入裁剪", [toggle("CropInput", "裁剪输入画面"),
        *[number("CropInput" + side, label + "裁剪（像素）", 0, 0, 16384, when={"CropInput": [True]}) for side, label in [("Left", "左侧"), ("Top", "顶部"), ("Right", "右侧"), ("Bottom", "底部")]]]),
    group("行为", [toggle("AutoScale", "自动缩放"), number("AutoScaleDelay", "自动缩放延迟（秒）", 0, 0, 3600, when={"AutoScale": [True]}), toggle("VRS", "可变速率着色（旧版选项）")]),
]
LANGUAGES = [("System", "跟随系统"), ("ChineseSimplified", "简体中文"), ("ChineseTraditional", "繁體中文"), ("English", "English"),
    *[(x, x) for x in ("Arabic", "Bulgarian", "Croatian", "Czech", "Danish", "Dutch", "Finnish", "French", "Georgian", "German", "Greek", "Hebrew", "Hungarian", "Indonesian", "Italian", "Japanese", "Korean", "Lithuanian", "Macedonian", "Norwegian", "Persian", "Polish", "Portuguese", "PortugueseBrazilian", "Romanian", "Serbian", "Slovak", "Russian", "Spanish", "Swedish", "Thai", "TokiPona", "Turkish", "Ukrainian", "Vietnamese")]]
GLOBAL_SCHEMA = [group("应用设置", [
    dict(key="Hotkey", title="缩放快捷键", kind="hotkey", default="Ctrl+Alt+S"),
    toggle("StartAsAdmin", "以管理员身份运行"), toggle("StartAtWindowsStartup", "小黄鸭开机自启"),
    toggle("MinimizeToTray", "最小化时收到托盘"), toggle("CloseToTray", "关闭窗口时收到托盘"),
    pick("Language", "小黄鸭界面语言", "System", LANGUAGES),
    pick("Theme", "小黄鸭主题", "System", [("System", "跟随系统"), ("Light", "浅色"), ("Dark", "深色")]),
])]
PROFILE_FIELDS = {f["key"]: f for g in PROFILE_SCHEMA for f in g["fields"]}
GLOBAL_FIELDS = {f["key"]: f for g in GLOBAL_SCHEMA for f in g["fields"]}


def parse_hotkey(text):
    if not isinstance(text, str) or len(text) > 100 or any(ord(c) < 32 for c in text):
        raise ValueError("请输入快捷键，例如 Ctrl+Alt+S")
    aliases = {"ctrl": "Control", "control": "Control", "alt": "Alt", "shift": "Shift", "win": "Windows", "windows": "Windows"}
    parts = [s.strip() for s in text.split("+")]
    if any(not p for p in parts): raise ValueError("快捷键格式错误")
    modifiers, key = [], None
    special = {s.lower(): s for s in ("Space", "Enter", "Return", "Tab", "Escape", "Back", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Left", "Right", "Up", "Down", "OemPlus", "OemMinus", "OemComma", "OemPeriod", "OemQuestion", "OemSemicolon", "OemQuotes", "OemOpenBrackets", "OemCloseBrackets", "OemPipe", "OemTilde", "Add", "Subtract", "Multiply", "Divide", "Decimal")}
    for part in parts:
        if part.lower() in aliases:
            mod = aliases[part.lower()]
            if mod in modifiers: raise ValueError("快捷键修饰键重复")
            modifiers.append(mod)
        elif key is None:
            if re.fullmatch(r"[a-zA-Z]", part): key = part.upper()
            elif re.fullmatch(r"[0-9]", part): key = "D" + part
            elif re.fullmatch(r"(?i)F([1-9]|1\d|2[0-4])|D[0-9]|NumPad[0-9]", part): key = "NumPad" + part[-1] if part.lower().startswith("numpad") else part.upper()
            elif part.lower() in special: key = special[part.lower()]
            else: raise ValueError("这个快捷键不受小黄鸭支持")
        else: raise ValueError("快捷键只能包含一个主键")
    if key is None: raise ValueError("快捷键需要一个主键")
    return key, " ".join(m for m in ("Alt", "Control", "Shift", "Windows") if m in modifiers) or "None"


def hotkey_text(root):
    mods = set((root.findtext("HotkeyModifierKeys") or "Alt Control").replace(",", " ").split())
    return "+".join([label for mod, label in [("Control", "Ctrl"), ("Alt", "Alt"), ("Shift", "Shift"), ("Windows", "Win")] if mod in mods] + [root.findtext("Hotkey") or "S"])


def field_value(element, field):
    raw = element.findtext(field["key"])
    if raw is None: return field["default"]
    if field["kind"] == "toggle": return raw.lower() == "true"
    if field["kind"] == "number":
        try:
            value = float(raw)
            return int(value) if field["integer"] and math.isfinite(value) else value if math.isfinite(value) else field["default"]
        except ValueError: return field["default"]
    return raw


def validate_patch(value, fields, identity=False):
    if not isinstance(value, dict) or len(value) > len(fields) + 2: raise ValueError("小黄鸭设置格式错误")
    result = {}
    for key, v in value.items():
        if identity and key in ("Title", "Path"):
            if not isinstance(v, str) or len(v) > (200 if key == "Title" else 32767) or any(ord(c) < 32 for c in v): raise ValueError("配置名称或匹配路径格式错误")
            if key == "Title" and not v.strip(): raise ValueError("请填写配置名称")
            result[key] = v.strip()
            continue
        if key not in fields: raise ValueError("不支持的小黄鸭设置：" + str(key))
        f = fields[key]
        if f["kind"] == "toggle":
            if type(v) is not bool: raise ValueError(f["title"] + "需要开关值")
        elif f["kind"] == "number":
            if type(v) not in (int, float) or not math.isfinite(v) or not f["min"] <= v <= f["max"] or f["integer"] and v != int(v):
                raise ValueError(f"{f['title']} 需在 {f['min']}–{f['max']} 之间" + ("，且为整数" if f["integer"] else ""))
            if f["integer"]: v = int(v)
        elif f["kind"] == "pick":
            if type(v) is not str or v not in [o["value"] for o in f["choices"]]: raise ValueError(f["title"] + "选项无效")
        elif f["kind"] == "hotkey": parse_hotkey(v)
        result[key] = v
    return result


def set_text(element, key, value):
    node = element.find(key)
    if node is None: node = ET.SubElement(element, key)
    node.text = ("true" if value else "false") if type(value) is bool else str(value)


def device_choices():
    """DXGI's native adapter/output ordering, with the same leading Auto entry as LS."""
    gpu, outputs = [dict(value=0, label="自动")], [dict(value=0, label="自动")]
    class Guid(ctypes.Structure):
        _fields_ = [("a", W.DWORD), ("b", W.WORD), ("c", W.WORD), ("d", ctypes.c_ubyte * 8)]
    class Luid(ctypes.Structure): _fields_ = [("low", W.DWORD), ("high", W.LONG)]
    class AdapterDesc(ctypes.Structure):
        _fields_ = [("name", W.WCHAR * 128), ("vendor", W.UINT), ("device", W.UINT), ("subsystem", W.UINT),
                    ("revision", W.UINT), ("video", ctypes.c_size_t), ("system", ctypes.c_size_t), ("shared", ctypes.c_size_t), ("luid", Luid), ("flags", W.UINT)]
    class OutputDesc(ctypes.Structure):
        _fields_ = [("name", W.WCHAR * 32), ("rect", W.RECT), ("attached", W.BOOL), ("rotation", W.UINT), ("monitor", W.HANDLE)]
    def method(pointer, index, result, *args):
        table = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        return ctypes.WINFUNCTYPE(result, ctypes.c_void_p, *args)(table[index])
    def release(pointer): method(pointer, 2, W.ULONG)(pointer)
    factory = ctypes.c_void_p()
    try:
        dxgi = ctypes.WinDLL("dxgi")
        create = dxgi.CreateDXGIFactory1
        create.argtypes, create.restype = [ctypes.POINTER(Guid), ctypes.POINTER(ctypes.c_void_p)], ctypes.c_long
        iid = Guid(0x770AAE78, 0xF26F, 0x4DBA, (ctypes.c_ubyte * 8)(0xA8,0x29,0x25,0x3C,0x83,0xD1,0xB3,0x87))
        if create(ctypes.byref(iid), ctypes.byref(factory)) < 0: return gpu, outputs
        for index in range(32):
            adapter = ctypes.c_void_p()
            if method(factory, 12, ctypes.c_long, W.UINT, ctypes.POINTER(ctypes.c_void_p))(factory, index, ctypes.byref(adapter)) < 0: break
            try:
                desc = AdapterDesc()
                if method(adapter, 10, ctypes.c_long, ctypes.POINTER(AdapterDesc))(adapter, ctypes.byref(desc)) < 0: continue
                if desc.flags & 2: continue  # Microsoft Basic Render Driver is not a physical GPU.
                gpu.append(dict(value=len(gpu), label=desc.name))
                for output_index in range(32):
                    output = ctypes.c_void_p()
                    if method(adapter, 7, ctypes.c_long, W.UINT, ctypes.POINTER(ctypes.c_void_p))(adapter, output_index, ctypes.byref(output)) < 0: break
                    try:
                        display = OutputDesc()
                        if method(output, 7, ctypes.c_long, ctypes.POINTER(OutputDesc))(output, ctypes.byref(display)) >= 0:
                            outputs.append(dict(value=len(outputs), label=f"显示器 {len(outputs)} · {display.rect.right-display.rect.left}×{display.rect.bottom-display.rect.top}"))
                    finally: release(output)
            finally: release(adapter)
    except (OSError, AttributeError): pass
    finally:
        if factory.value: release(factory)
    return gpu, outputs


class SettingsEditor:
    def __init__(self, data, path):
        self.data, self.path = Path(data), Path(path)

    def _load(self):
        try: raw = self.path.read_bytes()
        except FileNotFoundError: raw = None
        if raw is not None:
            if len(raw) > 4 * 1024 * 1024: raise ValueError("小黄鸭设置文件过大，未作修改")
            try: root = ET.fromstring(raw, parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True)))
            except ET.ParseError as e: raise ValueError("小黄鸭设置文件无法读取，未作修改") from e
            if root.tag != "Settings" or root.find("GameProfiles") is None: raise ValueError("不认识这个版本的小黄鸭配置，未作修改")
        else:
            root = ET.Element("Settings")
            for field in GLOBAL_FIELDS.values():
                if field["key"] != "Hotkey": set_text(root, field["key"], field["default"])
            set_text(root, "Hotkey", "S"); set_text(root, "HotkeyModifierKeys", "Alt Control")
            profile = ET.SubElement(ET.SubElement(root, "GameProfiles"), "Profile")
            set_text(profile, "Title", "默认")
            for field in PROFILE_FIELDS.values(): set_text(profile, field["key"], field["default"])
        profiles = root.find("GameProfiles").findall("Profile")
        if not profiles or not any(not (p.findtext("Path") or "").strip() for p in profiles): raise ValueError("小黄鸭缺少默认配置，未作修改")
        return raw, root, profiles

    @staticmethod
    def revision(raw): return hashlib.sha256(raw).hexdigest() if raw is not None else "missing"

    def read(self):
        raw, root, profiles = self._load()
        schema = copy.deepcopy(PROFILE_SCHEMA)
        gpu, displays = device_choices()
        for g in schema:
            for field in g["fields"]:
                if field["key"] == "PreferredGpuId": field["choices"] = gpu
                elif field["key"] == "OutputDisplayId": field["choices"] = displays
        return dict(revision=self.revision(raw), initialized=raw is not None,
                    globalValues={k: hotkey_text(root) if k == "Hotkey" else field_value(root, f) for k, f in GLOBAL_FIELDS.items()},
                    profiles=[dict(index=i, title=p.findtext("Title") or "未命名", path=p.findtext("Path") or "", default=not (p.findtext("Path") or "").strip(),
                                   values={**{k: field_value(p, f) for k, f in PROFILE_FIELDS.items()}, "Title": p.findtext("Title") or "未命名", "Path": p.findtext("Path") or ""}) for i, p in enumerate(profiles)],
                    profileSchema=schema, globalSchema=copy.deepcopy(GLOBAL_SCHEMA))

    def prepare(self, request):
        if not isinstance(request, dict) or set(request) != {"revision", "globalValues", "profiles", "remove"}: raise ValueError("小黄鸭设置请求格式错误")
        raw, root, profiles = self._load()
        if request["revision"] != self.revision(raw): raise ValueError("小黄鸭设置已在别处修改，请重新读取后再保存")
        globals_patch = validate_patch(request["globalValues"], GLOBAL_FIELDS)
        updates, remove = request["profiles"], request["remove"]
        if not isinstance(updates, list) or len(updates) > 200 or not isinstance(remove, list) or len(remove) > 200: raise ValueError("配置数量超出限制")
        if any(type(i) is not int or not 0 <= i < len(profiles) for i in remove) or len(set(remove)) != len(remove): raise ValueError("要删除的配置已不存在")
        if any(not (profiles[i].findtext("Path") or "").strip() for i in remove): raise ValueError("不能删除默认配置")
        holder, seen = root.find("GameProfiles"), set()
        for change in updates:
            if not isinstance(change, dict) or set(change) != {"index", "values"}: raise ValueError("游戏配置格式错误")
            index = change["index"]
            patch = validate_patch(change["values"], PROFILE_FIELDS, True)
            if index is None:
                if not patch.get("Title") or not patch.get("Path"): raise ValueError("新游戏配置需要名称与匹配程序")
                base = next(p for p in profiles if not (p.findtext("Path") or "").strip())
                target = copy.deepcopy(base); holder.append(target)
            else:
                if type(index) is not int or not 0 <= index < len(profiles) or index in seen or index in remove: raise ValueError("配置已不存在或重复")
                seen.add(index); target = profiles[index]
                if "Path" in patch and bool((target.findtext("Path") or "").strip()) != bool(patch["Path"]): raise ValueError("不能改变默认配置的身份")
            for key, value in patch.items(): set_text(target, key, value)
        for index in remove: holder.remove(profiles[index])
        for key, value in globals_patch.items():
            if key == "Hotkey":
                main, modifiers = parse_hotkey(value); set_text(root, "Hotkey", main); set_text(root, "HotkeyModifierKeys", modifiers)
            else: set_text(root, key, value)
        if raw:
            for _, (prefix, uri) in ET.iterparse(io.BytesIO(raw), events=("start-ns",)):
                if not re.fullmatch(r"ns\d+", prefix): ET.register_namespace(prefix, uri)
        ET.indent(root, space="  ")
        serialized = ET.tostring(root, encoding="unicode")
        source = raw.decode("utf-8-sig") if raw else '<Settings xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        head = re.search(r'<Settings\b[^>]*>', source)
        if head:
            for declaration in re.findall(r'xmlns(?::[\w.-]+)?="[^"]*"', head[0]):
                prefix = declaration.split("=")[0] + "="
                if prefix not in serialized.split(">", 1)[0]: serialized = serialized.replace("<Settings", "<Settings " + declaration, 1)
        serialized = '<?xml version="1.0" encoding="utf-8"?>\n' + serialized
        return dict(before=raw, after=serialized.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"), startup=globals_patch.get("StartAtWindowsStartup"))

    def atomic_write(self, raw):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd, name = tempfile.mkstemp(prefix=".claw-ls-", suffix=".tmp", dir=self.path.parent)
        try:
            with os.fdopen(fd, "wb") as stream: stream.write(raw); stream.flush(); os.fsync(stream.fileno())
            os.replace(name, self.path)
        finally:
            if os.path.exists(name): os.unlink(name)

    def commit(self, prepared, exe):
        try: current = self.path.read_bytes()
        except FileNotFoundError: current = None
        if current != prepared["before"]: raise ValueError("小黄鸭设置已在别处修改，请重新读取后再保存")
        if current is not None:
            backup = self.data / "lossless-backups"
            backup.mkdir(parents=True, exist_ok=True)
            (backup / (time.strftime("%Y%m%d-%H%M%S") + "-" + self.revision(current)[:12] + ".xml")).write_bytes(current)
        startup_before = None
        try:
            if prepared["startup"] is not None:
                startup_before = set_startup(exe, prepared["startup"])
            self.atomic_write(prepared["after"])
        except Exception:
            if startup_before is not None: restore_startup(startup_before)
            raise
        return self.read()


RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"


def set_startup(exe, enabled):
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
        try: old = winreg.QueryValueEx(key, "LosslessScaling")
        except FileNotFoundError: old = None
        if enabled: winreg.SetValueEx(key, "LosslessScaling", 0, winreg.REG_SZ, f'"{exe}" -StartMinimized')
        elif old is not None: winreg.DeleteValue(key, "LosslessScaling")
    return (old,)


def restore_startup(snapshot):
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
        if snapshot[0] is None:
            try: winreg.DeleteValue(key, "LosslessScaling")
            except FileNotFoundError: pass
        else: winreg.SetValueEx(key, "LosslessScaling", 0, snapshot[0][1], snapshot[0][0])
