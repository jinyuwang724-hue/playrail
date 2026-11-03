"""Install the two minimal backend additions over the hash-checked live baseline."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import time

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/gamebar-power-handoff'
STAGE = OUT / 'backend'
HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox'
expected = json.loads((STAGE / 'expected.json').read_text(encoding='utf-8'))
for name, digest in expected.items():
    assert hashlib.sha256((HOME / 'app' / name).read_bytes()).hexdigest() == digest, 'Installed module changed: ' + name
backup = HOME / 'repair-backups' / time.strftime('gamebar-power-%Y%m%d-%H%M%S')
backup.mkdir(parents=True)
spec = importlib.util.spec_from_file_location('deploy', ROOT / 'tools/Deploy-ScalingSettings.py')
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)
for name in expected:
    compile((STAGE / name).read_bytes(), name, 'exec')
    shutil.copy2(HOME / 'app' / name, backup / name)
    shutil.copy2(STAGE / name, HOME / 'app' / name)
try:
    previous = deploy.stop()
    current = deploy.start(previous)
    state = deploy.call('get_system_state')
    assert state['powerAnimation']['available']
    # Validate the actual agent whitelist without starting a visual preview here.
    try: deploy.call('preview_power_animation', 'not-accepted')
    except RuntimeError as error: assert 'positional' in str(error), str(error)
    else: raise AssertionError('Preview endpoint incorrectly accepts arguments')
except Exception:
    for name in expected: shutil.copy2(backup / name, HOME / 'app' / name)
    try: deploy.start(deploy.stop())
    except Exception: pass
    raise
(OUT / 'backend-deployment.json').write_text(json.dumps({
    'backup': str(backup), 'agentPid': current, 'animationAvailable': True,
    'previewEndpointVerified': True,
    'moduleSha256': {name: hashlib.sha256((HOME / 'app' / name).read_bytes()).hexdigest() for name in expected},
}, indent=2), encoding='utf-8')
print('Backend preview endpoint installed and validated; no animation or power operation was requested')
