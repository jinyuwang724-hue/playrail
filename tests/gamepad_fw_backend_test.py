"""Checks the pad firmware settings against a fake pad. With --live, also reads the real pad (never writes)."""
import asyncio
import importlib.util
import json
from pathlib import Path
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1] / "plugin"
spec = importlib.util.spec_from_file_location("claw_gamepad_fw", ROOT / "claw_gamepad_fw.py")
fw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fw)

PROFILE_LENGTH = 1478


def claw_8_ex_memory():
    """Profile memory as read from a Claw 8 EX (1T91, firmware 0419) on 2026-10-16, trimmed to the read regions."""
    memory = bytearray(PROFILE_LENGTH)
    memory[0x22:0x24] = bytes((100, 100))
    memory[0x35:0x3A] = bytes((0, 0, 0, 0, 0x0A))
    for slot in range(16):
        memory[0x3A + slot * 8:0x3A + slot * 8 + 4] = bytes((1 if slot < 12 else 0, 0, 0x0C, slot + 1 if slot < 12 else 0))
    memory[0xBA:0xC3] = bytes((0x01, 0x04, 0x0C, 0x76, 0x3E, 0, 0, 0, 0))
    memory[0x163:0x16C] = bytes((0x01, 0x04, 0x0C, 0x12, 0, 0, 0, 0, 0))
    memory[0x20C:0x21B] = bytes((0x01, 0x02, 0x0C, 0x00, 0x64, 0x13)) + bytes(9)
    memory[0x21B:0x22A] = bytes((0x01, 0x02, 0x0C, 0x00, 0x64, 0x14)) + bytes(9)
    stick = bytes((0x01, 0x01, 0x05, 0, 0, 0, 0, 0, 0, 0x01, 0x64)) + bytes(5)
    memory[0x22A:0x23A] = stick
    memory[0x23A:0x24A] = stick
    memory[0x24A] = 0
    memory[0x24B:0x24F] = bytes((0x01, 0x09, 0x03, 0x64))
    memory[0x24B + 220:0x24B + 224] = bytes((0x07, 0x09, 0x0A, 0x64))
    return memory


class FakePad:
    """Answers like the MCU: 0x04 / 0x21 / 0x22 / 0x24 / 0x26. Records every command."""
    def __init__(self, memory, version=0x0419, mode=(0x01, 0x00), stale=False):
        self.memory, self.rom, self.version, self.mode, self.stale = memory, bytes(memory), version, mode, stale
        self.product, self.commands, self.queue = 0x1901, [], []

    def __call__(self):
        return self

    def open(self):
        return self

    def close(self):
        pass

    def send(self, frame):
        assert len(frame) == 64 and frame[:4] == bytes((0x0F, 0, 0, 0x3C))
        self.commands.append(frame[4])
        reply = bytearray(64); reply[:4] = bytes((0x10, 0, 0, 0x3C))
        address, length = (frame[6] << 8) | frame[7], frame[8]
        if frame[4] == 0x26:
            reply[4:7] = bytes((0x27,) + self.mode)
        elif frame[4] == 0x04:
            assert frame[5] == 0x01 and 1 <= length <= 55
            if self.stale:
                # A late reply for another address arrives first and must be skipped.
                junk = bytearray(reply); junk[4:10] = bytes((0x05, 0x01, 0x00, 0x22, 0x01, 0x55))
                self.queue.append(bytes(junk))
            reply[4:9] = bytes((0x05, 0x01, frame[6], frame[7], length))
            reply[9:9 + length] = self.memory[address:address + length]
        elif frame[4] == 0x21:
            assert frame[5] == 0x01 and 1 <= length <= 55 and address + length <= PROFILE_LENGTH
            self.memory[address:address + length] = frame[9:9 + length]
            reply[4] = 0x06
        elif frame[4] == 0x22:
            self.rom = bytes(self.memory); reply[4] = 0x06
        elif frame[4] == 0x24:
            self.mode = (frame[5], frame[6])
            return
        else:
            raise AssertionError("unexpected command 0x%02x" % frame[4])
        self.queue.append(bytes(reply))

    def receive(self, timeout_ms):
        return self.queue.pop(0) if self.queue else None


def reading():
    assert fw.read_request(fw._rumble_default, 0x24A, 55)[:9] == bytes((0x0F, 0, 0, 0x3C, 0x04, 0x01, 0x02, 0x4A, 55))
    assert fw.write_request(fw._rumble_default, 0x22C, b"\x0a")[:10] == bytes((0x0F, 0, 0, 0x3C, 0x21, 1, 2, 0x2C, 1, 10))
    assert fw.key_name(0x76) == "KEY_LEFTALT" and fw.key_name(0x3E) == "KEY_F12" and fw.key_name(0x40) == "KEY_1"
    assert fw.key_name(0x49) == "KEY_0" and fw.key_name(0x5C) == "KEY_A" and fw.key_name(0x94) == "KEY_KP9"
    assert fw.spans([5, 3, 4, 20, 9], gap=0) == [(3, 3), (9, 1), (20, 1)]
    assert fw.spans([3, 9], gap=8) == [(3, 7)]

    pad = FakePad(claw_8_ex_memory())
    state = fw.GamepadFirmware(pad).read_state()
    assert set(pad.commands) == {0x26, 0x04}, pad.commands  # reading never writes
    assert state["available"] and state["firmware"] == "0419" and state["product"] == "1901"
    assert state["mode"] == "xinput" and state["mkeysFunction"] == "macro"
    assert state["rumble"] == {"left": 100, "right": 100}
    m1, m2 = state["backKeys"]["m1"], state["backKeys"]["m2"]
    assert m1["mapped"] and m1["keys"] == ["KEY_LEFTALT", "KEY_F12"] and m1["macroSteps"] == 0
    assert m2["keys"] == ["FR1"]
    assert state["keys"]["A"] == {"index": 1, "flags": 0, "turbo": False, "mapped": False, "turboSpeed": 12,
                                  "codes": [0x09], "keys": ["A"]}
    assert state["keys"]["LT"] == {"mapped": False, "codes": [0x13], "keys": ["LT"]}
    trigger = state["triggers"]["left"]
    assert (trigger["deadzoneEnabled"], trigger["deadzone"], trigger["outer"], trigger["quickTrigger"]) == (True, 0, 100, False)
    stick = state["sticks"]["left"]
    assert stick["deadzoneEnabled"] and stick["deadzone"] == 5 and stick["outerEnabled"] and stick["outer"] == 100
    assert state["sticks"]["right"] == stick and state["sticks"]["swap"] is False
    light = state["light"]
    assert light["effect"] == "off" and light["frames"] == 1 and light["brightness"] == 100
    assert light["color"] == [0, 0, 0] and light["speedLevel"] is None and light["animation"] == 0
    assert state["extras"] == {"dpadDiagonalLock": False, "abxySwap": False, "standby": 10}
    json.dumps(state)

    memory = claw_8_ex_memory()
    memory[0x24A] = 1
    light = fw.GamepadFirmware(FakePad(memory)).read_state()["light"]
    assert light["animation"] == 1 and light["frames"] == 7 and light["effect"] == "custom"
    assert fw.GamepadFirmware(FakePad(claw_8_ex_memory(), stale=True)).read_state()["sticks"]["left"]["deadzone"] == 5
    state = fw.GamepadFirmware(FakePad(claw_8_ex_memory(), mode=(0x04, 0x01))).read_state()
    assert state["mode"] == "desktop" and state["mkeysFunction"] == "uncertain" and state["mkeysFunctionRaw"] == 1

    old = fw.GamepadState(fw.GamepadFirmware(FakePad(claw_8_ex_memory(), version=0x0216))).read()
    assert old["available"] is False and "0216" in old["error"]
    silent = FakePad(claw_8_ex_memory())
    silent.send = lambda frame: None
    clock = iter(range(0, 1000)).__next__
    assert fw.GamepadState(fw.GamepadFirmware(silent, clock=clock)).read()["error"] == "手柄没有回应"


def light_effects():
    frames = fw.light_frames("ripple")
    assert len(frames) == 4 and frames[0][:4] == [[0, 0, 255], [0, 255, 0], [255, 255, 0], [255, 0, 0]]
    # Each step moves every colour one zone round both rings.
    assert frames[1][0] == frames[0][1] and frames[1][4] == frames[0][5] and fw.light_frames("ripple", direction="ccw")[1] == frames[3]
    assert [fw.hex_color(frame[0]) for frame in fw.light_frames("rainbow")] == list(fw.RAINBOW)
    assert fw.light_frames("breathe", [[1, 2, 3]]) == [[[1, 2, 3]] * 9, [[0, 0, 0]] * 9]
    data = fw.light_bytes(fw.light_frames("monocolor", [[9, 8, 7]]), 3, 50)
    assert data[:5] == [(0x24A, 255, 0), (0x24B, 255, 1), (0x24C, 255, 9), (0x24D, 255, 3), (0x24E, 255, 50)]
    assert len(data) == 5 + 27


async def control_flow():
    with tempfile.TemporaryDirectory() as folder:
        original = claw_8_ex_memory()
        pad = FakePad(claw_8_ex_memory())
        control = fw.GamepadControl(folder, fw.GamepadFirmware(pad))
        control.SYNC_DELAY = 0

        async def settle():
            if control._sync_task:
                await control._sync_task

        # Sticks: only the dead zone and outer bytes change; RAM first, ROM after the delay.
        await control.set_stick_deadzone("left", 12, 90)
        assert pad.memory[0x22C] == 12 and pad.memory[0x234] == 90 and pad.rom[0x22C] == 5
        await settle()
        assert pad.rom[0x22C] == 12
        changed = [i for i in range(PROFILE_LENGTH) if pad.memory[i] != original[i]]
        assert changed == [0x22C, 0x234], [hex(i) for i in changed]
        saved = json.loads((Path(folder) / "gamepad.json").read_text(encoding="utf-8"))
        assert saved["original"][str(0x22C)] == 5 and saved["original"][str(0x234)] == 100
        for bad in ((51, 90), (10, 74), (10, 101), (10.0, 90)):
            try:
                await control.set_stick_deadzone("left", *bad)
            except ValueError:
                continue
            raise AssertionError(bad)

        await control.set_stick_swap(True)
        state = (await control.read())
        assert state["sticks"]["swap"] and state["sticks"]["left"]["deadzone"] == 12
        await control.set_trigger_range("right", 10, 85)
        assert pad.memory[0x21C] == 0x02 and pad.memory[0x21E:0x220] == bytes((10, 85))
        for bad in ((50, 50), (60, 40), (-1, 50), (0, 101)):
            try:
                await control.set_trigger_range("left", *bad)
            except ValueError:
                continue
            raise AssertionError(bad)
        await control.set_quick_trigger("left", True)
        assert pad.memory[0x35] == 1

        # Buttons: LT remapped keeps the dead-zone bit of the shared flags byte.
        await control.set_key_map("LT", [0x14])
        assert pad.memory[0x20D] == 0x06 and pad.memory[0x211] == 0x14
        await control.set_trigger_range("left", 20, 80)
        assert pad.memory[0x20D] == 0x06 and pad.memory[0x20F:0x211] == bytes((20, 80))
        await control.set_key_map("LT", [0x13])
        assert pad.memory[0x20D] == 0x02 and pad.memory[0x211] == 0x13 and pad.memory[0x20F] == 20
        await control.set_key_map("A", [0x0A])
        assert pad.memory[0x7A:0x80] == bytes((1, 0x04, 0x0C, 0x0A, 0, 0))
        try:
            await control.set_key_map("A", [0x00])
        except ValueError:
            pass
        else:
            raise AssertionError("code 0 accepted")
        try:
            await control.set_key_map("A", [0xFF, 0x09])
        except ValueError:
            pass
        else:
            raise AssertionError("disabled mixed with keys")

        # M1 / M2 are kept: a ClawTweaks rewrite is put back by the next check.
        await control.set_back_key("m1", [0x9D])
        assert pad.memory[0xBA:0xC2] == bytes((1, 0x04, 0x0C, 0x9D, 0, 0, 0, 0))
        pad.memory[0xBD:0xBF] = bytes((0x76, 0x3E))
        assert (await control.enforce("recheck"))["changed"] == 2 and pad.memory[0xBD:0xBF] == bytes((0x9D, 0))

        # Lights: every effect reads back as itself.
        for effect, color, speed in (("monocolor", "#00ff80", 1), ("breathe", "#ff0000", 2),
                                     ("rainbow", None, 0), ("ripple", None, 1), ("modeinfo", None, 1), ("off", None, 1)):
            await control.set_light(effect, color, 60, speed)
            light = (await control.read())["light"]
            assert light["effect"] == effect and light["brightness"] == 60 and light["managed"], light
            if effect in fw.LIGHT_SPEEDS:
                assert light["speedLevel"] == speed
        assert light["color"] == [0, 0, 0]
        await control.set_light("modeinfo", None, 100, 1)
        assert (await control.read())["light"]["color"] == [0, 255, 0]
        await control.set_light("custom", None, 100, 0, "cw", ["#010203", ["#ffffff"] * 9])
        light = (await control.read())["light"]
        assert light["effect"] == "custom" and light["frames"] == 2 and light["keyframes"][0][0] == "#010203"
        for bad in (("glow", None), ("monocolor", "red"), ("breathe", None)):
            try:
                await control.set_light(*bad)
            except ValueError:
                continue
            raise AssertionError(bad)

        # Desktop mappings only reach the pad in desktop mode; the mode light follows the mode.
        await control.set_light("modeinfo", None, 100, 1)
        await control.set_desktop_key_map("A", [0x67])
        assert pad.memory[0x7D] == 0x0A
        await control.set_mode("desktop")
        assert pad.mode[0] == 4 and pad.memory[0x7D] == 0x67 and pad.memory[0x24F:0x252] == bytes((0, 0, 255))
        await control.set_mode("xinput")
        assert pad.mode[0] == 1 and pad.memory[0x7D] == 0x0A and pad.memory[0x24F:0x252] == bytes((0, 255, 0))
        assert (await control.read())["desktopKeys"] == {"A": [0x67]}

        # Everything back: the pad memory equals what it was before the first write.
        await control.restore("light")
        assert pad.memory[0x24A:0x24A + 32] == original[0x24A:0x24A + 32]
        await control.restore("all")
        await settle()
        changed = [hex(i) for i in range(PROFILE_LENGTH) if pad.memory[i] != original[i]]
        assert changed == [], changed
        assert pad.rom == bytes(original) and (await control.read())["managed"] == []
        assert json.loads((Path(folder) / "gamepad.json").read_text(encoding="utf-8"))["original"] == {}

        # A restarted control finds its backups and settings again.
        await control.set_back_key("m2", [0xFF])
        again = fw.GamepadControl(folder, fw.GamepadFirmware(pad))
        assert again.managed() == ["m2"]
        await again.restore("backkeys")
        assert bytes(pad.memory) == bytes(original)
        await control.close()


def main():
    reading()
    light_effects()
    asyncio.run(control_flow())
    if "--live" in sys.argv:
        print(json.dumps(fw.GamepadState().read(), ensure_ascii=False, indent=1))
    print("gamepad firmware checks passed")


main()
