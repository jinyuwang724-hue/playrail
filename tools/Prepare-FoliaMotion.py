"""Prepare a UI-only soft-focus update from the source matching installed 0.1.111."""
import hashlib
import json
from pathlib import Path
import shutil
import sys

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'artifacts/sleep-animation/settings-v2/integration-r2'
OUT = ROOT / 'artifacts/folia-motion'
STAGE = OUT / 'src'


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    resume = '--resume' in sys.argv
    if STAGE.exists() and not resume: raise RuntimeError('Prepared motion source already exists; do not overwrite it')
    exe = BASE / 'frontend/library-native/b/x64/Release/native/ClawLibrary.exe'
    if sha(exe) != '223cd224fbf3cf142820e6c53c696e4e450870ca92b950e4272975316c4c195b':
        raise RuntimeError('UI baseline changed')
    project = STAGE / 'frontend/library-native'
    shutil.copytree(BASE / 'frontend/library-native/ClawLibrary', project / 'ClawLibrary', dirs_exist_ok=resume)
    shutil.copytree(BASE / 'frontend/library-native/tools', project / 'tools', dirs_exist_ok=resume)
    shutil.copy2(BASE / 'frontend/library-native/Directory.Build.props', project / 'Directory.Build.props')
    for file in ('MainWindow.Shortcut.cs', 'Stage/WallStage.ShortcutMotion.cs'):
        shutil.copy2(ROOT / 'frontend/library-native/ClawLibrary' / file, project / 'ClawLibrary' / file)
    target = project / 'ClawLibrary/Stage/WallStage.cs'
    text = target.read_text(encoding='utf-8-sig')
    old = '    public void Dispose()\n    {\n'
    if text.count(old) != 1: raise RuntimeError('Stage disposal changed')
    target.write_text(text.replace(old, old + '        DisposeShortcutMotion();\n', 1), encoding='utf-8')
    for file in ('ClawLibrary.csproj', 'Package.appxmanifest'):
        target = project / 'ClawLibrary' / file
        text = target.read_text(encoding='utf-8-sig')
        before = '<Version>0.1.110</Version>' if file.endswith('.csproj') else 'Version="0.1.111.0"'
        after = '<Version>0.1.114</Version>' if file.endswith('.csproj') else 'Version="0.1.114.0"'
        if text.count(before) != 1: raise RuntimeError('Version baseline changed: ' + file)
        target.write_text(text.replace(before, after), encoding='utf-8')
    info = {'sourceRoot':str(BASE), 'baseVersion':'0.1.111.0', 'version':'0.1.114.0', 'baseExeSha256':sha(exe),
        'foliaRevision':'aecd330b720eb8fc728e6b10ebcec261b144d683',
        'reference':'src/components/visualizer/styleTransition/softFocus.ts',
        'changes': ['MainWindow.Shortcut.cs', 'Stage/WallStage.ShortcutMotion.cs', 'Stage/WallStage.cs']}
    (OUT / 'integration.json').write_text(json.dumps(info,indent=2), encoding='utf-8')
    print(project)


if __name__ == '__main__': main()
