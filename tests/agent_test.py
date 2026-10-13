import asyncio
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import types
import unittest

PLUGIN = Path(__file__).parents[1] / "plugin"


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, PLUGIN / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


agent_module = load("claw_agent_under_test", "claw_agent.py")
client_module = load("claw_agent_client_under_test", "claw_agent_client.py")


class FakeService:
    def __init__(self, emit):
        self.emit = emit
        self.calls = []
        self.gone = []
        self.closed = False

    async def start(self):
        pass

    async def close(self):
        self.closed = True

    async def get_power_state(self, sample=False):
        self.calls.append(("get_power_state", sample))
        await self.emit("power_status", {"sample": sample})
        return {"pl1": 12}

    async def frontend_ready(self, ready, frontend_id=None):
        self.calls.append(("frontend_ready", ready, frontend_id))
        return {"ready": ready}

    async def save_power(self, settings):
        raise ValueError("功耗设置格式错误")

    async def frontend_gone(self, ids):
        self.gone.append(set(ids))


class AgentTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.home = Path(self.temp.name)
        self.agent = agent_module.Agent(FakeService, endpoint=self.home / "agent.json")
        await self.agent.start()
        self.events = []

        async def on_event(name, value):
            self.events.append((name, value))
        self.client = client_module.AgentClient("test", on_event, home=self.home, starter=lambda home: "none")
        self.client.start()

    async def asyncTearDown(self):
        await self.client.close()
        await self.agent.close()
        self.temp.cleanup()

    async def test_calls_events_and_errors_round_trip(self):
        self.assertEqual(await self.client.call("get_power_state", True), {"pl1": 12})
        self.assertEqual(self.events, [("power_status", {"sample": True})])
        with self.assertRaisesRegex(RuntimeError, "功耗设置格式错误"):
            await self.client.call("save_power", {})
        self.assertEqual(self.client.info["version"], self.agent.version)

    async def test_private_and_unknown_methods_are_refused(self):
        for method in ("close", "_send", "start", "__init__", "set_clash"):
            with self.assertRaisesRegex(RuntimeError, "不支持"):
                await self.client.call(method)
        self.assertFalse(self.agent.service.closed)

    async def test_wrong_token_gets_nothing(self):
        endpoint = json.loads((self.home / "agent.json").read_text(encoding="utf-8"))
        reader, writer = await asyncio.open_connection("127.0.0.1", endpoint["port"])
        writer.write(b'{"hello": "guess", "client": "x"}\n{"id": 1, "method": "get_power_state", "args": []}\n')
        await writer.drain()
        self.assertEqual(await asyncio.wait_for(reader.read(), 3), b"")
        writer.close()
        self.assertEqual(self.agent.service.calls, [])

    async def test_closed_connection_releases_its_frontend_only(self):
        await self.client.call("frontend_ready", True, "steam-1")
        await self.client.call("frontend_ready", True, "steam-2")
        await self.client.call("frontend_ready", False, "steam-1")
        await self.client.close()
        for _ in range(50):
            if self.agent.service.gone:
                break
            await asyncio.sleep(.02)
        self.assertEqual(self.agent.service.gone, [{"steam-2"}])

    async def test_shutdown_request_stops_agent(self):
        self.assertEqual(await self.client.call("shutdown"), {"stopping": True})
        self.assertTrue(self.agent.stopped.is_set())

    async def test_websocket_speaks_the_same_protocol(self):
        import base64, os, struct
        endpoint = json.loads((self.home / "agent.json").read_text(encoding="utf-8"))
        reader, writer = await asyncio.open_connection("127.0.0.1", endpoint["wsPort"])
        key = base64.b64encode(os.urandom(16)).decode()
        writer.write(("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                      f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
        response = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), 3)
        self.assertIn(b"101 Switching Protocols", response)

        def frame(text):
            data, mask = text.encode(), os.urandom(4)
            size = bytes([0x80 | len(data)]) if len(data) < 126 else bytes([0x80 | 126]) + struct.pack(">H", len(data))
            return bytes([0x81]) + size + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(data))

        async def read():
            head = await reader.readexactly(2)
            length = head[1] & 0x7F
            if length == 126:
                length = struct.unpack(">H", await reader.readexactly(2))[0]
            return json.loads(await reader.readexactly(length))
        writer.write(frame(json.dumps({"hello": endpoint["token"], "client": "gamebar"})))
        self.assertEqual((await read())["hello"], "ok")
        writer.write(frame(json.dumps({"id": 7, "method": "get_power_state", "args": [False]})))
        messages = [await read(), await read()]
        self.assertIn({"id": 7, "result": {"pl1": 12}}, messages)
        self.assertIn({"event": "power_status", "value": {"sample": False}}, messages)
        writer.close()

    def test_widget_gets_its_endpoint_only_when_installed(self):
        record = {"port": 1, "wsPort": 2, "token": "t", "pid": 3, "version": "x"}
        self.agent.publish_widget_endpoint(record, self.home / "missing")
        self.assertFalse((self.home / "missing").exists())
        self.agent.publish_widget_endpoint(record, self.home)
        self.assertEqual(json.loads((self.home / "agent.json").read_text(encoding="utf-8")), {"wsPort": 2, "token": "t"})

    async def test_endpoint_removed_only_by_its_owner(self):
        await self.agent.close()
        self.assertFalse((self.home / "agent.json").exists())


class ClientStartTests(unittest.IsolatedAsyncioTestCase):
    async def test_missing_agent_is_started_then_connected(self):
        with tempfile.TemporaryDirectory() as folder:
            home = Path(folder)
            started = []
            agents = []

            def starter(path):
                started.append(path)
                return "task"

            async def noop(*args):
                pass
            client = client_module.AgentClient("test", noop, home=home, starter=starter)
            client.start()
            for _ in range(50):
                if started:
                    break
                await asyncio.sleep(.02)
            self.assertEqual(started, [home])
            agent = agent_module.Agent(FakeService, endpoint=home / "agent.json")
            agents.append(agent)
            await agent.start()
            self.assertEqual(await client.call("get_power_state"), {"pl1": 12})
            await client.close()
            await agent.close()

    async def test_call_fails_cleanly_when_agent_never_comes(self):
        with tempfile.TemporaryDirectory() as folder:
            async def noop(*args):
                pass
            client = client_module.AgentClient("test", noop, home=Path(folder), starter=lambda home: "none")
            client.start()
            with self.assertRaisesRegex(RuntimeError, "未运行"):
                await client.call("get_state", wait=.3)
            await client.close()


class MigrationTests(unittest.TestCase):
    def test_copies_toolbox_files_once_and_leaves_clash_in_decky(self):
        with tempfile.TemporaryDirectory() as folder:
            legacy, data = Path(folder) / "legacy", Path(folder) / "data"
            (legacy / "compatibility").mkdir(parents=True)
            (legacy / "compatibility" / "a.json").write_text("1")
            (legacy / "power.json").write_text('{"tdp": 12}')
            (legacy / "clash-ui.json").write_text("{}")
            (legacy / "verge-session.json").write_text("{}")
            (legacy / "verge-webview-9c818a22").mkdir()
            (legacy / "subscription-backups").mkdir()
            self.assertTrue(agent_module.migrate(legacy, data))
            self.assertEqual(sorted(p.name for p in data.iterdir()), [".migrated", "compatibility", "power.json"])
            self.assertEqual((data / "compatibility" / "a.json").read_text(), "1")
            (legacy / "power.json").write_text('{"tdp": 25}')
            self.assertFalse(agent_module.migrate(legacy, data))
            self.assertEqual((data / "power.json").read_text(), '{"tdp": 12}')

    def test_fresh_device_without_decky_settings(self):
        with tempfile.TemporaryDirectory() as folder:
            data = Path(folder) / "data"
            self.assertFalse(agent_module.migrate(Path(folder) / "missing", data))
            self.assertTrue((data / ".migrated").exists())


class DeckyProxyTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.emitted = []
        decky = types.ModuleType("decky")
        decky.DECKY_PLUGIN_SETTINGS_DIR = self.temp.name

        async def emit(name, value=None):
            self.emitted.append((name, value))
        decky.emit = emit
        sys.modules["decky"] = decky
        self.module = load("claw_decky_under_test", "main.py")

    def tearDown(self):
        self.temp.cleanup()

    def proxy(self, calls, version="0.17.0"):
        plugin = self.module.Plugin.__new__(self.module.Plugin)
        plugin._version, plugin._frontend, plugin._upgrade_requested = version, None, False

        class Client:
            connected = True

            async def call(self, method, *args):
                calls.append((method, *args))
                if method == "get_state":
                    return {"status": {"mode": "BigPicture"}}
                return {}
        plugin._client = Client()
        return plugin

    async def test_every_steam_call_is_forwarded(self):
        calls = []
        plugin = self.proxy(calls)
        for name in self.module.FORWARDED:
            self.assertTrue(callable(getattr(plugin, name)))
        self.assertLessEqual(set(self.module.FORWARDED), agent_module.METHODS)
        await plugin.save_power({"tdp": 15})
        self.assertEqual(calls, [("save_power", {"tdp": 15})])

    async def test_frontend_is_replayed_after_agent_restart(self):
        calls = []
        plugin = self.proxy(calls)
        await plugin.frontend_ready(True, "steam-1")
        calls.clear()
        await plugin._connected({"version": "0.17.0"})
        self.assertEqual(calls, [("frontend_ready", True, "steam-1"), ("get_state",)])
        await plugin.frontend_ready(False, "steam-1")
        calls.clear()
        await plugin._connected({"version": "0.17.0"})
        self.assertEqual(calls, [])

    async def test_older_agent_is_restarted_once_after_install(self):
        calls = []
        plugin = self.proxy(calls)
        home = Path(self.temp.name)
        (home / "current.json").write_text(json.dumps({"version": "0.17.0"}), encoding="utf-8")
        self.module._client_module.HOME = home
        await plugin._connected({"version": "0.16.9"})
        await plugin._connected({"version": "0.16.9"})
        self.assertEqual(calls, [("shutdown",)])

    async def test_disconnect_reports_offline_status(self):
        plugin = self.proxy([])
        await plugin._disconnected()
        self.assertEqual(self.emitted[0][0], "router_event")
        self.assertFalse(self.emitted[0][1]["enabled"])


if __name__ == "__main__":
    unittest.main()
