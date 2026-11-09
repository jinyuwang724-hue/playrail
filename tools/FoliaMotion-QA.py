"""Exercise the existing activation message on an isolated windowed UI preview."""
import ctypes
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/folia-motion'
EXE = OUT / 'src/frontend/library-native/b/x64/Debug/win-x64/publish/ClawLibrary.exe'
CACHE = OUT / 'qa-cache'
INFO = OUT / 'qa-instance.json'
user = ctypes.WinDLL('user32', use_last_error=True)
user.GetForegroundWindow.restype = w.HWND
user.GetWindowThreadProcessId.argtypes = [w.HWND, ctypes.POINTER(w.DWORD)]
user.GetWindowTextW.argtypes = [w.HWND,w.LPWSTR,ctypes.c_int]
user.IsIconic.argtypes = user.IsWindowVisible.argtypes = [w.HWND]
user.RegisterWindowMessageW.argtypes,user.RegisterWindowMessageW.restype=[w.LPCWSTR],w.UINT
user.PostMessageW.argtypes,user.PostMessageW.restype=[w.HWND,w.UINT,w.WPARAM,w.LPARAM],w.BOOL
callback_type = ctypes.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM)
user.EnumWindows.argtypes=[callback_type,w.LPARAM]


def main():
    action = sys.argv[1]
    if action == 'launch':
        CACHE.mkdir(exist_ok=True)
        live = Path(os.environ['LOCALAPPDATA']) / 'Packages/ClawToolbox.Library_ba3r68zr466za/LocalCache'
        for file in live.glob('*.ini'): shutil.copy2(file,CACHE / file.name)
        ui = CACHE / 'ui.ini'
        text = ui.read_text(encoding='utf-8-sig').replace('backgroundRun=True','backgroundRun=False').replace('backgroundRun=true','backgroundRun=false')
        ui.write_text(text,encoding='utf-8')
        if (live / 'art').exists(): shutil.copytree(live / 'art',CACHE / 'art',dirs_exist_ok=True)
        process = subprocess.Popen([str(EXE),'--windowed','--instance-key=folia-motion-qa','--cache-dir='+str(CACHE),'--shortcut-slow=4'])
        INFO.write_text(json.dumps({'pid':process.pid,'exe':str(EXE)}),encoding='utf-8')
        print(json.dumps({'pid':process.pid})); return
    instance = json.loads(INFO.read_text())
    kernel = ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.argtypes,kernel.OpenProcess.restype=[w.DWORD,w.BOOL,w.DWORD],w.HANDLE
    kernel.QueryFullProcessImageNameW.argtypes=[w.HANDLE,w.DWORD,w.LPWSTR,ctypes.POINTER(w.DWORD)]
    kernel.CloseHandle.argtypes=[w.HANDLE]
    handle=kernel.OpenProcess(0x1000,False,instance['pid'])
    if not handle: raise RuntimeError('The QA process has exited')
    try:
        text=ctypes.create_unicode_buffer(32768); size=w.DWORD(len(text))
        if not kernel.QueryFullProcessImageNameW(handle,0,text,ctypes.byref(size)) or Path(text.value).resolve()!=EXE.resolve():
            raise RuntimeError('QA process identity changed')
    finally: kernel.CloseHandle(handle)
    matches=[]
    def visit(window,_):
        pid=w.DWORD();user.GetWindowThreadProcessId(window,ctypes.byref(pid))
        if pid.value!=instance['pid']:return True
        title=ctypes.create_unicode_buffer(256); user.GetWindowTextW(window,title,len(title))
        if title.value=='Claw 游戏库':matches.append(int(window))
        return True
    user.EnumWindows(callback_type(visit),0)
    if len(matches)!=1:raise RuntimeError('Expected one QA window, got '+str(matches))
    window=matches[0]
    if action in ('show','hide'):
        message=user.RegisterWindowMessageW('ClawToolbox.Library.Activation.v1')
        if not user.PostMessageW(window,message,3 if action=='show' else 5,int(user.GetForegroundWindow() or 0)):
            raise ctypes.WinError(ctypes.get_last_error())
    cloaked=w.DWORD();dwm=ctypes.WinDLL('dwmapi')
    dwm.DwmGetWindowAttribute.argtypes=[w.HWND,w.DWORD,ctypes.c_void_p,w.DWORD]
    dwm.DwmGetWindowAttribute(window,14,ctypes.byref(cloaked),4)
    print(json.dumps({'window':window,'pid':instance['pid'],'visible':bool(user.IsWindowVisible(window)),
        'iconic':bool(user.IsIconic(window)),'cloaked':cloaked.value,'foreground':int(user.GetForegroundWindow() or 0)}))


if __name__=='__main__':main()
