"""Keyboard navigation for this machine's broken Game Bar controller delegation.

The production service owns its lifetime and stops it as soon as Game Bar closes.
Only gamepad state is read; no keyboard hook or typed text is collected.
"""
import ctypes
from ctypes import wintypes
import json,os,time
from pathlib import Path

class Pad(ctypes.Structure):
    _fields_=[('buttons',wintypes.WORD),('lt',ctypes.c_ubyte),('rt',ctypes.c_ubyte),('lx',ctypes.c_short),('ly',ctypes.c_short),('rx',ctypes.c_short),('ry',ctypes.c_short)]
class State(ctypes.Structure): _fields_=[('packet',wintypes.DWORD),('pad',Pad)]

def active():
    try:
        p=Path(os.environ['LOCALAPPDATA'])/'Packages/ClawToolbox.GameBarWidget_ba3r68zr466za/LocalState/power-visibility.json'
        data=json.loads(p.read_text(encoding='utf-8-sig'))
        if data.get('foreground') is not True:return False
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.OpenProcess.restype=wintypes.HANDLE;kernel.CloseHandle.argtypes=[wintypes.HANDLE]
        kernel.GetExitCodeProcess.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)]
        process=kernel.OpenProcess(0x1000,False,int(data['pid']))
        if not process:return False
        try:
            code=wintypes.DWORD();return bool(kernel.GetExitCodeProcess(process,ctypes.byref(code))) and code.value==259
        finally:kernel.CloseHandle(process)
    except (OSError,ValueError,KeyError,TypeError):return False

class Navigation:
    KEYS={1:['Up'],2:['Down'],4:['Left'],8:['Right'],0x1000:['Enter'],0x2000:['Esc'],0x100:['LShift','Tab'],0x200:['Tab'],0x10:['LShift','F10']}
    def __init__(self):
        self.dll=ctypes.WinDLL('xinput1_4')
        self.dll.XInputGetState.argtypes=[wintypes.DWORD,ctypes.POINTER(State)]
        self.previous=0;self.repeat={};self.samples=0;self.presses=0
    def read(self):
        selected=0
        for i in range(4):
            state=State()
            if self.dll.XInputGetState(i,ctypes.byref(state))!=0:continue
            b=state.pad.buttons
            if state.pad.lx < -16000:b|=4
            if state.pad.lx > 16000:b|=8
            if state.pad.ly > 16000:b|=1
            if state.pad.ly < -16000:b|=2
            selected|=b
        self.samples+=1
        return self.step(selected,time.monotonic())
    def step(self,buttons,now):
        commands=[]
        for mask,keys in self.KEYS.items():
            if not buttons&mask:self.repeat.pop(mask,None);continue
            if not self.previous&mask:
                commands.append(keys);self.repeat[mask]=now+.32;self.presses+=1
            elif mask<16 and now>=self.repeat.get(mask,now):
                commands.append(keys);self.repeat[mask]=now+.09
        self.previous=buttons
        return commands
