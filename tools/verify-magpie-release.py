"""Check installed and packaged bytes, including corresponding GPL source."""
from pathlib import Path
import hashlib
import json
import zipfile
root=Path(__file__).resolve().parents[1]
installed=Path.home()/"homebrew/plugins/claw-button-router"
version=json.loads((root/"plugin/plugin.json").read_text(encoding="utf-8"))["version"]
digest=lambda data:hashlib.sha256(data).hexdigest()
with zipfile.ZipFile(root/f"artifacts/claw-button-router-{version}.zip") as package:
    count=0
    for entry in package.infolist():
        if entry.is_dir(): continue
        relative=Path(*Path(entry.filename.replace('\\','/')).parts[1:])
        bundled=package.read(entry)
        assert digest(bundled)==digest((root/"plugin"/relative).read_bytes()),relative
        assert digest(bundled)==digest((installed/relative).read_bytes()),relative
        count+=1
    meta=json.loads(package.read("claw-button-router/plugin.json"))
    assert meta["version"]==version
with zipfile.ZipFile(root/"plugin/bin/magpie/magpie-source.zip") as source:
    names=source.namelist()
    assert "Claw/native/MagpieLauncher.cs" in names
    assert "Claw/MAGPIE_REAL_GAME_20261010.md" in names
    assert any(n.endswith("/ClawControl.cpp") for n in names)
assert (root/"plugin/bin/ClawRouter.exe").read_bytes()==(root/f"plugin/bin/ClawRouter-{version}.exe").read_bytes()
result={"version":version,"filesVerified":count,"correspondingSourceVerified":True,"routerBuildVerified":True,
        "packageSHA256":digest((root/f"artifacts/claw-button-router-{version}.zip").read_bytes())}
(root/f"artifacts/release-validation-{version}.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
print(json.dumps(result))
