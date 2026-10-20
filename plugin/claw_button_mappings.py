"""Two independent special-button contexts. Never rewrites legacy Steam actions."""
import copy
import ctypes
from ctypes import wintypes

SOURCES = ("QS", "Claw")
SYSTEM_ACTIONS = {"gamebar", "library", "controlCenter", "steamMainMenu", "steamQuickAccess",
                  "steamLibrary", "magpieToggle", "keyboard", "screenshot", "volumeUp", "volumeDown", "mute"}
DEFAULT_GENERAL = {"QS": {"type": "system", "action": "gamebar"},
                   "Claw": {"type": "system", "action": "library"}}
LEGACY_ACTIONS = {"MainMenu": "steamMainMenu", "QuickAccess": "steamQuickAccess", "MagpieToggle": "magpieToggle"}


def validate_maps(value, validate_command, *, general=False):
    if not isinstance(value, dict) or set(value) - set(SOURCES):
        raise ValueError("专用按键映射只能包含快速设置键和 Claw 键")
    result = {}
    for source, command in value.items():
        command = validate_command(command)
        if command["type"] == "gamepad":
            raise ValueError("这两个专用键目前支持系统动作和键盘鼠标指令")
        result[source] = command
    # Hardware ownership covers both special buttons. A new general profile always
    # has an explicit command for each; {} means leave general ownership disabled.
    return {**copy.deepcopy(DEFAULT_GENERAL), **result} if general and result else result


def selected(settings, source, steam):
    if source not in SOURCES:
        raise ValueError("不认识的专用按键")
    if not steam:
        return copy.deepcopy(settings.get("buttonMappings", {}).get(source, DEFAULT_GENERAL[source]))
    if source in settings.get("steamBindings", {}):
        return copy.deepcopy(settings["steamBindings"][source])
    action = settings["qamAction" if source == "QS" else "msiAction"]
    return {"type": "system", "action": LEGACY_ACTIONS[action]} if action in LEGACY_ACTIONS else {"type": "none"}


def toolbox_route(action, event, steam_mode, context):
    target = "control-center" if action == "controlCenter" else "library"
    eligible = (event.get("source") == "Claw" and event.get("scope") == "general"
                and steam_mode != "BigPicture" and context.get("xboxFse") is False)
    if not eligible:
        return target
    return ("minimize-" if context.get("foregroundLibrary") else "restore-") + target


def toolbox_context():
    """Capture foreground BEFORE protocol activation can move focus to the library."""
    result = {"xboxFse": None, "foregroundLibrary": False}
    try:
        query = ctypes.WinDLL("api-ms-win-gaming-experience-l1-1-0.dll").IsGamingFullScreenExperienceActive
        query.argtypes, query.restype = [], wintypes.BOOL
        result["xboxFse"] = bool(query())
    except (OSError, AttributeError):
        pass  # Unknown platform state keeps the existing open-only behavior.
    try:
        user, kernel = ctypes.WinDLL("user32", use_last_error=True), ctypes.WinDLL("kernel32", use_last_error=True)
        user.GetForegroundWindow.argtypes, user.GetForegroundWindow.restype = [], wintypes.HWND
        user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        user.GetWindowThreadProcessId.restype = wintypes.DWORD
        kernel.OpenProcess.argtypes, kernel.OpenProcess.restype = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE
        kernel.CloseHandle.argtypes, kernel.CloseHandle.restype = [wintypes.HANDLE], wintypes.BOOL
        kernel.GetPackageFamilyName.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.UINT), wintypes.LPWSTR]
        kernel.GetPackageFamilyName.restype = wintypes.LONG
        pid = wintypes.DWORD()
        if not user.GetWindowThreadProcessId(user.GetForegroundWindow(), ctypes.byref(pid)):
            return result
        handle = kernel.OpenProcess(0x1000, False, pid.value)
        if not handle:
            return result
        try:
            size = wintypes.UINT()
            if kernel.GetPackageFamilyName(handle, ctypes.byref(size), None) == 122 and 0 < size.value < 1024:
                family = ctypes.create_unicode_buffer(size.value)
                if kernel.GetPackageFamilyName(handle, ctypes.byref(size), family) == 0:
                    result["foregroundLibrary"] = family.value.startswith("ClawToolbox.Library_")
        finally:
            kernel.CloseHandle(handle)
    except (OSError, AttributeError):
        pass
    return result
