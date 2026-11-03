"""Snapshot the installed UI's source and apply the system-page-only copy pilot."""
from pathlib import Path
import difflib
import hashlib
import json
import shutil
import sys

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / '.claude/worktrees/sharp-pike-a76b59/artifacts/ws/frontend/library-native'
OUT = ROOT / 'artifacts/system-copy-pilot'
SRC = OUT / 'src/frontend/library-native'


def replace(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError('Ambiguous source anchor: ' + old[:100])
    return text.replace(old, new, 1)


def system(text):
    text = replace(text, '                var animation = v["powerAnimation"] as JsonObject;',
                   '                _syRows.Add(new PerfRow { Section = true, Title = "睡眠与唤醒" });\n                var animation = v["powerAnimation"] as JsonObject;')
    text = replace(text, 'void EnhancedRow(string title, string flag, string method, string detail)',
                   'void EnhancedRow(string title, string flag, string method, string detail = "", bool nested = false)')
    text = replace(text, 'Disabled = !available, Value = available ? Str(enhanced?["error"]) ?? detail : "睡眠后台暂未连接",',
                   'Indent = nested ? 20 : 0,\n                        Disabled = !available, Value = available ? Str(enhanced?["error"]) ?? detail : "睡眠后台暂未连接",')
    text = replace(text, 'EnhancedRow("增强睡眠", "enabled", "set_enhanced_sleep", "减少误唤醒 · 48 小时后休眠");',
                   'EnhancedRow("增强睡眠", "enabled", "set_enhanced_sleep", "抑制自动唤醒");')
    text = replace(text, 'EnhancedRow("睡眠时暂停游戏", "suspendGames", "set_sleep_games", "唤醒后继续 · 检测到反作弊时跳过");',
                   'EnhancedRow("暂停游戏", "suspendGames", "set_sleep_games", "检测到反作弊时跳过", nested: true);')
    text = replace(text, 'EnhancedRow("睡眠时暂停指纹识别", "fingerprint", "set_sleep_fingerprint", "唤醒时恢复指纹识别");',
                   'EnhancedRow("暂停指纹识别", "fingerprint", "set_sleep_fingerprint", nested: true);')
    text = text.replace('"睡眠多久后转休眠"', '"睡眠转休眠"').replace('"增强睡眠期间为 48 小时"', '"48 小时 · 增强睡眠"')
    text = text.replace('"这台机器没有开启休眠"', '"休眠未启用"').replace('"电池下多久关屏"', '"电池供电时关屏"')
    text = text.replace('"现在是自定义时间"', '"当前为自定义时间"').replace('"现在是从不关屏"', '"当前为从不关屏"')
    display_start = text.index('                int di = Array.IndexOf(SyDisplay,')
    display_end = text.index('\n            }', display_start)
    display = text[display_start:display_end]
    text = text[:display_start] + text[display_end:]
    text = replace(text, '                _syRows.Add(new PerfRow { Section = true, Title = "睡眠与唤醒" });',
                   display + '\n                _syRows.Add(new PerfRow { Section = true, Title = "睡眠与唤醒" });')
    text = text.replace('Title = "更新与维护"', 'Title = "更新"').replace('"查看全部更新"', '"系统与驱动更新"')
    text = text.replace('"系统 · 全部更新"', '"系统 · 更新"')
    text = replace(text, 'SyAct("系统与驱动更新", "updates", SyUpdateSummary(v),',
                   'SyAct("系统与驱动更新", "updates", SyUpdateSummary(v) + "  ›",')
    text = replace(text, '        _syRows.Add(new PerfRow { Section = true, Title = "更新" });\n        string checkText',
                   '        string checkText')
    text = replace(text, 'PaintRowList(ds, _syRows, _syFocus, _settingsTargets)',
                   'PaintRowList(ds, _syRows, _syFocus, _settingsTargets, numbers: false, leadingNotes: !_syUpdatesOpen)')
    return text


def perf(text):
    text = replace(text, '        public string Value = "";', '        public string Value = "";\n        public float Indent; // Optional child-row indentation; used by the system-page pilot.')
    text = replace(text, 'bool numbers = true, float listWidth = PerfRightW)', 'bool numbers = true, float listWidth = PerfRightW, bool leadingNotes = false)')
    if 'r.Glyph != null ? 50 : 36, cy, 400);' in text:
        text = replace(text, 'r.Glyph != null ? 50 : 36, cy, 400);', '(r.Glyph != null ? 50 : 36) + r.Indent, cy, 400);')
    else:
        text = replace(text, ' * a), 36, cy, 400);', ' * a), 36 + r.Indent, cy, 400);')
    text = replace(text, '                if (r.Value.Length > 0)\n                {', '                if (!leadingNotes && r.Value.Length > 0)\n                {')
    text = replace(text, '                var widths = r.Choices.Select(c => PWidth(ds, c, ui, 17)).ToArray();', '''                var widths = r.Choices.Select(c => PWidth(ds, c, ui, 17)).ToArray();
                if (leadingNotes && r.Value.Length > 0)
                {
                    // Keep controls on one axis; fit essential status into the space before them.
                    float choicesW = widths.Sum(w => targets != null ? Math.Max(44, w + 20) : w + 20);
                    float right = W - choicesW - 24;
                    float left = 36 + r.Indent + PWidth(ds, r.Title, ui, 17) + 24;
                    float available = Math.Max(0, right - left);
                    if (available > 0)
                    {
                        string note = GpFit(ds, r.Value, ui, 14, available);
                        float noteW = PWidth(ds, note, ui, 14);
                        PText(ds, note, ui, 14, Gfx.WithAlpha(CcText, 0.65f), right - noteW, cy, noteW + 4);
                    }
                }''')
    old_color = 'var col = Gfx.WithAlpha(CcText, (f || r.BrightValue ? 1f : 0.6f) * a);' if 'r.BrightValue ?' in text else 'var col = Gfx.WithAlpha(CcText, (f ? 1f : 0.6f) * a);'
    text = replace(text, old_color, old_color.replace('Gfx.WithAlpha(CcText, ', 'Gfx.WithAlpha(CcText, leadingNotes && r.Disabled ? 0.65f : '))
    return text


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


if (OUT / 'integration.json').exists():
    if '--refresh' not in sys.argv:
        raise RuntimeError('Pilot already prepared; use --refresh to rebuild from its unchanged baseline')
    recorded = json.loads((OUT / 'integration.json').read_text(encoding='utf-8'))
    for relative, entry in recorded['changes'].items():
        if digest(BASE / relative) != entry['baseSha256']:
            raise RuntimeError('The baseline changed: ' + relative)
shutil.copytree(BASE, SRC, ignore=shutil.ignore_patterns('.git', 'b', 'o', 'bin', 'obj', 'AppPackages'), dirs_exist_ok=True)
changes = {}
for relative, transform in [('ClawLibrary/Stage/WallStage.System.cs', system), ('ClawLibrary/Stage/WallStage.Perf.cs', perf)]:
    old = (BASE / relative).read_text(encoding='utf-8-sig')
    updated = transform(old)
    backup = OUT / 'baseline' / relative
    backup.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(BASE / relative, backup)
    (SRC / relative).write_text(updated, encoding='utf-8')
    (OUT / (Path(relative).name + '.patch')).write_text(''.join(difflib.unified_diff(old.splitlines(True), updated.splitlines(True), fromfile=relative + '.before', tofile=relative)), encoding='utf-8')
    changes[relative] = {'baseSha256': digest(BASE / relative), 'candidateSha256': digest(SRC / relative)}

manifest = SRC / 'ClawLibrary/Package.appxmanifest'
manifest.write_text(replace(manifest.read_text(encoding='utf-8-sig'), 'Version="0.1.175.0"', 'Version="0.1.176.0"'), encoding='utf-8')

# Apply the same small changes to the task's tracked sources without importing unrelated UI work.
for relative, transform in [('ClawLibrary/Stage/WallStage.System.cs', system), ('ClawLibrary/Stage/WallStage.Perf.cs', perf)]:
    target = ROOT / 'frontend/library-native' / relative
    saved = OUT / 'workspace-before' / relative
    saved.parent.mkdir(parents=True, exist_ok=True)
    if not saved.exists():
        shutil.copy2(target, saved)
    original = saved.read_text(encoding='utf-8-sig')
    target.write_text(transform(original), encoding='utf-8')

(OUT / 'integration.json').write_text(json.dumps({'sourceBaseline': str(BASE), 'baseVersion': '0.1.175.0', 'version': '0.1.176.0', 'changes': changes}, ensure_ascii=False, indent=2), encoding='utf-8')
print(SRC)
