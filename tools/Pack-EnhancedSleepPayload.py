"""Replace six entries in the existing OOBE agent payload; preserve all others."""
import json
from pathlib import Path
import sys
import zipfile

out = Path(sys.argv[1]).resolve()
source = out / "src"
manifest = json.loads((out / "integration.json").read_text(encoding="utf-8-sig"))
replacements = {"app/" + name.removeprefix("plugin/"): source / name
                for name in manifest["changes"] if name.startswith("plugin/")}
payload = source / "frontend/library-native/ClawLibrary/Payload/agent/agent.zip"
temporary = payload.with_suffix(".tmp.zip")
with zipfile.ZipFile(payload) as previous, zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED) as candidate:
    for entry in previous.infolist():
        if entry.filename.replace("\\", "/") not in replacements:
            candidate.writestr(entry, previous.read(entry))
    for name, path in replacements.items():
        candidate.write(path, name)
temporary.replace(payload)
print(f"Updated {len(replacements)} payload entries")
