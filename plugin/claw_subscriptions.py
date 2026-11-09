"""Invoke Clash Verge's own subscription commands through an on-demand local session.

Never implements a second subscription downloader or rewrites Verge's configuration index.
The native helper starts the app's WebView2 with a private loopback endpoint only after
the user requests an operation. No subscription URL or downloaded content is returned to the UI.
"""
import asyncio
import json
import re
import time
from pathlib import Path
from urllib.parse import urlsplit, parse_qs, unquote


class SubscriptionError(RuntimeError):
    pass


def subscription_url(value):
    if not isinstance(value, str) or not 1 <= len(value.strip()) <= 8192:
        raise ValueError("请粘贴有效的 Clash 订阅链接")
    value = value.strip()
    parsed = urlsplit(value)
    if parsed.scheme in ("clash", "clash-verge"):
        value = parse_qs(parsed.query).get("url", [""])[0]
        for _ in range(2):
            if urlsplit(value).scheme in ("http", "https"):
                break
            value = unquote(value)
        parsed = urlsplit(value)
    if (parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password
            or parsed.fragment or any(ord(char) < 32 for char in value)):
        raise ValueError("订阅链接须为 HTTP/HTTPS，或 Clash 导入链接")
    return value


class VergeSubscriptions:
    def __init__(self, control, settings_root):
        self._control = control
        self._root = Path(settings_root)
        self._closed = False
        self._socket = None
        self._http = None
        self._ready = (self._root / "verge-session.json").exists()
        self.view = {"connected": False, "phase": "可粘贴链接添加订阅，或刷新已有订阅", "items": [], "bootstrapNodes": []}

    async def _target(self):
        import aiohttp  # Already provided by Decky; imported only when this feature is used.
        if self._closed:
            raise SubscriptionError("订阅管理已关闭")
        try:
            record = await self._control._request({"command": "verge-session"})
            port = record["port"]
            if type(port) is not int or not 1024 <= port <= 65535:
                raise ValueError()
        except (RuntimeError, KeyError, ValueError):
            self._ready = False
            raise SubscriptionError("Clash Verge 已退出，请重新执行刚才的操作") from None
        if self._http is None or self._http.closed:
            self._http = aiohttp.ClientSession(trust_env=False)
        shown = False
        deadline = asyncio.get_running_loop().time() + 20
        while asyncio.get_running_loop().time() < deadline:
            try:
                owner = await self._control._request({"command": "verge-owner"})
                if owner.get("owned") is True:
                    async with self._http.get(f"http://127.0.0.1:{port}/json/list", timeout=aiohttp.ClientTimeout(total=2)) as response:
                        targets = await response.json(content_type=None)
                    pages = [target for target in targets if target.get("type") == "page" and
                             (urlsplit(target.get("url", "")).hostname == "tauri.localhost" or
                              target.get("url", "").startswith("tauri://localhost"))]
                    if len(pages) == 1:
                        endpoint = pages[0].get("webSocketDebuggerUrl", "")
                        parsed = urlsplit(endpoint)
                        if parsed.scheme != "ws" or parsed.hostname not in ("127.0.0.1", "localhost") or parsed.port != port:
                            raise SubscriptionError("本地订阅管理连接身份不匹配")
                        return endpoint, shown
                if not shown:
                    status = await self._control._request({"command": "verge-show"})
                    shown = status.get("shown") is True
            except SubscriptionError:
                raise
            except (RuntimeError, ValueError, KeyError, aiohttp.ClientError, asyncio.TimeoutError):
                pass
            await asyncio.sleep(.35)
        if shown:
            try:
                await self._control._request({"command": "verge-hide"})
            except (RuntimeError, OSError):
                pass
        raise SubscriptionError("Clash Verge 尚未就绪，请稍后重试")

    async def _invoke(self, command, args=None, timeout=30):
        import aiohttp
        # Only literals constructed in this module enter the application context.
        allowed = {"get_profiles", "get_sys_proxy", "get_verge_config", "import_profile", "update_profile",
                   "patch_profiles_config", "patch_profile", "patch_verge_config", "patch_clash_mode",
                   "read_profile_file", "save_profile_file", "enhance_profiles"}
        if command not in allowed:
            raise ValueError("不支持的订阅操作")
        endpoint, shown = await self._target()
        expression = ("(async()=>{for(let i=0;i<60&&!window.__TAURI_INTERNALS__?.invoke;i++)await new Promise(r=>setTimeout(r,100));"
                      "if(!window.__TAURI_INTERNALS__?.invoke)throw new Error('IPC unavailable');"
                      "return await window.__TAURI_INTERNALS__.invoke(" + json.dumps(command) + "," +
                      json.dumps(args or {}, ensure_ascii=False) + ");})()")
        try:
            async with self._http.ws_connect(endpoint, max_msg_size=33554432, timeout=5) as socket:
                self._socket = socket
                await socket.send_json({"id": 1, "method": "Runtime.evaluate", "params": {
                    "expression": expression, "awaitPromise": True, "returnByValue": True, "timeout": timeout * 1000}})
                async def receive():
                    async for message in socket:
                        if message.type == aiohttp.WSMsgType.TEXT:
                            data = json.loads(message.data)
                            if data.get("id") != 1:
                                continue
                            result = data.get("result", {})
                            if data.get("error") or result.get("exceptionDetails"):
                                # Native errors can include the URL/token. Keep them out of the panel and logs.
                                raise SubscriptionError("Clash Verge 未完成操作，请检查订阅链接和所选节点后重试")
                            return result.get("result", {}).get("value")
                    raise SubscriptionError("操作连接已结束，请重新打开面板查看结果后重试")
                return await asyncio.wait_for(receive(), timeout + 5)
        except (aiohttp.ClientError, asyncio.TimeoutError, ValueError):
            raise SubscriptionError("操作超时或连接中断，请重新打开面板确认结果后再重试") from None
        finally:
            self._socket = None
            if shown:
                try:
                    await self._control._request({"command": "verge-hide"})
                except (RuntimeError, OSError):
                    pass

    async def _profiles(self):
        profiles = await self._invoke("get_profiles")
        if not isinstance(profiles, dict) or not isinstance(profiles.get("items"), list):
            raise SubscriptionError("无法读取 Clash Verge 订阅列表")
        return profiles

    async def _read_view(self, message=None):
        profiles = await self._profiles()
        system = await self._invoke("get_sys_proxy")
        items = []
        for item in profiles["items"]:
            if item.get("type") not in ("remote", "local") or not isinstance(item.get("uid"), str):
                continue
            try:
                host = urlsplit(item.get("url") or "").hostname or "本地配置"
            except ValueError:
                host = "订阅来源"
            items.append({"uid": item["uid"], "name": str(item.get("name") or "未命名订阅")[:128],
                          "source": host, "remote": item.get("type") == "remote",
                          "updated": item.get("updated") if type(item.get("updated")) is int else None})
        self.view = {"connected": True, "phase": "订阅管理已连接", "current": profiles.get("current"),
                     "items": items, "systemProxy": isinstance(system, dict) and system.get("enable") is True,
                     "message": message, "bootstrapNodes": []}
        self._ready = True
        return self.view

    def decorate(self, proxy):
        view = dict(self.view)
        # Restrict suggestions to the actual selected group. No node is changed by reading state.
        group = next((group for group in proxy.get("groups", []) if group["name"] == proxy.get("group")), None)
        group_names = {group["name"] for group in proxy.get("groups", [])}
        view["bootstrapNodes"] = [node for node in (group["nodes"] if group else [])
                                  if node not in group_names and re.search(r"(?:^|[\s|｜(（])(?:订阅(?:专用|更新)|subscription|subscribe)", node, re.I)]
        return {**proxy, "subscriptions": view}

    async def refresh(self):
        if not self._ready:
            if self.view.get("connected"):
                self.view = {"connected": False, "phase": "可添加或刷新订阅", "items": [], "bootstrapNodes": []}
            return self.view
        async with self._control._lock:
            try:
                return await self._read_view(self.view.get("message"))
            except (RuntimeError, ValueError, OSError):
                self._ready = False
                self.view = {"connected": False, "phase": "可重新添加或刷新订阅", "items": [], "bootstrapNodes": []}
                return self.view

    async def _ensure_ready(self):
        # Initial panel reads never restart Verge. Only an explicit user operation initializes it.
        initialized = False
        try:
            try:
                await self._control._request({"command": "verge-session"})
            except (RuntimeError, OSError, ValueError):
                await self._control._request({"command": "verge-connect"})
                initialized = True
            self._ready = True
            await self._read_view()
        except (RuntimeError, OSError, ValueError):
            self._ready = False
            raise SubscriptionError("Clash Verge 尚未准备好，请稍后重试刚才的操作") from None
        finally:
            if initialized:
                try:
                    await self._control._request({"command": "verge-hide"})
                except (RuntimeError, OSError):
                    pass

    async def _activate(self, uid):
        profiles = await self._profiles()
        if not isinstance(uid, str) or uid not in {item.get("uid") for item in profiles["items"] if item.get("type") in ("remote", "local")}:
            raise ValueError("订阅列表已改变，请刷新后选择")
        if profiles.get("current") != uid:
            if await self._invoke("patch_profiles_config", {"profiles": {"current": uid}}) is not True:
                raise SubscriptionError("Clash Verge 未切换订阅，请稍后重试")
        if (await self._profiles()).get("current") != uid:
            raise SubscriptionError("当前订阅已改变，请刷新状态")

    async def _backup(self, item):
        uid = item["uid"]
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", uid):
            raise ValueError("订阅标识不可用")
        content = await self._invoke("read_profile_file", {"index": uid})
        if not isinstance(content, str) or not content.strip():
            raise SubscriptionError("无法备份已有订阅，已停止更新")
        folder = self._root / "subscription-backups" / (time.strftime("%Y%m%d-%H%M%S") + f"-{time.time_ns() % 1000000:06d}")
        folder.mkdir(parents=True, exist_ok=False)
        (folder / (uid + ".yaml")).write_text(content, encoding="utf-8")
        # Retain only the native profile's settings for rollback; URL already stays in Verge.
        (folder / "profile-settings.json").write_text(json.dumps({"uid": uid, "option": item.get("option"),
                                                                  "selected": item.get("selected"), "updated": item.get("updated")},
                                                                 ensure_ascii=False), encoding="utf-8")
        return content

    async def perform(self, action, value):
        async with self._control._lock:
            if action not in {"import", "profile", "system_proxy", "mode", "tun", "prepare", "update"}:
                raise ValueError("不支持的 Clash 操作")
            # Reject invalid input before preparing or restarting the native application.
            if action == "import":
                value = subscription_url(value)
            if action == "mode" and value not in ("rule", "global", "direct"):
                raise ValueError("请选择规则、全局或直连模式")
            if action in ("system_proxy", "tun") and type(value) is not bool:
                raise ValueError("代理开关格式错误")
            if action in ("mode", "tun", "system_proxy"):
                # Same-value callbacks must not initialize WebView or reapply native settings.
                try:
                    running = await self._control._request({"command": "state"})
                except (RuntimeError, OSError, ValueError):
                    running = {}
                actual = running.get("systemProxy" if action == "system_proxy" else action)
                if actual is not None and actual == value:
                    return self.view
            await self._ensure_ready()
            if action == "import":
                url = subscription_url(value)
                before = await self._profiles()
                existing = next((item for item in before["items"] if item.get("type") == "remote" and item.get("url") == url), None)
                if existing:
                    uid = existing["uid"]
                    note = "该订阅已存在，已选择它；在当前节点中选可用节点并开启系统代理后刷新"
                else:
                    # The first bootstrap download must work even when the current proxy is broken.
                    try:
                        await self._invoke("import_profile", {"url": url, "option": {"with_proxy": False, "self_proxy": False,
                                                                                     "timeout_seconds": 20}}, timeout=90)
                    except SubscriptionError:
                        raise SubscriptionError("初始添加未完成：请核对链接；旧节点不可用时，可先关闭系统代理和 TUN 再试。重新打开面板可查看是否已添加") from None
                    after = await self._profiles()
                    old_ids = {item.get("uid") for item in before["items"]}
                    added = [item for item in after["items"] if item.get("type") == "remote" and item.get("url") == url and item.get("uid") not in old_ids]
                    if len(added) != 1:
                        raise SubscriptionError("导入结果未确认，请刷新订阅列表后查看，避免重复导入")
                    uid = added[0]["uid"]
                    note = "订阅已添加并启用；在当前节点中选订阅专用节点并开启系统代理后，点击刷新订阅"
                await self._activate(uid)
                return await self._read_view(note)
            if action == "profile":
                await self._activate(value)
                return await self._read_view("已切换当前订阅")
            if action == "system_proxy":
                if type(value) is not bool:
                    raise ValueError("系统代理开关格式错误")
                await self._invoke("patch_verge_config", {"payload": {"enable_system_proxy": value}})
                result = await self._read_view("系统代理设置已交给 Clash Verge")
                if result["systemProxy"] != value:
                    raise SubscriptionError("系统代理实际状态未达到所选值，请在 Clash Verge 检查")
                return result
            if action in ("mode", "tun"):
                if action == "mode":
                    await self._invoke("patch_clash_mode", {"payload": value})
                else:
                    await self._invoke("patch_verge_config", {"payload": {"enable_tun_mode": value}}, timeout=45)
                actual = await self._control._request({"command": "state"})
                if actual.get(action) != value:
                    label = "代理模式" if action == "mode" else "TUN 模式"
                    raise SubscriptionError(label + "未达到所选状态，请在 Clash Verge 检查服务或权限")
                return await self._read_view("代理模式已切换" if action == "mode" else "TUN 模式已开启" if value else "TUN 模式已关闭")
            if action == "prepare":
                if not isinstance(value, dict) or not isinstance(value.get("group"), str) or not isinstance(value.get("node"), str):
                    raise ValueError("请选择订阅专用节点")
                raw = await self._control._request({"command": "state"})
                groups = raw.get("proxies", {})
                group = groups.get(value["group"], {})
                if group.get("type") != "Selector" or value["node"] not in group.get("all", []):
                    raise ValueError("订阅节点列表已改变，请刷新后选择")
                await self._invoke("patch_clash_mode", {"payload": "rule"})
                await self._invoke("patch_verge_config", {"payload": {"enable_system_proxy": True}})
                selected = []
                candidates = [name for name, item in groups.items() if name != "GLOBAL" and
                              item.get("type") == "Selector" and value["node"] in item.get("all", []) and len(item.get("all", [])) > 1]
                main = max(candidates, key=lambda name: sum(name in item.get("all", []) for item in groups.values()), default=value["group"])
                # Pin a dedicated subscription group too, so a separate rule cannot route elsewhere.
                for name, item in groups.items():
                    if (name in (value["group"], main) or re.search(r"订阅|subscription|subscribe", name, re.I)) and item.get("type") == "Selector" and value["node"] in item.get("all", []):
                        await self._control._request({"command": "select", "group": name, "node": value["node"]})
                        selected.append({"name": name, "now": value["node"]})
                profiles = await self._profiles()
                current = next((item for item in profiles["items"] if item.get("uid") == profiles.get("current")), None)
                if current:
                    changed = {entry["name"] for entry in selected}
                    keep = [entry for entry in (current.get("selected") or []) if entry.get("name") not in changed]
                    await self._invoke("patch_profile", {"index": current["uid"], "profile": {"selected": keep + selected}})
                self._control._group = main if value["group"] == "GLOBAL" else value["group"]
                self._control._path.write_text(json.dumps({"group": self._control._group}, ensure_ascii=False), encoding="utf-8")
                result = await self._read_view("订阅节点已选中，规则模式和系统代理已开启；现在可刷新订阅")
                if result["systemProxy"] is not True:
                    raise SubscriptionError("节点已选中，但系统代理未开启成功；请核对系统代理开关")
                return result
            if action == "update":
                profiles = await self._profiles()
                item = next((item for item in profiles["items"] if item.get("uid") == profiles.get("current") and item.get("type") == "remote"), None)
                if not item:
                    raise ValueError("当前不是远程订阅，请先导入或选择一个订阅")
                running = await self._control._request({"command": "state"})
                if running.get("mode") not in ("rule", "global"):
                    raise SubscriptionError("请先选择规则或全局模式，并选一个可用节点，再刷新订阅")
                system = await self._invoke("get_sys_proxy")
                if not isinstance(system, dict) or system.get("enable") is not True:
                    raise SubscriptionError("请在上方开启系统代理，并在当前节点中选可用节点，再刷新订阅")
                previous = await self._backup(item)
                try:
                    # Use the running core explicitly; retain all existing extensions and update settings.
                    options = {**(item.get("option") or {}), "with_proxy": True, "self_proxy": True, "timeout_seconds": 20}
                    await self._invoke("update_profile", {"index": item["uid"], "option": options}, timeout=90)
                    if (await self._profiles()).get("current") != item["uid"]:
                        raise SubscriptionError("订阅已下载，但当前订阅在更新中发生变化；请刷新状态")
                    # update_profile can swallow a core reload error; this native command propagates validation failure.
                    await self._invoke("enhance_profiles", timeout=45)
                except SubscriptionError:
                    restored = False
                    try:
                        latest = await self._profiles()
                        if latest.get("current") == item["uid"]:
                            await self._invoke("save_profile_file", {"index": item["uid"], "fileData": previous}, timeout=45)
                            await self._invoke("patch_profile", {"index": item["uid"], "profile": {
                                "option": item.get("option"), "selected": item.get("selected"), "updated": item.get("updated")}})
                            await self._invoke("enhance_profiles", timeout=45)
                            restored = True
                    except (RuntimeError, ValueError, OSError):
                        pass
                    raise SubscriptionError("刷新失败，已恢复更新前的订阅；可换另一个订阅专用节点再试" if restored else
                                            "刷新未完成，备份已保留；请刷新状态查看，或在 Clash Verge 恢复") from None
                return await self._read_view("订阅已刷新并加载；可查看新节点列表，选择日常使用节点")
            raise ValueError("不支持的订阅操作")

    async def close(self):
        self._closed = True
        if self._socket is not None:
            await self._socket.close()
        if self._http is not None:
            await self._http.close()
