"""Talk to the running Steam client through its own UI process (CEF remote debugging on 127.0.0.1:8080).

Steam's library UI exposes SteamClient.Apps (AddShortcut, SetShortcutName, SetCustomArtworkForApp, ...) and the
collection store in its "SharedJSContext" page; calling them there changes the library at once, without restarting
Steam. Only fixed expressions built in this module are evaluated; values go in as JSON literals.
Standard library only (the agent's embedded Python has no websocket package).
"""
import base64
import json
import os
import socket
import struct
import urllib.request

PORT = 8080


class CdpError(RuntimeError):
    pass


def _target():
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{PORT}/json/list", timeout=3) as r:
            targets = json.load(r)
    except (OSError, ValueError):
        return None
    for t in targets:
        url = t.get("webSocketDebuggerUrl", "")
        if t.get("title") == "SharedJSContext" and url.startswith(f"ws://127.0.0.1:{PORT}/"):
            return url
    return None


def available():
    return _target() is not None


def evaluate(expression, timeout=30):
    """Runtime.evaluate (await promise, value returned as JSON) in Steam's SharedJSContext."""
    url = _target()
    if url is None:
        raise CdpError("Steam 界面接口不可用")
    host, path = url[len("ws://"):].split("/", 1)
    sock = socket.create_connection(("127.0.0.1", PORT), timeout=timeout)
    try:
        key = base64.b64encode(os.urandom(16)).decode()
        sock.sendall((f"GET /{path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                      f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            chunk = sock.recv(4096)
            if not chunk:
                raise CdpError("Steam 界面接口断开")
            buf += chunk
        head, buf = buf.split(b"\r\n\r\n", 1)
        if b" 101 " not in head.split(b"\r\n", 1)[0]:
            raise CdpError("Steam 界面接口拒绝连接")
        payload = json.dumps({"id": 1, "method": "Runtime.evaluate", "params": {
            "expression": expression, "awaitPromise": True, "returnByValue": True, "timeout": timeout * 1000}}).encode()
        n = len(payload)
        header = bytes([0x81]) + (bytes([0x80 | n]) if n < 126 else
                                  bytes([0x80 | 126]) + struct.pack(">H", n) if n < 65536 else
                                  bytes([0x80 | 127]) + struct.pack(">Q", n))
        mask = os.urandom(4)
        sock.sendall(header + mask + bytes(b ^ mask[i & 3] for i, b in enumerate(payload)))

        def need(k):
            nonlocal buf
            while len(buf) < k:
                chunk = sock.recv(65536)
                if not chunk:
                    raise CdpError("Steam 界面接口断开")
                buf += chunk

        while True:
            need(2)
            opcode, length, offset = buf[0] & 0x0F, buf[1] & 0x7F, 2
            if length == 126:
                need(4)
                length, offset = struct.unpack(">H", buf[2:4])[0], 4
            elif length == 127:
                need(10)
                length, offset = struct.unpack(">Q", buf[2:10])[0], 10
            need(offset + length)
            data, buf = buf[offset:offset + length], buf[offset + length:]
            if opcode == 8:
                raise CdpError("Steam 界面接口断开")
            if opcode != 1:
                continue
            message = json.loads(data)
            if message.get("id") != 1:
                continue
            result = message.get("result") or {}
            if message.get("error") or result.get("exceptionDetails"):
                detail = (result.get("exceptionDetails") or {}).get("exception", {}).get("description") or message.get("error")
                raise CdpError(f"Steam 没完成：{str(detail)[:200]}")
            return (result.get("result") or {}).get("value")
    except (OSError, ValueError) as e:
        raise CdpError(f"Steam 界面接口出错：{type(e).__name__}") from None
    finally:
        sock.close()


# ELibraryAssetType in Steam's UI
ASSET_TYPES = {"p": 0, "hero": 1, "logo": 2, "header": 3}

_WAIT = "const wait=ms=>new Promise(r=>setTimeout(r,ms));"


def add_shortcut(name, exe, start_dir, args):
    """New non-Steam shortcut; returns Steam's appid for it (Steam picks the id)."""
    j = json.dumps
    return evaluate("(async()=>{" + _WAIT +
                    f"const id=await SteamClient.Apps.AddShortcut({j(name)},{j(exe)},{j(args)},'');"
                    "for(let i=0;i<40&&!appStore.GetAppOverviewByAppID(id);i++)await wait(100);"
                    f"SteamClient.Apps.SetShortcutName(id,{j(name)});"
                    f"SteamClient.Apps.SetShortcutStartDir(id,{j(chr(34) + start_dir + chr(34))});"
                    + (f"SteamClient.Apps.SetShortcutLaunchOptions(id,{j(args)});" if args else "") +
                    "return id;})()")


def update_shortcut(appid, name):
    return evaluate(f"(async()=>{{SteamClient.Apps.SetShortcutName({int(appid)},{json.dumps(name)});return true;}})()")


def set_shortcut_target(appid, exe, start_dir):
    """A non-Steam shortcut's program and its start folder (Steam keeps both quoted)."""
    j, q = json.dumps, chr(34)
    return evaluate(f"(async()=>{{SteamClient.Apps.SetShortcutExe({int(appid)},{j(q + exe + q)});"
                    f"SteamClient.Apps.SetShortcutStartDir({int(appid)},{j(q + start_dir + q)});return true;}})()")


def exists(appid):
    return bool(evaluate(f"!!appStore.GetAppOverviewByAppID({int(appid)})"))


def set_artwork(appid, kind, path):
    """One library image from a local file (png / jpg); path None clears Steam's custom image of that kind."""
    t = ASSET_TYPES[kind]
    if not path:
        return evaluate(f"(async()=>{{await SteamClient.Apps.ClearCustomArtworkForApp({int(appid)},{t});return true;}})()")
    ext = os.path.splitext(path)[1].lower()
    fmt = "png" if ext == ".png" else "jpg"
    with open(path, "rb") as f:
        data = base64.b64encode(f.read()).decode()
    return evaluate(f"(async()=>{{await SteamClient.Apps.SetCustomArtworkForApp({int(appid)},{json.dumps(data)},'{fmt}',{t});return true;}})()",
                    timeout=60)


def set_icon(appid, path):
    return evaluate(f"(async()=>{{SteamClient.Apps.SetShortcutIcon({int(appid)},{json.dumps(path or '')});return true;}})()")


def add_to_collections(appid, collection_ids, new_names):
    """Puts the app into user collections (by id) and into new ones (by name, reusing one with that name).
    Returns {name: id} for the named ones."""
    j = json.dumps
    return evaluate("(async()=>{" + _WAIT +
                    f"const id={int(appid)},ids={j(list(collection_ids))},names={j(list(new_names))},made={{}};"
                    "for(let i=0;i<40&&!appStore.GetAppOverviewByAppID(id);i++)await wait(100);"
                    "const ov=appStore.GetAppOverviewByAppID(id);if(!ov)throw new Error('app not in library');"
                    "for(const c of ids){if(collectionStore.GetCollection(c))collectionStore.AddOrRemoveApp([id],true,c);}"
                    "for(const n of names){let c=collectionStore.userCollections.find(x=>x.displayName===n);"
                    "if(c){collectionStore.AddOrRemoveApp([id],true,c.id);}"
                    "else{c=collectionStore.NewUnsavedCollection(n,undefined,[ov]);await c.Save();}"
                    "made[n]=c.id;}"
                    "return made;})()")


def user_collections():
    return evaluate("collectionStore.userCollections.filter(c=>c.bAllowsDragAndDrop&&(c.id==='favorite'||c.id.startsWith('uc-')))"
                    ".map(c=>({id:c.id,name:c.displayName,added:c.allApps.map(a=>String(a.appid))}))") or []


# ---------------- per-game properties (the library's game settings page) ----------------

def app_properties(appid):
    """What Steam's Properties dialog shows for an app (also a non-Steam shortcut)."""
    return evaluate("(async()=>{" + _WAIT +
                    f"const id={int(appid)};"
                    "const d=await new Promise(r=>{const h=SteamClient.Apps.RegisterForAppDetails(id,x=>{h.unregister();r(x)});setTimeout(()=>r(null),4000)});"
                    "if(!d)return null;"
                    "return {launchOptions:d.strLaunchOptions||'',overlay:!!d.bOverlayEnabled,cloudAvailable:!!d.bCloudAvailable,"
                    "cloud:!!d.bCloudEnabledForApp,folder:d.strInstallFolder||'',size:d.lDiskUsageBytes||0,"
                    "installed:!!d.bHasAnyLocalContent,favorite:collectionStore.BIsFavorite(id),hidden:collectionStore.BIsHidden(id)};"
                    "})()", timeout=15)


def set_app_property(appid, key, value):
    """One property; toggles are only flipped when they differ, so repeating a request is harmless."""
    j, a = json.dumps, int(appid)
    shortcut = a >= 0x80000000
    body = {
        "launchOptions": (f"SteamClient.Apps.{'SetShortcutLaunchOptions' if shortcut else 'SetAppLaunchOptions'}(id,{j(str(value))});"),
        "overlay": f"if(!!d.bOverlayEnabled!=={j(bool(value))})SteamClient.Apps.ToggleEnableSteamOverlayForApp(id);",
        "cloud": f"if(d.bCloudAvailable&&!!d.bCloudEnabledForApp!=={j(bool(value))})SteamClient.Apps.ToggleAppSteamCloudEnabled(id);",
        "favorite": f"collectionStore.SetAppsAsFavorite([id],{j(bool(value))});",
        "hidden": f"collectionStore.SetAppsAsHidden([id],{j(bool(value))});",
        "verify": "SteamClient.Apps.VerifyApp(id);",
    }.get(key)
    if body is None:
        raise ValueError("不认识的游戏设置")
    return evaluate("(async()=>{" + _WAIT + f"const id={a};"
                    "const d=await new Promise(r=>{const h=SteamClient.Apps.RegisterForAppDetails(id,x=>{h.unregister();r(x)});setTimeout(()=>r({}),4000)});"
                    + body + "return true;})()", timeout=15)
