"""Merge enhanced sleep into the installed agent and Claude's latest UI snapshot.

Creates a complete reviewable source/build tree. Existing worktrees and installed
files are read-only here; hashes prevent deploying over a later app revision.
"""
import ast
import argparse
import difflib
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"
UI_LIVE = ROOT / ".claude/worktrees/magical-lehmann-c7fb0a/artifacts/oobe/src/frontend/library-native"
options = argparse.ArgumentParser()
options.add_argument("--out", type=Path, default=ROOT / "artifacts/enhanced-sleep/final")
options.add_argument("--refresh", action="store_true")
options.add_argument("--working-ui", action="store_true")
options.add_argument("--version", type=str)
options.add_argument("--ui", type=Path)
arguments = options.parse_args()
if arguments.ui:
    UI_LIVE = arguments.ui.resolve()
if not (UI_LIVE / "ClawLibrary/.git").exists():
    arguments.working_ui = True
OUT = arguments.out.resolve()
UI_BASE = OUT / "baseline/frontend/library-native"
SRC = OUT / "src"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return path.read_text(encoding="utf-8-sig")


def once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError("Ambiguous merge anchor: " + old[:100])
    return text.replace(old, new, 1)


def method(text, parent, name):
    cls = next(n for n in ast.parse(text).body if isinstance(n, ast.ClassDef) and n.name == parent)
    node = next(n for n in cls.body if getattr(n, "name", "") == name)
    return "".join(text.splitlines(True)[node.lineno - 1:node.end_lineno])


def main():
    if (OUT / "integration.json").exists() and not arguments.refresh:
        raise RuntimeError("Integration already exists; use its recorded hashes before refreshing")
    if (OUT / "integration.json").exists():
        shutil.copy2(OUT / "integration.json", OUT / "integration.previous.json")
    OUT.mkdir(parents=True, exist_ok=True)
    # Claude is still editing its working copy. Snapshot the immutable committed
    # OOBE revision, which already contains the released session-fix merge.
    ui_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=UI_LIVE / "ClawLibrary", text=True).strip()
    if arguments.working_ui:
        shutil.copytree(UI_LIVE / "ClawLibrary", UI_BASE / "ClawLibrary", ignore=shutil.ignore_patterns(".git", "bin", "obj"), dirs_exist_ok=True)
        ui_commit += "+working-copy"
    else:
        archive = subprocess.check_output(["git", "archive", "--format=zip", ui_commit], cwd=UI_LIVE / "ClawLibrary")
        with zipfile.ZipFile(io.BytesIO(archive)) as package:
            for entry in package.infolist():
                target = (UI_BASE / "ClawLibrary" / entry.filename).resolve()
                if not target.is_relative_to((UI_BASE / "ClawLibrary").resolve()):
                    raise RuntimeError("Invalid archive path")
            package.extractall(UI_BASE / "ClawLibrary")
    # Install payloads are intentionally ignored by Git; retain the exact local
    # binaries used by Claude's build, rather than dropping OOBE dependencies.
    shutil.copytree(UI_LIVE / "ClawLibrary/Payload", UI_BASE / "ClawLibrary/Payload", dirs_exist_ok=True)
    # Use the committed installer, which belongs to the captured source version.
    if not arguments.working_ui:
        installer = subprocess.check_output(["git", "show", ui_commit + ":Payload/Install-Components.ps1"], cwd=UI_LIVE / "ClawLibrary")
        (UI_BASE / "ClawLibrary/Payload/Install-Components.ps1").write_bytes(installer)
    shutil.copytree(UI_LIVE / "tools", UI_BASE / "tools", dirs_exist_ok=True)
    if (UI_LIVE / "Directory.Build.props").exists():
        shutil.copy2(UI_LIVE / "Directory.Build.props", UI_BASE / "Directory.Build.props")
    ui = SRC / "frontend/library-native"
    shutil.copytree(UI_BASE, ui, ignore=shutil.ignore_patterns(".git", "bin", "obj", "b", "o", "AppPackages"), dirs_exist_ok=True)
    changes = {}

    def save(relative, original, updated, base):
        target = SRC / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(updated, encoding="utf-8")
        changes[relative] = {"baseSha256": digest(base) if base else None, "candidateSha256": digest(target)}
        patch = OUT / "patches" / (relative.replace("/", "__") + ".patch")
        patch.parent.mkdir(exist_ok=True)
        patch.write_text("".join(difflib.unified_diff(original.splitlines(True), updated.splitlines(True), fromfile=relative + ".before", tofile=relative)), encoding="utf-8")

    system_path = UI_BASE / "ClawLibrary/Stage/WallStage.System.cs"
    original = read(system_path)
    ours = read(ROOT / "frontend/library-native/ClawLibrary/Stage/WallStage.System.cs")
    start = ours.index('                var enhanced = animation?')
    end = ours.index('                else if (hib)', start)
    added = ours[start:end] + '                else if (hib)'
    updated = original if 'var enhanced = animation?' in original else once(original, '                AnimationRow("唤醒动画", "resumeEnabled", "set_wake_animation");\n                if (hib)',
                   '                AnimationRow("唤醒动画", "resumeEnabled", "set_wake_animation");\n' + added)
    save("frontend/library-native/ClawLibrary/Stage/WallStage.System.cs", original, updated, system_path)

    pad_path = UI_BASE / "ClawLibrary/Input/GamepadPoller.cs"
    original = read(pad_path)
    already_parked = '_sleepStop' in original
    original_pad = original
    if already_parked:
        # Undo just our known prior patch in memory so the merge below stays
        # idempotent while retaining every unrelated current source change.
        original = original.replace('using Microsoft.Win32.SafeHandles;\n', '')
        ours_pad = read(ROOT / "frontend/library-native/ClawLibrary/Input/GamepadPoller.cs")
        start_pad = ours_pad.index('    private readonly EventWaitHandle _sleepStop')
        end_pad = ours_pad.index('    private readonly Dictionary', start_pad)
        original = once(original, ours_pad[start_pad:end_pad], '')
        original = once(original, '        SafeWaitHandle? awake = null; long nextSleepCheck = 0;\n        try\n        {\n', '')
        start_pad = ours_pad.index('                if (awake == null'); end_pad = ours_pad.index('                if (now >= nextFg)', start_pad)
        original = once(original, ours_pad[start_pad:end_pad], '')
        original = once(original, '        }\n        finally { awake?.Dispose(); }\n', '')
        original = once(original, '        _sleepStop.Set();\n', '')
    ours = read(ROOT / "frontend/library-native/ClawLibrary/Input/GamepadPoller.cs")
    updated = once(original, 'using System.Runtime.InteropServices;', 'using System.Runtime.InteropServices;\nusing Microsoft.Win32.SafeHandles;')
    start = ours.index('    private readonly EventWaitHandle _sleepStop')
    end = ours.index('    private readonly Dictionary', start)
    updated = once(updated, '    private volatile bool _stop;', '    private volatile bool _stop;\n' + ours[start:end].rstrip())
    updated = once(updated, '        var pads = new XInputGamepad[4];\n        while (!_stop)',
                   '        var pads = new XInputGamepad[4];\n        SafeWaitHandle? awake = null; long nextSleepCheck = 0;\n        try\n        {\n        while (!_stop)')
    start = ours.index('                if (awake == null')
    end = ours.index('                if (now >= nextFg)', start)
    updated = once(updated, '                if (now >= nextFg)', ours[start:end] + '                if (now >= nextFg)')
    updated = once(updated, '            Thread.Sleep(4);\n        }\n    }', '            Thread.Sleep(4);\n        }\n        }\n        finally { awake?.Dispose(); }\n    }')
    updated = once(updated, '        _stop = true;', '        _stop = true;\n        _sleepStop.Set();')
    save("frontend/library-native/ClawLibrary/Input/GamepadPoller.cs", original_pad, updated, pad_path)

    session_path = UI_BASE / "ClawLibrary/Stage/WallStage.NowPlaying.cs"
    original = read(session_path)
    if 'SessionSource' in original:
        anchor = '        catch (Exception ex) { Log.Warn($"game session start: {ex.Message}"); }'
        updated = once(original, anchor, anchor + '\n        EnhancedSleepClient.Publish(s.Pid);')
        anchor = '        if (SessionRunning(g))\n        {'
        updated = once(updated, anchor, anchor + '\n            if (g.Pid > 0) EnhancedSleepClient.Publish(g.Pid);')
    else:
        anchor = '        catch (Exception ex) { Log.Warn($"game session start: {ex.Message}"); }\n        _game = g;'
        updated = once(original, anchor, anchor + '\n        EnhancedSleepClient.Publish(g.Pid);')
        updated = once(updated, '        if (Alive(g.Pid, g.ProcessStart))\n        {', '        if (Alive(g.Pid, g.ProcessStart))\n        {\n            EnhancedSleepClient.Publish(g.Pid);')
        updated = once(updated, '                    g.Pid = (int)pid; g.ProcessStart = p.StartTime; g.Hwnd = fg;',
                       '                    g.Pid = (int)pid; g.ProcessStart = p.StartTime; g.Hwnd = fg;\n                    EnhancedSleepClient.Publish(g.Pid);')
    anchor = '    private void EndGameSession(GameSession g)\n    {\n        _game = null;'
    updated = once(updated, anchor, anchor + '\n        EnhancedSleepClient.Publish(0);')
    save("frontend/library-native/ClawLibrary/Stage/WallStage.NowPlaying.cs", original, updated, session_path)
    client = ROOT / "frontend/library-native/ClawLibrary/Data/EnhancedSleepClient.cs"
    save("frontend/library-native/ClawLibrary/Data/EnhancedSleepClient.cs", "", read(client), None)

    manifest = UI_BASE / "ClawLibrary/Package.appxmanifest"
    original = read(manifest)
    base_version = re.search(r'<Identity[^>]+Version="([^"]+)"', original)[1]
    parts = [int(p) for p in base_version.split('.')]
    parts[2] += 1
    version = arguments.version or '.'.join(map(str, parts))
    parts = [int(p) for p in version.split('.')]
    if len(parts) != 4 or tuple(parts) <= tuple(map(int, base_version.split('.'))):
        raise RuntimeError("Candidate version must be newer than its source")
    save("frontend/library-native/ClawLibrary/Package.appxmanifest", original, once(original, f'Version="{base_version}"', f'Version="{version}"'), manifest)
    project = UI_BASE / "ClawLibrary/ClawLibrary.csproj"
    original = read(project)
    updated = re.sub(r'<Version>[^<]+</Version>', '<Version>' + '.'.join(map(str, parts[:3])) + '</Version>', original)
    save("frontend/library-native/ClawLibrary/ClawLibrary.csproj", original, updated, project)

    (SRC / "plugin").mkdir(parents=True, exist_ok=True)
    for source in [*(HOME / "app").glob("*.py"), HOME / "app/plugin.json"]:
        shutil.copy2(source, SRC / "plugin" / source.name)
    for name in ("claw_system.py", "claw_service.py", "claw_agent.py", "claw_power.py", "claw_sleep_animation.py", "claw_input_layer.py"):
        base = HOME / "app" / name
        original = read(base)
        ours = read(ROOT / "plugin" / name)
        updated = original
        if name in ("claw_system.py", "claw_service.py"):
            cls = "SystemControl" if name == "claw_system.py" else next(n.name for n in ast.parse(original).body if isinstance(n, ast.ClassDef) and any(getattr(m, "name", "") == "set_wake_animation" for m in n.body))
            anchor = method(original, cls, "set_wake_animation")
            if 'async def set_enhanced_sleep(' not in updated:
                updated = once(updated, anchor, anchor + '\n\n' + '\n\n'.join(method(ours, cls, m) for m in ("set_enhanced_sleep", "set_sleep_games", "set_sleep_fingerprint")))
        elif name == "claw_agent.py":
            if '"set_enhanced_sleep"' not in updated:
                updated = once(updated, '"set_wake_animation",', '"set_wake_animation", "set_enhanced_sleep", "set_sleep_games", "set_sleep_fingerprint",')
        elif name == "claw_sleep_animation.py":
            # The installed module and the workspace share the same animation
            # protocol. Refuse to discard any unrelated installed changes.
            functions = {n.name for n in ast.parse(original).body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef))}
            ours_functions = {n.name for n in ast.parse(ours).body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef))}
            if functions - ours_functions:
                raise RuntimeError("Installed sleep client has unknown additional methods")
            updated = ours
        elif name == "claw_input_layer.py":
            cls = next(n.name for n in ast.parse(original).body if isinstance(n, ast.ClassDef) and any(getattr(m, "name", "") == "exe" for m in n.body))
            updated = once(original, method(original, cls, "exe"), method(ours, cls, "exe"))
        else:
            if 'async def _sleep_game_tick(' in updated:
                save("plugin/" + name, original, updated, base)
                continue
            marker = '_close_spec.loader.exec_module(_close_module)'
            start = ours.index('_sleep_spec = '); end = ours.index('\n\n', start)
            updated = once(updated, marker, marker + '\n' + ours[start:end])
            pause = '                if _sleep_module.is_sleeping():\n                    await _sleep_module.wait_awake()\n                    continue\n'
            for fn in ("_loop", "_frame_loop", "_overlay_loop", "_telemetry_loop", "_close_target_loop"):
                body = method(updated, "PowerControl", fn)
                if fn == "_loop":
                    changed = once(body, '                await asyncio.sleep(TICK)\n', '                await asyncio.sleep(TICK)\n' +
                        '                if _sleep_module.is_sleeping():\n                    await self.presentmon_fallback.close()\n                    await _sleep_module.wait_awake()\n                    continue\n')
                    changed = once(changed, '                await self._detect_game()', '                await self._detect_game()\n                if tick % 5 == 1 and _sleep_module.enhanced_enabled():\n                    await self._sleep_game_tick()')
                else:
                    changed = once(body, '            try:\n', '            try:\n' + pause)
                updated = once(updated, body, changed)
            # Use the current confirmed session, not the candidate tracker and
            # not RTSS. The library's launch identity takes priority in the host.
            function = '''    async def _sleep_game_tick(self):
        current = self.sessions.current
        pid = 0
        if current:
            actual = await asyncio.to_thread(self.sessions.probe.process, current["pid"])
            if _session_module.identity(actual) == _session_module.identity(current):
                pid = current["pid"]
        try:
            await _sleep_module.set_game(pid)
        except (OSError, RuntimeError):
            pass
'''
            anchor = method(updated, "PowerControl", "_detect_game")
            updated = once(updated, anchor, anchor + '\n\n' + function.rstrip())
        compile(updated, name, "exec")
        save("plugin/" + name, original, updated, base)

    native = SRC / "native/SleepAnimation"
    shutil.copytree(ROOT / "native/SleepAnimation", native, ignore=shutil.ignore_patterns("bin", "obj"), dirs_exist_ok=True)
    input_base = Path(os.environ["USERPROFILE"]) / ".codex/worktrees/notice-alpha-fix/Claw-Playhub-ButtonRouter-Handoff"
    if digest(input_base / "plugin/bin/ClawInput-0.17.58.exe") != digest(HOME / "app/bin/ClawInput-0.17.58.exe"):
        raise RuntimeError("Input-engine source baseline no longer matches the installed binary")
    input_target = SRC / "native/InputLayer"
    shutil.copytree(input_base / "native/InputLayer", input_target, dirs_exist_ok=True)
    shutil.copy2(ROOT / "native/InputLayer/EnhancedSleepGate.cs", input_target / "EnhancedSleepGate.cs")
    path = input_base / "native/InputLayer/Program.cs"
    original = read(path)
    updated = once(original, '            using (var ticker = new Ticker(pollHz)) {', '            using (var sleep = new EnhancedSleepGate())\n            using (var ticker = new Ticker(pollHz)) {')
    updated = once(updated, '                while (running) {\n                    ticker.Wait();', '                while (running) {\n                    if (sleep.Sleeping) { ticker.Pause(); sleep.Wait(500); continue; }\n                    ticker.Wait();')
    updated = once(updated, '                long lastBeat = -1, lastChange = Win.UnbiasedMs();', '                long lastBeat = -1, lastChange = Win.UnbiasedMs(), wakeGraceUntil = 0;\n                using (var sleep = new EnhancedSleepGate()) {')
    updated = once(updated, '                    long now = Win.UnbiasedMs();', '                    long now = Win.UnbiasedMs();\n                    if (sleep.Sleeping) { lastChange = now; wakeGraceUntil = now + 30000; }')
    updated = once(updated, 'view != null && now - lastChange > StaleMs', 'view != null && now > wakeGraceUntil && now - lastChange > StaleMs')
    updated = once(updated, '            } finally {\n                if (view != null)', '                }\n            } finally {\n                if (view != null)')
    save("native/InputLayer/Program.cs", original, updated, path)
    path = input_base / "native/InputLayer/Devices.cs"
    original = read(path)
    updated = once(original, '        long next;\n        public readonly bool HighResolution;', '        long next;\n        bool paused;\n        public readonly bool HighResolution;')
    start = read(ROOT / "native/InputLayer/Devices.cs").index('        public void Pause()')
    end = read(ROOT / "native/InputLayer/Devices.cs").index('            long now = System.Diagnostics.Stopwatch.GetTimestamp()', start)
    updated = once(updated, '        public void Wait() {\n', read(ROOT / "native/InputLayer/Devices.cs")[start:end])
    updated = once(updated, 'else timeEndPeriod(1); }', 'else if (!paused) timeEndPeriod(1); }')
    save("native/InputLayer/Devices.cs", original, updated, path)
    # Native project references the shared branding from the workspace.
    shutil.copytree(ROOT / "assets/branding", SRC / "assets/branding", dirs_exist_ok=True)
    (OUT / "integration.json").write_text(json.dumps({"uiBase": str(UI_BASE), "uiSource": str(UI_LIVE), "uiCommit": ui_commit, "baseVersion": base_version, "version": version, "changes": changes}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"source": str(SRC), "version": version, "changedFiles": len(changes)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
