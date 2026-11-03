"""Read the production RPC state; optionally send the same registered route as the router."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import socket
import sys
import time

HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox'
OUT = Path(__file__).resolve().parents[1] / 'artifacts/overlay-focus-fix-191'

def call(method, *args):
    ep = json.loads((HOME / 'agent.json').read_text())
    with socket.create_connection(('127.0.0.1', ep['port']), 30) as sock:
        f = sock.makefile('rwb')
        f.write((json.dumps({'hello':ep['token'],'client':'overlay-probe'})+'\n').encode()); f.flush(); f.readline()
        f.write((json.dumps({'id':1,'method':method,'args':list(args)})+'\n').encode()); f.flush()
        while True:
            msg = json.loads(f.readline())
            if msg.get('id') == 1:
                if 'error' in msg: raise RuntimeError(msg['error'])
                return msg.get('result')

user = ctypes.WinDLL('user32', use_last_error=True)
user.GetForegroundWindow.restype = wintypes.HWND
user.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
user.GetWindowLongPtrW.restype = ctypes.c_ssize_t
user.GetPropW.argtypes = [wintypes.HWND, wintypes.LPCWSTR]
user.GetPropW.restype = wintypes.HANDLE
user.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]

def library():
    found = []
    cb = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    @cb
    def visit(hwnd, data):
        if user.GetPropW(hwnd, 'ClawToolbox.Library.Activation.v1'): found.append(hwnd)
        return True
    user.EnumWindows(visit, 0)
    if len(found) != 1: raise RuntimeError('Expected one library window: '+str(found))
    return found[0]

def state():
    hwnd = library()
    return {'foreground':user.GetForegroundWindow(), 'library':hwnd,
            'visible':bool(user.IsWindowVisible(hwnd)),
            'noActivate':bool(user.GetWindowLongPtrW(hwnd, -20)&0x08000000),
            'topmost':bool(user.GetWindowLongPtrW(hwnd, -20)&8),
            'input':call('get_input_layer_state'), 'widget':call('widget_state')}

if __name__ == '__main__':
    if len(sys.argv)>1 and sys.argv[1] in ('quick','library','hide','hide-quick'):
        route={'quick':2,'library':1,'hide':5,'hide-quick':6}[sys.argv[1]]
        hwnd=library(); fg=user.GetForegroundWindow()
        msg=user.RegisterWindowMessageW('ClawToolbox.Library.Activation.v1')
        if not user.PostMessageW(hwnd,msg,route,fg): raise ctypes.WinError(ctypes.get_last_error())
        time.sleep(1.5)
    result=state()
    path=OUT / ((sys.argv[1] if len(sys.argv)>1 else 'state')+'.json')
    path.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    engine = result['input'].get('engine') or {}
    monitor = result['widget'].get('power',{}).get('osd',{}).get('monitor',{})
    sample = monitor.get('sample',{})
    print(json.dumps({'foreground':result['foreground'],'library':result['library'],'noActivate':result['noActivate'],
                      'topmost':result['topmost'],'visible':result['visible'],
                      'inputPhase':result['input']['phase'],'held':engine.get('overlayHeld'),
                      'gameFps':sample.get('fps'),'frameCount':sample.get('frameCount'),
                      'gamePid':result['widget'].get('system',{}).get('game',{}).get('pid')},ensure_ascii=True))
