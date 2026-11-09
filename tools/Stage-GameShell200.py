from pathlib import Path
import difflib,hashlib,json,subprocess
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'artifacts/game-shell-200';SRC=OUT/'src';OLD=ROOT/'artifacts/special-keys-197/src'
changes={};patch=[];sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
for folder in ('native','frontend/library-native'):
    for p in sorted((SRC/folder).rglob('*')):
        if not p.is_file() or p.suffix not in ('.cs','.appxmanifest') or any(x in ('bin','obj','b','o','Payload') for x in p.relative_to(SRC/folder).parts):continue
        name=p.relative_to(SRC).as_posix();base=(OUT/'base'/name) if folder=='native' else OLD/name
        if base.exists() and p.read_bytes()==base.read_bytes():continue
        changes[name]={'baseSha256':sha(base) if base.exists() else None,'candidateSha256':sha(p)}
        patch.extend(difflib.unified_diff(base.read_text(encoding='utf-8-sig').splitlines(True) if base.exists() else [],p.read_text(encoding='utf-8-sig').splitlines(True),fromfile='a/'+name if base.exists() else '/dev/null',tofile='b/'+name))
path=ROOT/'tests/SpecialKeys197/game-shell-200.patch';path.write_text(''.join(patch),encoding='utf-8')
(OUT/'integration.json').write_text(json.dumps({'baseVersion':'0.1.199.0','version':'0.1.200.0','changes':changes},indent=2),encoding='utf-8')
replay=OUT/'patch-replay';replay.mkdir(exist_ok=True)
for name,change in changes.items():
    base=OUT/'base'/name if name.startswith('native/') else OLD/name;target=replay/name;target.parent.mkdir(parents=True,exist_ok=True)
    if base.exists():target.write_text(base.read_text(encoding='utf-8-sig'),encoding='utf-8')
    elif target.exists():target.unlink()
subprocess.run(['git','apply','--whitespace=nowarn','--unsafe-paths','--directory='+replay.relative_to(ROOT).as_posix(),str(path)],cwd=ROOT,check=True)
for name in changes:assert (replay/name).read_text(encoding='utf-8-sig')==(SRC/name).read_text(encoding='utf-8-sig'),name
lib=SRC/'frontend/library-native/ClawLibrary'
assert sha(lib/'Payload/agent/agent.zip')==sha(OLD/'frontend/library-native/ClawLibrary/Payload/agent/agent.zip')
assert sha(lib/'Stage/WallStage.Switcher.cs')==sha(OLD/'frontend/library-native/ClawLibrary/Stage/WallStage.Switcher.cs')
assert sha(lib/'Payload/shell/ClawShell.exe')==sha(OUT/'ClawShell.exe')
print(json.dumps({'patchReplays':True,'switcherReusedUnchanged':True,'backendPayloadUnchanged':True,'changedFiles':len(changes)}))
