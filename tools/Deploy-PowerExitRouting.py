import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import time

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / 'artifacts/startup-shield/backend'
HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox'
expected = json.loads((STAGE / 'expected.json').read_text(encoding='utf-8'))
for name, digest in expected.items():
    assert hashlib.sha256((HOME / 'app' / name).read_bytes()).hexdigest() == digest, 'Installed module changed: ' + name
backup = HOME / 'repair-backups' / time.strftime('animated-power-%Y%m%d-%H%M%S')
backup.mkdir(parents=True)
spec = importlib.util.spec_from_file_location('deploy', ROOT / 'tools/Deploy-ScalingSettings.py')
deploy = importlib.util.module_from_spec(spec); spec.loader.exec_module(deploy)
for name in expected:
    shutil.copy2(HOME / 'app' / name, backup / name)
    shutil.copy2(STAGE / name, HOME / 'app' / name)
try:
    previous = deploy.stop(); current = deploy.start(previous)
    state = deploy.call('get_system_state')
    assert state['powerAnimation']['available']
except Exception:
    for name in expected: shutil.copy2(backup / name, HOME / 'app' / name)
    try: deploy.start(deploy.stop())
    except Exception: pass
    raise
(STAGE.parent / 'backend-deployment.json').write_text(json.dumps({'backup': str(backup), 'agentPid': current, 'animationAvailable': True}, indent=2), encoding='utf-8')
print('Power animation routing installed')
