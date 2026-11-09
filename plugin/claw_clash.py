"""On-demand Clash Verge controls; no background poll or configuration rewrite."""
import asyncio
import json
import subprocess
from pathlib import Path


def proxy_view(raw, preferred=""):
    proxies = raw.get("proxies", {})
    groups = []
    for name, item in proxies.items():
        members = item.get("all")
        if item.get("type") != "Selector" or not isinstance(members, list) or not members:
            continue
        groups.append({"name": name, "current": item.get("now", ""),
                       "nodes": [node for node in members if isinstance(node, str)]})
    names = {group["name"] for group in groups}
    mode = raw.get("mode")
    if mode not in ("rule", "direct", "global"):
        raise ValueError("无法识别 Clash 运行模式")
    if mode == "global" and "GLOBAL" in names:
        preferred = "GLOBAL"
    elif preferred not in names or preferred == "GLOBAL":
        candidates = [group for group in groups if group["name"] != "GLOBAL" and len(group["nodes"]) > 1]
        # The shared outbound selector generally has the most references from other groups.
        preferred = max(candidates, key=lambda group: sum(group["name"] in other["nodes"] for other in groups),
                        default=groups[0] if groups else {"name": ""})["name"]
    chosen = next((group for group in groups if group["name"] == preferred), None)
    current = chosen["current"] if chosen else ""
    path, seen = [], set()
    node = preferred
    while node in proxies and node not in seen:
        seen.add(node)
        path.append(node)
        next_node = proxies[node].get("now")
        if not isinstance(next_node, str) or not next_node:
            break
        node = next_node
    history = proxies.get(path[-1] if path else "", {}).get("history", [])
    delay = history[-1].get("delay") if history and isinstance(history[-1], dict) else None
    test = raw.get("test")
    test_view = None
    if isinstance(test, dict) and type(test.get("ok")) is bool:
        test_view = {"ok": test["ok"], "node": str(test.get("node", ""))[:256],
                     "message": str(test.get("message", ""))[:256]}
        if type(test.get("delay")) is int and test["delay"] > 0:
            test_view["delay"] = test["delay"]
    return {"available": True, "enabled": mode != "direct", "mode": mode,
            "phase": {"rule": "运行中 · 规则模式", "direct": "运行中 · 直连模式", "global": "运行中 · 全局模式"}[mode],
            "systemProxy": raw.get("systemProxy") if type(raw.get("systemProxy")) is bool else None,
            "tun": raw.get("tun") is True, "group": preferred, "groups": groups,
            "current": current, "resolved": path[-1] if len(path) > 1 else current,
            "delay": delay if type(delay) is int and delay > 0 else None, "test": test_view,
            "nodeCount": raw.get("nodeCount") if type(raw.get("nodeCount")) is int else None}


class ClashControl:
    def __init__(self, plugin_root, settings_root):
        self._exe = Path(plugin_root) / "bin" / "ClashBridge.exe"
        self._path = Path(settings_root) / "clash-ui.json"
        self._lock = asyncio.Lock()
        self._process = None
        self._closed = False
        self._group = ""
        self.state = {"available": False, "phase": "正在读取 Clash Verge", "groups": []}
        try:
            value = json.loads(self._path.read_text(encoding="utf-8"))
            if isinstance(value.get("group"), str):
                self._group = value["group"][:256]
        except (OSError, ValueError, AttributeError):
            pass

    async def _request(self, payload):
        if self._closed:
            raise RuntimeError("代理面板已关闭")
        process = await asyncio.create_subprocess_exec(
            str(self._exe), stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
        self._process = process
        try:
            output, _ = await asyncio.wait_for(process.communicate(
                (json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8")), 20)
            result = json.loads(output.decode("utf-8-sig"))
            if result.get("ok") is not True:
                raise RuntimeError("Clash Verge 连接不可用，或操作未完成；请刷新后重试")
            return result["state"]
        except (ValueError, OSError, asyncio.TimeoutError):
            raise RuntimeError("无法连接 Clash Verge，请确认它正在运行") from None
        finally:
            if process.returncode is None:
                process.kill()
                await process.wait()
            if self._process is process:
                self._process = None

    async def refresh(self):
        async with self._lock:
            try:
                self.state = proxy_view(await self._request({"command": "state"}), self._group)
                if self.state["mode"] != "global":
                    self._group = self.state["group"]
            except (RuntimeError, OSError, ValueError):
                self.state = {"available": False, "phase": "未连接 Clash Verge，请启动后刷新", "groups": []}
            return self.state

    async def apply(self, action, value):
        async with self._lock:
            if action == "launch":
                await self._request({"command": "launch"})
                # Only the explicit launch action retries startup, with a bounded lifetime.
                deadline = asyncio.get_running_loop().time() + 12
                for attempt in range(8):
                    try:
                        remaining = deadline - asyncio.get_running_loop().time()
                        if remaining <= 0:
                            break
                        raw = await asyncio.wait_for(self._request({"command": "state"}), remaining)
                        self.state = proxy_view(raw, self._group)
                        if self.state["mode"] != "global":
                            self._group = self.state["group"]
                        return self.state
                    except (RuntimeError, OSError, ValueError, asyncio.TimeoutError):
                        if self._closed:
                            raise RuntimeError("代理面板已关闭") from None
                        await asyncio.sleep(.5)
                raise RuntimeError("Clash Verge 已启动，请稍后刷新状态")
            fresh = proxy_view(await self._request({"command": "state"}), self._group)
            self.state = fresh
            if action == "group":
                if fresh["mode"] == "global":
                    raise ValueError("全局模式使用 GLOBAL 出口组，请切换规则模式后选择其他代理组")
                if not isinstance(value, str) or value not in {group["name"] for group in fresh["groups"]}:
                    raise ValueError("代理组已改变，请刷新后选择")
                if value == fresh["group"]:
                    return self.state
                self._group = value
                self._path.write_text(json.dumps({"group": value}, ensure_ascii=False), encoding="utf-8")
                self.state = proxy_view(await self._request({"command": "state"}), self._group)
            elif action == "enabled":
                if type(value) is not bool:
                    raise ValueError("代理开关格式错误")
                mode = "rule" if value else "direct"
                if mode == fresh["mode"]:
                    return self.state
                self.state = proxy_view(await self._request({"command": "mode", "mode": mode}), self._group)
                if self.state["mode"] != mode:
                    raise RuntimeError("模式切换未生效，请刷新状态")
            elif action == "node":
                shown_group = fresh["group"]
                group = next((group for group in fresh["groups"] if group["name"] == shown_group), None)
                # Include the group shown when the user opened the dropdown, preventing stale selections.
                if (not isinstance(value, dict) or value.get("group") != shown_group or group is None
                        or not isinstance(value.get("node"), str) or value["node"] not in group["nodes"]):
                    raise ValueError("节点列表已改变，请刷新后选择")
                if value["node"] == fresh["current"]:
                    return self.state
                self.state = proxy_view(await self._request({"command": "select", "group": shown_group,
                                                            "node": value["node"]}), self._group)
                if self.state["current"] != value["node"]:
                    raise RuntimeError("节点切换未生效，请刷新状态")
            elif action == "test":
                if not fresh["group"]:
                    raise ValueError("没有可测试的代理组")
                self.state = proxy_view(await self._request({"command": "test", "group": fresh["group"]}), self._group)
            else:
                raise ValueError("不支持的代理操作")
            return self.state

    async def close(self):
        self._closed = True
        process = self._process
        if process and process.returncode is None:
            process.kill()
            await process.wait()
