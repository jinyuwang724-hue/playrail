"""Install the verified limiter change, retaining all newer installed features."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import time

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/driver-frame-limit'
HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox'
spec = importlib.util.spec_from_file_location('deployment', ROOT / 'tools/Deploy-ScalingSettings.py')
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    plan = json.loads((OUT / 'preparation.json').read_text())
    for name, expected in plan['expected'].items():
        if digest(HOME / 'app' / name) != expected: raise RuntimeError('Installed file changed: ' + name)
        if name.endswith('.py'): compile((OUT / 'backend' / name).read_bytes(), name, 'exec')
    verification = json.loads((OUT / 'dx11-verification.json').read_text(encoding='utf-8-sig'))
    for check in verification['checks']:
        if check['measured']['rtssInjected']: raise RuntimeError('Verification was affected by RTSS')
        if check['requested'] and abs(check['requested'] - check['measured']['fps']) > 1:
            raise RuntimeError('The driver did not achieve the requested rate')
    old = deploy.call('widget_state')
    # A nonzero old RTSS limit needs an explicit backed-up migration, not a second stacked limit.
    if old.get('system', {}).get('fpsLimit') not in (None, 0):
        raise RuntimeError('The old RTSS limit is active; migrate its backed-up value before deployment')
    saved = {file.name: file.read_bytes() for file in (HOME / 'data').glob('*.json')
             if file.name in ('power.json', 'game-performance.json', 'ecore.json')}
    backup = HOME / 'repair-backups' / time.strftime('driver-frame-limit-%Y%m%d-%H%M%S')
    backup.mkdir(parents=True)
    for name in plan['expected']: shutil.copy2(HOME / 'app' / name, backup / name)
    candidate, target = OUT / plan['newHelper'], HOME / 'app/bin' / plan['newHelper']
    if not target.exists() or digest(candidate) != digest(target): shutil.copy2(candidate, target)
    try:
        for name in plan['expected']: shutil.copy2(OUT / 'backend' / name, HOME / 'app' / name)
        previous = deploy.stop()
        current = deploy.start(previous)
        view = deploy.call('widget_state')
        if view['system'].get('fpsLimitProvider') != 'intel': raise RuntimeError('Driver limiter is not active')
        # Check the unchanged UI endpoint and the actual driver readback, then restore off.
        try:
            changed = deploy.call('set_fps_limit', 60)
            if changed.get('fpsLimit') != 60: raise RuntimeError('Limiter endpoint did not read back 60 FPS')
        finally: deploy.call('set_fps_limit', 0)
        for name, value in saved.items():
            if (HOME / 'data' / name).read_bytes() != value: raise RuntimeError('Unrelated saved settings changed: ' + name)
        view = deploy.call('widget_state')
        report = {'backup': str(backup), 'agentPid': current, 'helper': plan['newHelper'],
                  'system': {key: value for key, value in view['system'].items() if key.startswith('fpsLimit')},
                  'savedSettingsPreserved': True, 'dx11NoRtss': True,
                  'sha256': {'helper': digest(HOME / 'app/bin' / plan['newHelper']),
                             **{name: digest(HOME / 'app' / name) for name in plan['expected']}}}
        (OUT / 'deployment.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(report, ensure_ascii=True))
    except Exception:
        for name in plan['expected']: shutil.copy2(backup / name, HOME / 'app' / name)
        try: deploy.start(deploy.stop())
        except Exception: pass
        raise


if __name__ == '__main__': main()
