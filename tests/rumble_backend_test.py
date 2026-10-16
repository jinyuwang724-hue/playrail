"""Checks the Claw firmware rumble protocol against a fake pad. With --live, also reads (never writes) the real pad."""
import asyncio
import importlib.util
from pathlib import Path
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1] / "plugin"
spec = importlib.util.spec_from_file_location("claw_rumble", ROOT / "claw_rumble.py")
rumble = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rumble)


class FakePad:
    """Speaks the hid-msi profile protocol; counts ROM syncs."""
    def __init__(self, version=0x0419, values=(100, 100)):
        self.version, self.rom, self.syncs, self.sent, self.queue = version, {0x22: values[0], 0x23: values[1]}, 0, [], []

    def __call__(self):
        return self

    def open(self):
        return self

    def close(self):
        pass

    def send(self, frame):
        assert len(frame) == 64 and frame[:4] == bytes((0x0F, 0, 0, 0x3C))
        self.sent.append(frame)
        cmd, address = frame[4], (frame[6] << 8) | frame[7]
        reply = bytearray(64); reply[:4] = bytes((0x10, 0, 0, 0x3C))
        if cmd == 0x04:
            reply[4:10] = bytes((0x05, 0x01, frame[6], frame[7], 0x01, self.rom[address]))
        elif cmd == 0x21:
            self.rom[address] = frame[9]; reply[4] = 0x06
        elif cmd == 0x22:
            self.syncs += 1; reply[4] = 0x06
        self.queue.append(bytes(reply))

    def receive(self, timeout_ms):
        return self.queue.pop(0) if self.queue else None


def main():
    assert rumble.supported_firmware(0x0419) and rumble.supported_firmware(0x0217) and rumble.supported_firmware(0x0166)
    assert not rumble.supported_firmware(0x0216) and not rumble.supported_firmware(0x0165)
    assert rumble.packet(0x21, rumble.rumble_field(0x23, 60))[:10] == bytes((0x0F, 0, 0, 0x3C, 0x21, 1, 0, 0x23, 1, 60))

    pad = FakePad()
    control = rumble.ClawRumble(pad)
    assert control.read() == [100, 100]
    assert control.write(60, 60) == {"before": [100, 100], "after": [60, 60], "written": True}
    assert pad.syncs == 2
    # Unchanged values never touch the ROM.
    assert control.write(60, 60)["written"] is False and pad.syncs == 2
    for bad in (-1, 101, 50.0, None):
        try:
            control.write(bad, 50)
        except ValueError:
            continue
        raise AssertionError(bad)
    try:
        rumble.ClawRumble(FakePad(version=0x0216)).read()
    except OSError:
        pass
    else:
        raise AssertionError("old firmware accepted")

    async def plugin_flow():
        with tempfile.TemporaryDirectory() as folder:
            pad = FakePad(values=(90, 80))
            owner = rumble.RumbleControl(folder, rumble.ClawRumble(pad))
            view = await owner.set_strength(40)
            assert view["current"] == [40, 40] and view["original"] == [90, 80] and view["managed"]
            # ClawTweaks resets its ceiling on start; the next check puts ours back.
            pad.rom.update({0x22: 100, 0x23: 100})
            assert (await owner.enforce("startup"))["current"] == [40, 40]
            again = rumble.RumbleControl(folder, rumble.ClawRumble(pad))
            assert again.view()["strength"] == 40 and again.view()["original"] == [90, 80]
            restored = await again.restore()
            assert restored["current"] == [90, 80] and not restored["managed"]
            # Left and right apart; one argument still sets both.
            view = await again.set_strength(30, 60)
            assert view["current"] == [30, 60] and (view["left"], view["right"], view["strength"]) == (30, 60, 45)
            assert rumble.RumbleControl(folder, rumble.ClawRumble(pad)).view()["left"] == 30
            assert (await again.set_strength(70))["current"] == [70, 70]
            for bad in ((30, 101), (30, -1), (30, 5.0)):
                try:
                    await again.set_strength(*bad)
                except ValueError:
                    continue
                raise AssertionError(bad)
            assert (await again.restore())["current"] == [90, 80]
    asyncio.run(plugin_flow())

    if "--live" in sys.argv:
        print("live pad:", rumble.ClawRumble().read())
    print("rumble backend checks passed")


main()
