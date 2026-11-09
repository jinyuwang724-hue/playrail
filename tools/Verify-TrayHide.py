"""Exercise the installed library's activation and tray handlers, recording real HWND state."""
import ctypes
from ctypes import wintypes
import json
from pathlib import Path
import runpy
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
verify = runpy.run_path(str(ROOT / 'tools/Verify-LibraryToggle.py'))
user = ctypes.WinDLL('user32', use_last_error=True)
shell = ctypes.WinDLL('shell32')
dwm = ctypes.WinDLL('dwmapi')


class IconId(ctypes.Structure):
    _fields_ = [('size', wintypes.DWORD), ('window', wintypes.HWND),
                ('id', wintypes.UINT), ('guid', ctypes.c_byte * 16)]


shell.Shell_NotifyIconGetRect.argtypes = [ctypes.POINTER(IconId), ctypes.POINTER(wintypes.RECT)]
shell.Shell_NotifyIconGetRect.restype = ctypes.c_long
dwm.DwmGetWindowAttribute.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
user.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user.PostMessageW.restype = wintypes.BOOL


def snapshot():
    state = verify['snapshot']()
    if 'window' not in state: return state
    identifier = IconId(size=ctypes.sizeof(IconId), window=state['window'], id=1)
    rect = wintypes.RECT()
    result = shell.Shell_NotifyIconGetRect(ctypes.byref(identifier), ctypes.byref(rect))
    state.update(trayIconResult=result, trayIconRect=[rect.left, rect.top, rect.right, rect.bottom])
    cloaked = wintypes.DWORD()
    if dwm.DwmGetWindowAttribute(state['window'], 14, ctypes.byref(cloaked), ctypes.sizeof(cloaked)) == 0:
        state['cloaked'] = cloaked.value
    return state


def main():
    action = sys.argv[1] if len(sys.argv) > 1 else 'state'
    report = {'action':action, 'before':snapshot()}
    if action == 'press':
        report['dispatch'] = verify['deploy'].call('test_button_mapping', 'Claw', False)
    elif action in ('system-minimize', 'tray-open'):
        window = report['before']['window']
        # Target the verified installed app's own Win32 message handler only.
        message, wp, lp = (0x112, 0xf020, 0) if action == 'system-minimize' else (0x8051, 0, 0x203)
        assert user.PostMessageW(window, message, wp, lp), 'App message rejected'
    elif action != 'state':
        raise ValueError(action)
    if action != 'state': time.sleep(1.2)
    report['after'] = snapshot()
    with (ROOT / 'artifacts/tray-hide/live-verification.jsonl').open('a', encoding='utf-8') as out:
        out.write(json.dumps(report, ensure_ascii=False) + '\n')
    print(json.dumps(report, ensure_ascii=True))


if __name__ == '__main__': main()
