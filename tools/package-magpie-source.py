"""Bundle corresponding GPL source, excluding generated and downloaded build files."""
from pathlib import Path
import zipfile
import shutil
import re
root = Path(__file__).resolve().parents[1]
source = root/"native/upstream/Magpie-0.12.1"
bundle = root/"plugin/bin/magpie"
notes = root/"docs/archive/toolbox-2026-10"
props = source/"obj/x64/Release/_ConanDeps/Magpie"
for file in props.glob("*_vars_*.props"):
    for name, folder in re.findall(r"<(Conan[A-Za-z0-9_-]+RootFolder)>([^<]+)</", file.read_text(encoding="utf-8")):
        licenses = Path(folder)/"licenses"
        if licenses.is_dir(): shutil.copytree(licenses,bundle/"third-party-licenses"/name,dirs_exist_ok=True)
for package in (source/"packages").iterdir():
    for file in package.glob("*LICENSE*"):
        destination = bundle/"third-party-licenses"/package.name/file.name
        destination.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(file,destination)
with zipfile.ZipFile(bundle/"magpie-source.zip","w",zipfile.ZIP_DEFLATED) as archive:
    for path in source.rglob("*"):
        relative = path.relative_to(source)
        if not path.is_file() or any(p in {"packages","obj","bin","publish",".vs",".git"} for p in relative.parts): continue
        if path.suffix in {".log",".user"}: continue
        archive.write(path,Path("Magpie-0.12.1-claw")/relative)
    archive.write(root/"build-magpie.ps1","Claw/build-magpie.ps1")
    archive.write(root/"native/MagpieLauncher.cs","Claw/native/MagpieLauncher.cs")
    archive.write(root/"tools/magpie-catalog.py","Claw/tools/magpie-catalog.py")
    archive.write(notes/"MAGPIE_IMPLEMENTATION_20261009.md","Claw/MAGPIE_IMPLEMENTATION_20261009.md")
    archive.write(notes/"MAGPIE_REAL_GAME_20261010.md","Claw/MAGPIE_REAL_GAME_20261010.md")
print(bundle/"magpie-source.zip")
