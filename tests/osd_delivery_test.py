import asyncio
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location(
    "claw_overlay", Path(__file__).resolve().parents[1] / "plugin" / "claw_overlay.py"
)
overlay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(overlay)


async def test_deltas():
    messages = []
    now = 10

    async def drain():
        pass

    stdin = SimpleNamespace(write=lambda data: messages.append(json.loads(data)), drain=drain)
    renderer = overlay.FixedOverlay(Path("."))
    renderer.process = SimpleNamespace(returncode=None, stdin=stdin)
    settings = {"enabled": True, "level": 2, "renderer": "gamebar", "position": 0}
    sample = {"gpuUsage": 20, "fps": 60, "cpuCores": [1, 2]}
    views = []

    def view():
        views.append(1)
        return {"sample": sample}

    real_time = overlay.time
    overlay.time = SimpleNamespace(monotonic=lambda: now)
    try:
        await renderer.refresh(settings, 1, view)
        await renderer.refresh(settings, 1, view)
        assert len(views) == 1
        assert messages[0]["sample"] == {"gpuUsage": 20, "fps": 60}
        await renderer.refresh(settings, 2, view)
        assert len(messages) == 1

        sample["gpuUsage"] = 21
        sample.pop("fps")
        await renderer.refresh(settings, 3, view)
        assert messages[1] == {"delta": {"gpuUsage": 21}, "remove": ["fps"]}

        now += 5
        await renderer.refresh(settings, 3, view)
        assert messages[2] == {"heartbeat": True}
    finally:
        overlay.time = real_time


def test_frame_cadence():
    now, hwnd = 10.0, 100
    cadence = overlay.FrameCadence(lambda: now, lambda: hwnd)
    sample = {"fps": 60, "graphEnd": 12.0, "graphEpoch": "game"}
    assert cadence.due()
    cadence.finish(sample)
    now += .05
    assert cadence.due()
    cadence.finish({**sample, "graphEnd": 12.05})
    now += .31
    cadence.finish({**sample, "graphEnd": 12.05})
    assert not cadence.due()
    hwnd = 200
    assert cadence.due()
    cadence.finish({"fps": 60, "frameHeld": True})
    assert not cadence.due()
    now += .26
    assert cadence.due()


async def test_hardware_display():
    messages = []
    now = 0.0

    async def drain():
        pass

    renderer = overlay.FixedOverlay(Path("."))
    stdin = SimpleNamespace(write=lambda data: messages.append(json.loads(data)), drain=drain)
    renderer.process = SimpleNamespace(returncode=None, stdin=stdin)
    settings = {"enabled": True, "level": 3, "renderer": "gamebar"}

    async def send(sample_id, watts, fps=60, source="intelPsys", cpu=0, gpu=0):
        sample = {"sampleId": sample_id, "batteryW": -watts, "batteryWSource": source,
                  "batteryRemaining": 40000, "charger": False, "cpuPower": watts / 2, "fps": fps,
                  "cpuUsage": cpu, "gpuUsage": gpu}
        await renderer.refresh(settings, (sample_id, fps, source), lambda: {"sample": sample})

    real_time = overlay.time
    overlay.time = SimpleNamespace(monotonic=lambda: now)
    try:
        await send(0, 14)
        assert messages[-1]["sample"]["batteryW"] == -14
        assert messages[-1]["sample"]["minutes"] is None
        assert messages[-1]["sample"]["cpuUsage"] == messages[-1]["sample"]["gpuUsage"] == 0
        now = 0.5
        await send(1, 16, cpu=60, gpu=90)
        await send(1, 16, fps=61, cpu=60, gpu=90)
        assert messages[-1] == {"delta": {"fps": 61}}
        now = 2
        await send(2, 15, cpu=30, gpu=60)
        assert messages[-1]["delta"]["batteryW"] == -15
        assert messages[-1]["delta"]["cpuPower"] == 7.5
        assert messages[-1]["delta"]["cpuUsage"] == 30
        assert messages[-1]["delta"]["gpuUsage"] == 50
        for tick in range(3, 17):
            now = tick
            await send(tick, 18)
        assert messages[-1]["delta"]["minutes"] == 137
        assert renderer.last_payload["sample"]["batteryW"] == -18
        assert renderer.last_payload["sample"]["cpuUsage"] == renderer.last_payload["sample"]["gpuUsage"] == 0
        now = 16.5
        await send(17, 12, source="sensor")
        assert messages[-1]["delta"]["batteryW"] == -12
        assert messages[-1]["delta"]["minutes"] is None
    finally:
        overlay.time = real_time


if __name__ == "__main__":
    asyncio.run(test_deltas())
    test_frame_cadence()
    asyncio.run(test_hardware_display())
    print("OSD delivery tests passed")
