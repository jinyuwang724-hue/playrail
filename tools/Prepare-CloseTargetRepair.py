"""Rebase only close-target changes onto the installed agent; keep newer OSD work intact."""
import argparse
import ast
import difflib
import hashlib
import json
import os
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[1]


def method(source, name):
    cls = next(n for n in ast.parse(source).body if isinstance(n, ast.ClassDef) and n.name == "PowerControl")
    node = next(n for n in cls.body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef)) and n.name == name)
    return "".join(source.splitlines(keepends=True)[node.lineno - 1:node.end_lineno])


def once(source, before, after):
    if source.count(before) != 1:
        raise RuntimeError("Missing or ambiguous integration anchor: " + before[:80])
    return source.replace(before, after, 1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", type=Path, default=Path(os.environ["LOCALAPPDATA"]) / "ClawToolbox/app")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    stage = args.output / "app"
    stage.mkdir()
    for item in args.app.iterdir():
        if item.suffix == ".py" or item.name == "plugin.json":
            shutil.copy2(item, stage / item.name)
    before = (args.app / "claw_power.py").read_text(encoding="utf-8-sig")
    ours = (ROOT / "plugin/claw_power.py").read_text(encoding="utf-8-sig")
    after = before
    if "_close_spec =" not in before:
        after = once(after, "import winreg\n", "import winreg\n" + ours.split("import winreg\n", 1)[1].split("# Intel rates", 1)[0])
        after = once(after, "        self.sys_task = None\n", "        self.sys_task = None\n        self.close_target = _close_module.CloseTarget()\n        self.close_target_task = None\n")
        after = once(after, "            self.task = asyncio.create_task(self._loop())", "            self.close_target_task = asyncio.create_task(self._close_target_loop())\n            self.task = asyncio.create_task(self._loop())")
        after = once(after, "        self.closing = True\n", "        self.closing = True\n        if self.close_target_task:\n            self.close_target_task.cancel()\n            await asyncio.gather(self.close_target_task, return_exceptions=True)\n")
    for name in ("system", "system_cached", "close_game"):
        after = once(after, method(after, name), method(ours, name))
    if "    async def _close_target_loop(self):" in after:
        after = once(after, method(after, "_close_target_loop"), method(ours, "_close_target_loop"))
    else:
        after = once(after, "    async def system(self):", method(ours, "_close_target_loop") + "\n\n    async def system(self):")
    (stage / "claw_power.py").write_text(after, encoding="utf-8")
    shutil.copy2(ROOT / "plugin/claw_close_target.py", stage / "claw_close_target.py")
    changes = {}
    for name in ("claw_power.py", "claw_close_target.py"):
        target = args.app / name
        base = target.read_bytes() if target.exists() else None
        candidate = (stage / name).read_bytes()
        compile(candidate, name, "exec")
        changes[name] = {"baseSha256": hashlib.sha256(base).hexdigest() if base is not None else None,
                         "sha256": hashlib.sha256(candidate).hexdigest()}
    (args.output / "claw_power.patch").write_text("".join(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile="installed/claw_power.py", tofile="candidate/claw_power.py")), encoding="utf-8")
    (args.output / "repair.json").write_text(json.dumps({"app": str(args.app.resolve()), "changes": changes}, indent=2), encoding="utf-8")
    print(json.dumps({"stage": str(stage.resolve()), "changes": changes}))


if __name__ == "__main__": main()
