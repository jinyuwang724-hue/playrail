"""Reproduce the touch update from the installed 0.1.177 source, preserving the older workspace checkout."""
from pathlib import Path
import argparse
import difflib
import hashlib
import json
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'artifacts/all-pages-copy/src/frontend/library-native'
OUT = ROOT / 'artifacts/touch-pages'
SRC = OUT / 'src/frontend/library-native'
PATCH = ROOT / 'tests/TouchPages/touch-pages.patch'
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()


def snapshot():
    changes, patches = {}, []
    for path in sorted((SRC / 'ClawLibrary').rglob('*')):
        if not path.is_file() or path.suffix not in ('.cs', '.appxmanifest'):
            continue
        if any(p in ('bin', 'obj') for p in path.relative_to(SRC).parts):
            continue
        rel = path.relative_to(SRC).as_posix()
        old = BASE / rel
        if old.exists() and old.read_bytes() == path.read_bytes():
            continue
        changes[rel] = {'baseSha256': digest(old) if old.exists() else None, 'candidateSha256': digest(path)}
        before = old.read_text(encoding='utf-8-sig').splitlines(keepends=True) if old.exists() else []
        after = path.read_text(encoding='utf-8-sig').splitlines(keepends=True)
        patches.extend(difflib.unified_diff(before, after, fromfile='a/' + rel if old.exists() else '/dev/null', tofile='b/' + rel))
    PATCH.parent.mkdir(parents=True, exist_ok=True)
    PATCH.write_text(''.join(patches), encoding='utf-8', newline='\n')
    upgrade_path = OUT / 'preview-baseline.json'
    upgrade = json.loads(upgrade_path.read_text(encoding='utf-8-sig')) if upgrade_path.exists() else None
    (OUT / 'integration.json').write_text(json.dumps({
        'sourceBaseline': str(BASE), 'sourceVersion': '0.1.177.0',
        'baseVersion': upgrade['version'] if upgrade else '0.1.177.0', 'version': '0.1.179.0',
        'baseExecutableSha256': upgrade['executableSha256'] if upgrade else digest(BASE / 'b/x64/Release/win-x64/publish/ClawLibrary.exe'),
        'changes': changes, 'workspaceSource': str(SRC),
        'note': 'The installed source baseline is newer than the root checkout; full source and replayable patch are preserved without replacing unrelated workspace edits.'
    }, ensure_ascii=False, indent=2), encoding='utf-8')
    print(f'Prepared {len(changes)} source changes and replayable patch')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--snapshot', action='store_true', help='Record the current reviewed candidate instead of creating it')
    args = parser.parse_args()
    if not args.snapshot:
        if SRC.exists():
            raise SystemExit('Candidate already exists; use --snapshot to refresh metadata without replacing it.')
        if not PATCH.exists():
            raise SystemExit('Reviewed patch is missing.')
        shutil.copytree(BASE, SRC, ignore=shutil.ignore_patterns('b', 'o', 'bin', 'obj', 'AppPackages'))
        command = ['git', 'apply', '--ignore-space-change', '--whitespace=nowarn',
                   '--directory=' + SRC.relative_to(ROOT).as_posix(), str(PATCH)]
        subprocess.run(command[:2] + ['--check'] + command[2:], cwd=ROOT, check=True)
        subprocess.run(command, cwd=ROOT, check=True)
    snapshot()
