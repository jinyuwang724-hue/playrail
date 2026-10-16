"""MSI Claw pad firmware settings: mode, M1/M2 and button mapping, stick and trigger dead zones, lights.

Same channel as claw_rumble.py (MI_01 vendor collection, 64-byte reports):
  0x04 read profile memory -> 0x05 (at most 55 bytes per reply)
  0x21 write profile memory (RAM, takes effect at once) -> ack 0x06
  0x22 sync RAM to ROM (acked only after the USB transfer)
  0x26 read gamepad mode -> 0x27;  0x24 switch mode (no ack, the pad re-enumerates)

The profile memory layout comes from MSI Center M (API_ControlMode.dll, ProfileInfo.Parse) and
agrees with Linux hid-msi (rumble 0x22, M1 0xBB, M2 0x164, lights 0x24A) and with the addresses
ClawTweaks writes (stick dead zones 0x22C / 0x23C). See docs/gamepad-firmware-protocol.md.

Every setting is a list of (address, mask, value) bytes. The pad's own value of each byte is kept
in data/gamepad.json before the first write, so every setting can be put back.
"""
import asyncio
import importlib.util
import json
import os
from pathlib import Path
import threading
import time

_rumble_spec = importlib.util.spec_from_file_location("claw_toolbox_rumble_channel",
                                                      Path(__file__).with_name("claw_rumble.py"))
_rumble_default = importlib.util.module_from_spec(_rumble_spec)
_rumble_spec.loader.exec_module(_rumble_default)

PRODUCTS = {0x1901: "xinput", 0x1902: "dinput", 0x1903: "desktop"}
MODES = {1: "xinput", 2: "dinput", 3: "msi", 4: "desktop", 5: "bios", 6: "testing"}
# 0 is "macro" (M keys act on their own mapping) in both MSI Center M and hid-msi; the two
# disagree on 1 and 2 (MSI: 1 combination, 2 disabled; hid-msi v5+: 1 disabled, 2 combination).
MKEYS_FUNCTIONS = {0: "macro"}

_READ, _READ_ACK, _ACK, _WRITE, _SYNC = 0x04, 0x05, 0x06, 0x21, 0x22
_SWITCH, _READ_MODE, _MODE_ACK = 0x24, 0x26, 0x27
_CHUNK = 55
_PROFILE = 0x01

# Profile memory offsets (firmware 1.66+ / 2.17+ / 3+, i.e. the layout claw_rumble supports).
EXTEND = 0x35        # quick L/R trigger, d-pad diagonal lock, ABXY swap, standby
KEYS = 0x3A          # 16 KeyProfiles of 8 bytes: index, flags, turbo speed, 5 codes
M1, M2 = 0xBA, 0x163  # FunctionKeyProfile: KeyProfile (8) + macro (1 + 32 x 5)
L2, R2 = 0x20C, 0x21B  # TriggerKeyProfile (15): index, flags, turbo, dead, edge, 5 codes, linear (5)
LEFT_STICK, RIGHT_STICK = 0x22A, 0x23A  # StickKeyProfile (16)
LIGHT = 0x24A        # active animation index, then 4 animations of 4 + 8 x 9 x RGB
ANIMATION_LENGTH = 4 + 8 * 27
LIGHT_ZONES = 9

# Source keys of the button mapping page: KeyProfile slot, or trigger block for LT / RT.
KEY_SLOTS = {"Up": 0, "Down": 1, "Left": 2, "Right": 3, "LB": 4, "RB": 5, "LS": 6, "RS": 7,
             "A": 8, "B": 9, "X": 10, "Y": 11}
TRIGGER_KEYS = {"LT": L2, "RT": R2}
OWN_CODES = {"Up": 0x01, "Down": 0x02, "Left": 0x03, "Right": 0x04, "LB": 0x05, "RB": 0x06, "LS": 0x07,
             "RS": 0x08, "A": 0x09, "B": 0x0A, "X": 0x0B, "Y": 0x0C, "LT": 0x13, "RT": 0x14}

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

# Lights. Speeds are MSI Center M's slow / medium / fast per effect (firmware byte = 20 - MSI speed).
LIGHT_EFFECTS = ("off", "monocolor", "breathe", "rainbow", "ripple", "custom", "modeinfo")
LIGHT_SPEEDS = {"breathe": (9, 6, 3), "rainbow": (7, 5, 3), "ripple": (6, 3, 0), "custom": (9, 6, 3)}
LIGHT_FIXED_SPEED = {"off": 5, "monocolor": 3, "modeinfo": 3}
RAINBOW = ("#ff0000", "#ffff00", "#00ff00", "#00ffff", "#0000ff", "#ff00ff")
RIPPLE = ("#ff0000", "#ffff00", "#00ff00", "#0000ff")
MODE_COLORS = {"gamepad": "#00ff00", "desktop": "#0000ff", "msi": "#ff0000"}


def key_name(code):
    return KEY_NAMES.get(code, "0x%02x" % code)


def read_request(rumble, address, length):
    return rumble.packet(_READ, bytes((_PROFILE, address >> 8, address & 0xFF, length)))


def write_request(rumble, address, data):
    return rumble.packet(_WRITE, bytes((_PROFILE, address >> 8, address & 0xFF, len(data))) + bytes(data))


def parse_read_reply(frame):
    """(address, data) for a 0x05 profile reply, else None."""
    if len(frame) < 9 or frame[0] != 0x10 or frame[3] != 0x3C or frame[4] != _READ_ACK:
        return None
    length = frame[8]
    if 9 + length > len(frame):
        return None
    return (frame[6] << 8) | frame[7], bytes(frame[9:9 + length])


def is_reply(frame, command):
    return bool(frame) and len(frame) >= 5 and frame[0] == 0x10 and frame[3] == 0x3C and frame[4] == command


def rgb(text):
    if not isinstance(text, str) or len(text) != 7 or text[0] != "#":
        raise ValueError("颜色要写成 #rrggbb")
    try:
        return [int(text[1:3], 16), int(text[3:5], 16), int(text[5:7], 16)]
    except ValueError:
        raise ValueError("颜色要写成 #rrggbb") from None


def hex_color(zone):
    return "#%02x%02x%02x" % tuple(zone)


def mode_class(mode):
    return "desktop" if mode == 4 else "msi" if mode == 3 else "gamepad"


def parse_key(data):
    """KeyProfile (8 bytes): index, flags, turbo speed, 5 mapped codes."""
    flags = data[1]
    codes = [code for code in data[3:8] if code != 0x00]
    return {"index": data[0], "flags": flags, "turbo": bool(flags & 1), "mapped": bool(flags & 4),
            "turboSpeed": data[2], "codes": codes, "keys": [key_name(code) for code in codes]}


def parse_trigger(data, quick):
    flags = data[1]
    codes = [code for code in data[5:10] if code != 0x00]
    return {"deadzoneEnabled": bool(flags & 2), "deadzone": data[3], "outer": data[4],
            "mapped": bool(flags & 4), "codes": codes, "keys": [key_name(code) for code in codes],
            "quickTrigger": quick, "flags": flags}


def parse_stick(data):
    """StickKeyProfile (16 bytes), MSI Center M StickKeyProfile.Parse order."""
    return {"enabled": data[0] == 1, "deadzoneEnabled": data[1] == 1, "deadzone": data[2],
            "mapEnabled": data[3] == 1, "flipX": data[4] == 1, "flipY": data[5] == 1, "mapValue": data[6],
            "outerEnabled": data[9] == 1, "outer": data[10]}


def light_frames(effect, colors=None, direction="cw", mode="gamepad", frames=None):
    """Keyframes (lists of 9 [r, g, b]) for an effect, the way MSI Center M builds them."""
    black = [0, 0, 0]
    solid = lambda color: [list(color) for _ in range(LIGHT_ZONES)]
    if effect == "off":
        return [solid(black)]
    if effect == "monocolor":
        return [solid(colors[0])]
    if effect == "modeinfo":
        return [solid(rgb(MODE_COLORS[mode]))]
    if effect == "breathe":
        return [solid(colors[0]), solid(black)]
    if effect == "rainbow":
        return [solid(rgb(color)) for color in RAINBOW]
    if effect == "ripple":
        palette = [rgb(color) for color in RIPPLE] if not colors else [list(c) for c in (colors * 4)[:4]]
        result = []
        for step in range(4):
            a = [palette[(i - step) % 4] for i in range(4)]
            # LightPresetServices.LeftStick(a1..a4) = zones 0-3 as a4 a3 a2 a1; RightStick = a2 a1 a4 a3; ABXY = a1.
            result.append([a[3], a[2], a[1], a[0], a[1], a[0], a[3], a[2], a[0]])
        if direction == "ccw":
            result = [result[0]] + result[:0:-1]
        return [[list(zone) for zone in frame] for frame in result]
    if effect == "custom":
        return [[list(zone) for zone in frame] for frame in frames]
    raise ValueError("不支持的灯光效果")


def light_bytes(frames, speed, brightness):
    """(address, mask, value) for slot 0: active slot 0, frame count, effect 9, speed, brightness, frames."""
    data = [0, len(frames), 9, speed, brightness] + [value for frame in frames for zone in frame for value in zone]
    return [(LIGHT + i, 0xFF, value) for i, value in enumerate(data)]


def parse_animation(data):
    """AnimationInfo: keyframe count, effect, speed, brightness, 8 keyframes of 9 RGB zones."""
    count = data[0]
    frames = [[list(data[4 + f * 27 + z * 3:4 + f * 27 + z * 3 + 3]) for z in range(LIGHT_ZONES)]
              for f in range(max(0, min(count, 8)))]
    uniform = lambda frame: all(zone == frame[0] for zone in frame)
    dark = lambda frame: all(zone == [0, 0, 0] for zone in frame)
    if not 1 <= count <= 8:
        effect = "unknown"
    elif count == 1 and dark(frames[0]):
        effect = "off"
    elif count == 1 and uniform(frames[0]):
        effect = "monocolor"
    elif count == 2 and uniform(frames[0]) and dark(frames[1]):
        effect = "breathe"
    elif count == 6 and [hex_color(frame[0]) for frame in frames] == list(RAINBOW) and all(map(uniform, frames)):
        effect = "rainbow"
    elif count == 4 and not uniform(frames[0]):
        effect = "ripple"
    else:
        effect = "custom"
    speeds = LIGHT_SPEEDS.get(effect)
    return {"effect": effect, "frames": count, "effectNum": data[1], "speedRaw": data[2],
            "speedLevel": speeds.index(data[2]) if speeds and data[2] in speeds else None,
            "brightness": data[3], "color": list(frames[0][0]) if frames else None,
            "direction": None, "keyframes": [[hex_color(zone) for zone in frame] for frame in frames]}


def spans(addresses, gap=0):
    """Sorted runs of addresses; runs closer than gap are merged."""
    runs = []
    for address in sorted(set(addresses)):
        if runs and address <= runs[-1][1] + gap + 1:
            runs[-1][1] = address
        else:
            runs.append([address, address])
    return [(start, end - start + 1) for start, end in runs]


class GamepadFirmware:
    """Talks to the pad: reads, writes RAM, syncs ROM, switches mode. One request at a time."""

    def __init__(self, channel_factory=None, clock=time.monotonic, rumble=None, sleep=time.sleep):
        self._rumble = rumble or _rumble_default
        self._factory = channel_factory or (lambda: self._rumble.HidChannel(tuple(PRODUCTS)))
        self._clock = clock
        self._sleep = sleep
        self._lock = getattr(self._rumble, "CHANNEL_LOCK", None) or threading.Lock()

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
            channel.send(read_request(self._rumble, at, part))
            # Replies for other addresses (stale, or another reader on the pad) are skipped.
            reply = self._await(channel, lambda frame: (parsed := parse_read_reply(frame))
                                and parsed[0] == at and len(parsed[1]) == part and parsed[1] or None)
            data += reply
        return data

    def _write(self, channel, address, data):
        for offset in range(0, len(data), _CHUNK):
            channel.send(write_request(self._rumble, address + offset, data[offset:offset + _CHUNK]))
            self._await(channel, lambda frame: True if is_reply(frame, _ACK) else None)

    def _sync(self, channel):
        channel.send(self._rumble.packet(_SYNC))
        # The MCU acks a ROM sync only after the USB transfer; drain it.
        try:
            self._await(channel, lambda frame: True if is_reply(frame, _ACK) else None, 0.15)
        except TimeoutError:
            pass

    def _read_mode(self, channel):
        channel.send(self._rumble.packet(_READ_MODE))
        return self._await(channel, lambda frame: (frame[5], frame[6]) if len(frame) >= 7
                           and is_reply(frame, _MODE_ACK) else None)

    def _open(self):
        channel = self._factory().open()
        if not self._rumble.supported_firmware(channel.version):
            channel.close()
            raise OSError("手柄固件版本 %04x 太旧，不支持读写配置" % channel.version)
        return channel

    def read_mode(self):
        with self._lock:
            channel = self._open()
            try:
                return self._read_mode(channel)
            finally:
                channel.close()

    def read_bytes(self, addresses):
        """{address: value} for the given addresses."""
        with self._lock:
            channel = self._open()
            try:
                result = {}
                for start, length in spans(addresses, gap=8):
                    data = self._read(channel, start, length)
                    result.update({start + i: value for i, value in enumerate(data)})
                return {address: result[address] for address in addresses}
            finally:
                channel.close()

    def apply(self, desired, sync=False):
        """Make the pad's bytes equal desired {address: value}; writes only what differs, reads back."""
        with self._lock:
            channel = self._open()
            try:
                current = {}
                for start, length in spans(desired, gap=8):
                    data = self._read(channel, start, length)
                    current.update({start + i: value for i, value in enumerate(data)})
                changed = [address for address in desired if current[address] != desired[address]]
                for start, length in spans(changed, gap=8):
                    self._write(channel, start, bytes(desired.get(start + i, current[start + i])
                                                      for i in range(length)))
                if changed and sync:
                    self._sync(channel)
                after = {}
                for start, length in spans(desired, gap=8):
                    data = self._read(channel, start, length)
                    after.update({start + i: value for i, value in enumerate(data)})
            finally:
                channel.close()
        wrong = [address for address in desired if after[address] != desired[address]]
        if wrong:
            raise OSError("手柄没有保存新的设置（0x%03x 等 %d 个字节）" % (wrong[0], len(wrong)))
        return {"changed": len(changed), "before": {address: current[address] for address in changed}}

    def sync(self):
        with self._lock:
            channel = self._open()
            try:
                self._sync(channel)
            finally:
                channel.close()

    def switch_mode(self, mode, mkeys):
        """Send 0x24 and wait until the pad (which may re-enumerate) reports the new mode."""
        with self._lock:
            channel = self._open()
            try:
                channel.send(self._rumble.packet(_SWITCH, bytes((mode, mkeys))))
                try:
                    self._await(channel, lambda frame: True if is_reply(frame, _ACK) else None, 0.1)
                except TimeoutError:
                    pass
            finally:
                channel.close()
        deadline = self._clock() + 5
        while self._clock() < deadline:
            self._sleep(0.3)
            try:
                if self.read_mode()[0] == mode:
                    return True
            except (OSError, TimeoutError):
                pass
        raise OSError("切换手柄模式后没有读到新的模式")

    def read_state(self):
        with self._lock:
            channel = self._open()
            try:
                mode, mkeys = self._read_mode(channel)
                head = self._read(channel, 0x22, KEYS + 16 * 8 - 0x22)
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
        extend = head[EXTEND - 0x22:EXTEND - 0x22 + 5]
        keys = {name: parse_key(head[KEYS - 0x22 + slot * 8:KEYS - 0x22 + slot * 8 + 8])
                for name, slot in KEY_SLOTS.items()}
        left_trigger = parse_trigger(controls[0:15], extend[0] == 1)
        right_trigger = parse_trigger(controls[15:30], extend[1] == 1)
        for name, trigger in (("LT", left_trigger), ("RT", right_trigger)):
            keys[name] = {"mapped": trigger["mapped"], "codes": trigger["codes"], "keys": trigger["keys"]}
        left_stick = parse_stick(controls[LEFT_STICK - L2:LEFT_STICK - L2 + 16])
        right_stick = parse_stick(controls[RIGHT_STICK - L2:RIGHT_STICK - L2 + 16])
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
            "keys": keys,
            "triggers": {"left": left_trigger, "right": right_trigger},
            "sticks": {"left": left_stick, "right": right_stick,
                       "swap": left_stick["mapEnabled"] and left_stick["mapValue"] == 2
                       and right_stick["mapEnabled"] and right_stick["mapValue"] == 1},
            "light": dict(parse_animation(animation), animation=active),
            "extras": {"dpadDiagonalLock": extend[2] == 1, "abxySwap": extend[3] == 1, "standby": extend[4]},
        }


def _codes(codes):
    if not isinstance(codes, list) or not 1 <= len(codes) <= 5:
        raise ValueError("键码要 1 到 5 个")
    for code in codes:
        if type(code) is not int or code == 0x00 or code not in KEY_NAMES:
            raise ValueError("不认识的键码 %r" % (code,))
    if 0xFF in codes and codes != [0xFF]:
        raise ValueError("「不映射」(0xFF) 只能单独用")
    return codes + [0] * (5 - len(codes))


def _int(value, low, high, label):
    if type(value) is not int or not low <= value <= high:
        raise ValueError("%s 需在 %d 到 %d 之间" % (label, low, high))
    return value


def _side(side):
    if side not in ("left", "right"):
        raise ValueError("side 只能是 left 或 right")
    return side


class GamepadControl:
    """Service-side owner of the firmware settings.

    Writes go to RAM at once and to ROM 0.7 s after the last change. ClawTweaks rewrites
    dead zones and M keys on its own (helper start, virtual pad), so managed bytes are re-checked
    a few times after start and then every minute; ROM is re-synced at most every 10 minutes.
    """
    SYNC_DELAY = 0.7
    RECHECK_DELAYS = (3, 30, 120)
    RECHECK_INTERVAL = 60
    ENFORCE_SYNC_INTERVAL = 600

    def __init__(self, data_dir, firmware=None, log=None, clock=time.monotonic):
        self._path = Path(data_dir) / "gamepad.json"
        self._firmware = firmware or GamepadFirmware()
        self._log = log or (lambda *args: None)
        self._clock = clock
        self._config = {"version": 1, "original": {}, "items": {}, "light": None}
        self._lock = asyncio.Lock()
        self._sync_task = None
        self._task = None
        self._last_enforce_sync = None
        self._mode = None
        try:
            loaded = json.loads(self._path.read_text(encoding="utf-8"))
            if loaded.get("version") == 1 and isinstance(loaded.get("items"), dict):
                self._config.update({key: loaded[key] for key in ("original", "items", "light") if key in loaded})
        except (OSError, ValueError, AttributeError):
            pass

    # ---- bookkeeping -------------------------------------------------------------------------

    def _save(self):
        temp = self._path.with_suffix(".tmp")
        temp.write_text(json.dumps(self._config), encoding="utf-8")
        os.replace(temp, self._path)

    def _original(self):
        return {int(address): value for address, value in self._config["original"].items()}

    def _desired(self, mode):
        """{address: value} for every byte any item touches, given the pad's mode class."""
        original = self._original()
        desired = {}
        touched = set()
        for name in sorted(self._config["items"]):
            item = self._config["items"][name]
            touched.update(address for address, _, _ in item["bytes"])
            if item.get("mode") not in (None, mode):
                continue
            for address, mask, value in item["bytes"]:
                base = desired.get(address, original[address])
                desired[address] = (base & ~mask & 0xFF) | (value & mask)
        for address in touched:
            desired.setdefault(address, original[address])
        return desired

    async def _run(self, function, *args):
        return await asyncio.get_running_loop().run_in_executor(None, function, *args)

    async def _current_mode(self):
        self._mode = mode_class((await self._run(self._firmware.read_mode))[0])
        return self._mode

    def _refresh_light(self, mode):
        light = self._config["light"]
        if light and light.get("effect") == "modeinfo" and "light" in self._config["items"]:
            frames = light_frames("modeinfo", mode=mode)
            self._config["items"]["light"]["bytes"] = light_bytes(frames, LIGHT_FIXED_SPEED["modeinfo"],
                                                                  light["brightness"])

    async def _set_items(self, items, light=None):
        """items: {name: (bytes, mode) or None to drop}. Backs up the pad's values first."""
        async with self._lock:
            new_addresses = {address for entry in items.values() if entry for address, _, _ in entry[0]}
            missing = sorted(new_addresses - set(self._original()))
            if missing:
                values = await self._run(self._firmware.read_bytes, missing)
                self._config["original"].update({str(address): values[address] for address in missing})
            dropped = set()
            for name, entry in items.items():
                old = self._config["items"].pop(name, None)
                if old:
                    dropped.update(address for address, _, _ in old["bytes"])
                if entry is not None:
                    self._config["items"][name] = {"mode": entry[1], "bytes": [list(b) for b in entry[0]]}
            if light is not False:
                self._config["light"] = light if light is not None else self._config["light"]
            mode = await self._current_mode()
            self._refresh_light(mode)
            self._save()
            original = self._original()
            desired = self._desired(mode)
            # Bytes no item touches any more go back to the pad's own value.
            desired.update({address: original[address] for address in dropped if address not in desired})
            result = await self._run(self._firmware.apply, desired)
            if result["changed"]:
                self._schedule_sync()
            # Forget backups nothing refers to any more, so a later first write backs up afresh.
            used = {address for item in self._config["items"].values() for address, _, _ in item["bytes"]}
            self._config["original"] = {key: value for key, value in self._config["original"].items()
                                        if int(key) in used}
            self._save()
            return result

    def _schedule_sync(self):
        if self._sync_task and not self._sync_task.done():
            self._sync_task.cancel()

        async def later():
            await asyncio.sleep(self.SYNC_DELAY)
            await self._run(self._firmware.sync)
        self._sync_task = asyncio.create_task(later())

    async def flush(self):
        """Run a pending ROM sync now (before exit)."""
        if self._sync_task and not self._sync_task.done():
            self._sync_task.cancel()
            try:
                await self._sync_task
            except asyncio.CancelledError:
                pass
            await self._run(self._firmware.sync)

    # ---- settings ----------------------------------------------------------------------------

    async def set_stick_deadzone(self, side, deadzone, outer):
        base = LEFT_STICK if _side(side) == "left" else RIGHT_STICK
        _int(deadzone, 0, 50, "摇杆死区")
        _int(outer, 75, 100, "摇杆外圈阈值")
        return await self._set_items({"stick." + side: ([(base + 1, 0xFF, 1), (base + 2, 0xFF, deadzone),
                                                         (base + 9, 0xFF, 1), (base + 10, 0xFF, outer)], None)})

    async def set_stick_swap(self, enabled):
        if type(enabled) is not bool:
            raise ValueError("enabled 要是 true / false")
        # MSI Center M: left stick maps to 2 (right), right stick to 1 (left).
        entry = ([(LEFT_STICK + 3, 0xFF, 1), (LEFT_STICK + 6, 0xFF, 2),
                  (RIGHT_STICK + 3, 0xFF, 1), (RIGHT_STICK + 6, 0xFF, 1)], None) if enabled else None
        return await self._set_items({"stick.swap": entry})

    async def set_trigger_range(self, side, start, end):
        base = L2 if _side(side) == "left" else R2
        _int(start, 0, 100, "扳机起点")
        _int(end, 0, 100, "扳机终点")
        if start >= end:
            raise ValueError("扳机起点要小于终点")
        return await self._set_items({"trigger." + side: ([(base + 1, 0x02, 0x02), (base + 3, 0xFF, start),
                                                           (base + 4, 0xFF, end)], None)})

    async def set_quick_trigger(self, side, enabled):
        if type(enabled) is not bool:
            raise ValueError("enabled 要是 true / false")
        address = EXTEND if _side(side) == "left" else EXTEND + 1
        return await self._set_items({"quick." + side: ([(address, 0xFF, 1)], None) if enabled else None})

    async def set_back_key(self, key, codes):
        if key not in ("m1", "m2"):
            raise ValueError("背键只能是 m1 或 m2")
        codes = _codes(codes)
        base = M1 if key == "m1" else M2
        # KeyProfile: index 1 (mapping, 2 would be macro), flags: mapping on, turbo off.
        return await self._set_items({key: ([(base, 0xFF, 1), (base + 1, 0x05, 0x04)]
                                            + [(base + 3 + i, 0xFF, code) for i, code in enumerate(codes)], None)})

    def _key_bytes(self, source, codes):
        if source not in OWN_CODES:
            raise ValueError("不认识的源键 %r" % (source,))
        padded = _codes(codes)
        mapped = 0x04 if any(code != OWN_CODES[source] for code in padded if code) else 0
        if source in TRIGGER_KEYS:
            base = TRIGGER_KEYS[source]
            return [(base + 1, 0x05, mapped)] + [(base + 5 + i, 0xFF, code) for i, code in enumerate(padded)]
        base = KEYS + KEY_SLOTS[source] * 8
        return [(base, 0xFF, 1), (base + 1, 0x05, mapped)] + [(base + 3 + i, 0xFF, code)
                                                              for i, code in enumerate(padded)]

    async def set_key_map(self, source, codes):
        """Gamepad-mode mapping of a front button; [own code] puts the key back."""
        if codes is None or codes == [OWN_CODES.get(source)]:
            return await self._set_items({"key." + source: None})
        return await self._set_items({"key." + source: (self._key_bytes(source, codes), "gamepad")})

    async def set_desktop_key_map(self, source, codes):
        """Desktop-mode mapping: written to the pad only while it is in desktop mode (like MSI Center M)."""
        if codes is None or codes == [OWN_CODES.get(source)]:
            return await self._set_items({"dkey." + source: None})
        return await self._set_items({"dkey." + source: (self._key_bytes(source, codes), "desktop")})

    async def set_light(self, effect, color=None, brightness=100, speed=1, direction="cw", frames=None):
        if effect not in LIGHT_EFFECTS:
            raise ValueError("灯光效果只能是 " + " / ".join(LIGHT_EFFECTS))
        _int(brightness, 0, 100, "亮度")
        _int(speed, 0, 2, "速度")
        if direction not in ("cw", "ccw"):
            raise ValueError("方向只能是 cw 或 ccw")
        colors = None
        if effect in ("monocolor", "breathe"):
            colors = [rgb(color)]
        elif effect == "ripple" and color:
            colors = [rgb(c) for c in (color if isinstance(color, list) else [color])][:4]
        custom = None
        if effect == "custom":
            if not isinstance(frames, list) or not 1 <= len(frames) <= 8:
                raise ValueError("自定义灯光要 1 到 8 帧")
            custom = [[rgb(frame)] * LIGHT_ZONES if isinstance(frame, str) else [rgb(zone) for zone in frame]
                      for frame in frames]
            if any(len(frame) != LIGHT_ZONES for frame in custom):
                raise ValueError("每帧要 9 个区的颜色（或一个颜色）")
        mode = self._mode or "gamepad"
        built = light_frames(effect, colors, direction, mode, custom)
        raw = LIGHT_SPEEDS[effect][speed] if effect in LIGHT_SPEEDS else LIGHT_FIXED_SPEED[effect]
        setting = {"effect": effect, "color": color if isinstance(color, str) else None,
                   "brightness": brightness, "speed": speed, "direction": direction,
                   "frames": frames if effect == "custom" else None}
        return await self._set_items({"light": (light_bytes(built, raw, brightness), None)}, light=setting)

    async def set_mode(self, name):
        """xinput / desktop. Re-applies per-mode mappings and the mode light afterwards."""
        target = {"xinput": 1, "desktop": 4}.get(name)
        if target is None:
            raise ValueError("模式只能是 xinput 或 desktop")
        async with self._lock:
            mode, mkeys = await self._run(self._firmware.read_mode)
            if mode != target:
                await self._run(self._firmware.switch_mode, target, mkeys)
        await self.enforce("mode")
        return True

    async def restore(self, what="all"):
        groups = {"sticks": ("stick.left", "stick.right", "stick.swap"),
                  "triggers": ("trigger.left", "trigger.right", "quick.left", "quick.right"),
                  "backkeys": ("m1", "m2"), "light": ("light",)}
        names = set(self._config["items"])
        if what == "all":
            chosen = names
        elif what in groups:
            chosen = names & set(groups[what])
        elif what == "keys":
            chosen = {name for name in names if name.startswith("key.")}
        elif what == "desktopkeys":
            chosen = {name for name in names if name.startswith("dkey.")}
        elif what in names:
            chosen = {what}
        else:
            raise ValueError("没有这一项可以恢复：%r" % (what,))
        if not chosen:
            return {"changed": 0}
        return await self._set_items({name: None for name in chosen},
                                     light={} if "light" in chosen else False)

    def managed(self):
        return sorted(self._config["items"])

    # ---- state and upkeep --------------------------------------------------------------------

    async def read(self):
        try:
            state = await self._run(self._firmware.read_state)
        except Exception as error:
            return {"available": False, "error": str(error), "managed": self.managed()}
        self._mode = mode_class(state["modeRaw"])
        light = self._config["light"]
        if light and "light" in self._config["items"]:
            desired = self._desired(self._mode)
            mine = [address for address, _, _ in self._config["items"]["light"]["bytes"]]
            frames = [list(map(rgb, frame)) for frame in state["light"]["keyframes"]]
            current = light_bytes(frames, state["light"]["speedRaw"], state["light"]["brightness"])
            current = {address: value for address, _, value in current}
            if state["light"]["animation"] == 0 and all(current.get(address) == desired[address] for address in mine):
                state["light"].update({"effect": light["effect"], "speedLevel": light["speed"],
                                       "direction": light["direction"]})
                if light.get("color"):
                    state["light"]["color"] = rgb(light["color"])
        state["light"]["managed"] = "light" in self._config["items"]
        state["managed"] = self.managed()
        state["desktopKeys"] = {name[5:]: [value for address, mask, value in item["bytes"][-5:] if value]
                                for name, item in self._config["items"].items() if name.startswith("dkey.")}
        return state

    async def enforce(self, reason):
        if not self._config["items"]:
            return {"changed": 0}
        async with self._lock:
            try:
                mode = await self._current_mode()
                self._refresh_light(mode)
                result = await self._run(self._firmware.apply, self._desired(mode))
            except Exception as error:
                self._log("Claw pad settings %s failed: %s" % (reason, error))
                return {"changed": 0, "error": str(error)}
            if result["changed"]:
                self._log("Claw pad settings %s: put back %d byte(s) %s" % (
                    reason, result["changed"], {hex(a): v for a, v in list(result["before"].items())[:8]}))
                now = self._clock()
                if (reason != "recheck" or self._last_enforce_sync is None
                        or now - self._last_enforce_sync >= self.ENFORCE_SYNC_INTERVAL):
                    self._last_enforce_sync = now
                    self._schedule_sync()
            return result

    def start(self):
        async def recheck():
            for delay in self.RECHECK_DELAYS:
                await asyncio.sleep(delay)
                await self.enforce("startup")
            while True:
                await asyncio.sleep(self.RECHECK_INTERVAL)
                await self.enforce("recheck")
        self._task = asyncio.create_task(recheck())

    async def close(self):
        if self._task and not self._task.done():
            self._task.cancel()
            try:
                await self._task
            except asyncio.CancelledError:
                pass
        try:
            await self.flush()
        except Exception:
            pass


class GamepadState:
    """Read-only wrapper kept for callers that only read."""

    def __init__(self, firmware=None):
        self._firmware = firmware or GamepadFirmware()

    def read(self):
        try:
            return self._firmware.read_state()
        except Exception as error:
            return {"available": False, "error": str(error)}
