"""Install this focused library activation update without replacing power/OSD modules."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
STAGE = ROOT / 'artifacts/library-toggle/src'
HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox'
FILES = ('claw_service.py', 'claw_button_mappings.py')
spec = importlib.util.spec_from_file_location('deploy', Path(__file__).with_name('Deploy-ScalingSettings.py'))
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    info = json.loads((STAGE.parent / 'integration.json').read_text())
    for file in FILES:
        if sha(HOME / 'app' / file) not in (info['backendBaseHashes'][file], info.get('previousBackendHashes', {}).get(file), sha(STAGE / 'plugin' / file)):
            raise RuntimeError('Installed module changed: ' + file)
        compile((STAGE / 'plugin' / file).read_bytes(), file, 'exec')
    packages = list((STAGE / 'frontend/library-native/AppPackages').glob('ClawLibrary_0.1.110.0_x64_Test/*.msix'))
    if len(packages) != 1:
        raise RuntimeError('Build exactly one 0.1.110.0 package first')
    backup = HOME / 'repair-backups' / time.strftime('library-toggle-%Y%m%d-%H%M%S')
    backup.mkdir(parents=True)
    for file in FILES:
        shutil.copy2(HOME / 'app' / file, backup / file)
    snapshots = {str(p.relative_to(HOME)): sha(p) for p in (HOME / 'data/settings.json', HOME / 'data/power.json', HOME / 'current.json', HOME / 'app/plugin.json') if p.exists()}
    old_package = Path(info['sourceRoot']) / 'frontend/library-native/AppPackages/ClawLibrary_0.1.107.0_x64_Test/ClawLibrary_0.1.107.0_x64.msix'
    shutil.copy2(old_package, backup / old_package.name)
    for old in (STAGE / 'frontend/library-native/AppPackages').glob('ClawLibrary_0.1.108.0_x64_Test/*.msix'):
        shutil.copy2(old, backup / old.name)
    for old in (STAGE / 'frontend/library-native/AppPackages').glob('ClawLibrary_0.1.109.0_x64_Test/*.msix'):
        shutil.copy2(old, backup / old.name)
    subprocess.run(['powershell.exe', '-NoProfile', '-Command',
        "$ErrorActionPreference='Stop'; Add-AppxPackage -Path '" + str(packages[0]).replace("'", "''") + "' -ForceApplicationShutdown"], check=True, timeout=90)
    try:
        if any(sha(HOME / 'app' / f) != sha(STAGE / 'plugin' / f) for f in FILES):
            for file in FILES:
                shutil.copy2(STAGE / 'plugin' / file, HOME / 'app' / file)
            previous = deploy.stop()
            pid = deploy.start(previous)
        else:
            pid = deploy.call('agent_info')['pid']
        status = deploy.call('get_state')
        assert all(sha(HOME / p) == value for p, value in snapshots.items()), 'Saved settings changed'
        assert status is not None
    except Exception:
        for file in FILES:
            shutil.copy2(backup / file, HOME / 'app' / file)
        try:
            previous = deploy.stop()
            deploy.start(previous)
        finally:
            print('Backup retained at ' + str(backup))
        raise
    report = {'backup': str(backup), 'agentPid': pid, 'libraryVersion': '0.1.110.0',
        'settingsPreserved': True, 'package': str(packages[0]),
        'modules': {f: sha(HOME / 'app' / f) for f in FILES}}
    (STAGE.parent / 'deployment.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report))


if __name__ == '__main__':
    main()
