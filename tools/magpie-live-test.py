"""Owned disposable window + source-built engine; never touches a real game."""
import asyncio
import json
from pathlib import Path
import sys
import time
from PIL import ImageGrab, ImageChops, ImageStat

root = Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/"plugin"))
from claw_magpie import Magpie, DEFAULTS

async def run():
    folder = root/"artifacts/magpie-live"
    folder.mkdir(parents=True,exist_ok=True)
    evidence = {"started":time.time(),"steps":[]}
    game = engine = None
    try:
        game = await asyncio.create_subprocess_exec(str(folder/"MagpieTestGame.exe"),stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.DEVNULL)
        hwnd = int((await asyncio.wait_for(game.stdout.readline(),10)).decode().strip())
        async def emit(value):
            evidence["steps"].append({k:value.get(k) for k in ("phase","active","source","dest","crop","error")})
        engine = Magpie(root/"plugin",folder,emit)
        settings = {**DEFAULTS,"target":hwnd,"sharpen":False}
        await engine.apply(settings)
        game.stdin.write(b"focus\n"); await game.stdin.drain()
        deadline = time.monotonic()+40
        while not engine.state["active"] and engine.state["enabled"] and time.monotonic()<deadline:
            await asyncio.sleep(.15)
        if not engine.state["active"]:
            raise RuntimeError("No actual output: "+json.dumps(engine.view(),ensure_ascii=False))
        source, dest = engine.state["source"],engine.state["dest"]
        assert dest[2]-dest[0] == 1920 and dest[3]-dest[1] == 1200, dest
        assert source[2]-source[0] == 640 and source[3]-source[1] == 400, source
        await asyncio.sleep(.6)
        off = ImageGrab.grab(bbox=tuple(dest)); off.save(folder/"cover.png")
        pid = engine.process.pid
        await engine.pause("panel",True)
        await asyncio.sleep(.6)
        paused_output = engine.windows.output(pid)
        assert engine.process.pid == pid and (not paused_output or not paused_output["visible"]), "Pause did not remove output"
        await engine.pause("panel",False)
        game.stdin.write(b"focus\n"); await game.stdin.drain()
        for _ in range(100):
            if engine.state["active"]: break
            await asyncio.sleep(.1)
        assert engine.state["active"] and engine.process.pid == pid, "Resume restarted child or failed"
        await engine.apply({**settings,"sharpen":True,"strength":1})
        game.stdin.write(b"focus\n"); await game.stdin.drain()
        for _ in range(200):
            if engine.state["active"] or not engine.state["enabled"]: break
            await asyncio.sleep(.1)
        assert engine.state["active"],engine.view()
        await asyncio.sleep(.6)
        on = ImageGrab.grab(bbox=tuple(engine.state["dest"])); on.save(folder/"cover-sharpen.png")
        diff = ImageChops.difference(off,on)
        evidence["pixelDifferenceMean"] = ImageStat.Stat(diff).mean
        assert max(evidence["pixelDifferenceMean"]) > .01, "Sharpen had no pixel effect"
        await engine.stop()
        assert engine.process is None and not engine.state["enabled"]
        evidence["ok"] = True
    except Exception as error:
        evidence["ok"] = False; evidence["error"] = str(error)
    finally:
        if engine: await engine.close()
        if game and game.returncode is None:
            game.stdin.close()
            try: await asyncio.wait_for(game.wait(),5)
            except asyncio.TimeoutError: game.terminate(); await game.wait()
        (folder/"result.json").write_text(json.dumps(evidence,ensure_ascii=False,indent=2),encoding="utf-8")

asyncio.run(run())
