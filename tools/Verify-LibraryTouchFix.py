"""Verify the minimal repair against the installed 0.1.183 source baseline."""
from pathlib import Path
import hashlib
import json
import os
import re
import subprocess
import tempfile
import shutil
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/library-touch-fix-184'
SRC = OUT / 'src/frontend/library-native'
metadata = json.loads((OUT / 'integration.json').read_text(encoding='utf-8'))
BASE = Path(metadata['sourceBaseline'])
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()


def sources(root):
    return {p.relative_to(root).as_posix(): p for p in (root / 'ClawLibrary').rglob('*')
            if p.is_file() and not any(part in ('bin', 'obj') for part in p.relative_to(root).parts)}


before, after = sources(BASE), sources(SRC)
actual = {name for name in before.keys() | after.keys()
          if name not in before or name not in after or digest(before[name]) != digest(after[name])}
assert actual == set(metadata['changes']), actual
for name, entry in metadata['changes'].items():
    assert digest(SRC / name) == entry['candidateSha256'], name
    assert (digest(BASE / name) if (BASE / name).exists() else None) == entry['baseSha256'], name

with tempfile.TemporaryDirectory(prefix='touch-replay-', dir=OUT) as folder:
    replay = Path(folder)
    assert replay.resolve().is_relative_to(OUT.resolve())
    for name, entry in metadata['changes'].items():
        if entry['baseSha256']:
            target = replay / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(BASE / name, target)
    subprocess.run(['git', 'apply', '--ignore-space-change', '--whitespace=nowarn',
                    '--directory=' + replay.relative_to(ROOT).as_posix(),
                    str(ROOT / 'tests/LibraryTouchFix/library-touch-fix.patch')], cwd=ROOT,
                   check=True, capture_output=True, text=True)
    for name in metadata['changes']:
        assert (replay / name).read_text(encoding='utf-8-sig') == (SRC / name).read_text(encoding='utf-8-sig'), name

if '--source-only' in sys.argv:
    print(json.dumps({'baseVersion': metadata['baseVersion'], 'changedFiles': sorted(actual),
                      'otherSourcesAssetsAndPayloadPreserved': True, 'patchReproducesCandidate': True}))
    raise SystemExit(0)

read = lambda name: json.loads((OUT / name).read_text(encoding='utf-8-sig'))
library = read('library-touch-probe.json')
touch = read('touch-probe.json')
assert not library['error'] and all(item['passed'] for item in library['checks']), library
assert not touch['error'] and all(item['passed'] for item in touch['checks']), touch
assert all(any(c['name'].startswith(category + ':') for c in library['checks']) for category in ('all', 'steam', 'local'))
navigation = (SRC / 'ClawLibrary/Stage/WallStage.Touch.Navigation.cs').read_text(encoding='utf-8-sig')
assert not re.search(r'DrawChevron\(|\bBack\(\)', navigation)
assert digest(SRC / 'ClawLibrary/Stage/WallStage.QuickPanel.cs') == digest(BASE / 'ClawLibrary/Stage/WallStage.QuickPanel.cs')
install = read('installation.json')
assert install['beforeVersion'] == metadata['baseVersion'] and install['afterVersion'] == metadata['version']
assert not install['changedPreferences']
cache = Path(os.environ['LOCALAPPDATA']) / 'Packages/ClawToolbox.Library_ba3r68zr466za/LocalCache'
preferences = {name: digest(cache / name).upper() == old for name, old in install['preferencesBefore'].items() if name.endswith('.ini')}
assert all(preferences.values()), preferences
report = {'sourceVersion': metadata['sourceVersion'], 'baseVersion': metadata['baseVersion'], 'version': metadata['version'],
          'libraryClickAndNoBackChecks': len(library['checks']), 'sharedTouchRegressionChecks': len(touch['checks']),
          'latestQuickPanelPreserved': True, 'otherSourcesAssetsAndPayloadPreserved': True,
          'patchReproducesCandidate': True, 'preferencesPreserved': preferences,
          'removedAddedBackIconsAndHotspots': True, 'categoriesCovered': ['all', 'steam', 'local'],
          'deviceCoverage': 'Real WinUI hit-testing and activation; UI mouse clicks share Pointer routing. No physical touch injection.'}
(OUT / 'verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=True))
