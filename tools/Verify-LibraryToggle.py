"""Inspect the actual installed window or exercise the existing Claw mapping API."""
import ctypes
from ctypes import wintypes
import importlib.util
import json
import os
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('agent_deploy', Path(__file__).with_name('Deploy-ScalingSettings.py'))
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)
spec = importlib.util.spec_from_file_location('buttons', Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox/app/claw_button_mappings.py')
buttons = importlib.util.module_from_spec(spec)
spec.loader.exec_module(buttons)
user = ctypes.WinDLL('user32', use_last_error=True)
user.IsIconic.argtypes = user.IsWindowVisible.argtypes = [wintypes.HWND]
user.GetWindowLongPtrW.argtypes, user.GetWindowLongPtrW.restype = [wintypes.HWND, ctypes.c_int], ctypes.c_ssize_t
user.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user.GetPropW.argtypes, user.GetPropW.restype = [wintypes.HWND, wintypes.LPCWSTR], wintypes.HANDLE


def snapshot():
    context = buttons.toolbox_context()
    title = ctypes.create_unicode_buffer(256)
    user.GetWindowTextW(context['foregroundWindow'], title, len(title))
    window = buttons._library_window()
    state = {'foreground': context['foregroundWindow'], 'foregroundTitle': title.value, 'foregroundLibrary': context['foregroundLibrary']}
    if window:
        state.update(window=window[0], pid=window[1], visible=bool(user.IsWindowVisible(window[0])),
            iconic=bool(user.IsIconic(window[0])), topmost=bool(user.GetWindowLongPtrW(window[0], -20) & 8),
            directActivation=bool(user.GetPropW(window[0], buttons.WINDOW_MESSAGE)))
    return state


def main():
    result = {'before': snapshot()}
    if len(sys.argv) > 1 and sys.argv[1] == 'press':
        start = time.monotonic()
        result['dispatch'] = deploy.call('test_button_mapping', 'Claw', False)
        result['dispatchMs'] = round((time.monotonic() - start) * 1000, 1)
        if 'window' in result['before']:
            minimize = result['dispatch'].get('windowRoute', '').startswith('minimize-') and not result['before'].get('iconic')
            for _ in range(100):
                state = snapshot()
                done = (state.get('iconic') or state.get('visible') is False) if minimize else state.get('foregroundLibrary') and state.get('visible') and not state.get('iconic')
                if done:
                    result['windowChangeMs'] = round((time.monotonic() - start) * 1000, 1)
                    break
                time.sleep(.01)
        time.sleep(.5 if 'window' in result['before'] else 1.5)
    result['after'] = snapshot()
    print(json.dumps(result, ensure_ascii=True))
    with (ROOT / 'artifacts/library-toggle/live-verification.jsonl').open('a', encoding='utf-8') as out:
        out.write(json.dumps(result, ensure_ascii=False) + '\n')


if __name__ == '__main__':
    main()
