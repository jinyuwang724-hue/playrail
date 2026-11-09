"""Two independent special-button contexts. Never rewrites legacy Steam actions."""
import copy
import ctypes
from ctypes import wintypes

SOURCES = ("QS", "Claw")
SYSTEM_ACTIONS = {"gamebar", "library", "controlCenter", "steamMainMenu", "steamQuickAccess",
                  "steamLibrary", "magpieToggle", "keyboard", "screenshot", "steamScreenshot", "volumeUp", "volumeDown", "mute"}
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


def desktop_quick_settings_hold(event):
    if not event or event.get("type") != "mapped_action" or event.get("source") != "QS" \
            or event.get("scope") != "general" or event.get("gameMode") is True:
        return False
    command = event.get("command")
    if not isinstance(command, dict) or command.get("type") != "system" or command.get("action") != "gamebar":
        return False
    # Older routers labeled the keyboard copy "short" and left the WMI copy
    # unlabeled. A physical desktop tap uses desktop_gamebar_input instead.
    return event.get("gesture") in (None, "short", "long")


def toolbox_route(action, event, steam_mode, context):
    target = "control-center" if action == "controlCenter" else "library"
    eligible = event.get("source") == "Claw" and event.get("scope") in ("general", "steam")
    if not eligible:
        return target
    return ("minimize-" if context.get("foregroundLibrary") else "restore-") + target


WINDOW_MESSAGE = "ClawToolbox.Library.Activation.v1"
WINDOW_ROUTES = ("library", "control-center", "restore-library", "restore-control-center",
                 "minimize-library", "minimize-control-center")


def _library_window():
    """Find the actual library HWND, including its minimized/hidden window."""
    user = ctypes.WinDLL("user32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user.GetWindowThreadProcessId.restype = wintypes.DWORD
    user.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    kernel.OpenProcess.argtypes, kernel.OpenProcess.restype = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.GetPackageFamilyName.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.UINT), wintypes.LPWSTR]
    kernel.GetPackageFamilyName.restype = wintypes.LONG
    found = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def visit(window, _):
        title = ctypes.create_unicode_buffer(256)
        user.GetWindowTextW(window, title, len(title))
        if title.value != "Claw 游戏库":
            return True
        pid = wintypes.DWORD()
        user.GetWindowThreadProcessId(window, ctypes.byref(pid))
        handle = kernel.OpenProcess(0x1000, False, pid.value)
        if handle:
            try:
                size = wintypes.UINT(256)
                family = ctypes.create_unicode_buffer(size.value)
                if kernel.GetPackageFamilyName(handle, ctypes.byref(size), family) == 0 and family.value == "ClawToolbox.Library_ba3r68zr466za":
                    found.append((int(window), pid.value))
                    return False
            finally:
                kernel.CloseHandle(handle)
        return True
    user.EnumWindows.argtypes, user.EnumWindows.restype = [callback_type, wintypes.LPARAM], wintypes.BOOL
    user.EnumWindows(callback_type(visit), 0)
    return found[0] if found else None


def activate_toolbox(route, context):
    """Fast path avoids shell activation changing foreground before the toggle."""
    import os
    if route not in WINDOW_ROUTES:
        raise ValueError("Unknown library route")
    target = _library_window()
    if target:
        user = ctypes.WinDLL("user32", use_last_error=True)
        user.AllowSetForegroundWindow.argtypes = [wintypes.DWORD]
        user.RegisterWindowMessageW.argtypes, user.RegisterWindowMessageW.restype = [wintypes.LPCWSTR], wintypes.UINT
        user.GetPropW.argtypes, user.GetPropW.restype = [wintypes.HWND, wintypes.LPCWSTR], wintypes.HANDLE
        user.PostMessageW.argtypes, user.PostMessageW.restype = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM], wintypes.BOOL
        user.AllowSetForegroundWindow(target[1])
        message = user.RegisterWindowMessageW(WINDOW_MESSAGE)
        if message and user.GetPropW(target[0], WINDOW_MESSAGE) and user.PostMessageW(target[0], message, WINDOW_ROUTES.index(route) + 1, context.get("foregroundWindow", 0)):
            return
    # Cold start and compatibility with older installations retain the protocol.
    os.startfile("claw-library://" + route + "?returnWindow=" + str(context.get("foregroundWindow", 0)))


def xbox_fse_active():
    try:
        query = ctypes.WinDLL("api-ms-win-gaming-experience-l1-1-0.dll").IsGamingFullScreenExperienceActive
        query.argtypes, query.restype = [], wintypes.BOOL
        return bool(query())
    except (OSError, AttributeError):
        return None


def toolbox_context():
    """Capture foreground BEFORE protocol activation can move focus to the library."""
    result = {"xboxFse": xbox_fse_active(), "foregroundLibrary": False, "foregroundWindow": 0}
    try:
        user, kernel = ctypes.WinDLL("user32", use_last_error=True), ctypes.WinDLL("kernel32", use_last_error=True)
        user.GetForegroundWindow.argtypes, user.GetForegroundWindow.restype = [], wintypes.HWND
        user.IsIconic.argtypes, user.IsIconic.restype = [wintypes.HWND], wintypes.BOOL
        user.IsWindowVisible.argtypes, user.IsWindowVisible.restype = [wintypes.HWND], wintypes.BOOL
        user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        user.GetWindowThreadProcessId.restype = wintypes.DWORD
        kernel.OpenProcess.argtypes, kernel.OpenProcess.restype = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD], wintypes.HANDLE
        kernel.CloseHandle.argtypes, kernel.CloseHandle.restype = [wintypes.HANDLE], wintypes.BOOL
        kernel.GetPackageFamilyName.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.UINT), wintypes.LPWSTR]
        kernel.GetPackageFamilyName.restype = wintypes.LONG
        pid = wintypes.DWORD()
        foreground = user.GetForegroundWindow()
        result["foregroundWindow"] = int(foreground or 0)
        # Windows can briefly report the old foreground HWND after minimizing.
        # Such a window must use the restore route and preserve its current page.
        if not user.IsWindowVisible(foreground) or user.IsIconic(foreground):
            return result
        if not user.GetWindowThreadProcessId(foreground, ctypes.byref(pid)):
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
