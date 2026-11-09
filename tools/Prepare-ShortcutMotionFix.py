"""Prepare the fix over the exact installed 0.1.118 library source."""
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'artifacts/startup-shield/src/frontend/library-native'
OUT = ROOT / 'artifacts/shortcut-motion-fix'
PROJECT = OUT / 'src/frontend/library-native'
INSTALLED = Path('C:/Program Files/WindowsApps/ClawToolbox.Library_0.1.118.0_x64__ba3r68zr466za/ClawLibrary.exe')
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
assert sha(INSTALLED) == sha(BASE / 'b/x64/Release/win-x64/publish/ClawLibrary.exe')
assert not PROJECT.exists(), 'Do not overwrite prepared source'
shutil.copytree(BASE, PROJECT, ignore=shutil.ignore_patterns('b', 'o', 'bin', 'obj', 'AppPackages', '.vs'))
for relative in ('MainWindow.Shortcut.cs', 'Engine/ShortcutMotionState.cs'):
    shutil.copy2(ROOT / 'frontend/library-native/ClawLibrary' / relative, PROJECT / 'ClawLibrary' / relative)
app = PROJECT / 'ClawLibrary/App.cs'
text = app.read_text(encoding='utf-8-sig')
assert text.count('++_shortcutMotionVersion;') == 1
text = text.replace('++_shortcutMotionVersion;', '_shortcutMotion.Cancel();', 1)
assert text.count('        AppWindow.Show();') == 1
text = text.replace('        AppWindow.Show();', '        if (!AppWindow.IsVisible) AppWindow.Show(false);', 1)
assert text.count('        Activate();') == 1
text = text.replace('        Activate();\n', '', 1)
app.write_text(text, encoding='utf-8')
for relative, before, after in (
        ('Package.appxmanifest', 'Version="0.1.118.0"', 'Version="0.1.119.0"'),
        ('ClawLibrary.csproj', '<Version>0.1.118</Version>', '<Version>0.1.119</Version>')):
    file = PROJECT / 'ClawLibrary' / relative
    text = file.read_text(encoding='utf-8-sig')
    assert text.count(before) == 1
    file.write_text(text.replace(before, after, 1), encoding='utf-8')
(OUT / 'base.json').write_text(json.dumps({'installedVersion': '0.1.118.0', 'installedExeSha256': sha(INSTALLED),
    'newVersion': '0.1.119.0', 'source': str(BASE)}, indent=2), encoding='utf-8')
print(PROJECT)
