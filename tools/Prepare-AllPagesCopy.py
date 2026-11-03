"""Extend the reviewed system-page copy style to the current native application."""
from pathlib import Path
import difflib
import hashlib
import json
import shutil
import sys

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / 'artifacts/system-copy-pilot/src/frontend/library-native'
OUT = ROOT / 'artifacts/all-pages-copy'
SRC = OUT / 'src/frontend/library-native'
STAGE = 'ClawLibrary/Stage/'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError('Ambiguous anchor: ' + old[:110])
    return text.replace(old, new, 1)


edits = {}


def change(file, old, new, unique=False):
    edits.setdefault(STAGE + file, []).append((old, new, unique))


change('WallStage.Perf.cs', 'bool numbers = true, float listWidth = PerfRightW, bool leadingNotes = false)',
       'bool numbers = false, float listWidth = PerfRightW, bool leadingNotes = true)', True)
for old, new in {
    '不限频 · 按负载运行': '不限频',
    '开机风扇服务 · 退出界面仍生效': '后台调速',
    'EC 高温保护 · 冷却后恢复曲线': '高温保护 · 冷却后恢复',
    '编辑这档曲线': '编辑当前曲线',
    '恢复这档默认曲线': '恢复当前默认曲线',
    '首次修改前 ›': '首次修改前的值 ›',
}.items():
    change('WallStage.Perf.cs', old, new)

change('WallStage.SettingsPage.cs', '_settingsTargets, true, rw)', '_settingsTargets, false, rw)', True)
change('WallStage.SettingsPage.cs', '        _stRows.Add(StInfo("重新打开", "双击托盘图标，或再次打开游戏库"));\n', '', True)
for old, new in {
    '开机自动运行工具箱后台': '工具箱后台自启动',
    '立即在托盘启动 Steam': '启动 Steam 至托盘',
    '立即收到后台': '转入后台',
    '开机落在的集合': '默认集合',
    'folder + "（重新载入游戏库后生效）"': 'folder',
    '添加游戏文件夹（重新载入游戏库后生效）': '添加游戏文件夹',
    '显示在 Steam 里隐藏的游戏（重新载入游戏库后生效）': '显示 Steam 隐藏游戏',
    '重新生成全部封面（重新载入游戏库后生效）': '重新生成全部封面',
    '收到后台并暂停': '转入后台并暂停',
    '开关灯屏保': '封面灯光屏保',
    '闲置时随机亮起游戏封面': '',
    '切换亮起的游戏': '封面切换间隔',
    '已被工具箱替代': '第三方软件',
    '性能读数（帧时间）': '帧时间读数',
}.items():
    change('WallStage.SettingsPage.cs', old, new)
change('WallStage.SettingsPage.cs', 'Title = "游戏来源"', 'Title = "游戏来源 · 重新载入后生效"', True)
change('WallStage.SettingsPage.cs', 'StAction("重新载入游戏库", "reload", "", RestartLibrary)',
       'StAction("重新载入游戏库", "reload", _stNeedsReload ? "待重新载入" : "", RestartLibrary)', True)
# This action actually restarts the library immediately after confirmation; state that once at its group.
change('WallStage.SettingsPage.cs', 'Title = "封面"', 'Title = "封面 · 将重新载入游戏库"', True)
change('WallStage.SettingsPage.cs', 'StToggle("以管理员身份启动（重启 Steam 后生效）"', 'StToggle("以管理员身份启动"', True)
change('WallStage.SettingsPage.cs', '}, false, note);', '}, false, note.Length > 0 ? note : "重启 Steam 后生效");', True)
change('WallStage.SettingsPage.cs', 'StInfo("以管理员身份启动（重启 Steam 后生效）", later)', 'StInfo("以管理员身份启动", later)', True)

change('WallStage.Audio.cs', 'Title = "试听整套音效", Value = "导航 · 确认 · 详情 · 开关 · 菜单 · 提示 · 启动"',
       'Title = "试听音效", Value = ""', True)
change('WallStage.Audio.cs', 'StToggle("主页背景音乐"', 'StToggle("背景音乐"', True)
change('WallStage.Audio.cs', '"MP3 / WAV / M4A / FLAC / AAC / WMA"', '""', True)
change('WallStage.Audio.cs', '前端在前台时播放，进入游戏后淡出', '仅前台播放 · 游戏中淡出', True)

change('WallStage.OsdSettings.cs', '        _stRows.Add(new PerfRow { Section = true, Title = "状态" });\n        _stRows.Add(StInfo("操作", "Game Bar 性能显示：关闭 → 1 → 2 → 3 → 4 → 关闭"));\n', '', True)
change('WallStage.OsdSettings.cs', '            _stRows.Add(StInfo("悬浮窗", "位置固定，鼠标穿透；只在设置中更改位置"));\n', '', True)
change('WallStage.OsdSettings.cs', 'StPick("显示位置",', 'StPick("悬浮窗位置",', True)

for old, new in {
    '"已识别 · 后台运行中" : "已识别 · 后台未运行"': '"运行中" : "未运行"',
    '重新检测本机': '重新检测',
    '有未保存的修改': '待保存',
    '选择小黄鸭 EXE…': '选择程序…',
}.items():
    change('WallStage.LosslessSettings.cs', old, new)

change('WallStage.Services.cs', 'Value = cloud ? "Key 已加密保存" : "配置 Web API Key"', 'Value = cloud ? "已配置" : "需配置 Web API Key"', True)
change('WallStage.Services.cs', 'Value = "打开 Steam 网页  ›"', 'Value = "›"', True)

change('WallStage.GamepadPicker.cs', 'Value = "按下不起作用  ›"', 'Value = "›"')
change('WallStage.GamepadPicker.cs', 'Value = $"{GpSourceName(src)}自己  ›"', 'Value = GpSourceName(src) + "  ›"')
change('WallStage.GamepadPicker.cs', 'Value = "长按、双击、组合键……  ›"', 'Value = "›"', True)
change('WallStage.GamepadPicker.cs', 'Value = value + " · 第二批", Disabled = true, Press = () => Toast("这一项在软件输入层的第二批里做")',
       'Value = "暂不可用", Disabled = true, Press = () => Toast("此功能暂不可用")', True)
change('WallStage.GamepadPicker.cs', '按住以重复（连发）', '按住连发')
change('WallStage.GamepadPicker.cs', 'PText(ds, $"现在：{now}  ·  改了立刻生效",', 'PText(ds, $"当前：{now}",', True)
change('WallStage.GamepadPicker.cs', '                PText(ds, "照 Steam 输入", ui, 13, muted, GpPickX, 170, 900);\n', '', True)
change('WallStage.GamepadPicker.cs', 'Ctrl / Shift / Alt / Win 按一下是叠加，可以组合；再按一个普通键完成', '先选修饰键，再选普通键完成组合')
change('WallStage.GamepadPicker.cs', '  再按一个普通键完成', '  选择普通键完成')
change('WallStage.GamepadPicker.cs', ': GpIsSpecial(p.Source) ? "专用功能键支持系统动作、键盘组合和鼠标指令" : p.Tab == 4 ? "系统动作与专用按键共用，选择后立即生效" : null;', ': null;', True)
change('WallStage.GamepadPages.cs', 'Value = "恢复方向、死区和外圈  ›"', 'Value = "方向 · 死区 · 外圈  ›"', True)
change('WallStage.GamepadPages.cs', '手柄模式 · 手柄输出', '手柄模式')
change('WallStage.GamepadPages.cs', '桌面模式 · 键盘鼠标', '桌面模式')
change('WallStage.SpecialButtons.cs', '            Info("使用范围", steam ? "大屏打开时按原接管开关生效" : "关闭 Steam 大屏后使用这套映射"),\n', '', True)
change('WallStage.SpecialButtons.cs', 'Value = "按下不执行  ›"', 'Value = "›"', True)
change('WallStage.SpecialButtons.cs', 'Title = "测试这项指令", Value = "立即执行  ›"', 'Title = "测试指令", Value = "立即执行  ›"', True)

change('WallStage.Oobe.cs', 'L("你好，", "Hello, ") + ObName()', 'L("首次设置", "Setup")', True)
change('WallStage.Oobe.cs', 'L("一切就绪", "All set")', 'L("设置完成", "Setup complete")', True)

change('WallStage.Shots.Paint.cs', '        string folder = Captures.GameBarFolder();\n', '', True)
change('WallStage.Shots.Paint.cs', '            else if (_shLoaded) PText(ds, $"Game Bar 截图（{folder}）、Steam 截图和 Steam 上传的截图都会出现在这里", TextKit.Ui(400), 14, Gfx.WithAlpha(CcText, 0.5), 0, 52, W, CanvasHorizontalAlignment.Center);\n', '', True)
change('WallStage.Shots.Paint.cs', '按 View 换一款游戏或取消筛选', 'View 更改或清除筛选')

# Opt explicitly into the same unnumbered row style in the few callers that passed true.
for file, old, new in [
    ('WallStage.AddGameMatch.cs', 'null, true, AgMatchW)', 'null, false, AgMatchW)'),
    ('WallStage.AddGamePage.cs', 'null, true, AgRowsW)', 'null, false, AgRowsW)'),
    ('WallStage.QuickPanel.cs', 'numbers: true, listWidth: W - 80', 'numbers: false, listWidth: W - 80'),
]:
    change(file, old, new, True)
change('WallStage.ServicesProbe.cs', '立即在托盘启动 Steam', '启动 Steam 至托盘', True)
change('WallStage.cs', '        if (Program.Args.Contains("--settings-edit-probe")) _ = SettingsEditProbeAsync();',
       '        if (Program.Args.Contains("--settings-edit-probe")) _ = SettingsEditProbeAsync();\n        if (Program.Args.Contains("--copy-review-probe")) _ = CopyReviewProbeAsync();', True)

# Concise existing labels already describe these screens; retain their operational hints and warnings.
AUDITED = {
    'library': '保留游戏名、集合、平台、安装及运行状态；移除公共列表行号。',
    'control': '保留卡片名称、实时读数及操作入口，现有卡片无教学段落。',
    'performance': '统一开关轴线，缩短 CPU 与风扇状态；保留重启要求和温控保护。',
    'display': '统一列表风格；保留 XeSS 使用条件、驱动错误及不可用原因。',
    'gamepad': '精简映射子页说明和开发批次文案；保留组合键操作和场景限制。',
    'settings': '精简通用、游戏库、声音、性能显示、平台及缩放设置。',
    'per-game': '共用新的设置列表；保留独立配置、未安装及程序缺失状态。',
    'add-game': '精简公共行标记；保留游戏匹配、分类、图种和确认按钮。',
    'screenshots': '删除空状态的来源教学段落；保留筛选恢复和删除确认。',
    'onboarding': '用首次设置和设置完成替代问候及氛围文案。',
    'quick-settings': '保留磁贴值、交互状态与错误反馈；统一其列表模式。',
    'switcher': '现有页面以窗口标题、进程状态和关闭操作为主，保留。',
    'now-playing': '保留运行时长、回到游戏、退出和强制退出确认。',
    'system': '沿用已验收的 0.1.176 试点。',
}

translations = {
    '工具箱后台自启动': 'Start background service at sign-in', '启动 Steam 至托盘': 'Start Steam in tray',
    '转入后台': 'Run in background', '默认集合': 'Default collection', '显示 Steam 隐藏游戏': 'Show hidden Steam games',
    '游戏来源 · 重新载入后生效': 'Game sources · reload required', '待重新载入': 'Reload pending',
    '封面 · 将重新载入游戏库': 'Covers · library will reload', '封面灯光屏保': 'Cover light screensaver',
    '封面切换间隔': 'Cover interval', '重启 Steam 后生效': 'Restart Steam to apply', '帧时间读数': 'Frame times',
    '试听音效': 'Preview sounds', '悬浮窗位置': 'Overlay position', '仅前台播放 · 游戏中淡出': 'Foreground only · fades during games',
    '待保存': 'Unsaved changes', '需配置 Web API Key': 'Web API Key required', '按住连发': 'Hold to repeat',
    '当前：{0}': 'Current: {0}', '先选修饰键，再选普通键完成组合': 'Select modifiers, then a key',
    '选择普通键完成': 'Select a key to finish', '方向 · 死区 · 外圈  ›': 'Direction · dead zone · outer zone  ›',
    '组合：{0} + …  选择普通键完成': 'Chord: {0} + … select a key to finish',
    '测试指令': 'Test command', 'View 更改或清除筛选': 'View: change or clear filter', '后台调速': 'Background fan control',
    '高温保护 · 冷却后恢复': 'Thermal protection · resumes after cooling', '编辑当前曲线': 'Edit current curve',
    '恢复当前默认曲线': 'Reset current curve', '首次修改前的值 ›': 'Original values ›', '转入后台并暂停': 'Pause in background',
    '睡眠与唤醒': 'Sleep & wake', '抑制自动唤醒': 'Suppress automatic wakes', '暂停游戏': 'Pause games',
    '暂停指纹识别': 'Pause fingerprint reader', '检测到反作弊时跳过': 'Skipped when anti-cheat is detected',
    '睡眠转休眠': 'Hibernate after sleep', '48 小时 · 增强睡眠': '48 hours · enhanced sleep',
    '电池供电时关屏': 'Turn off display on battery', '系统与驱动更新': 'System & driver updates', '系统 · 更新': 'System · Updates',
    '不限频': 'Unlimited', '以管理员身份启动': 'Run as administrator', '已配置': 'Configured',
    '暂不可用': 'Unavailable', '未运行': 'Not running', '此功能暂不可用': 'This feature is unavailable',
    '添加游戏文件夹': 'Add game folder', '选择程序…': 'Choose program…', '重新检测': 'Detect again',
    '重新生成全部封面': 'Regenerate all covers', '首次设置': 'Setup', '设置完成': 'Setup complete',
}


def main():
    previous = OUT / 'integration.json'
    if previous.exists() and '--refresh' not in sys.argv:
        raise RuntimeError('Already prepared; use --refresh with the unchanged baseline')
    if previous.exists():
        meta = json.loads(previous.read_text(encoding='utf-8'))
        for relative, data in meta['changes'].items():
            if data['baseSha256'] is not None and digest(BASE / relative) != data['baseSha256']:
                raise RuntimeError('Baseline changed: ' + relative)
    if not SRC.exists():
        shutil.copytree(BASE, SRC, ignore=shutil.ignore_patterns('.git', 'b', 'o', 'bin', 'obj', 'AppPackages'))
    changes, workspace = {}, {}
    for relative, replacements in edits.items():
        source = BASE / relative
        before = source.read_text(encoding='utf-8-sig')
        after = before
        for old, new, unique in replacements:
            if old not in after:
                raise RuntimeError('Missing anchor in ' + relative + ': ' + old[:90])
            after = once(after, old, new) if unique else after.replace(old, new)
        saved = OUT / 'baseline' / relative
        saved.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, saved)
        if (SRC / relative).read_text(encoding='utf-8-sig') != after:
            (SRC / relative).write_text(after, encoding='utf-8')
        changes[relative] = {'baseSha256': digest(source), 'candidateSha256': digest(SRC / relative)}
        patch = OUT / 'patches' / (Path(relative).name + '.patch')
        patch.parent.mkdir(parents=True, exist_ok=True)
        patch.write_text(''.join(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile=relative + '.before', tofile=relative)), encoding='utf-8')
        # Apply matching fragments to tracked older sources without replacing unrelated work.
        target = ROOT / 'frontend/library-native' / relative
        if target.exists():
            backup = OUT / 'workspace-before' / relative
            backup.parent.mkdir(parents=True, exist_ok=True)
            if not backup.exists():
                shutil.copy2(target, backup)
            current = backup.read_text(encoding='utf-8-sig')
            applied, skipped = 0, []
            for old, new, unique in replacements:
                if old in current and (not unique or current.count(old) == 1):
                    current = current.replace(old, new, 1 if unique else -1)
                    applied += 1
                else:
                    skipped.append(old)
            target.write_text(current, encoding='utf-8')
            workspace[relative] = {'applied': applied, 'unmatchedOlderSource': skipped}
    lang = SRC / 'ClawLibrary/Assets/Lang/en.tsv'
    old_lang = (BASE / 'ClawLibrary/Assets/Lang/en.tsv').read_text(encoding='utf-8-sig')
    keys = {line.split('\t')[0] for line in old_lang.splitlines()}
    lang.write_text(old_lang.rstrip() + '\n' + ''.join(zh + '\t' + en + '\n' for zh, en in translations.items() if zh not in keys), encoding='utf-8')
    changes['ClawLibrary/Assets/Lang/en.tsv'] = {'baseSha256': digest(BASE / 'ClawLibrary/Assets/Lang/en.tsv'), 'candidateSha256': digest(lang)}
    probe = STAGE + 'WallStage.CopyReviewProbe.cs'
    if not (SRC / probe).exists() or digest(ROOT / 'tests/UiCopyReview/WallStage.CopyReviewProbe.cs') != digest(SRC / probe):
        shutil.copy2(ROOT / 'tests/UiCopyReview/WallStage.CopyReviewProbe.cs', SRC / probe)
    changes[probe] = {'baseSha256': None, 'candidateSha256': digest(SRC / probe)}
    manifest = SRC / 'ClawLibrary/Package.appxmanifest'
    manifest_text = once((BASE / 'ClawLibrary/Package.appxmanifest').read_text(encoding='utf-8-sig'), 'Version="0.1.176.0"', 'Version="0.1.177.0"')
    if manifest.read_text(encoding='utf-8-sig') != manifest_text:
        manifest.write_text(manifest_text, encoding='utf-8')
    (OUT / 'integration.json').write_text(json.dumps({'sourceBaseline': str(BASE), 'baseVersion': '0.1.176.0', 'version': '0.1.177.0', 'changes': changes, 'workspace': workspace, 'auditedPages': AUDITED}, ensure_ascii=False, indent=2), encoding='utf-8')
    print('Prepared', len(changes), 'changed files;', len(AUDITED), 'page families reviewed')


if __name__ == '__main__':
    main()
