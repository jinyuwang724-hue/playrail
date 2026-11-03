"""Upgrade only the library UI, retaining the physical-button listener and preferences."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/folia-motion'
HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox'
CACHE = Path(os.environ['LOCALAPPDATA']) / 'Packages/ClawToolbox.Library_ba3r68zr466za/LocalCache'


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    info = json.loads((OUT / 'integration.json').read_text())
    previous_version = info.get('expectedInstalledVersion', info['baseVersion'])
    version = info['version']
    installed = Path('C:/Program Files/WindowsApps/ClawToolbox.Library_' + previous_version + '_x64__ba3r68zr466za/ClawLibrary.exe')
    if not installed.exists() or sha(installed) != info.get('expectedInstalledExeSha256', info['baseExeSha256']):
        raise RuntimeError('The installed library changed; rebase the UI update first')
    packages = list((OUT / 'src/frontend/library-native/AppPackages').glob('ClawLibrary_' + version + '_x64_Test/*.msix'))
    if len(packages) != 1: raise RuntimeError('Build the signed ' + version + ' package first')
    backup = HOME / 'repair-backups' / time.strftime('folia-motion-%Y%m%d-%H%M%S')
    backup.mkdir(parents=True)
    previous = Path(info['sourceRoot']) / 'frontend/library-native/AppPackages/ClawLibrary_0.1.111.0_x64_Test/ClawLibrary_0.1.111.0_x64.msix'
    shutil.copy2(previous, backup / previous.name)
    paths = [HOME / 'data/settings.json', HOME / 'data/power.json', HOME / 'app/claw_service.py', HOME / 'app/claw_button_mappings.py', HOME / 'app/bin/ClawRouter-0.17.58.exe']
    paths += list(CACHE.glob('*.ini'))
    before = {str(p):sha(p) for p in paths if p.exists()}
    for p in CACHE.glob('*.ini'): shutil.copy2(p, backup / p.name)
    endpoint = json.loads((HOME / 'agent.json').read_text())['pid']
    subprocess.run(['powershell.exe','-NoProfile','-Command', "$ErrorActionPreference='Stop'; Add-AppxPackage -Path '" + str(packages[0]).replace("'", "''") + "' -ForceApplicationShutdown"], check=True, timeout=90)
    assert all(sha(Path(p)) == digest for p,digest in before.items()), 'A preserved preference/backend file changed'
    assert endpoint == json.loads((HOME / 'agent.json').read_text())['pid'], 'Agent restarted during UI-only update'
    exe = Path('C:/Program Files/WindowsApps/ClawToolbox.Library_' + version + '_x64__ba3r68zr466za/ClawLibrary.exe')
    built = OUT / 'src/frontend/library-native/b/x64/Release/native/ClawLibrary.exe'
    assert sha(exe) == sha(built), 'Installed executable differs from the verified build'
    report = {'backup':str(backup),'version':version,'exeSha256':sha(exe),'agentPid':endpoint,
        'backendUnchanged':True,'preferencesUnchanged':True,'package':str(packages[0]), 'preservedHashes':before}
    (OUT / 'deployment.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='preservedHashes'}))


if __name__ == '__main__': main()
