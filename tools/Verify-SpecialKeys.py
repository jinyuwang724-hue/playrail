from pathlib import Path
import hashlib,json,os,subprocess,zipfile
ROOT=Path(__file__).resolve().parents[1];OUT=ROOT/'artifacts/special-keys-197';SRC=OUT/'src';LIB=SRC/'frontend/library-native/ClawLibrary'
BASE_UI=ROOT/'artifacts/overlay-focus-fix-191/src/frontend/library-native'
report=json.loads((OUT/'integration.json').read_text(encoding='utf-8'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
replay=OUT/'patch-replay';replay.mkdir(exist_ok=True)
for name,change in report['changes'].items():
    assert sha(SRC/name)==change['candidateSha256'],name
    if not name.endswith(('.py','.cs','.appxmanifest')):continue
    if name.startswith('plugin/'):base=OUT/'base'/name
    elif name.startswith('native/'):base=OUT/'base'/name
    else:base=BASE_UI/name.removeprefix('frontend/library-native/')
    target=replay/name;target.parent.mkdir(parents=True,exist_ok=True)
    if base.exists():target.write_text(base.read_text(encoding='utf-8-sig'),encoding='utf-8')
    elif target.exists():target.unlink()
subprocess.run(['git','apply','--whitespace=nowarn','--unsafe-paths','--directory='+replay.relative_to(ROOT).as_posix(),str(ROOT/'tests/SpecialKeys197/special-keys.patch')],cwd=ROOT,check=True)
for name in report['changes']:
    if name.endswith(('.py','.cs','.appxmanifest')):assert (replay/name).read_text(encoding='utf-8-sig')==(SRC/name).read_text(encoding='utf-8-sig'),name
with zipfile.ZipFile(LIB/'Payload/agent/agent.zip') as new,zipfile.ZipFile(BASE_UI/'ClawLibrary/Payload/agent/agent.zip') as old:
    replacements={'app/'+name.removeprefix('plugin/') for name in report['changes'] if name.startswith('plugin/')}
    for name in replacements:assert new.read(name)==(SRC/'plugin'/name.removeprefix('app/')).read_bytes(),name
    for name in old.namelist():
        if name not in replacements:assert old.read(name)==new.read(name),name
home=Path(os.environ['LOCALAPPDATA'])/'ClawToolbox/app'
for name in report['changes']:
    if name.startswith('plugin/'):assert sha(home/name.removeprefix('plugin/'))==sha(SRC/name),name
assert sha(LIB/'Payload/shell/ClawShell.exe')==sha(OUT/'ClawShell.exe')
print(json.dumps({'patchReplays':True,'backendAndPayloadMatch':True,'otherPayloadEntriesPreserved':True,'changedFiles':len(report['changes']),'version':report['version']}))
