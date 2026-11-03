import ctypes,json,os,sys,time,importlib.util
from pathlib import Path
from ctypes import wintypes
ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'artifacts/special-keys-197'
s=importlib.util.spec_from_file_location('probe',ROOT/'tools/Probe-OverlayFocus.py');p=importlib.util.module_from_spec(s);s.loader.exec_module(p)
u=p.user
class Point(ctypes.Structure):_fields_=[('x',wintypes.LONG),('y',wintypes.LONG)]
class Monitor(ctypes.Structure):_fields_=[('size',wintypes.DWORD),('rect',wintypes.RECT),('work',wintypes.RECT),('flags',wintypes.DWORD)]
u.GetClientRect.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.RECT)]
u.GetWindowRect.argtypes=[wintypes.HWND,ctypes.POINTER(wintypes.RECT)]
u.ClientToScreen.argtypes=[wintypes.HWND,ctypes.POINTER(Point)]
u.MonitorFromWindow.argtypes=[wintypes.HWND,wintypes.DWORD];u.MonitorFromWindow.restype=wintypes.HANDLE
u.GetMonitorInfoW.argtypes=[wintypes.HANDLE,ctypes.POINTER(Monitor)]
def rect(r):return [r.left,r.top,r.right,r.bottom]
def geometry():
    hwnd=p.library();win=wintypes.RECT();client=wintypes.RECT();origin=Point();info=Monitor();info.size=ctypes.sizeof(info)
    u.GetWindowRect(hwnd,ctypes.byref(win));u.GetClientRect(hwnd,ctypes.byref(client));u.ClientToScreen(hwnd,ctypes.byref(origin));u.GetMonitorInfoW(u.MonitorFromWindow(hwnd,2),ctypes.byref(info))
    actual=[origin.x,origin.y,origin.x+client.right,origin.y+client.bottom]
    return {'window':rect(win),'client':actual,'monitor':rect(info.rect),'exact':actual==rect(info.rect),'visible':bool(u.IsWindowVisible(hwnd)),'noActivate':bool(u.GetWindowLongPtrW(hwnd,-20)&0x8000000),'topmost':bool(u.GetWindowLongPtrW(hwnd,-20)&8),'foreground':u.GetForegroundWindow(),'hwnd':hwnd}
def route(n):u.PostMessageW(p.library(),u.RegisterWindowMessageW('ClawToolbox.Library.Activation.v1'),n,u.GetForegroundWindow())
if __name__=='__main__':
    if len(sys.argv)>1:route(int(sys.argv[1]));time.sleep(.7)
    result=geometry();state=p.call('get_state');result['router']={k:state['status'].get(k) for k in ('sessionGameMode','rawKeyboardReady','firmwareWindows','observed','routed')};result['mapping']=state.get('buttonMappingResult')
    print(json.dumps(result,ensure_ascii=False));(OUT/'runtime-latest.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
