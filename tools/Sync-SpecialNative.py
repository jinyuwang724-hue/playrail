"""Update only root native files whose original contents were checked."""
from pathlib import Path
import shutil
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'artifacts/special-keys-197'
names=('ClawRouter.cs','FirmwareShortcutFilter.cs','RawKeyboardSource.cs','SessionModeProbe.cs','ClawShell/ShellGuard.cs','ClawShell/WindowsShellSystem.cs')
for name in names:
    source=OUT/'src/native'/name;target=ROOT/'native'/name;base=OUT/'base/native'/name
    if target.exists() and target.read_bytes()!=source.read_bytes():
        if not base.exists() or target.read_bytes()!=base.read_bytes():raise RuntimeError('Root source changed: '+name)
for name in names:shutil.copyfile(OUT/'src/native'/name,ROOT/'native'/name)
print('Synced',len(names),'reviewed native source files')
