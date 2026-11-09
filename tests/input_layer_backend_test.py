"""Input layer, agent side: layout format, storage, compile, the on / off switch with a fake engine and fake
firmware (M1 / M2 and remapped keys taken and put back), crash handling and the conflict report."""
import asyncio
import importlib.util
import json
from pathlib import Path
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1] / "plugin"
spec = importlib.util.spec_from_file_location("claw_input_layer", ROOT / "claw_input_layer.py")
il = importlib.util.module_from_spec(spec)
spec.loader.exec_module(il)

DEMO = {"name": "演示", "buttons": {
    "A": [{"trigger": "long", "longPressMs": 300, "command": {"type": "gamepad", "button": "B"}},
          {"trigger": "regular", "command": {"type": "gamepad", "button": "A"}}],
    "X": [{"trigger": "double", "doublePressMs": 250, "command": {"type": "keyboard", "keys": ["Tab"]}},
          {"trigger": "regular", "command": {"type": "gamepad", "button": "X"}}]}}


def refused(layout, text):
    try:
        il.validate_layout(layout)
    except ValueError as error:
        assert text in str(error), (text, str(error))
        return
    raise AssertionError("accepted: %r" % (layout,))


def layouts():
    v = il.validate_layout(DEMO)
    a = v["buttons"]["A"]
    assert a[0] == {"trigger": "long", "longPressMs": 300, "doublePressMs": 250, "startDelayMs": 0, "endDelayMs": 0,
                    "repeatHz": 0, "toggle": False, "interruptible": True, "command": {"type": "gamepad", "button": "B"}}
    assert v["triggers"] == {"LT": {"threshold": 50}, "RT": {"threshold": 50}}
    assert v["sticks"] == {"left": {"mode": "stick", "output": "left"}, "right": {"mode": "stick", "output": "right"}}
    assert il.validate_layout(v) == v, "normalized layouts validate to themselves"
    ctrl = il.validate_layout({"buttons": {"M1": [{"trigger": "regular", "command": {"type": "keyboard", "keys": ["Ctrl", "C"]}}]}})
    assert ctrl["buttons"]["M1"][0]["command"]["keys"] == ["LCtrl", "C"]

    refused({"buttons": {"Q": []}}, "不认识的键")
    refused({"buttons": {"A": [{"trigger": "triple", "command": {"type": "none"}}]}}, "触发方式只能是")
    refused({"buttons": {"A": [{"trigger": "chord", "command": {"type": "none"}}]}}, "组合键")
    refused({"buttons": {"A": [{"trigger": "chord", "chordWith": "A", "command": {"type": "none"}}]}}, "组合键")
    refused({"buttons": {"A": [{"trigger": "long", "longPressMs": 10, "command": {"type": "none"}}]}}, "longPressMs")
    refused({"buttons": {"A": [{"trigger": "regular", "repeatHz": 100, "command": {"type": "none"}}]}}, "repeatHz")
    refused({"buttons": {"A": [{"trigger": "regular", "toggle": 1, "command": {"type": "none"}}]}}, "toggle")
    refused({"buttons": {"A": [{"trigger": "regular", "command": {"type": "keyboard", "keys": ["Hyper"]}}]}}, "不认识的键盘键")
    refused({"buttons": {"A": [{"trigger": "regular", "command": {"type": "keyboard", "keys": ["A", "A"]}}]}}, "重复")
    refused({"buttons": {"A": [{"trigger": "regular", "command": {"type": "system", "action": "shutdown"}}]}}, "系统动作")
    refused({"buttons": {"A": [{"trigger": "regular", "haptic": "high", "command": {"type": "none"}}]}}, "第二批")
    refused({"sticks": {"right": {"mode": "mouse"}}}, "摇杆当鼠标第二批")
    refused({"actionSets": [{"name": "驾驶"}]}, "操作集第二批")
    refused({"triggers": {"LT": {"threshold": 0}}}, "按下阈值")

    c = il.compile_layout(v)
    assert c["buttons"]["A"][0] == {"type": "long", "longMs": 300, "doubleMs": 250, "startDelayMs": 0, "endDelayMs": 0,
                                    "repeatHz": 0, "toggle": False, "interruptible": True, "chord": None,
                                    "command": {"kind": "pad", "pad": 0x2000}}
    assert c["buttons"]["X"][0]["command"] == {"kind": "keys", "keys": [9], "extended": [False]}
    assert c["ltThreshold"] == 128 and c["leftStick"] == "left" and c["tapMs"] == 40
    sys_ = il.compile_layout(il.validate_layout({"buttons": {
        "Y": [{"trigger": "start", "command": {"type": "system", "action": "screenshot"}}],
        "B": [{"trigger": "regular", "command": {"type": "system", "action": "keyboard"}}],
        "LB": [{"trigger": "regular", "command": {"type": "mouse", "wheel": "Down"}}],
        "RB": [{"trigger": "regular", "command": {"type": "gamepad", "button": "LStickUp"}}]},
        "sticks": {"right": {"mode": "none"}}}))
    assert sys_["buttons"]["Y"][0]["command"] == {"kind": "keys", "keys": [0x5B, 0xA4, 0x2C], "extended": [True, False, True]}
    assert sys_["buttons"]["B"][0]["command"] == {"kind": "action", "action": "keyboard"}
    assert sys_["buttons"]["LB"][0]["command"] == {"kind": "wheel", "value": 2}
    assert sys_["buttons"]["RB"][0]["command"] == {"kind": "axis", "value": 2}
    assert sys_["rightStick"] == "none"

    cat = il.catalog()
    assert "Tab" in cat["commands"]["keyboard"] and cat["sources"][-2:] == ["M1", "M2"]
    json.dumps(cat)


def storage():
    with tempfile.TemporaryDirectory() as folder:
        store = il.Layouts(folder)
        assert store.get() is None and store.all()["global"]["buttons"] == {}
        store.save(DEMO)
        store.save({"name": "法环"}, "EldenRing.exe")
        assert store.games() == ["eldenring.exe"] and store.get("eldenring.exe")["name"] == "法环"
        for bad in ("../x.exe", "a\\b.exe", "game", " x.exe", "c:x.exe"):
            try:
                store.save({}, bad)
                raise AssertionError(bad)
            except ValueError:
                pass
        store.save(None, "eldenring.exe")
        assert store.games() == []
        try:
            store.save(None)
            raise AssertionError("global deleted")
        except ValueError:
            pass


class FakeEngine:
    """Stands in for the bridge + engine: answers the line protocol, can crash."""

    def __init__(self):
        self.commands = []
        self.lines = asyncio.Queue()
        self.returncode = None
        self.lines.put_nowait(b'{"type":"ready","pid":4242}\n')
        engine = self

        class Stdin:
            def write(self, data):
                message = json.loads(data.decode("utf-8"))
                engine.commands.append(message)
                status = {"enabled": message["command"] == "enable", "layout": "global",
                          "hidhide": {"cloak": True, "hidden": ["HID\\VID_0DB0&PID_1901&IG_03\\X"], "apps": ["\\Device\\X\\ClawInput.exe"],
                                      "blacklist": ["HID\\VID_0DB0&PID_1901&IG_03\\X"], "whitelist": []},
                          "latency": {"samples": 10, "avgMs": 0.4}, "otherVirtualPads": 0}
                engine.lines.put_nowait((json.dumps({"id": message["id"], "ok": True, "status": status}) + "\n").encode())
                if message["command"] == "stop":
                    engine.end()

            async def drain(self):
                pass

            def close(self):
                engine.end()

        class Stdout:
            async def readline(self):
                return await engine.lines.get()

        self.stdin, self.stdout = Stdin(), Stdout()

    def end(self):
        if self.returncode is None:
            self.returncode = 0
            self.lines.put_nowait(b"")

    async def wait(self):
        return self.returncode

    def kill(self):
        self.end()


class FakeGamepad:
    def __init__(self):
        self.calls = []
        self.state = {"available": True, "managed": ["m1", "m2"],
                      "backKeys": {"m1": {"codes": [118, 62]}, "m2": {"codes": [18]}},
                      "keys": {name: {"codes": [code]} for name, code in
                               {"Up": 1, "Down": 2, "Left": 3, "Right": 4, "LB": 5, "RB": 6, "LS": 7, "RS": 8,
                                "A": 10, "B": 10, "X": 11, "Y": 12, "LT": 19, "RT": 20}.items()}}

    async def read(self):
        return self.state

    async def set_back_key(self, key, codes):
        self.calls.append(("back", key, codes))

    async def set_key_map(self, source, codes):
        self.calls.append(("key", source, codes))

    async def restore(self, what):
        self.calls.append(("restore", what))


async def settle(layer, phase, engines=None, count=None):
    for _ in range(300):
        if layer._phase == phase and (count is None or len(engines) == count):
            return
        await asyncio.sleep(0.01)
    raise AssertionError("phase %s, wanted %s" % (layer._phase, phase))


async def switch():
    with tempfile.TemporaryDirectory() as folder:
        root = Path(folder) / "plugin"
        root.mkdir()
        (root / "plugin.json").write_text('{"version": "0.0.1"}', encoding="utf-8")
        events, engines = [], []

        async def emit(event, value):
            events.append((event, value))

        async def spawn():
            engines.append(FakeEngine())
            return engines[-1]

        pad = FakeGamepad()
        data = Path(folder) / "data"

        def make():
            layer = il.InputLayer(root, data, emit, gamepad=pad, not_games=["steam.exe"], python="C:\\py\\pythonw.exe",
                                  spawn=spawn, processes=lambda: {"steam.exe"}, xbox_support=lambda: True,
                                  extra_apps=lambda: ["C:\\Steam\\steam.exe", "C:\\Steam\\bin\\cef\\cef.win64\\steamwebhelper.exe"])
            layer.RETRY_DELAY = 0.01
            return layer

        layer = make()
        await layer.start(pad_mode="xinput")
        state = await layer.state()
        assert state["phase"] == "off" and state["auto"] and not state["wanted"] and engines == [], "no layout binds a key: off"

        # The first layout that binds a key starts the layer, no switch involved.
        layout = dict(DEMO, buttons=dict(DEMO["buttons"], M1=[{"trigger": "regular", "command": {"type": "keyboard", "keys": ["F5"]}}]))
        await layer.save_layout(layout)
        state = await layer.state()
        assert state["phase"] == "active" and state["owner"] == "toolbox" and state["wanted"] and state["auto"], state
        assert [c["command"] for c in engines[0].commands] == ["configure", "enable"]
        configure = engines[0].commands[0]
        assert configure["layouts"]["global"]["buttons"]["A"][0]["command"] == {"kind": "pad", "pad": 0x2000}
        assert "clawlibrary.exe" in configure["notGames"]
        assert configure["whitelist"] == ["C:\\py\\pythonw.exe", "C:\\Steam\\steam.exe", "C:\\Steam\\bin\\cef\\cef.win64\\steamwebhelper.exe"]
        # M1 used -> F22; A (remapped to B in the firmware) used -> back to A. M2 and B untouched.
        assert pad.calls == [("back", "m1", [0x9D]), ("key", "A", [0x09])], pad.calls
        record = json.loads((data / "input-layer" / "firmware.json").read_text(encoding="utf-8"))
        assert record["items"] == {"m1": {"codes": [118, 62], "managed": True}, "key.A": {"codes": [10], "managed": False}}
        assert not any("白名单" in w for w in state["conflicts"]["warnings"]), "Steam seeing the pad is intended now"
        assert any("Xbox 配置支持" in w for w in state["conflicts"]["warnings"])

        # Saving without M1 while on: the engine gets it at once and M1 goes back; still on.
        pad.calls.clear()
        await layer.save_layout(DEMO)
        assert engines[0].commands[-1]["command"] == "configure" and layer._phase == "active"
        assert pad.calls == [("back", "m1", [118, 62])], pad.calls

        # A per-game layout keeps it on even when the global one binds nothing.
        await layer.save_layout({"buttons": {"Y": []}}, "game.exe")
        pad.calls.clear()
        await layer.save_layout({"name": "空"})
        assert layer._phase == "active" and len(engines) == 1, "game.exe still binds Y (disabled counts as bound)"
        assert pad.calls == [("restore", "key.A")], "A is no longer used anywhere: back at once"

        # Last bound key gone: off.
        pad.calls.clear()
        await layer.save_layout(None, "game.exe")
        state = await layer.state()
        assert state["phase"] == "off" and not state["wanted"] and engines[0].returncode == 0
        assert engines[0].commands[-1]["command"] == "stop"
        assert pad.calls == [], pad.calls
        assert not (data / "input-layer" / "firmware.json").exists()

        # Debug override: on without layouts, back to the rule with None.
        state = await layer.set_enabled(True)
        assert state["phase"] == "active" and state["override"] is True and not state["auto"] and len(engines) == 2
        state = await layer.set_enabled(None)
        assert state["phase"] == "off" and state["auto"] and engines[1].returncode == 0

        # Desktop mode pauses the layer, gamepad mode resumes it.
        await layer.save_layout(layout)
        assert layer._phase == "active" and len(engines) == 3
        await layer.set_pad_mode("desktop")
        state = await layer.state()
        assert state["phase"] == "off" and not state["wanted"] and state["padMode"] == "desktop" and engines[2].returncode == 0
        await layer.set_pad_mode("xinput")
        assert layer._phase == "active" and len(engines) == 4

        # A crash: firmware goes back, then the layer starts again by itself.
        pad.calls.clear()
        engines[-1].end()
        await settle(layer, "active", engines, 5)
        assert ("back", "m1", [118, 62]) in pad.calls and ("restore", "key.A") in pad.calls, pad.calls
        assert ("back", "m1", [0x9D]) in pad.calls, "taken again on the restart"
        assert any(event == "input_layer" and value["phase"] == "error" for event, value in events)

        # Next agent start: the rule again (layouts still bind keys).
        await layer.close()
        again = make()
        await again.start(pad_mode="xinput")
        assert again._phase == "active" and engines[-1].commands[-1]["command"] == "enable"
        await again.close()
        assert engines[-1].returncode == 0
        desktop = make()
        await desktop.start(pad_mode="desktop")
        assert desktop._phase == "off", "started in desktop mode: stays off"


def conflict_report():
    status = {"hidhide": {"hidden": ["HID\\PAD"], "apps": ["\\Device\\H\\ClawInput.exe"],
                          "blacklist": ["HID\\PAD", "HID\\OTHER"],
                          "whitelist": ["\\Device\\H\\ClawInput.exe", "\\Device\\H\\x\\XboxGamingBarHelper.exe"]},
              "otherVirtualPads": 1}
    report = il.conflicts(status, {"xboxgamingbarhelper.exe"}, None)
    assert report["clawTweaks"] == {"running": True, "processes": ["xboxgamingbarhelper.exe"], "helperSeesPad": True}
    assert report["otherHiddenDevices"] == ["HID\\OTHER"] and report["otherVirtualPads"] == 1
    assert len(report["warnings"]) == 3, report["warnings"]
    seen = il.conflicts(dict(status, hidhide=dict(status["hidhide"], whitelist=["\\Device\\H\\Steam\\steam.exe"])), {"steam.exe"}, False)
    assert seen["steamInput"]["steamSeesPad"] and not any("Steam" in w for w in seen["warnings"])
    assert il.conflicts({}, set(), None)["warnings"] == []


def main():
    layouts()
    storage()
    asyncio.run(switch())
    conflict_report()
    print("input layer backend checks passed")


main()
