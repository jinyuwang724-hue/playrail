"""Stage the foreground-only recall fix over exact installed 0.1.119 sources."""
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'artifacts/shortcut-motion-fix/src/frontend/library-native'
OUT = ROOT / 'artifacts/shortcut-foreground-fix'
PROJECT = OUT / 'src/frontend/library-native'
installed = Path('C:/Program Files/WindowsApps/ClawToolbox.Library_0.1.119.0_x64__ba3r68zr466za/ClawLibrary.exe')
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
assert sha(installed) == sha(BASE / 'b/x64/Release/win-x64/publish/ClawLibrary.exe')
assert not PROJECT.exists()
shutil.copytree(BASE, PROJECT, ignore=shutil.ignore_patterns('b', 'o', 'bin', 'obj', 'AppPackages', '.vs'))
for name in ('MainWindow.Shortcut.cs', 'Engine/ShortcutMotionState.cs'):
    shutil.copy2(ROOT / 'frontend/library-native/ClawLibrary' / name, PROJECT / 'ClawLibrary' / name)
for name, before, after in (
        ('Package.appxmanifest', 'Version="0.1.119.0"', 'Version="0.1.120.0"'),
        ('ClawLibrary.csproj', '<Version>0.1.119</Version>', '<Version>0.1.120</Version>')):
    file = PROJECT / 'ClawLibrary' / name
    text = file.read_text(encoding='utf-8-sig')
    assert text.count(before) == 1
    file.write_text(text.replace(before, after, 1), encoding='utf-8')
(OUT / 'base.json').write_text(json.dumps({'installedVersion': '0.1.119.0', 'installedExeSha256': sha(installed),
    'newVersion': '0.1.120.0', 'source': str(BASE)}, indent=2), encoding='utf-8')
installer = (ROOT / 'tools/Install-ShortcutMotionFix.ps1').read_text(encoding='utf-8-sig')
installer = installer.replace('shortcut-motion-fix', 'shortcut-foreground-fix')
installer = installer.replace('0.1.119.0', 'NEW_VERSION').replace('0.1.118.0', '0.1.119.0').replace('NEW_VERSION', '0.1.120.0')
installer = installer.replace("artifacts\\startup-shield\\src", "artifacts\\shortcut-motion-fix\\src")
installer = installer.replace('stateRegressions=15', 'stateRegressions=18')
(ROOT / 'tools/Install-ShortcutForegroundFix.ps1').write_text(installer, encoding='utf-8')
print(PROJECT)
