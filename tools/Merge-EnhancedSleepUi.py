"""Apply only the two sleep UI/input changes to Claude's current source."""
import hashlib
import json
from pathlib import Path
import shutil
import sys

out = Path(sys.argv[1]).resolve()
full = "--full" in sys.argv
integration = json.loads((out / "integration.json").read_text(encoding="utf-8-sig"))
live = Path(integration["uiSource"]) / "ClawLibrary"
base = Path(integration["uiBase"]) / "ClawLibrary"
candidate = out / "src/frontend/library-native/ClawLibrary"
files = ["Stage/WallStage.System.cs", "Input/GamepadPoller.cs"]
if full:
    files += ["Stage/WallStage.NowPlaying.cs", "Data/EnhancedSleepClient.cs", "Stage/WallStage.Oobe.cs",
              "Package.appxmanifest", "ClawLibrary.csproj", "Payload/Install-Components.ps1", "Payload/agent/agent.zip"]
digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
for name in files:
    actual = digest(live / name) if (live / name).exists() else None
    expected = digest(base / name) if (base / name).exists() else None
    if actual not in (expected, digest(candidate / name)):
        raise RuntimeError("Claude changed this file; rebase before merging: " + name)
backup = out / "live-ui-before"
for name in files:
    target = live / name
    saved = backup / name
    saved.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        shutil.copy2(target, saved)
    # Verify again immediately before the atomic replacement.
    if target.exists() and digest(target) != digest(saved):
        raise RuntimeError("Concurrent edit: " + name)
    temporary = target.with_suffix(".enhanced.tmp")
    shutil.copyfile(candidate / name, temporary)
    temporary.replace(target)
if full:
    if (live / "Payload/sleep").exists():
        raise RuntimeError("Existing sleep payload must be inspected before replacement")
    shutil.copytree(candidate / "Payload/sleep", live / "Payload/sleep")
(out / "live-ui-merge.json").write_text(json.dumps({"source": str(live), "files": {name: digest(live / name) for name in files}}, indent=2), encoding="utf-8")
print("Merged enhanced sleep into Claude's current source" + (" and OOBE payload" if full else "; version preserved"))
