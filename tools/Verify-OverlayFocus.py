"""Verify the review patch, preserved baselines and packaged input/agent payload."""
from pathlib import Path
import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('overlay_prepare', ROOT / 'tools/Prepare-OverlayFocus.py')
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
m.snapshot()
report = json.loads((m.OUT / 'integration.json').read_text())
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
baselines = {'frontend/library-native/ClawLibrary':m.BASE, 'plugin':m.APP,
             'native/InputLayer':ROOT / 'artifacts/enhanced-sleep/final/src/native/InputLayer'}
with tempfile.TemporaryDirectory(prefix='patch-replay-', dir=m.OUT) as folder:
    replay = Path(folder)
    for name, item in report['changes'].items():
        for prefix, base in baselines.items():
            if name.startswith(prefix+'/') and item['baseSha256']:
                old = base / name[len(prefix)+1:]
                assert digest(old) == item['baseSha256'], name
                dest = replay / name; dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(old,dest)
        if name.endswith('.py'): compile((m.SRC / name).read_bytes(), name, 'exec')
    subprocess.run(['git','apply','--ignore-space-change','--whitespace=nowarn',
                    '--directory='+replay.relative_to(ROOT).as_posix(),
                    str(ROOT/'tests/OverlayFocus/overlay-focus.patch')],cwd=ROOT,check=True,capture_output=True,text=True)
    for name in report['changes']:
        if name.endswith(('.cs','.py','.appxmanifest')):
            assert (replay/name).read_text(encoding='utf-8-sig') == (m.SRC/name).read_text(encoding='utf-8-sig'),name

payload = m.LIB / 'Payload/agent/agent.zip'
with zipfile.ZipFile(payload) as z:
    for name in report['changes']:
        if name.startswith('plugin/'):
            key='app/'+name.removeprefix('plugin/')
            assert z.read(key) == (m.SRC/name).read_bytes(), key
with zipfile.ZipFile(m.BASE/'Payload/agent/agent.zip') as old, zipfile.ZipFile(payload) as new:
    replacements={'app/'+n.removeprefix('plugin/') for n in report['changes'] if n.startswith('plugin/')}
    for name in old.namelist():
        if name not in replacements: assert old.read(name) == new.read(name), name

assert (m.LIB/'MainWindow.Overlay.cs').read_bytes() == (ROOT/'tests/OverlayFocus/MainWindow.Overlay.cs').read_bytes()
installation=json.loads((m.OUT/'installation.json').read_text(encoding='utf-8-sig'))
assert not installation['changedPreferences']
if installation['afterVersion']=='0.1.196.0':
    exe=Path(os.environ['ProgramFiles'])/'WindowsApps/ClawToolbox.Library_0.1.196.0_x64__ba3r68zr466za/ClawLibrary.exe'
    assert digest(exe).upper()==installation['exeSha256']
print(json.dumps({'patchReproducesCandidate':True,'payloadMatchesBackend':True,'otherPayloadEntriesPreserved':True,
                  'changedFiles':len(report['changes']),'installedVersion':installation['afterVersion'],'preferencesPreserved':True}))
