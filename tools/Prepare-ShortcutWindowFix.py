"""Complete the recall fix with a single foreground-before-topmost handoff."""
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'artifacts/shortcut-foreground-fix/src/frontend/library-native'
OUT = ROOT / 'artifacts/shortcut-window-fix'
PROJECT = OUT / 'src/frontend/library-native'
installed = Path('C:/Program Files/WindowsApps/ClawToolbox.Library_0.1.120.0_x64__ba3r68zr466za/ClawLibrary.exe')
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
assert sha(installed) == sha(BASE / 'b/x64/Release/win-x64/publish/ClawLibrary.exe')
assert not PROJECT.exists()
shutil.copytree(BASE, PROJECT, ignore=shutil.ignore_patterns('b', 'o', 'bin', 'obj', 'AppPackages', '.vs'))
for name in ('MainWindow.Shortcut.cs', 'Data/LibraryWindow.cs'):
    shutil.copy2(ROOT / 'frontend/library-native/ClawLibrary' / name, PROJECT / 'ClawLibrary' / name)
for name, before, after in (
        ('Package.appxmanifest', 'Version="0.1.120.0"', 'Version="0.1.121.0"'),
        ('ClawLibrary.csproj', '<Version>0.1.120</Version>', '<Version>0.1.121</Version>')):
    file = PROJECT / 'ClawLibrary' / name
    text = file.read_text(encoding='utf-8-sig')
    assert text.count(before) == 1
    file.write_text(text.replace(before, after, 1), encoding='utf-8')
(OUT / 'base.json').write_text(json.dumps({'installedVersion': '0.1.120.0', 'installedExeSha256': sha(installed),
    'newVersion': '0.1.121.0', 'source': str(BASE)}, indent=2), encoding='utf-8')
installer = (ROOT / 'tools/Install-ShortcutForegroundFix.ps1').read_text(encoding='utf-8-sig')
installer = installer.replace('shortcut-foreground-fix', 'shortcut-window-fix')
installer = installer.replace('0.1.120.0', 'NEW_VERSION').replace('0.1.119.0', '0.1.120.0').replace('NEW_VERSION', '0.1.121.0')
installer = installer.replace("artifacts\\shortcut-motion-fix\\src", "artifacts\\shortcut-foreground-fix\\src")
(ROOT / 'tools/Install-ShortcutWindowFix.ps1').write_text(installer, encoding='utf-8')
print(PROJECT)
