"""Stage the animation controls over the sources matching the installed app."""
import ast
import difflib
import hashlib
import json
import os
from pathlib import Path
import shutil
import zipfile

ROOT = Path(__file__).resolve().parents[1]
BASE = Path(r"C:\Users\13304\.codex\worktrees\native-gamebar-osd\Claw-Playhub-ButtonRouter-Handoff")
UI = ROOT / "artifacts/library-toggle/src"
OUT = ROOT / "artifacts/sleep-animation/settings-v2/integration-r2"
HOME = Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def function(text, name, parent):
    cls = next(n for n in ast.parse(text).body if isinstance(n, ast.ClassDef) and n.name == parent)
    node = next(n for n in cls.body if getattr(n, "name", "") == name)
    return "".join(text.splitlines(True)[node.lineno - 1:node.end_lineno])


def once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError("Ambiguous integration anchor: " + old[:70])
    return text.replace(old, new, 1)


def main():
    if OUT.exists():
        raise RuntimeError("Use a fresh integration directory")
    OUT.mkdir(parents=True)
    changes = {}

    def save(relative, before, after, base_file):
        target = OUT / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(after, encoding="utf-8")
        changes[relative] = {"baseSha256": digest(base_file) if base_file else None, "candidateSha256": digest(target)}
        (OUT / (relative.replace("/", "__") + ".patch")).write_text(
            "".join(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile=relative + ".before", tofile=relative)), encoding="utf-8")

    for name in ("claw_system.py", "claw_service.py", "claw_agent.py"):
        source = HOME / "app" / name
        before = source.read_text(encoding="utf-8-sig")
        ours = (ROOT / "plugin" / name).read_text(encoding="utf-8-sig")
        after = before
        if name == "claw_system.py":
            start = ours.index('_animation_spec = ')
            end = ours.index('\n\n', start)
            after = once(after, '_updates_spec.loader.exec_module(_updates_module)',
                         '_updates_spec.loader.exec_module(_updates_module)\n' + ours[start:end])
            start = ours.index('        animation = await _animation.view()')
            end = ours.index('\n        try:', start)
            after = once(after, '        system = await self.call("system_read")',
                         '        system = await self.call("system_read")\n' + ours[start:end])
            after = once(after, '            "power": system.get("power"), "powerError": system.get("powerError"),',
                         '            "power": system.get("power"), "powerError": system.get("powerError"),\n            "powerAnimation": animation,')
            after = once(after, function(before, "set_power_button", "SystemControl"),
                         '\n\n'.join(function(ours, method, "SystemControl") for method in ("set_power_button", "set_sleep_animation", "set_wake_animation")))
        elif name == "claw_service.py":
            parent = next(n.name for n in ast.parse(before).body if isinstance(n, ast.ClassDef) and any(getattr(m, "name", "") == "set_power_button" for m in n.body))
            anchor = function(before, "set_power_button", parent)
            after = once(after, anchor, anchor + '\n\n' + '\n\n'.join(function(ours, method, parent) for method in ("set_sleep_animation", "set_wake_animation")))
        else:
            after = once(after, '"set_power_button",', '"set_power_button", "set_sleep_animation", "set_wake_animation",')
        compile(after, name, "exec")
        save("plugin/" + name, before, after, source)
    module = ROOT / "plugin/claw_sleep_animation.py"
    save("plugin/claw_sleep_animation.py", "", module.read_text(encoding="utf-8"), None)

    manifest = UI / "frontend/library-native/ClawLibrary/Package.appxmanifest"
    if 'Version="0.1.110.0"' not in manifest.read_text(encoding="utf-8"):
        raise RuntimeError("UI baseline version changed")
    shutil.copytree(UI / "frontend/library-native", OUT / "frontend/library-native",
                    ignore=shutil.ignore_patterns("bin", "obj", "AppPackages", ".vs", "b", "o"))
    relative = "frontend/library-native/ClawLibrary/Stage/WallStage.System.cs"
    before = (UI / relative).read_text(encoding="utf-8-sig")
    ours = (ROOT / relative).read_text(encoding="utf-8-sig")
    start = ours.index('                var animation = v["powerAnimation"]')
    end = ours.index('\n                if (hib)', start)
    after = once(before, '                if (hib)\n                {\n                    int hi =', ours[start:end] + '\n                if (hib)\n                {\n                    int hi =')
    save(relative, before, after, UI / relative)
    relative = "frontend/library-native/ClawLibrary/Package.appxmanifest"
    before = (UI / relative).read_text(encoding="utf-8-sig")
    save(relative, before, once(before, 'Version="0.1.110.0"', 'Version="0.1.111.0"'), UI / relative)
    result = {"uiBaseline": str(UI), "backendBaseline": str(HOME / "app"), "expectedInstalledUiVersion": "0.1.110.0", "uiVersion": "0.1.111.0", "changes": changes}
    (OUT / "integration.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"stage": str(OUT), "files": len(changes), "uiVersion": result["uiVersion"]}))


if __name__ == "__main__":
    main()
