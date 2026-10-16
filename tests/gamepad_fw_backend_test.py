"""Checks the read-only pad firmware reader against a fake pad. With --live, also reads the real pad (never writes)."""
import importlib.util
import json
from pathlib import Path
import sys

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
    """Answers 0x26 and 0x04 like the MCU; records every command it gets."""
    def __init__(self, memory, version=0x0419, mode=(0x01, 0x00), stale=False):
        self.memory, self.version, self.mode, self.stale = memory, version, mode, stale
        self.product, self.commands, self.queue, self.opened = 0x1901, [], [], 0

    def __call__(self):
        return self

    def open(self):
        self.opened += 1
        return self

    def close(self):
        pass

    def send(self, frame):
        assert len(frame) == 64 and frame[:4] == bytes((0x0F, 0, 0, 0x3C))
        self.commands.append(frame[4])
        reply = bytearray(64); reply[:4] = bytes((0x10, 0, 0, 0x3C))
        if frame[4] == 0x26:
            reply[4:7] = bytes((0x27,) + self.mode)
        elif frame[4] == 0x04:
            address, length = (frame[6] << 8) | frame[7], frame[8]
            assert frame[5] == 0x01 and 1 <= length <= 55
            if self.stale:
                # A late reply for another address arrives first and must be skipped.
                junk = bytearray(reply); junk[4:9] = bytes((0x05, 0x01, 0x00, 0x22, 0x01)); junk[9] = 0x55
                self.queue.append(bytes(junk))
            reply[4:9] = bytes((0x05, 0x01, frame[6], frame[7], length))
            reply[9:9 + length] = self.memory[address:address + length]
        else:
            raise AssertionError("unexpected command 0x%02x" % frame[4])
        self.queue.append(bytes(reply))

    def receive(self, timeout_ms):
        return self.queue.pop(0) if self.queue else None


def main():
    assert fw.read_request(0x24A, 55)[:9] == bytes((0x0F, 0, 0, 0x3C, 0x04, 0x01, 0x02, 0x4A, 55))
    assert fw.key_name(0x76) == "KEY_LEFTALT" and fw.key_name(0x3E) == "KEY_F12" and fw.key_name(0x40) == "KEY_1"
    assert fw.key_name(0x49) == "KEY_0" and fw.key_name(0x5C) == "KEY_A" and fw.key_name(0x94) == "KEY_KP9"

    pad = FakePad(claw_8_ex_memory())
    state = fw.GamepadFirmware(pad).read_state()
    assert set(pad.commands) == {0x26, 0x04}, pad.commands  # read only: no 0x21 write, 0x22 sync, 0x24 mode switch
    assert state["available"] and state["firmware"] == "0419" and state["product"] == "1901"
    assert state["mode"] == "xinput" and state["mkeysFunction"] == "macro"
    assert state["rumble"] == {"left": 100, "right": 100}
    m1, m2 = state["backKeys"]["m1"], state["backKeys"]["m2"]
    assert m1["mapped"] and m1["keys"] == ["KEY_LEFTALT", "KEY_F12"] and m1["macroSteps"] == 0
    assert m2["keys"] == ["FR1"]
    left_trigger = state["triggers"]["left"]
    assert left_trigger == {"deadzoneEnabled": True, "deadzone": 0, "outer": 100, "codes": [0x13],
                            "quickTrigger": False, "flags": 2}
    assert state["triggers"]["right"]["codes"] == [0x14]
    stick = state["sticks"]["left"]
    assert stick["deadzoneEnabled"] and stick["deadzone"] == 5 and stick["outerEnabled"] and stick["outer"] == 100
    assert state["sticks"]["right"] == stick
    light = state["light"]
    assert light["effect"] == "off" and light["frames"] == 1 and light["brightness"] == 100
    assert light["speed"] == 17 and light["animation"] == 0
    assert state["extras"] == {"dpadDiagonalLock": False, "abxySwap": False, "standby": 10}
    json.dumps(state)

    # Lights: a coloured single frame is monocolor; colour then black is breathe; another active slot is read.
    memory = claw_8_ex_memory()
    memory[0x24F:0x24F + 27] = bytes((255, 255, 255) * 9)
    assert fw.GamepadFirmware(FakePad(memory)).read_state()["light"]["effect"] == "monocolor"
    memory[0x24B] = 2
    assert fw.GamepadFirmware(FakePad(memory)).read_state()["light"]["effect"] == "breathe"
    memory[0x24A] = 1
    light = fw.GamepadFirmware(FakePad(memory)).read_state()["light"]
    assert light["animation"] == 1 and light["frames"] == 7 and light["effect"] == "animated" and light["speed"] == 10

    # Stale replies for other addresses are skipped.
    assert fw.GamepadFirmware(FakePad(claw_8_ex_memory(), stale=True)).read_state()["sticks"]["left"]["deadzone"] == 5
    # The disputed M-key function values are reported raw, not guessed.
    state = fw.GamepadFirmware(FakePad(claw_8_ex_memory(), mode=(0x04, 0x01))).read_state()
    assert state["mode"] == "desktop" and state["mkeysFunction"] == "uncertain" and state["mkeysFunctionRaw"] == 1

    # Old firmware and errors come back as a state, not an exception.
    old = fw.GamepadState(fw.GamepadFirmware(FakePad(claw_8_ex_memory(), version=0x0216))).read()
    assert old["available"] is False and "0216" in old["error"]
    silent = FakePad(claw_8_ex_memory())
    silent.send = lambda frame: None
    clock = iter(range(0, 1000)).__next__
    assert fw.GamepadState(fw.GamepadFirmware(silent, clock=clock)).read()["error"] == "手柄没有回应"

    if "--live" in sys.argv:
        print(json.dumps(fw.GamepadState().read(), ensure_ascii=False, indent=1))
    print("gamepad firmware reader checks passed")


main()
