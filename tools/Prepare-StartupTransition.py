"""Stage readiness changes over the sources matching installed 0.1.116."""
import hashlib
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'artifacts/tray-hide/src/frontend/library-native'
OUT = ROOT / 'artifacts/startup-transition/src/frontend/library-native'
installed = Path(r'C:\Program Files\WindowsApps\ClawToolbox.Library_0.1.116.0_x64__ba3r68zr466za\ClawLibrary.exe')
expected = json.loads((ROOT / 'artifacts/tray-hide/deployment.json').read_text(encoding='utf-8-sig'))['exeSha256']
assert hashlib.sha256(installed.read_bytes()).hexdigest().lower() == expected.lower(), 'Installed UI changed'
if OUT.exists():
    assert (OUT / 'ClawLibrary/Stage/WallStage.cs').read_bytes() == (BASE / 'ClawLibrary/Stage/WallStage.cs').read_bytes(), 'Staging already modified'
else:
    shutil.copytree(BASE, OUT, ignore=shutil.ignore_patterns('bin', 'obj', 'AppPackages', '.vs', 'b', 'o'))

def replace(path, old, new):
    text = path.read_text(encoding='utf-8-sig')
    assert text.count(old) == 1, str(path) + ': ambiguous integration anchor'
    path.write_text(text.replace(old, new), encoding='utf-8')

for folder in (OUT,):
    file = folder / 'ClawLibrary/Stage/WallStage.cs'
    replace(file, '            FirstFrameLogged = true; PlayStartupSound();',
            '            FirstFrameLogged = true; PlayStartupSound();\n            BeginStartupReadiness();')
    replace(file, '        DisposeShortcutMotion();', '        DisposeStartupReadiness();\n        DisposeShortcutMotion();')
shutil.copy2(ROOT / 'frontend/library-native/ClawLibrary/Stage/WallStage.Startup.cs', OUT / 'ClawLibrary/Stage/WallStage.Startup.cs')
replace(OUT / 'ClawLibrary/Package.appxmanifest', 'Version="0.1.116.0"', 'Version="0.1.117.0"')
replace(OUT / 'ClawLibrary/ClawLibrary.csproj', '<Version>0.1.116</Version>', '<Version>0.1.117</Version>')
(OUT.parents[2] / 'integration.json').write_text(json.dumps({'baseline': str(BASE), 'installedExeSha256': expected,
    'expectedInstalledVersion': '0.1.116.0', 'candidateVersion': '0.1.117.0', 'scope': 'first-frame and cover readiness only'}, indent=2), encoding='utf-8')
print(str(OUT))
