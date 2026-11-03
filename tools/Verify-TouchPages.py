"""Verify the touch release against the current copy-reviewed source and runtime evidence."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import shutil

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/touch-pages'
SRC = OUT / 'src/frontend/library-native'
metadata = json.loads((OUT / 'integration.json').read_text(encoding='utf-8'))
BASE = Path(metadata['sourceBaseline'])
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
read = lambda name: json.loads((OUT / name).read_text(encoding='utf-8-sig'))
assert digest(ROOT / 'frontend/library-native/ClawLibrary/Engine/TouchPointer.cs') == digest(SRC / 'ClawLibrary/Engine/TouchPointer.cs')
for rel, entry in metadata['changes'].items():
    assert digest(SRC / rel) == entry['candidateSha256'], rel
    if entry['baseSha256']:
        assert digest(BASE / rel) == entry['baseSha256'], rel

def inputs(root):
    return {p.relative_to(root).as_posix(): p for p in (root / 'ClawLibrary').rglob('*')
            if p.is_file() and not any(s in ('bin', 'obj') for s in p.relative_to(root).parts)}

before, after = inputs(BASE), inputs(SRC)
actual = {rel for rel in before.keys() | after.keys()
          if rel not in before or rel not in after or digest(before[rel]) != digest(after[rel])}
assert actual == set(metadata['changes']), actual - set(metadata['changes'])
assert all(not name.startswith(('ClawLibrary/Payload/', 'ClawLibrary/Assets/')) for name in actual)

# Validate that the durable patch really reproduces every changed source, including new files.
# Use a fresh workspace child for each replay; cleanup stays inside this artifact directory.
import tempfile
with tempfile.TemporaryDirectory(prefix='touch-replay-', dir=OUT) as folder:
    replay = Path(folder)
    assert replay.resolve().is_relative_to(OUT.resolve())
    for rel, entry in metadata['changes'].items():
        if entry['baseSha256']:
            target = replay / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(BASE / rel, target)
    subprocess.run(['git', 'apply', '--ignore-space-change', '--whitespace=nowarn',
                    '--directory=' + replay.relative_to(ROOT).as_posix(),
                    str(ROOT / 'tests/TouchPages/touch-pages.patch')], cwd=ROOT, check=True,
                   capture_output=True, text=True)
    for rel in metadata['changes']:
        assert (replay / rel).read_text(encoding='utf-8-sig') == (SRC / rel).read_text(encoding='utf-8-sig'), rel

touch, editing, layout, install = (read(name) for name in
    ('touch-probe.json', 'settings-edit-probe.json', 'copy-review-probe.json', 'installation.json'))
assert not touch['error'] and all(x['passed'] for x in touch['checks']), touch
assert len(editing) == 55 and all(x.get('passed') for x in editing), editing
assert not layout['error'] and len(layout['pages']) == 32
assert all(not p['layoutIssues'] and not p['clippedStatus'] for p in layout['pages'])
assert install['afterVersion'] == metadata['version'] and not install['changedPreferences']
cache = Path(os.environ['LOCALAPPDATA']) / 'Packages/ClawToolbox.Library_ba3r68zr466za/LocalCache'
preferences = {name: digest(cache / name).upper() == install['preferencesBefore'][name]
               for name in ('ui.ini', 'background.ini', 'shots.ini', 'controlcenter.ini')}
assert all(preferences.values()), preferences
engine = (OUT / 'touch-engine-tests.log').read_text(encoding='utf-8-sig')
queue = (OUT / 'settings-queue-tests.log').read_text(encoding='utf-8-sig')
assert '18 checks passed' in engine
report = {'version': metadata['version'], 'changedSourceFiles': len(actual),
          'pointerOwnershipChecks': engine.count('PASS '), 'existingQueueAndGestureChecks': queue.count('PASS '),
          'nativeTouchChecks': len(touch['checks']), 'existingInputChecks': len(editing),
          'languageLayoutChecks': len(layout['pages']), 'preferencesPreserved': preferences,
          'assetsAndPayloadPreserved': True, 'patchReproducesCandidate': True,
          'physicalTouchInjection': False,
          'verificationMethod': 'Native WinUI page handlers with in-memory fixtures; actual mouse click/drag through the shared Pointer handlers; no hardware setting writes.'}
(OUT / 'verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=True))
