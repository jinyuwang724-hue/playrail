"""Stage only this feature over a newer checkout, preserving its unrelated changes.

Usage: python tools/Prepare-EcoreIntegration.py --source-root <current deployed source>
No source checkout or installed application is modified by this script.
"""
import argparse
import ast
import difflib
import hashlib
import json
from pathlib import Path
import re
import shutil

ROOT = Path(__file__).resolve().parents[1]


def function(text, name, parent=None):
    tree = ast.parse(text)
    nodes = tree.body if parent is None else next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == parent).body
    node = next(n for n in nodes if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)) and n.name == name)
    return "".join(text.splitlines(keepends=True)[node.lineno - 1:node.end_lineno])


def replace_once(text, before, after):
    if text.count(before) != 1:
        raise ValueError("Integration anchor is missing or ambiguous: " + before[:90])
    return text.replace(before, after, 1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/ecore/integration")
    args = parser.parse_args()
    source, out = args.source_root.resolve(), args.output.resolve()
    if out.exists():
        raise ValueError("Use a new staging directory")
    out.mkdir(parents=True)
    changes = {}

    def read(relative):
        return (source / relative).read_text(encoding="utf-8-sig")

    def write(relative, before, after):
        target = out / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(after, encoding="utf-8")
        changes[relative] = {"baseSha256": hashlib.sha256((source / relative).read_bytes()).hexdigest(),
                             "candidateSha256": hashlib.sha256(target.read_bytes()).hexdigest()}
        diff = "".join(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile=relative + ".before", tofile=relative))
        (out / (relative.replace("/", "__") + ".patch")).write_text(diff, encoding="utf-8")

    relative = "plugin/claw_power.py"
    before, ours = read(relative), (ROOT / relative).read_text(encoding="utf-8-sig")
    result = before
    helpers = "\n\n".join(function(ours, name) for name in ("small_cores_topology", "small_cores_snapshot", "small_cores_enabled", "boot_session_id"))
    result = replace_once(result, function(before, "small_cores_enabled"), helpers)
    for name in ("ecore_view", "set_ecore"):
        result = replace_once(result, function(before, name, "PowerControl"), function(ours, name, "PowerControl"))
    additions = "\n\n".join(function(ours, name, "PowerControl") for name in ("_save_ecore_record", "_ecore_original", "restore_ecore"))
    result = replace_once(result, "    # ---- system controls for the Game Bar widget", additions + "\n\n    # ---- system controls for the Game Bar widget")
    result = replace_once(result, '        self.ecore_path = self.data / "ecore.json"',
                          '        self.ecore_path = self.data / "ecore.json"\n        self.ecore_backup_path = self.data / "ecore-backup.json"\n        self.ecore_lock = asyncio.Lock()')
    result = replace_once(result, 'if command == "ecore_set":', 'if command in ("ecore_set", "ecore_restore"):')
    compile(result, relative, "exec")
    write(relative, before, result)

    relative = "plugin/claw_service.py"
    before, ours = read(relative), (ROOT / relative).read_text(encoding="utf-8-sig")
    parent = next(n.name for n in ast.parse(before).body if isinstance(n, ast.ClassDef) and any(getattr(m, "name", "") == "set_ecore" for m in n.body))
    ours_parent = next(n.name for n in ast.parse(ours).body if isinstance(n, ast.ClassDef) and any(getattr(m, "name", "") == "restore_ecore" for m in n.body))
    anchor = function(before, "set_ecore", parent)
    write(relative, before, replace_once(before, anchor, anchor + "\n\n" + function(ours, "restore_ecore", ours_parent)))

    relative = "plugin/claw_agent.py"
    before = read(relative)
    write(relative, before, replace_once(before, '"set_ecore",', '"set_ecore", "restore_ecore",'))

    # Preserve the deployed helper revision and all of its telemetry changes.
    relative = "plugin/plugin.json"
    before = read(relative)
    manifest = json.loads(before)
    manifest["powerHelperRevision"] = int(manifest.get("powerHelperRevision", 0)) + 1
    write(relative, before, json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")

    for file in ("ClawMappedInput.cs", "ClawFanHardware.cs", "ClawDisplay.cs", "ClawGpu.cs", "ClawSystem.cs", "ClawUpdate.cs"):
        (out / "native").mkdir(exist_ok=True)
        shutil.copy2(source / "native" / file, out / "native" / file)
    shutil.copy2(ROOT / "native/ClawSmallCores.cs", out / "native/ClawSmallCores.cs")
    relative = "native/ClawPower.cs"
    before = read(relative)
    start = before.index("static class SmallCores {")
    end = before.index("\n}\n", start) + 3
    result = before[:start] + before[end:]
    anchor = '            else if (command == "ecore_set") reply["ecore"] = SmallCores.Set(true.Equals(request.ContainsKey("enabled") ? request["enabled"] : false));'
    ours = (ROOT / relative).read_text(encoding="utf-8-sig")
    start = ours.index('            else if (command == "ecore_set") {')
    end = ours.index('\n', ours.index('else if (command == "ecore_restore")', start))
    result = replace_once(result, anchor, ours[start:end])
    write(relative, before, result)

    # Build newer UI packages from the current source, with this feature's rows only.
    ignore = shutil.ignore_patterns("b", "o", "bin", "obj", "AppPackages", "Generated Files", "BundleArtifacts", ".vs")
    shutil.copytree(source / "frontend/library-native", out / "frontend/library-native", ignore=ignore)
    shutil.copytree(source / "native/GameBarWidget", out / "native/GameBarWidget", ignore=ignore)
    relative = "frontend/library-native/ClawLibrary/Stage/WallStage.Perf.cs"
    before, ours = read(relative), (ROOT / relative).read_text(encoding="utf-8-sig")
    start = ours.index("    private async Task PerfRestoreEcore()")
    end = ours.index("\n    }", start) + 6
    result = replace_once(before, "    // ---------------- 读设置的小工具", ours[start:end] + "\n\n    // ---------------- 读设置的小工具")
    start = result.index('        bool ecore = Bool(_perfView?["ecore"]?["enabled"])')
    end = result.index('\n\n', start)
    our_start = ours.index('        bool ecore = Bool(_perfView?["ecore"]?["enabled"])')
    our_end = ours.index('\n\n', our_start)
    result = result[:start] + ours[our_start:our_end] + result[end:]
    write(relative, before, result)

    relative = "native/GameBarWidget/WidgetPage.cpp"
    before = read(relative)
    start = before.index('        else if (id == L"ecore")\n        {')
    end = before.index('\n        }', start) + 10
    ours = (ROOT / relative).read_text(encoding="utf-8-sig")
    our_start = ours.index('        else if (id == L"ecore")\n        {')
    our_end = ours.index('\n        }', our_start) + 10
    write(relative, before, before[:start] + ours[our_start:our_end] + before[end:])

    versions = {}
    for relative in ("frontend/library-native/ClawLibrary/Package.appxmanifest", "native/GameBarWidget/Package.appxmanifest"):
        before = read(relative)
        match = re.search(r'<Identity[^>]*Version="([0-9.]+)"', before)
        old = match[1]
        parts = old.split(".")
        parts[2] = str(int(parts[2]) + 1)
        new = ".".join(parts)
        write(relative, before, before[:match.start(1)] + new + before[match.end(1):])
        versions[relative] = new
    relative = "frontend/library-native/ClawLibrary/ClawLibrary.csproj"
    before = read(relative)
    new = versions["frontend/library-native/ClawLibrary/Package.appxmanifest"].removesuffix(".0")
    write(relative, before, re.sub(r"<Version>[^<]+</Version>", "<Version>" + new + "</Version>", before))
    (out / "integration.json").write_text(json.dumps({"sourceRoot": str(source), "versions": versions, "changes": changes}, indent=2), encoding="utf-8")
    print(json.dumps({"output": str(out), "versions": versions, "powerHelperRevision": manifest["powerHelperRevision"]}))


if __name__ == "__main__":
    main()
