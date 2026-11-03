"""Record the exact 196-to-197 integration and stage the unchanged payload entries."""
from pathlib import Path
import difflib,hashlib,json,shutil,zipfile,re
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'artifacts/special-keys-197';SRC=OUT/'src'
BASE_UI=ROOT/'artifacts/overlay-focus-fix-191/src/frontend/library-native'
LIB=SRC/'frontend/library-native/ClawLibrary'
digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
changes={};patch=[]
for folder,base in [('plugin',OUT/'base/plugin'),('native',OUT/'base/native'),('frontend/library-native',BASE_UI)]:
    for p in sorted((SRC/folder).rglob('*')):
        if not p.is_file() or p.suffix not in ('.cs','.py','.appxmanifest') or any(x in ('bin','obj','b','o','Payload') for x in p.relative_to(SRC/folder).parts):continue
        name=p.relative_to(SRC).as_posix();old=base/p.relative_to(SRC/folder)
        if old.exists() and old.read_bytes()==p.read_bytes():continue
        changes[name]={'baseSha256':digest(old) if old.exists() else None,'candidateSha256':digest(p)}
        patch.extend(difflib.unified_diff(old.read_text(encoding='utf-8-sig').splitlines(True) if old.exists() else [],p.read_text(encoding='utf-8-sig').splitlines(True),fromfile='a/'+name if old.exists() else '/dev/null',tofile='b/'+name))
binary='bin/ClawRouter-0.17.58-specialkeys.exe'
(SRC/'plugin/bin').mkdir(exist_ok=True)
shutil.copyfile(OUT/Path(binary).name,SRC/'plugin'/binary)
changes['plugin/'+binary]={'baseSha256':None,'candidateSha256':digest(SRC/'plugin'/binary)}
replacements={'app/'+name.removeprefix('plugin/'):(SRC/name).read_bytes() for name in changes if name.startswith('plugin/')}
payload=LIB/'Payload/agent/agent.zip';temp=payload.with_suffix('.new')
with zipfile.ZipFile(BASE_UI/'ClawLibrary/Payload/agent/agent.zip') as old,zipfile.ZipFile(temp,'w',zipfile.ZIP_DEFLATED) as new:
    for info in old.infolist():new.writestr(info,replacements.pop(info.filename,old.read(info.filename)))
    for name,data in replacements.items():new.writestr(name,data)
temp.replace(payload)
shutil.copyfile(OUT/'ClawShell.exe',LIB/'Payload/shell/ClawShell.exe')
(ROOT/'tests/SpecialKeys197/special-keys.patch').write_text(''.join(patch),encoding='utf-8')
version=re.search(r'Version="(0\.1\.\d+\.0)"',(LIB/'Package.appxmanifest').read_text(encoding='utf-8')).group(1)
(OUT/'integration.json').write_text(json.dumps({'baseVersion':'0.1.196.0','version':version,'changes':changes},indent=2),encoding='utf-8')
print('Staged',len(changes),'changed files')
