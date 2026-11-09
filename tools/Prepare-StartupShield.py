import hashlib
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'artifacts/startup-transition/src/frontend/library-native'
OUT = ROOT / 'artifacts/startup-shield/src/frontend/library-native'
installed = Path(r'C:\Program Files\WindowsApps\ClawToolbox.Library_0.1.117.0_x64__ba3r68zr466za\ClawLibrary.exe')
assert hashlib.sha256(installed.read_bytes()).digest() == hashlib.sha256((BASE / 'b/x64/Release/win-x64/publish/ClawLibrary.exe').read_bytes()).digest()
assert not OUT.exists()
shutil.copytree(BASE, OUT, ignore=shutil.ignore_patterns('bin', 'obj', 'AppPackages', '.vs', 'b', 'o'))
for relative in ('ClawLibrary/Stage/WallStage.Startup.cs', 'ClawLibrary/Engine/StartupVisibilityGate.cs', 'ClawLibrary/Assets/StartupPages.png'):
    shutil.copy2(ROOT / 'frontend/library-native' / relative, OUT / relative)

def replace(path, old, new):
    text = path.read_text(encoding='utf-8-sig')
    assert text.count(old) == 1, str(path) + ': anchor mismatch'
    path.write_text(text.replace(old, new), encoding='utf-8')

replace(OUT / 'ClawLibrary/App.cs', '                _stage.Start();', '                _stage.StartupShieldRequested = _pendingRoute == "activate";\n                _stage.Start();')
replace(OUT / 'ClawLibrary/Stage/WallStage.cs', '        // 第一帧真正画出来的时刻', '        PrepareStartupShield();\n        // 第一帧真正画出来的时刻')
replace(OUT / 'ClawLibrary/Package.appxmanifest', 'Version="0.1.117.0"', 'Version="0.1.118.0"')
replace(OUT / 'ClawLibrary/ClawLibrary.csproj', '<Version>0.1.117</Version>', '<Version>0.1.118</Version>')
print(str(OUT))
