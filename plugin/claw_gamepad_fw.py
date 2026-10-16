"""MSI Claw pad firmware settings, read only: mode, M1/M2, stick and trigger dead zones, lights.

Same channel as claw_rumble.py (MI_01 vendor collection, 64-byte reports). Only two commands
are ever sent: 0x26 read gamepad mode (reply 0x27) and 0x04 read profile memory (reply 0x05,
at most 55 bytes per reply). Nothing is written and nothing is synced to ROM.

The profile memory layout comes from MSI Center M (API_ControlMode.dll, ProfileInfo.Parse) and
agrees with Linux hid-msi (rumble 0x22, M1 0xBB, M2 0x164, lights 0x24A) and with the addresses
ClawTweaks writes (stick dead zones 0x22C / 0x23C). See docs/gamepad-firmware-protocol.md.
"""
import importlib.util
from pathlib import Path
import threading
import time

_rumble_spec = importlib.util.spec_from_file_location("claw_toolbox_rumble_channel",
                                                      Path(__file__).with_name("claw_rumble.py"))
_rumble = importlib.util.module_from_spec(_rumble_spec)
_rumble_spec.loader.exec_module(_rumble)

PRODUCTS = {0x1901: "xinput", 0x1902: "dinput", 0x1903: "desktop"}
MODES = {1: "xinput", 2: "dinput", 3: "msi", 4: "desktop", 5: "bios", 6: "testing"}
# 0 is "macro" (M keys act on their own mapping) in both MSI Center M and hid-msi; the two
# disagree on 1 and 2 (MSI: 1 combination, 2 disabled; hid-msi v5+: 1 disabled, 2 combination).
MKEYS_FUNCTIONS = {0: "macro"}

_READ, _READ_ACK, _READ_MODE, _MODE_ACK = 0x04, 0x05, 0x26, 0x27
_CHUNK = 55
_PROFILE = 0x01

# Profile memory offsets (firmware 1.66+ / 2.17+ / 3+, i.e. the layout claw_rumble supports).
EXTEND = 0x35        # quick L/R trigger, d-pad diagonal lock, ABXY swap, standby
M1, M2 = 0xBA, 0x163  # FunctionKeyProfile: KeyProfile (8) + macro (1 + 32 x 5)
L2, R2 = 0x20C, 0x21B  # TriggerKeyProfile (15)
LEFT_STICK, RIGHT_STICK = 0x22A, 0x23A  # StickKeyProfile (16)
LIGHT = 0x24A        # active animation index, then 4 animations of 4 + 8 x 9 x RGB
ANIMATION_LENGTH = 4 + 8 * 27
LIGHT_ZONES = 9

KEY_NAMES = {
    0x00: None, 0xFF: "DISABLED",
    0x01: "DPAD_UP", 0x02: "DPAD_DOWN", 0x03: "DPAD_LEFT", 0x04: "DPAD_RIGHT",
    0x05: "LB", 0x06: "RB", 0x07: "LS", 0x08: "RS", 0x09: "A", 0x0A: "B", 0x0B: "X", 0x0C: "Y",
    0x0D: "GUIDE", 0x0E: "VIEW", 0x0F: "MENU", 0x10: "CAMERA", 0x11: "FL1", 0x12: "FR1",
    0x13: "LT", 0x14: "RT",
    0x15: "LSTICK_UP", 0x16: "LSTICK_DOWN", 0x17: "LSTICK_LEFT", 0x18: "LSTICK_RIGHT",
    0x19: "RSTICK_UP", 0x1A: "RSTICK_DOWN", 0x1B: "RSTICK_LEFT", 0x1C: "RSTICK_RIGHT",
    0x1D: "DPAD_UP_LEFT", 0x1E: "DPAD_UP_RIGHT", 0x1F: "DPAD_DOWN_LEFT", 0x20: "DPAD_DOWN_RIGHT",
    0x21: "LSTICK_UP_LEFT", 0x22: "LSTICK_UP_RIGHT", 0x23: "LSTICK_DOWN_LEFT", 0x24: "LSTICK_DOWN_RIGHT",
    0x25: "RSTICK_UP_LEFT", 0x26: "RSTICK_UP_RIGHT", 0x27: "RSTICK_DOWN_LEFT", 0x28: "RSTICK_DOWN_RIGHT",
    0x32: "KEY_ESC", 0x3F: "KEY_GRAVE", 0x4A: "KEY_MINUS", 0x4B: "KEY_EQUAL", 0x4C: "KEY_BACKSPACE",
    0x4D: "KEY_TAB", 0x58: "KEY_LEFTBRACE", 0x59: "KEY_RIGHTBRACE", 0x5A: "KEY_BACKSLASH",
    0x5B: "KEY_CAPSLOCK", 0x65: "KEY_SEMICOLON", 0x66: "KEY_APOSTROPHE", 0x67: "KEY_ENTER",
    0x68: "KEY_LEFTSHIFT", 0x70: "KEY_COMMA", 0x71: "KEY_DOT", 0x72: "KEY_SLASH", 0x73: "KEY_RIGHTSHIFT",
    0x74: "KEY_LEFTCTRL", 0x75: "KEY_LEFTMETA", 0x76: "KEY_LEFTALT", 0x77: "KEY_SPACE",
    0x78: "KEY_RIGHTALT", 0x79: "KEY_RIGHTCTRL", 0x7A: "KEY_INSERT", 0x7B: "KEY_HOME",
    0x7C: "KEY_PAGEUP", 0x7D: "KEY_DELETE", 0x7E: "KEY_END", 0x7F: "KEY_PAGEDOWN",
    0x80: "KEY_UP", 0x81: "KEY_DOWN", 0x82: "KEY_LEFT", 0x83: "KEY_RIGHT", 0x84: "KEY_NUMLOCK",
    0x85: "KEY_KPSLASH", 0x86: "KEY_KPASTERISK", 0x87: "KEY_KPMINUS", 0x88: "KEY_KPPLUS",
    0x89: "KEY_KPDOT", 0x8A: "KEY_KPENTER",
    0x95: "MEDIA_PLAY", 0x96: "MEDIA_STOP", 0x97: "MEDIA_NEXT", 0x98: "MEDIA_PREV",
    0x99: "MEDIA_VOLUME_UP", 0x9A: "MEDIA_VOLUME_DOWN", 0x9B: "MEDIA_MUTE", 0x9C: "KEY_F23", 0x9D: "KEY_F22",
    0xC8: "MOUSE_LEFT", 0xC9: "MOUSE_MIDDLE", 0xCA: "MOUSE_RIGHT", 0xCB: "MOUSE_BACK",
    0xCC: "MOUSE_FORWARD", 0xCD: "MOUSE_WHEEL_UP", 0xCE: "MOUSE_WHEEL_DOWN",
}
KEY_NAMES.update({0x33 + i: "KEY_F%d" % (i + 1) for i in range(12)})
KEY_NAMES.update({0x40 + i: "KEY_%d" % ((i + 1) % 10) for i in range(10)})
KEY_NAMES.update({0x8B + i: "KEY_KP%d" % i for i in range(10)})
for start, letters in ((0x4E, "QWERTYUIOP"), (0x5C, "ASDFGHJKL"), (0x69, "ZXCVBNM")):
    KEY_NAMES.update({start + i: "KEY_" + letter for i, letter in enumerate(letters)})


def key_name(code):
    return KEY_NAMES.get(code, "0x%02x" % code)


def read_request(address, length):
    return _rumble.packet(_READ, bytes((_PROFILE, address >> 8, address & 0xFF, length)))


def parse_read_reply(frame):
    """(address, data) for a 0x05 profile reply, else None."""
    if len(frame) < 9 or frame[0] != 0x10 or frame[3] != 0x3C or frame[4] != _READ_ACK:
        return None
    length = frame[8]
    if 9 + length > len(frame):
        return None
    return (frame[6] << 8) | frame[7], bytes(frame[9:9 + length])


def parse_key(data):
    """KeyProfile (8 bytes): index, flags, turbo speed, 5 mapped codes."""
    flags = data[1]
    codes = [code for code in data[3:8] if code not in (0x00, 0xFF)]
    return {"index": data[0], "flags": flags, "turbo": bool(flags & 1), "mapped": bool(flags & 4),
            "turboSpeed": data[2], "codes": codes, "keys": [key_name(code) for code in codes]}


def parse_trigger(data, quick):
    """TriggerKeyProfile (15 bytes): index, flags, turbo, dead range, edge dead range, 5 codes, linear (5)."""
    flags = data[1]
    return {"deadzoneEnabled": bool(flags & 2), "deadzone": data[3], "outer": data[4],
            "codes": [code for code in data[5:10] if code not in (0x00, 0xFF)],
            "quickTrigger": quick, "flags": flags}


def parse_stick(data):
    """StickKeyProfile (16 bytes), MSI Center M StickKeyProfile.Parse order."""
    return {"enabled": data[0] == 1, "deadzoneEnabled": data[1] == 1, "deadzone": data[2],
            "mapEnabled": data[3] == 1, "flipX": data[4] == 1, "flipY": data[5] == 1, "mapValue": data[6],
            "outerEnabled": data[9] == 1, "outer": data[10]}


def parse_animation(data):
    """AnimationInfo: keyframe count, effect, speed, brightness, 8 keyframes of 9 RGB zones."""
    count = data[0]
    frames = [[list(data[4 + f * 27 + z * 3:4 + f * 27 + z * 3 + 3]) for z in range(LIGHT_ZONES)]
              for f in range(max(0, min(count, 8)))]
    uniform = bool(frames) and all(zone == frames[0][0] for zone in frames[0])
    dark = lambda frame: all(zone == [0, 0, 0] for zone in frame)
    if not 1 <= count <= 8:
        effect = "unknown"
    elif count == 1 and dark(frames[0]):
        effect = "off"
    elif count == 1 and uniform:
        effect = "monocolor"
    elif count == 2 and uniform and dark(frames[1]):
        effect = "breathe"
    elif count == 1:
        effect = "custom"
    else:
        effect = "animated"
    return {"effect": effect, "frames": count, "effectNum": data[1],
            # Firmware speed 0 is fastest; hid-msi shows 20 - raw (0..20).
            "speedRaw": data[2], "speed": max(0, 20 - data[2]), "brightness": data[3],
            "color": frames[0][0] if frames else None, "uniform": uniform, "keyframes": frames}


class GamepadFirmware:
    """Reads the pad's current firmware settings. Never writes."""

    def __init__(self, channel_factory=None, clock=time.monotonic):
        self._factory = channel_factory or (lambda: _rumble.HidChannel(tuple(PRODUCTS)))
        self._clock = clock
        self._lock = threading.Lock()

    def _await(self, channel, accept, timeout=0.3):
        deadline = self._clock() + timeout
        while (left := deadline - self._clock()) > 0:
            frame = channel.receive(max(1, int(left * 1000)))
            if frame and (value := accept(frame)) is not None:
                return value
        raise TimeoutError("手柄没有回应")

    def _read(self, channel, address, length):
        data = b""
        while len(data) < length:
            part, at = min(_CHUNK, length - len(data)), address + len(data)
            channel.send(read_request(at, part))
            # Replies for other addresses (stale, or another reader on the pad) are skipped.
            reply = self._await(channel, lambda frame: (parsed := parse_read_reply(frame))
                                and parsed[0] == at and len(parsed[1]) == part and parsed[1] or None)
            data += reply
        return data

    def _read_mode(self, channel):
        channel.send(_rumble.packet(_READ_MODE))
        return self._await(channel, lambda frame: (frame[5], frame[6]) if len(frame) >= 7 and frame[0] == 0x10
                           and frame[3] == 0x3C and frame[4] == _MODE_ACK else None)

    def read_state(self):
        with self._lock:
            channel = self._factory().open()
            try:
                if not _rumble.supported_firmware(channel.version):
                    raise OSError("手柄固件版本 %04x 太旧，不支持读取配置" % channel.version)
                mode, mkeys = self._read_mode(channel)
                head = self._read(channel, 0x22, EXTEND + 5 - 0x22)
                m1, m2 = self._read(channel, M1, 9), self._read(channel, M2, 9)
                controls = self._read(channel, L2, LIGHT - L2)
                light = self._read(channel, LIGHT, 1 + ANIMATION_LENGTH)
                active = light[0]
                if 0 < active < 4:
                    animation = self._read(channel, LIGHT + 1 + active * ANIMATION_LENGTH, ANIMATION_LENGTH)
                else:
                    animation = light[1:]
            finally:
                channel.close()
        extend = head[EXTEND - 0x22:]
        product = getattr(channel, "product", None)
        return {
            "available": True,
            "firmware": "%04x" % channel.version,
            "product": "%04x" % product if product else None,
            "mode": MODES.get(mode, "unknown"), "modeRaw": mode,
            "mkeysFunction": MKEYS_FUNCTIONS.get(mkeys, "uncertain"), "mkeysFunctionRaw": mkeys,
            "rumble": {"left": head[0], "right": head[1]},
            "backKeys": {"m1": dict(parse_key(m1[:8]), macroSteps=m1[8]),
                         "m2": dict(parse_key(m2[:8]), macroSteps=m2[8])},
            "triggers": {"left": parse_trigger(controls[0:15], extend[0] == 1),
                         "right": parse_trigger(controls[15:30], extend[1] == 1)},
            "sticks": {"left": parse_stick(controls[LEFT_STICK - L2:LEFT_STICK - L2 + 16]),
                       "right": parse_stick(controls[RIGHT_STICK - L2:RIGHT_STICK - L2 + 16])},
            "light": dict(parse_animation(animation), animation=active),
            "extras": {"dpadDiagonalLock": extend[2] == 1, "abxySwap": extend[3] == 1, "standby": extend[4]},
        }


class GamepadState:
    """Service-side owner: one read per call, errors reported instead of raised."""

    def __init__(self, firmware=None):
        self._firmware = firmware or GamepadFirmware()

    def read(self):
        try:
            return self._firmware.read_state()
        except Exception as error:
            return {"available": False, "error": str(error)}
