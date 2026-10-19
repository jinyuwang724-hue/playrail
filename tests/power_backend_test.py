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


def main():
    base = json.loads(json.dumps(power.DEFAULTS))
    assert power.validate(base)["ac"] == {"tdp": 35, "freq": 2750}
    expect_error({**base, "mode": "turbo"})
    expect_error({**base, "ac": {"tdp": 41, "freq": 0}})
    expect_error({**base, "dc": {"tdp": 4, "freq": 0}})
    expect_error({**base, "manual": {"tdp": 20.5, "freq": 0}})
    expect_error({**base, "boost": 16})
    expect_error({**base, "manual": {"tdp": 20, "freq": 1234}})

    assert power.validate(base)["smart"] == {"profile": "performance"}
    assert power.validate({**base, "smart": {"target": 60, "floor": 8, "acMax": 35, "dcMax": 25}})["smart"] == {"profile": "performance"}
    expect_error({**base, "smart": {"profile": "turbo"}})
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
    assert power.validate(base)["fan"]["mode"] == "balanced"
    assert power.validate({**base, "fan": {"mode": "firmware"}})["fan"]["mode"] == "balanced"
    expect_error({**base, "fan": {"mode": "turbo", "full": False}})
    expect_error({**base, "fan": {"mode": "quiet", "curves": {"quiet": [1, 2, 3, 4, 5, 101]}, "full": False}})
    expect_error({**base, "fan": {"mode": "quiet", "curves": {"loud": [1, 2, 3, 4, 5, 6]}, "full": False}})
    old = power.validate({**base, "fan": {"mode": "custom", "custom": [60, 64, 68, 74, 80, 84], "full": False}})["fan"]
    assert old == {"mode": "balanced", "curves": power.FAN_PRESETS, "full": False}, "0.16.9 custom becomes 均衡"
    edited = power.validate({**base, "fan": {"mode": "quiet", "curves": {"quiet": [20, 30, 50, 70, 90, 100]}, "full": False}})["fan"]
    assert edited["curves"]["quiet"] == [20, 30, 50, 70, 76, 76] and edited["curves"]["balanced"] == power.FAN_PRESETS["balanced"]
    assert power.fan_points(edited)[0] == [50, 20]
    assert max(power.FAN_PRESETS["quiet"] + power.FAN_PRESETS["balanced"]) <= power.FAN_CAP < max(power.FAN_PRESETS["performance"]), "静音/均衡 stay under 4700 rpm"
    assert power.validate({**base, "fan": {"mode": "performance", "curves": {"performance": [30, 45, 63, 76, 88, 100], "balanced": [20, 30, 50, 63, 90, 100]}, "full": False}})["fan"]["curves"]["balanced"][-1] == power.FAN_CAP
    curve = power.fan_points({"mode": "balanced", "curves": power.FAN_PRESETS})
    assert power.fan_duty_at(curve, 30) == 20 and power.fan_duty_at(curve, 65) == 40 and power.fan_duty_at(curve, 80) == power.FAN_CAP
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
            # Frequency caps follow this CPU: 0x2F = 4.7 GHz turbo -> nothing at or above it is offered.
            assert power.max_turbo_mhz({"turboRatios": "2E2E2E2E2E2E2E2F"}) == 4700
            assert power.max_turbo_mhz({}) is None
            assert power.freq_offered(4700) == [0, 1500, 2000, 2250, 2500, 2750, 3000, 3250, 3500, 4000, 4500]
            assert power.freq_offered(4700, [5000, 2750])[-1] == 5000, "a saved older cap stays selectable"
            assert power.freq_offered(0)[-1] == 4500
            # E-core switch: "pending" means the registry differs from what this boot started with.
            state = {"on": True, "boot": 1000.0}
            power.small_cores_enabled, power.boot_time = (lambda: state["on"]), (lambda: state["boot"])
            assert control.ecore_view() == {"enabled": True, "pending": False}
            state["on"] = False
            assert control.ecore_view() == {"enabled": False, "pending": True}
            state["boot"] = 5000.0  # restarted: the new state is what Windows uses now
            assert control.ecore_view() == {"enabled": False, "pending": False}
            state["on"] = True
            assert control.ecore_view()["pending"]
            try:
                await control.set_ecore("on")
                raise AssertionError("accepted a non-boolean switch")
            except ValueError:
                pass
            if "--live" in sys.argv:
                view = await control.get(sample=True)
                print(json.dumps(view["actual"], ensure_ascii=False))
                assert view["actual"] and view["actual"]["pl1"] > 0, view["error"]
                assert not Path(data, "power-backup.json").exists(), "a read must not write a backup"
                await control.close()
    asyncio.run(policy())
    print("power backend checks passed")


main()
