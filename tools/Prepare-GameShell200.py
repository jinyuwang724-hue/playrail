from pathlib import Path
import shutil,hashlib,json
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'artifacts/game-shell-200';OLD=ROOT/'artifacts/special-keys-197/src';SRC=OUT/'src'
OUT.mkdir(exist_ok=True)
if not (SRC/'frontend/library-native').exists():
    shutil.copytree(OLD/'frontend/library-native',SRC/'frontend/library-native',ignore=shutil.ignore_patterns('bin','obj','b','o','AppPackages','__pycache__'))
for name in ('ShellGuard.cs','WindowsShellSystem.cs','Program.cs'):
    for folder in ('base/native/ClawShell','src/native/ClawShell'):
        target=OUT/folder/name;target.parent.mkdir(parents=True,exist_ok=True)
        if not target.exists():shutil.copyfile(ROOT/'native/ClawShell'/name,target)
print('Prepared game-shell 200 from installed 199 source')
