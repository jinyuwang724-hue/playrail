"""Read-only verification of user-triggered v10 preview and physical-button cycles."""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/gamebar-dismiss-diagnosis'
log = Path(os.environ['PROGRAMDATA']) / 'ClawToolbox/SleepAnimation/S-1-5-21-1209625938-3549308280-2412461226-1001/animation.jsonl'
rows = [json.loads(line) for line in log.read_text(encoding='utf-8-sig').splitlines()]
monitor = max(i for i, row in enumerate(rows) if row['kind'] == 'monitor-ready')
rows = rows[monitor:]
last_preview = max(i for i, row in enumerate(rows) if row['kind'] == 'power-exit-black' and row['data']['preview'])
begin = max(i for i, row in enumerate(rows[:last_preview]) if row['kind'] == 'gamebar-dismiss-inspect')
end = next(i for i in range(last_preview, len(rows)) if rows[i]['kind'] == 'power-exit-animation-cleared')
preview = rows[begin:end + 1]
assert any(row['kind'] == 'gamebar-dismiss' and row['data']['method'] == 'input-owner-escape'
           and row['data']['presses'] == 1 and not row['data']['remaining'] for row in preview)
assert any(row['kind'] == 'motion-played' and row['data']['completed'] for row in preview)
assert not any(row['kind'] == 'power-exit-requested' for row in preview)
result = {'preview': {'events': preview, 'actualPowerRequests': 0,
                     'userFeedback': 'Game Bar 收起，动画完整显示', 'visualConfirmed': True},
          'physicalButtonVisualConfirmed': False}
if any(row['kind'] == 'button-cycle-complete' for row in rows):
    cycle_end = max(i for i, row in enumerate(rows) if row['kind'] == 'button-cycle-complete')
    cycle_start = max(i for i, row in enumerate(rows[:cycle_end]) if row['kind'] == 'button-animation-start')
    cycle = rows[cycle_start:cycle_end + 1]
    assert any(row['kind'] in ('button-sleep-requested', 'button-hibernate-requested') for row in cycle)
    result['physicalButton'] = {'events': cycle, 'visualConfirmation': 'pending-user-feedback'}
    if '--confirm-physical' in sys.argv:
        assert any(row['kind'] == 'gamebar-dismiss' and row['data']['direction'] == 'suspend'
                   and row['data']['presses'] == 1 and not row['data']['remaining'] for row in cycle)
        assert all(any(row['kind'] == 'motion-played' and row['data']['direction'] == direction
                       and row['data']['completed'] for row in cycle) for direction in ('suspend', 'wake'))
        assert any(row['kind'] == 'gamebar-dismiss' and row['data']['direction'] == 'wake'
                   and not row['data']['remaining'] for row in cycle)
        result['physicalButtonVisualConfirmed'] = True
        result['physicalButton']['visualConfirmation'] = 'confirmed-by-user'
        result['physicalButton']['userFeedback'] = 'Game Bar 收起，进入和唤醒动画都完整可见'
(OUT / 'v10-live-verification.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print('Preview verified; physical-button visual confirmation: ' + str(result['physicalButtonVisualConfirmed']))
