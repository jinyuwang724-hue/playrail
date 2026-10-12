"""Read-only diagnostics for the selected real game's window and token."""
import ctypes
from pathlib import Path
import json
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/"plugin"))
from claw_magpie import Windows
w = Windows()
items=[]
def visit(hwnd,_):
    pid=w.pid(hwnd)
    if not w.path(pid).endswith("\\hamidashi.exe"): return True
    title,cls=ctypes.create_unicode_buffer(1024),ctypes.create_unicode_buffer(256)
    w.u.GetWindowTextW(hwnd,title,1024); w.u.GetClassNameW(hwnd,cls,256)
    items.append({"pid":pid,"hwnd":hwnd,"visible":bool(w.u.IsWindowVisible(hwnd)),"title":title.value,
                  "class":cls.value,"elevated":w.elevated(pid)})
    return True
w.u.EnumWindows(w.callback(visit),0)
print(json.dumps(items,ensure_ascii=True))
