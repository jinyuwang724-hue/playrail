"""Capture and copy the installed 0.1.196 baseline without overwriting other work."""
from pathlib import Path
import hashlib
import json
import os
import shutil

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'artifacts/special-keys-197'
BASE_UI=ROOT/'artifacts/overlay-focus-fix-191/src/frontend/library-native'
APP=Path(os.environ['LOCALAPPDATA'])/'ClawToolbox/app'

def main():
    OUT.mkdir(parents=True,exist_ok=True)
    source=OUT/'src'; source.mkdir(exist_ok=True)
    def ignore(folder,names):
        path=Path(folder)
        return [n for n in names if (path==BASE_UI and n in ('b','o','AppPackages')) or (path==BASE_UI/'ClawLibrary' and n in ('bin','obj'))]
    if not (source/'frontend/library-native').exists():
        shutil.copytree(BASE_UI,source/'frontend/library-native',ignore=ignore)
    for file in APP.glob('*.py'):
        for base in (OUT/'base/plugin',source/'plugin'):
            base.mkdir(parents=True,exist_ok=True)
            if not (base/file.name).exists():shutil.copy2(file,base/file.name)
    for base in (OUT/'base/plugin',source/'plugin'):shutil.copy2(APP/'plugin.json',base/'plugin.json')
    for file in ('ClawRouter.cs','FirmwareShortcutFilter.cs','GameBarGuard.cs'):
        for base in (OUT/'base/native',source/'native'):
            base.mkdir(parents=True,exist_ok=True)
            if not (base/file).exists():shutil.copy2(ROOT/'native'/file,base/file)
    for base in (OUT/'base/native/ClawShell',source/'native/ClawShell'):
        if not base.exists():shutil.copytree(ROOT/'native/ClawShell',base)
    print(source)

if __name__=='__main__':main()
