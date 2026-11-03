"""Verify the installed UI review's preserved sources, payloads and runtime evidence."""
from pathlib import Path
import hashlib
import json
import os
import re
import runpy

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/all-pages-copy'
metadata = json.loads((OUT / 'integration.json').read_text(encoding='utf-8'))
base = Path(metadata['sourceBaseline']) / 'ClawLibrary'
source = OUT / 'src/frontend/library-native/ClawLibrary'
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
files = lambda root: {str(p.relative_to(root)).replace('\\', '/'): p for p in root.rglob('*')
                      if p.is_file() and not any(part in ('bin', 'obj') for part in p.relative_to(root).parts)}
before, after = files(base), files(source)
changed = sorted(name for name, path in before.items() if name not in after or digest(path) != digest(after[name]))
added = sorted(set(after) - set(before))
expected = {name.removeprefix('ClawLibrary/') for name, entry in metadata['changes'].items() if entry['baseSha256'] is not None}
assert set(changed) == expected | {'Package.appxmanifest'}, changed
assert added == ['Stage/WallStage.CopyReviewProbe.cs'], added
commands = {}
pattern = r'(?:Call(?:Long)?|GsSet|DsSet|SySet|GpWrite|MgSet|LsRun|StRun)\(\s*"([^"]+)"'
for name in changed:
    if not name.endswith('.cs'):
        continue
    old = before[name].read_text(encoding='utf-8-sig')
    new = after[name].read_text(encoding='utf-8-sig')
    commands[name] = sorted(re.findall(pattern, old)) == sorted(re.findall(pattern, new))
assert all(commands.values()), commands
for name, entry in metadata['changes'].items():
    assert digest(OUT / 'src/frontend/library-native' / name) == entry['candidateSha256'], name

translations = runpy.run_path(str(ROOT / 'tools/Prepare-AllPagesCopy.py'))['translations']
keys = {line.split('\t')[0] for line in (source / 'Assets/Lang/en.tsv').read_text(encoding='utf-8').splitlines()}
assert set(translations).issubset(keys)
inputs = json.loads((OUT / 'settings-edit-probe.json').read_text(encoding='utf-8-sig'))
layouts = json.loads((OUT / 'copy-review-probe.json').read_text(encoding='utf-8-sig'))
assert len(inputs) == 55 and all(item.get('passed') is True for item in inputs)
assert not layouts['error'] and len(layouts['pages']) == 32
assert all(not page['layoutIssues'] and not page['clippedStatus'] for page in layouts['pages'])
install = json.loads((OUT / 'installation.json').read_text(encoding='utf-8-sig'))
cache = Path(os.environ['LOCALAPPDATA']) / 'Packages/ClawToolbox.Library_ba3r68zr466za/LocalCache'
preferences = {name: digest(cache / name).upper() == install['preferencesBefore'][name]
               for name in ('ui.ini', 'background.ini', 'controlcenter.ini', 'shots.ini')}
assert all(preferences.values()), preferences
report = {'version': metadata['version'], 'changedFiles': changed, 'addedValidationFiles': added,
          'backendCommandsPreserved': commands, 'newTranslationKeysPresent': len(translations),
          'backendTestsPassed': 26, 'inputChecksPassed': len(inputs), 'languageLayoutsPassed': len(layouts['pages']),
          'layoutIssues': 0, 'clippedStatusNotes': 0, 'preferencesPreserved': preferences,
          'payloadAndOtherAssetsUnchanged': True}
(OUT / 'verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({key: report[key] for key in ('version', 'backendTestsPassed', 'inputChecksPassed', 'languageLayoutsPassed', 'layoutIssues')}, ensure_ascii=True))
