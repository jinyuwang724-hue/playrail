"""Remove added back icons and repair card clicks while a library tab owns focus."""
from pathlib import Path
import difflib
import hashlib
import json
import shutil

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / '.claude/worktrees/elegant-tu-7265c7/artifacts/qp/src/frontend/library-native'
OUT = ROOT / 'artifacts/library-touch-fix-184'
SRC = OUT / 'src/frontend/library-native'
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()


def replace(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError('Ambiguous source anchor: ' + old[:100])
    return text.replace(old, new, 1)


def main():
    if not SRC.exists():
        shutil.copytree(BASE, SRC, ignore=shutil.ignore_patterns('b', 'o', 'bin', 'obj', 'AppPackages'))
    stage = 'ClawLibrary/Stage/'
    edits = {
        stage + 'WallStage.Touch.Navigation.cs': [
            ('// 0 = hidden, 1 = back, 2 = control centre. No extra explanatory text.', '// 0 = hidden, 2 = home control-centre menu. Child pages have no added back button.'),
            (': _ob != null ? (_obStep is ObStep.Account or ObStep.Home ? 1 : 0)', ': _ob != null ? 0'),
            ('|| _stack.Count > 1 ? 1 : 2;', '|| _stack.Count > 1 ? 0 : 2;'),
            ('private bool TouchNavigationHit(double x, double y) => TouchFilterHit(x, y) || TouchNavigationMode != 0\n        && x >= 0 && (x < 40 * TouchScale || TouchShotStyleHit(x, y)) && y >= 24 * TouchScale && y < 72 * TouchScale;',
             'private bool TouchNavigationHit(double x, double y) => TouchFilterHit(x, y) || TouchShotStyleHit(x, y)\n        || TouchNavigationMode == 2 && x >= 0 && x < 40 * TouchScale && y >= 24 * TouchScale && y < 72 * TouchScale;'),
            ('if (TouchNavigationMode == 1) Back(); else ToggleControlCenter();', 'ToggleControlCenter();'),
            ('_touchNavigation.IsVisible = mode != 0;\n        if (mode == 0) return;', '_touchNavigation.IsVisible = mode == 2 || filter;\n        if (mode == 0 && !filter) return;'),
            ('if (mode == 1) DrawChevron(ds, 23, 24, true, CcText);\n            else for (int row = 0; row < 3; row++)', 'if (mode == 2) for (int row = 0; row < 3; row++)'),
        ],
        stage + 'WallStage.cs': [
            ('        var key = hit.Slot.Key;\n        if (FocusKey != key)',
             '        var key = hit.Slot.Key;\n        bool fromRail = _railFocused;\n        if (fromRail)\n        {\n            // A card hit transfers input ownership before Activate dispatches the action.\n            _railFocused = false;\n            _railFocus = -1;\n            RepaintRail();\n        }\n        if (FocusKey != key)'),
            ('            Focus(key);\n        }\n        Activate();', '            Focus(key);\n        }\n        else if (fromRail) Focus(key);\n        Activate();'),
            ('        if (Program.Args.Contains("--touch-probe")) _ = TouchProbeAsync();',
             '        if (Program.Args.Contains("--touch-probe")) _ = TouchProbeAsync();\n        if (Program.Args.Contains("--library-touch-probe")) _ = LibraryTouchProbeAsync();'),
        ],
        stage + 'WallStage.TouchProbe.cs': [
            ('Check("navigation: control centre exposes back", TouchNavigationMode == 1 && TouchNavigationHit(20 * _ccK, 48 * _ccK));',
             'Check("navigation: control centre has no added back button or hotspot", TouchNavigationMode == 0 && !TouchNavigationHit(20 * _ccK, 48 * _ccK));'),
        ],
        'ClawLibrary/Package.appxmanifest': [('Version="0.1.183.0"', 'Version="0.1.185.0"')],
    }
    changes, patches = {}, []
    for rel, replacements in edits.items():
        text = (BASE / rel).read_text(encoding='utf-8-sig')
        for old, new in replacements:
            text = replace(text, old, new)
        target = SRC / rel
        if target.read_text(encoding='utf-8-sig') != text:
            target.write_text(text, encoding='utf-8')
    probe = stage + 'WallStage.LibraryTouchProbe.cs'
    shutil.copy2(ROOT / 'tests/LibraryTouchFix/WallStage.LibraryTouchProbe.cs', SRC / probe)
    for rel in list(edits) + [probe]:
        base = BASE / rel
        target = SRC / rel
        changes[rel] = {'baseSha256': digest(base) if base.exists() else None, 'candidateSha256': digest(target)}
        before = base.read_text(encoding='utf-8-sig').splitlines(keepends=True) if base.exists() else []
        patches.extend(difflib.unified_diff(before, target.read_text(encoding='utf-8-sig').splitlines(keepends=True),
                                          fromfile='a/' + rel if base.exists() else '/dev/null', tofile='b/' + rel))
    (ROOT / 'tests/LibraryTouchFix/library-touch-fix.patch').write_text(''.join(patches), encoding='utf-8', newline='\n')
    upgrade_path = OUT / 'upgrade-baseline.json'
    upgrade = json.loads(upgrade_path.read_text(encoding='utf-8-sig')) if upgrade_path.exists() else None
    (OUT / 'integration.json').write_text(json.dumps({
        'sourceBaseline': str(BASE), 'sourceVersion': '0.1.183.0',
        'baseVersion': upgrade['version'] if upgrade else '0.1.183.0', 'version': '0.1.185.0',
        'baseExecutableSha256': upgrade['executableSha256'] if upgrade else digest(BASE / 'b/x64/Release/win-x64/publish/ClawLibrary.exe'),
        'changes': changes,
    }, ensure_ascii=False, indent=2), encoding='utf-8')
    print('Prepared', len(changes), 'source changes')


if __name__ == '__main__':
    main()
