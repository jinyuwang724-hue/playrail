"""Read-only evidence from a preview started by the user in the actual widget."""
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/gamebar-power-handoff'
local = Path(os.environ['LOCALAPPDATA'])
trace = json.loads((local / 'Packages/ClawToolbox.GameBarWidget_ba3r68zr466za/LocalState/power-transition.json').read_text(encoding='utf-8-sig'))
assert trace['preview'] is True, 'This verifier requires a non-destructive menu preview'
assert trace['phase'] == 'ready-to-animate' and trace['libraryLaunched'] and not trace['gameBarForeground']
start = trace['startedUnixMs'] / 1000
log = Path(os.environ['PROGRAMDATA']) / 'ClawToolbox/SleepAnimation/S-1-5-21-1209625938-3549308280-2412461226-1001/animation.jsonl'
events = []
for line in log.read_text(encoding='utf-8-sig').splitlines():
    row = json.loads(line)
    if datetime.fromisoformat(row['at'].replace('Z', '+00:00')).timestamp() >= start:
        events.append(row)
assert any(e['kind'] == 'motion-played' and e['data']['completed'] for e in events)
assert any(e['kind'] == 'power-exit-black' and e['data']['preview'] for e in events)
assert any(e['kind'] == 'power-exit-animation-cleared' and e['data']['preview'] for e in events)
assert not any(e['kind'] == 'power-exit-requested' for e in events), 'A real OS power request was recorded'
spec = importlib.util.spec_from_file_location('deploy', ROOT / 'tools/Deploy-ScalingSettings.py')
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)
state = deploy.call('get_system_state')['powerAnimation']
assert state['available'] and state['active']
result = {'checkedAtUtc': datetime.now(timezone.utc).isoformat(), 'widgetHandoff': trace,
          'events': events, 'actualPowerRequests': 0, 'animationState': state,
          'visualConfirmation': 'pending-user-feedback'}
(OUT / 'live-preview.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print('Verified: actual widget handoff left Game Bar foreground, preview completed and cleared; OS power requests = 0. Visual result pending user feedback.')
