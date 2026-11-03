"""Run against the production library/router while the test game is foreground."""
import importlib.util
import json
import subprocess
import time
import sys
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
s=importlib.util.spec_from_file_location('overlay_probe',ROOT/'tools/Probe-OverlayFocus.py');p=importlib.util.module_from_spec(s);s.loader.exec_module(p)
mode=json.loads((p.HOME/'shell/state.json').read_text())
assert mode['mode']=='game' and not mode['explorer'],mode

def snap():
    r=p.state();sample=r['widget']['power']['osd']['monitor']['sample']
    return {'foreground':r['foreground'],'library':r['library'],'visible':r['visible'],
            'topmost':r['topmost'],'noActivate':r['noActivate'],'held':r['input']['engine'].get('overlayHeld'),
            'fps':sample.get('fps'),'frameCount':sample.get('frameCount'),
            'gamePid':r['widget']['system']['game']['pid']}

def press(source, delay=1):
    result=p.call('test_button_mapping',source,False)
    assert result['ok'],result
    time.sleep(delay)
    return snap()

before=snap()
if before['foreground']==before['library']:
    # A fresh library launch owns focus. Return to its remembered game through
    # the real quick-panel close route before testing foreground preservation.
    if len(sys.argv) < 2: raise RuntimeError('Pass the current test-game HWND when the library initially owns focus.')
    target=int(sys.argv[1])
    message=p.user.RegisterWindowMessageW('ClawToolbox.Library.Activation.v1')
    p.user.PostMessageW(p.library(),message,2,target);time.sleep(.8)
    p.user.PostMessageW(p.library(),message,6,target);time.sleep(1.5)
    before=snap()
assert before['foreground']!=before['library'],before
if before['held']:
    press('QS');before=snap()

quick1=press('QS');time.sleep(2);quick2=snap()
for r in (quick1,quick2):
    assert r['foreground']==before['foreground'] and r['visible'] and r['topmost'] and r['noActivate'] and r['held'],r
assert quick2['frameCount']>quick1['frameCount'] and quick2['fps']>20,(quick1,quick2)

library1=press('Claw');time.sleep(2);library2=snap()
assert library2['foreground']==before['foreground'] and library2['held'] and library2['topmost'] and library2['noActivate'],library2
assert library2['frameCount']>library1['frameCount'] and library2['fps']>20,(library1,library2)
closed=press('Claw',1.5)
assert closed['foreground']==before['foreground'] and not closed['held'] and not closed['topmost'] and not closed['noActivate'],closed

for i in range(10):
    result=p.call('test_button_mapping','QS',False);assert result['ok'],result;time.sleep(.09)
time.sleep(3)
stress=snap()
assert stress['foreground']==before['foreground'] and not stress['held'] and not stress['topmost'] and not stress['noActivate'],stress
render_path=p.HOME/'data/overlay-render.json'
old=json.loads(render_path.read_text());time.sleep(.8);new=json.loads(render_path.read_text())
assert new['paintCount']>old['paintCount'] and new['visible'] and new['topmost'],(old,new)
report={'version':'0.1.196.0','mode':mode,'before':before,'quick':[quick1,quick2],'library':[library1,library2],
        'closed':closed,'rapidToggleCount':10,'afterStress':stress,'osdRestored':True,
        'coverage':'Production router RPC path and real P3R PresentMon frames. Physical touch and button presses not simulated.'}
(p.OUT/'runtime-verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report))
