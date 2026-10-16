"""Build the private Python runtime the toolbox agent runs on (the device has no system Python).

Usage: python tools/Build-AgentRuntime.py <out dir>
Run it with the Python 3.x that should be bundled; it copies that interpreter, its DLLs and its
standard library compiled into pythonXY.zip, plus a ._pth file that isolates it from any other
Python on the machine. Third-party packages (site-packages) and Tk are left out."""
import json
from pathlib import Path
import py_compile
import shutil
import sys
import tempfile
import zipfile

SKIP_DIRS = {"site-packages", "test", "tests", "idlelib", "tkinter", "turtledemo", "ensurepip", "lib2to3",
             "__pycache__", "venv", "pydoc_data", "unittest"}
SKIP_DLLS = {"_tkinter.pyd", "tcl86t.dll", "tk86t.dll"}


def main(out):
    out = Path(out)
    source = Path(sys.base_prefix)
    tag = f"python{sys.version_info.major}{sys.version_info.minor}"
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    for name in ("python.exe", "pythonw.exe", f"{tag}.dll", "python3.dll", "vcruntime140.dll", "vcruntime140_1.dll",
                 "LICENSE.txt"):
        if (source / name).exists():
            shutil.copy2(source / name, out / name)
    for item in (source / "DLLs").iterdir():
        if item.suffix.lower() in (".pyd", ".dll") and item.name not in SKIP_DLLS and not item.name.startswith(("_test", "_ctypes_test")):
            shutil.copy2(item, out / item.name)
    lib = source / "Lib"
    count = 0
    with tempfile.TemporaryDirectory() as temp, zipfile.ZipFile(out / f"{tag}.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(lib.rglob("*.py")):
            relative = path.relative_to(lib)
            if any(part in SKIP_DIRS for part in relative.parts[:-1]):
                continue
            compiled = Path(temp) / relative.with_suffix(".pyc")
            compiled.parent.mkdir(parents=True, exist_ok=True)
            try:
                py_compile.compile(str(path), cfile=str(compiled), dfile=str(relative), doraise=True, optimize=0)
            except py_compile.PyCompileError:
                continue
            archive.write(compiled, relative.with_suffix(".pyc").as_posix())
            count += 1
    (out / f"{tag}._pth").write_text(f"{tag}.zip\n.\n", encoding="ascii")
    (out / "runtime.json").write_text(json.dumps({"version": sys.version.split()[0], "tag": tag, "modules": count}),
                                      encoding="utf-8")
    print(f"Runtime {sys.version.split()[0]}: {count} modules -> {out}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    main(sys.argv[1])
