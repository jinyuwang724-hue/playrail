"""Export the compiled upstream catalog without opening or capturing any window."""
import asyncio
import json
import shutil
from pathlib import Path

async def main():
    root = Path(__file__).resolve().parents[1]
    folder = root / "artifacts/magpie-catalog-runtime"
    shutil.copytree(root / "plugin/bin/magpie", folder, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("magpie-source.zip", "logs", "cache"))
    (folder / "config").mkdir(exist_ok=True)
    # Empty portable config constructs upstream defaults in this owned directory.
    (folder / "config/config.json").write_text("", encoding="utf-8")
    process = await asyncio.create_subprocess_exec(str(folder / "Magpie.exe"), "--claw",
        cwd=folder, stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, limit=2**21)
    try:
        process.stdin.write(b'{"id":1,"command":"catalog"}\n')
        await process.stdin.drain()
        reply = json.loads(await asyncio.wait_for(process.stdout.readline(), 30))
        assert reply["ok"]
        catalog = reply["catalog"]
        # Upstream's CRT preset retains an obsolete interlace parameter that the
        # shipped shader no longer declares. Match the executable effect schema.
        schemas = {e["name"]: {p["name"] for p in e["parameters"]} for e in catalog["effects"]}
        for preset in catalog["presets"]:
            for stage in preset["effects"]:
                stage["parameters"] = {k:v for k,v in stage["parameters"].items() if k in schemas[stage["name"]]}
        path = root / "plugin/bin/magpie/claw-catalog.json"
        path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"effects":len(catalog["effects"]), "presets":[p["name"] for p in catalog["presets"]]}))
    finally:
        process.stdin.close()
        try: await asyncio.wait_for(process.wait(), 5)
        except asyncio.TimeoutError: process.kill(); await process.wait()

asyncio.run(main())
