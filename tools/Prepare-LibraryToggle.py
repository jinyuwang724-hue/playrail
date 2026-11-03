"""Build this focused repair from the source matching installed library 0.1.107.0."""
import hashlib
import json
import os
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]
BASE = Path.home() / '.codex/worktrees/native-gamebar-osd/Claw-Playhub-ButtonRouter-Handoff'
STAGE = ROOT / 'artifacts/library-toggle/src'


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError('Source changed: ' + old[:100])
    return text.replace(old, new, 1)


def main():
    if STAGE.exists():
        raise RuntimeError('Prepared source already exists; preserve it before preparing a new baseline')
    before = BASE / 'frontend/library-native/b/x64/Release/native/ClawLibrary.exe'
    expected = '1de66adfa1dfbbdb639854685a6dbb2ccd854e78a8d9f6fbe0afcb97c37ee89e'
    if hashlib.sha256(before.read_bytes()).hexdigest() != expected:
        raise RuntimeError('Library build baseline changed')
    project = STAGE / 'frontend/library-native'
    shutil.copytree(BASE / 'frontend/library-native/ClawLibrary', project / 'ClawLibrary')
    shutil.copy2(BASE / 'frontend/library-native/Directory.Build.props', project / 'Directory.Build.props')
    shutil.copytree(BASE / 'frontend/library-native/tools', project / 'tools')
    # App and Program differ only by the independent development preview options.
    for file in ('App.cs', 'Program.cs', 'MainWindow.Shortcut.cs', 'Data/ToolboxActivation.cs', 'Data/LibraryWindow.cs'):
        text = (ROOT / 'frontend/library-native/ClawLibrary' / file).read_text(encoding='utf-8-sig')
        if file == 'App.cs':
            text = replace_once(text, '            Log.Init(Path.Combine(CacheDir, "logs"));',
                '            if (!Packaged && Program.Args.FirstOrDefault(a => a.StartsWith("--cache-dir=")) is { } cacheArg)\n'
                '                CacheDir = Path.GetFullPath(cacheArg["--cache-dir=".Length..]);\n'
                '            Log.Init(Path.Combine(CacheDir, "logs"));')
        if file == 'Program.cs':
            text = replace_once(text, 'var main = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey("main");',
                'var instanceKey = args.FirstOrDefault(a => a.StartsWith("--instance-key="))?["--instance-key=".Length..] ?? "main";\n'
                '            var main = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey(instanceKey);')
        (project / 'ClawLibrary' / file).write_text(text, encoding='utf-8')
    file = project / 'ClawLibrary/Stage/WallStage.cs'
    text = file.read_text(encoding='utf-8-sig')
    text = replace_once(text, '    private readonly Window _window;', '    private readonly Window _window;\n    private readonly nint _windowHandle;')
    text = replace_once(text, '        _window = window;', '        _window = window;\n        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);')
    text = replace_once(text, '        try { return GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(_window); }\n        catch { return true; }',
        '        return (_window as MainWindow)?.AcceptsGamepad != false && !ToolboxWindow.IsIconic(_windowHandle) && GetForegroundWindow() == _windowHandle;')
    file.write_text(text, encoding='utf-8')
    file = project / 'ClawLibrary/Input/GamepadPoller.cs'
    text = file.read_text(encoding='utf-8-sig')
    text = replace_once(text, 'if (!_stage.GamepadInputBlocked) a();', 'if (!_stop && _foreground() && !_stage.GamepadInputBlocked) a();')
    text = replace_once(text, 'nextFg = now + 250;', 'nextFg = now + 16;')
    file.write_text(text, encoding='utf-8')
    for file in ('ClawLibrary.csproj', 'Package.appxmanifest'):
        target = project / 'ClawLibrary' / file
        target.write_text(replace_once(target.read_text(encoding='utf-8-sig'), '0.1.107', '0.1.110'), encoding='utf-8')
    plugin = STAGE / 'plugin'
    plugin.mkdir()
    live = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox/app'
    text = (live / 'claw_service.py').read_text(encoding='utf-8-sig')
    text = replace_once(text, 'event.get("source") == "Claw" and event.get("scope") == "general"\n',
        'event.get("source") == "Claw" and event.get("scope") in ("general", "steam")\n')
    text = replace_once(text,
        '            eligible = event and event.get("source") == "Claw" and event.get("scope") == "general" and mode != "BigPicture"\n'
        '            context = await asyncio.to_thread(_buttons_module.toolbox_context) if eligible else {}',
        '            context = await asyncio.to_thread(_buttons_module.toolbox_context)')
    text = replace_once(text, '            await asyncio.to_thread(os.startfile, "claw-library://" + route)',
        '            await asyncio.to_thread(_buttons_module.activate_toolbox, route, context)')
    (plugin / 'claw_service.py').write_text(text, encoding='utf-8')
    shutil.copy2(ROOT / 'plugin/claw_button_mappings.py', plugin / 'claw_button_mappings.py')
    info = {'sourceRoot': str(BASE), 'baseLibraryHash': expected,
        'backendBaseHashes': {f: hashlib.sha256((live / f).read_bytes()).hexdigest() for f in ('claw_service.py', 'claw_button_mappings.py')}}
    (STAGE.parent / 'integration.json').write_text(json.dumps(info, indent=2), encoding='utf-8')
    print(STAGE)


if __name__ == '__main__':
    main()
