"""Checks claw_power settings policy. With --live, also reads (never writes) the real helper."""
import asyncio
import importlib.util
import json
from pathlib import Path
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1] / "plugin"
spec = importlib.util.spec_from_file_location("claw_power", ROOT / "claw_power.py")
power = importlib.util.module_from_spec(spec)
spec.loader.exec_module(power)


def expect_error(value):
    try:
        power.validate(value)
    except ValueError:
        return
    raise AssertionError(f"accepted {value}")


class Clock:
    def __init__(self): self.now = 0.0
    def __call__(self): return self.now


def simulate(game_fps, target, cap, seconds=120, floor=8, start=None):
    """game_fps(limit) -> (real fps, watts drawn). Returns the governor's limit history."""
    clock = Clock(); g = power.SmartGovernor(clock)
    history = []
    for _ in range(seconds):
        limit = g.limit if history else (start or cap)
        fps, draw = game_fps(limit)
        history.append(g.update("game.exe", fps, draw, target, floor, cap, start))
        clock.now += 1
    return history, g


def governor_checks():
    # GPU-bound AAA: fps scales with watts, draws the whole limit.
    aaa = lambda w: (min(100, 2.6 * w), w)
    h, _ = simulate(aaa, 60, 35)
    assert 22 <= h[-1] <= 25, h[-20:]
    # A heavier game on battery cap 25 W cannot reach 60: stays at the cap.
    h, _ = simulate(lambda w: (min(100, 2.0 * w), w), 60, 25)
    assert h[-1] == 25, h[-10:]
    # Frame-generated game: real fps is what counts (presented would be 4x).
    h, _ = simulate(lambda w: (min(50, 2.0 * w), w), 60, 35)
    assert h[-1] >= 24, h[-10:]
    # Static visual novel: ~0.4 fps, draws ~5 W. Must fall to the floor, not rise.
    h, _ = simulate(lambda w: (0.4, min(w, 5.0)), 60, 35)
    assert h[-1] == 8, h[-10:]
    # CPU-bound game stuck at 50 fps drawing 14 W: no point giving it 35 W.
    h, _ = simulate(lambda w: (min(50, 3 * w), min(w, 14.0)), 60, 35)
    assert h[-1] <= 16, h[-10:]
    # Bound but saturated (memory-bound): watts above 18 add < 3 % fps.
    sat = lambda w: ((2.5 * w) if w <= 18 else (45 + 0.1 * (w - 18)), w)
    h, g = simulate(sat, 60, 35, start=16)
    assert max(h[-30:]) <= 20, h[-30:]
    # Uncapped target: climbs while fps still grows.
    h, _ = simulate(aaa, 0, 35)
    assert h[-1] >= 34, h[-10:]


def main():
    base = json.loads(json.dumps(power.DEFAULTS))
    assert power.validate(base)["ac"] == {"tdp": 35, "freq": 2750}
    expect_error({**base, "mode": "turbo"})
    expect_error({**base, "ac": {"tdp": 41, "freq": 0}})
    expect_error({**base, "dc": {"tdp": 4, "freq": 0}})
    expect_error({**base, "manual": {"tdp": 20.5, "freq": 0}})
    expect_error({**base, "boost": 16})
    expect_error({**base, "manual": {"tdp": 20, "freq": 1234}})

    assert power.validate(base)["smart"] == {"target": 60, "acMax": 35, "dcMax": 25, "floor": 8}
    expect_error({**base, "smart": {"target": 55, "acMax": 35, "dcMax": 25, "floor": 8}})
    expect_error({**base, "smart": {"target": 60, "acMax": 36, "dcMax": 25, "floor": 8}})
    governor_checks()
    full = {"time": "19:30", "rtssFps": 199.6, "frameTime": 5.0, "fps": 50, "presented": 200, "fps01": 41, "package": 21.5, "limit": 25, "target": 60,
            "cpuUsage": 35, "cpuMhz": 2480, "cpuTemp": 68, "gpuUsage": 90, "gpuMhz": 2100, "gpuTemp": 70, "gpuPower": 15.2,
            "ramUsed": 15.8, "gpuMem": 4198, "battery": 85, "charger": False, "batteryW": -12.4, "minutes": 105, "fanRpm": 5161, "fanDuty": 84, "cpuPower": 8.4, "ramTotal": 31.5, "graph": True}
    import re
    visible = lambda s: re.sub(r"<[^>]*>", "", s)
    for level in (1, 2, 3, 4):
        text = power.osd_text(level, full)
        assert "\n" not in text, text
        text.encode("gbk")  # RTSS takes ANSI text
        print(level, len(visible(text)), visible(text))
    assert "REAL 50" in visible(power.osd_text(1, full)), "frame generation shows the real rendered rate"
    for level in (1, 2, 3, 4):
        assert all(ord(c) < 128 for c in visible(power.osd_text(level, full))), "no CJK in the OSD"
    assert "{GRAPH}" in power.osd_text(4, full) and "{GRAPH}" not in power.osd_text(3, full)
    assert "CPU" not in power.osd_text(2, full) and "68oC" in visible(power.osd_text(2, full))
    assert len(visible(power.osd_text(4, full))) < 260, "level 4 stays one line"
    assert "\n" in power.osd_text(4, full, "large") and "\n" in power.osd_text(4, full, "small", 2)
    assert power.validate({**base, "osd": {"enabled": True, "time": True, "fps": True}})["osd"] == {"enabled": True, "level": 2, "size": "medium", "rows": 1, "smallUnits": False}
    expect_error({**base, "osd": {"enabled": True, "level": 5, "size": "medium"}})
    assert power.validate(base)["fan"]["mode"] == "firmware"
    expect_error({**base, "fan": {"mode": "turbo", "custom": [1, 2, 3, 4, 5, 6], "full": False}})
    expect_error({**base, "fan": {"mode": "custom", "custom": [1, 2, 3, 4, 5, 101], "full": False}})
    assert power.fan_points({"mode": "quiet", "custom": [0] * 6}) == power.FAN_PRESETS["quiet"]
    assert power.validate({**base, "fan": {"mode": "custom", "custom": [20, 30, 50, 70, 90, 100], "full": False}})["fan"]["custom"] == [20, 30, 50, 70, 76, 76]
    assert all(max(d for _, d in c) <= power.FAN_CAP for c in power.FAN_PRESETS.values()), "presets stay under 4700 rpm"
    curve = power.FAN_PRESETS["balanced"]
    assert power.fan_duty_at(curve, 30) == 20 and power.fan_duty_at(curve, 65) == 40 and power.fan_duty_at(curve, 99) == power.FAN_CAP
    fan = power.FanGovernor(); assert fan.update(curve, 50, 0) == 20
    for t in range(1, 4): assert fan.update(curve, 80, t) == 20, "a 3 s spike does not speed the fan up"
    for t in range(4, 20): assert fan.update(curve, 50, t) == 20
    for t in range(20, 25): assert fan.update(curve, 70, t) == 20
    assert fan.update(curve, 70, 25) == 50, "6 s above speeds it up"
    for t in range(26, 30): assert fan.update(curve, 50, t) == 50, "a short dip does not slow it down"
    for t in range(30, 36): fan.update(curve, 50, t)
    assert fan.duty == 20, "10 s below slows it down"
    assert fan.update(curve, 96, 36) == power.FAN_CAP, "very hot reacts at once"
    assert "5161" in power.osd_text(3, full) and "5161" not in power.osd_text(2, full)
    assert "VRAM" not in power.osd_text(4, full) and "1h45min" in visible(power.osd_text(2, full))
    charging = {**full, "charger": True, "batteryW": 18.0, "chargeMinutes": 40, "minutes": None}
    assert "+18.0W" in visible(power.osd_text(3, charging)) and "FULL in 40min" in visible(power.osd_text(3, charging))
    assert visible(power.osd_text(4, full)).endswith("BAT -12.4W 85% 1h45min") and "GPU" in visible(power.osd_text(4, full)).split("TDP")[0] and "FAN" in visible(power.osd_text(4, full)).split("TDP")[1], visible(power.osd_text(4, full))
    rate = power.BatteryRate(Clock()); rate.clock.now = 0; assert rate.update(80000, False) is None
    rate.clock.now = 60; assert abs(rate.update(79800, False) - (-12.0)) < 0.01
    assert "FULL" in power.osd_text(2, {**full, "charger": True, "battery": 100, "batteryW": 0, "minutes": None})
    async def policy():
        with tempfile.TemporaryDirectory() as data:
            async def emit(_):
                pass
            control = power.PowerControl(ROOT, data, emit)
            control.settings = power.validate({**base, "enabled": True, "mode": "auto"})
            control.charger = True
            assert control.target() == (35, 36)
            control.charger = False
            assert control.target() == (25, 26)
            control.settings["mode"] = "manual"
            control.charger = True
            assert control.target() == (20, 21), "manual ignores both presets"
            assert control.freq_target() == (0, 0)
            control.settings["mode"] = "auto"
            assert control.freq_target() == (2750, 2750)
            if "--live" in sys.argv:
                view = await control.get(sample=True)
                print(json.dumps(view["actual"], ensure_ascii=False))
                assert view["actual"] and view["actual"]["pl1"] > 0, view["error"]
                assert not Path(data, "power-backup.json").exists(), "a read must not write a backup"
                await control.close()
    asyncio.run(policy())
    print("power backend checks passed")


main()
