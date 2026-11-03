"""Install only the tray-enabled UI, preserving preferences and the router."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/tray-hide'
HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox'
CACHE = Path(os.environ['LOCALAPPDATA']) / 'Packages/ClawToolbox.Library_ba3r68zr466za/LocalCache'


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    info = json.loads((OUT / 'integration.json').read_text())
    version = info['version']
    expected = info.get('expectedInstalledVersion', info['baseVersion'])
    installed = Path('C:/Program Files/WindowsApps/ClawToolbox.Library_' + expected + '_x64__ba3r68zr466za/ClawLibrary.exe')
    assert installed.exists() and sha(installed) == info.get('expectedInstalledExeSha256', info['baseExeSha256']), 'Installed UI changed: rebase before updating'
    packages = list((OUT / 'src/frontend/library-native/AppPackages').glob('ClawLibrary_' + version + '_x64_Test/*.msix'))
    assert len(packages) == 1, 'Build the signed package first'
    previous = Path(info['sourceRoot']) / ('AppPackages/ClawLibrary_' + info['baseVersion'] + '_x64_Test/ClawLibrary_' + info['baseVersion'] + '_x64.msix')
    assert previous.exists(), 'Previous signed package required for backup'
    backup = HOME / 'repair-backups' / time.strftime('tray-hide-%Y%m%d-%H%M%S')
    backup.mkdir(parents=True)
    shutil.copy2(previous, backup / previous.name)
    paths = [HOME / 'data/settings.json', HOME / 'data/power.json', HOME / 'app/claw_service.py', HOME / 'app/claw_button_mappings.py']
    paths += list((HOME / 'app/bin').glob('ClawRouter*.exe')) + list(CACHE.glob('*.ini'))
    before = {str(p):sha(p) for p in paths if p.exists()}
    for p in CACHE.glob('*.ini'): shutil.copy2(p, backup / p.name)
    agent = json.loads((HOME / 'agent.json').read_text())['pid']
    subprocess.run(['powershell.exe','-NoProfile','-Command', "$ErrorActionPreference='Stop'; Add-AppxPackage -Path '" + str(packages[0]).replace("'", "''") + "' -ForceApplicationShutdown"], check=True, timeout=90)
    assert all(sha(Path(p)) == digest for p,digest in before.items()), 'Preserved preference/backend file changed'
    assert agent == json.loads((HOME / 'agent.json').read_text())['pid'], 'Agent restarted'
    exe = Path('C:/Program Files/WindowsApps/ClawToolbox.Library_' + version + '_x64__ba3r68zr466za/ClawLibrary.exe')
    assert sha(exe) == sha(OUT / 'src/frontend/library-native/b/x64/Release/native/ClawLibrary.exe'), 'Installed/build EXE differs'
    report = {'backup':str(backup),'version':version,'exeSha256':sha(exe),'agentPid':agent,
              'backendUnchanged':True,'preferencesUnchanged':True,'package':str(packages[0]),'preservedHashes':before}
    (OUT / 'deployment.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({k:v for k,v in report.items() if k!='preservedHashes'}))


if __name__ == '__main__': main()
