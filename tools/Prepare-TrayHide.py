"""Build the tray update on the exact source of installed 0.1.114."""
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'artifacts/folia-motion/src/frontend/library-native'
OUT = ROOT / 'artifacts/tray-hide'
PROJECT = OUT / 'src/frontend/library-native'
BASE_SHA = 'db1089556d8d4286204be1d358f97bc967566e38aa3d351512628b0885c2000a'


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    installed = Path('C:/Program Files/WindowsApps/ClawToolbox.Library_0.1.114.0_x64__ba3r68zr466za/ClawLibrary.exe')
    assert sha(installed) == sha(BASE / 'b/x64/Release/native/ClawLibrary.exe') == BASE_SHA, 'UI baseline changed'
    assert not PROJECT.exists(), 'Do not overwrite an existing integration'
    shutil.copytree(BASE / 'ClawLibrary', PROJECT / 'ClawLibrary')
    shutil.copytree(BASE / 'tools', PROJECT / 'tools')
    shutil.copy2(BASE / 'Directory.Build.props', PROJECT / 'Directory.Build.props')
    changed = ['App.cs', 'MainWindow.Shortcut.cs', 'Data/LibraryWindow.cs']
    # Root App.cs may contain other work: apply only this focused replacement.
    app = PROJECT / 'ClawLibrary/App.cs'
    source = app.read_text(encoding='utf-8-sig')
    old = '''        CancelShortcutTransition();
        _shortcutWindow?.SetTopmost(false);
        if (_tray?.EnsureIcon() != true) { ToolboxWindow.Minimize(WinRT.Interop.WindowNative.GetWindowHandle(this)); return; }
        AppWindow.Hide();
        _stage?.SuspendToTray();
        Log.Info("library hidden to tray");'''
    new = '''        CancelShortcutTransition();
        ++_shortcutMotionVersion;
        _stage?.EndShortcutSoftFocus();
        CompleteShortcutHide();'''
    assert source.count(old) == 1, 'Background hide changed'
    app.write_text(source.replace(old, new), encoding='utf-8')
    for name in changed[1:]:
        shutil.copy2(ROOT / 'frontend/library-native/ClawLibrary' / name, PROJECT / 'ClawLibrary' / name)
    for name in ('ClawLibrary.csproj', 'Package.appxmanifest'):
        target = PROJECT / 'ClawLibrary' / name
        text = target.read_text(encoding='utf-8-sig')
        before, after = ('<Version>0.1.114</Version>', '<Version>0.1.116</Version>') if name.endswith('.csproj') else ('Version="0.1.114.0"', 'Version="0.1.116.0"')
        assert text.count(before) == 1, 'Version baseline changed'
        target.write_text(text.replace(before, after), encoding='utf-8')
    info = {'sourceRoot':str(BASE), 'baseVersion':'0.1.114.0', 'baseExeSha256':BASE_SHA,
            'version':'0.1.116.0', 'changes':changed}
    (OUT / 'integration.json').write_text(json.dumps(info, indent=2), encoding='utf-8')
    print(PROJECT)


if __name__ == '__main__': main()
