"""Local (non-Steam) games added from ClawLibrary: artwork from SteamGridDB, and the same game written
into Steam as a non-Steam shortcut with its artwork and collection.

Everything lives in data\\local-games: games.json (read directly by ClawLibrary at load), art\\ (the
chosen images, named the way Steam's grid folder names them: <appid>p, <appid>_hero, <appid>_logo,
<appid>, plus <appid>_icon), thumbs\\ (candidate previews) and the user's optional SteamGridDB API key (never
logged; without one the website's own search endpoints are used).

A game's id is the Steam shortcut appid it will get (crc32 of exe + name with the top bit set, the way
Steam ROM Manager does it), fixed when the game is added. So once it is in shortcuts.vdf, the library's
automatic import of new shortcuts sees the same id and the game appears only once.

Steam is not touched while adding. sync() writes every pending game in one go: Steam is shut down
(Steam rewrites shortcuts.vdf and its collections from memory, so they cannot be changed while it
runs), shortcuts.vdf gets the new entries, the grid folder the images, the collection store the new
members, and Steam is started again the way it was started at sign-in. Collections are cloud-synced,
so state() checks afterwards whether Steam kept them.
"""
from concurrent.futures import ThreadPoolExecutor, wait
import hashlib
import importlib.util
import json
import logging
import os
from pathlib import Path
import random
import re
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import winreg
import zlib

SGDB = "https://www.steamgriddb.com/api/v2"
# The website's own (undocumented) endpoints: no key needed. Used when the user has not set a key.
SGDB_PUBLIC = "https://www.steamgriddb.com/api/public"
MAX_IMAGE = 40 * 1024 * 1024
# Steam's own UI interface (live changes without a restart); loaded from next to this file like the service loads us
_cdp_spec = importlib.util.spec_from_file_location("claw_toolbox_steam_cdp", Path(__file__).with_name("claw_steam_cdp.py"))
cdp = importlib.util.module_from_spec(_cdp_spec)
_cdp_spec.loader.exec_module(cdp)
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
STEAM_TASK = r"\ClawToolbox\SteamAdmin"
# Art kinds, as Steam names the files in userdata\<id>\config\grid (the icon is not a grid file: shortcuts.vdf's
# "icon" field points at it, so it is copied there as <appid>_icon too)
KINDS = {"p": "p", "header": "", "hero": "_hero", "logo": "_logo", "icon": "_icon"}
ID_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+*"


def shortcut_appid(exe, name):
    return str((zlib.crc32((exe + name).encode("utf-8")) & 0xFFFFFFFF) | 0x80000000)


def clean_title(name):
    """Folder names like "[月ノ森汉化组]甜蜜女友 3 - Ver1.0.0" → "甜蜜女友 3" for searching."""
    s = re.sub(r"[\[【(（][^\]】)）]*[\]】)）]", " ", name.replace("_", " "))
    s = re.sub(r"(?i)\b(v(er)?\.?\s*\d+(\.\d+)*[a-z]?|build\s*\d+)\b", " ", s)
    s = re.sub(r"(汉化|中文|硬盘|免安装|完整|豪华|特装|官中|版)+$", " ", s.strip())
    s = re.sub(r"\s*[-–—~]+\s*$", "", re.sub(r"\s+", " ", s).strip())
    return s.strip() or name.strip()


# ---------------- Steam files ----------------

def steam_root():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            path = winreg.QueryValueEx(key, "SteamPath")[0]
        return Path(path) if path and Path(path).is_dir() else None
    except OSError:
        return None


def steam_user(root):
    """The account whose localconfig.vdf changed last (the same choice ClawLibrary makes)."""
    if root is None:
        return None
    best = None
    for d in (root / "userdata").glob("*"):
        cfg = d / "config" / "localconfig.vdf"
        if cfg.is_file() and (best is None or cfg.stat().st_mtime > best[0]):
            best = (cfg.stat().st_mtime, d)
    return best[1] if best else None


def steam_running():
    out = subprocess.run(["tasklist", "/fi", "imagename eq steam.exe", "/nh"], capture_output=True, creationflags=NO_WINDOW)
    return b"steam.exe" in out.stdout.lower()


class Vdf:
    """Binary KeyValues (shortcuts.vdf): a node is a list of [type, key, value]; maps nest lists."""

    @staticmethod
    def parse(data):
        pos = 0

        def string():
            nonlocal pos
            end = data.index(b"\0", pos)
            s = data[pos:end].decode("utf-8", "replace")
            pos = end + 1
            return s

        def node():
            nonlocal pos
            items = []
            while pos < len(data):
                kind = data[pos]
                pos += 1
                if kind == 0x08:
                    return items
                key = string()
                if kind == 0x00:
                    items.append([0x00, key, node()])
                elif kind == 0x01:
                    items.append([0x01, key, string()])
                elif kind == 0x02:
                    items.append([0x02, key, int.from_bytes(data[pos:pos + 4], "little")])
                    pos += 4
                elif kind == 0x07:
                    items.append([0x07, key, int.from_bytes(data[pos:pos + 8], "little")])
                    pos += 8
                else:
                    raise ValueError(f"shortcuts.vdf: unknown type {kind}")
            return items

        return node()

    @staticmethod
    def dump(items):
        out = bytearray()

        def write(nodes):
            for kind, key, value in nodes:
                out.append(kind)
                out.extend(key.encode("utf-8") + b"\0")
                if kind == 0x00:
                    write(value)
                    out.append(0x08)
                elif kind == 0x01:
                    out.extend(value.encode("utf-8") + b"\0")
                elif kind == 0x02:
                    out.extend((value & 0xFFFFFFFF).to_bytes(4, "little"))
                elif kind == 0x07:
                    out.extend(value.to_bytes(8, "little"))

        write(items)
        out.append(0x08)
        return bytes(out)

    @staticmethod
    def get(items, key):
        for kind, k, value in items:
            if k.lower() == key.lower():
                return value
        return None


def unquote(path):
    return (path or "").strip().strip('"')


def launch_target(exe, args):
    """The game a shortcut really starts: its own exe, or the one a launcher is handed (Playhub's
    GameSession.exe --game "<exe>"), so per-game settings follow the game's window, not the launcher."""
    exe = unquote(exe)
    found = re.search(r'--game\s+(?:"([^"]+)"|(\S+))', args or "")
    target = found and (found.group(1) or found.group(2))
    return target if target and target.lower().endswith(".exe") else exe


def exe_key(path):
    return unquote(path).replace("/", "\\").lower()


def read_shortcuts(user):
    path = user / "config" / "shortcuts.vdf"
    if not path.is_file():
        return [[0x00, "shortcuts", []]]
    return Vdf.parse(path.read_bytes())


def shortcut_entries(tree):
    shortcuts = Vdf.get(tree, "shortcuts")
    return shortcuts if isinstance(shortcuts, list) else []


def new_shortcut(game, template):
    """A shortcuts.vdf entry; field order copied from an existing entry so Steam sees what it writes itself."""
    values = {
        "appid": int(game["id"]), "AppName": game["name"], "Exe": f'"{game["exe"]}"', "StartDir": f'"{game["dir"]}"',
        "icon": "", "ShortcutPath": "", "LaunchOptions": game.get("args", ""), "IsHidden": 0, "AllowDesktopConfig": 1,
        "AllowOverlay": 1, "OpenVR": 0, "Devkit": 0, "DevkitGameID": "", "DevkitOverrideAppID": 0, "LastPlayTime": 0,
        "FlatpakAppID": "", "sortas": "",
    }
    kinds = {k: (0x02 if isinstance(v, int) else 0x01) for k, v in values.items()}
    fields = []
    order = [k for kind, k, _ in template if kind != 0x00] if template else list(values)
    for key in order:
        match = next((k for k in values if k.lower() == key.lower()), None)
        if match is not None:
            fields.append([kinds[match], match if not template else key, values.pop(match)])
    fields += [[kinds[k], k, v] for k, v in values.items()]
    fields.append([0x00, "tags", []])
    return fields


# ---------------- Steam collections (config\cloudstorage) ----------------

def read_collections(user):
    """User collections that accept games by hand: [{id, name, added}]."""
    result = []
    path = user / "config" / "cloudstorage" / "cloud-storage-namespace-1.json" if user else None
    if not path or not path.is_file():
        return result
    for key, meta in json.loads(path.read_text(encoding="utf-8")):
        if not key.startswith("user-collections.") or meta.get("is_deleted") or "value" not in meta:
            continue
        value = json.loads(meta["value"])
        if value.get("filterSpec") or not value.get("name") or value.get("id") == "hidden":
            continue
        added = set(value.get("added", [])) - set(value.get("removed", []))
        result.append({"id": value["id"], "name": value["name"], "added": [str(a & 0xFFFFFFFF) for a in added if isinstance(a, int)]})
    result.sort(key=lambda c: (c["id"] != "favorite", c["name"]))
    return result


def write_collections(user, wanted):
    """wanted: {collection id or "new:<name>": [appid str]}. Returns {"new:<name>": new id}. Steam must be closed."""
    folder = user / "config" / "cloudstorage"
    path = folder / "cloud-storage-namespace-1.json"
    spaces_path = folder / "cloud-storage-namespaces.json"
    modified_path = folder / "cloud-storage-namespace-1.modified.json"
    entries = json.loads(path.read_text(encoding="utf-8")) if path.is_file() else []
    spaces = json.loads(spaces_path.read_text(encoding="utf-8")) if spaces_path.is_file() else []
    modified = json.loads(modified_path.read_text(encoding="utf-8")) if modified_path.is_file() else []
    current = max([int(v.get("version", 0)) for _, v in entries] + [int(s[1]) for s in spaces if s[0] == 1] + [0])
    version = str(current + 1)
    now = int(time.time())
    by_key = {k: v for k, v in entries}
    created = {}
    for target, appids in wanted.items():
        ids = [int(a) for a in appids]
        if target.startswith("new:"):
            name = target[4:]
            same = [v for k, v in entries if k.startswith("user-collections.") and "value" in v and not v.get("is_deleted")
                    and json.loads(v["value"]).get("name") == name]
            if same:
                target = json.loads(same[0]["value"])["id"]
            else:
                ident = "uc-" + "".join(random.choice(ID_ALPHABET) for _ in range(12))
                created["new:" + name] = ident
                key = "user-collections." + ident
                meta = {"key": key, "timestamp": now, "value": json.dumps({"id": ident, "name": name, "added": ids, "removed": []},
                        ensure_ascii=False, separators=(",", ":")), "version": version}
                entries.append([key, meta])
                by_key[key] = meta
                if key not in modified:
                    modified.append(key)
                continue
        key = "user-collections." + target
        meta = by_key.get(key)
        if meta is None or "value" not in meta:
            continue
        value = json.loads(meta["value"])
        value["added"] = list(value.get("added", [])) + [a for a in ids if a not in value.get("added", [])]
        value["removed"] = [a for a in value.get("removed", []) if a not in ids]
        meta.update(timestamp=now, version=version,
                    value=json.dumps(value, ensure_ascii=False, separators=(",", ":")))
        if key not in modified:
            modified.append(key)
    spaces = [s for s in spaces if s[0] != 1] + [[1, version]]
    folder.mkdir(parents=True, exist_ok=True)
    for target, value in ((path, entries), (spaces_path, spaces), (modified_path, modified)):
        tmp = target.with_suffix(".claw-tmp")
        tmp.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
        os.replace(tmp, target)
    return created


def live_or_file_collections(user):
    """Steam's collections as the running client has them (changes made live show at once); the cloud-storage file
    when Steam is closed or its UI interface is off."""
    if cdp.available():
        try:
            live = cdp.user_collections()
            live.sort(key=lambda c: (c["id"] != "favorite", c["name"]))
            return live
        except cdp.CdpError:
            pass
    return read_collections(user) if user else []


# ---------------- the store ----------------

class LocalGames:
    def __init__(self, data, log=logging.info, game_running=None, fetch=None):
        self.dir = Path(data) / "local-games"
        self.path = self.dir / "games.json"
        self.art = self.dir / "art"
        self.thumbs = self.dir / "thumbs"
        self.key_path = self.dir / "steamgriddb.key"
        self.log = log
        self.game_running = game_running or (lambda: False)
        self.fetch = fetch or self._fetch
        self._lock = threading.RLock()
        self.job = {"phase": "", "done": 0, "total": 0}
        self.last_sync = None
        self._cache = {}

    # ---- files ----
    def _load(self):
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"))
            return value if isinstance(value, list) else []
        except (OSError, ValueError):
            return []

    def _save(self, games):
        self.dir.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps(games, ensure_ascii=False, indent=1), encoding="utf-8")
        os.replace(tmp, self.path)

    def _key(self):
        try:
            return self.key_path.read_text(encoding="utf-8").strip() or None
        except OSError:
            return None

    # ---- network ----
    @staticmethod
    def _fetch(url, headers=None, limit=MAX_IMAGE, data=None, timeout=20, tries=3):
        """GET (or POST json when data is given), three tries: SteamGridDB's CDN drops TLS handshakes now and then."""
        body = None
        for attempt in range(tries):
            request = urllib.request.Request(url, data=data, headers={"User-Agent": "Mozilla/5.0 ClawToolbox", **(headers or {})})
            try:
                with urllib.request.urlopen(request, timeout=timeout) as r:
                    body = r.read(limit + 1)
                break
            except urllib.error.HTTPError:
                raise
            except OSError:
                if attempt == tries - 1:
                    raise
                time.sleep(0.4 * (attempt + 1))
        if len(body) > limit:
            raise ValueError("too large")
        return body

    def _json(self, url, headers=None, payload=None, quick=False):
        """quick: the Steam store from China is sometimes very slow; one short try, the search does without it."""
        data = json.dumps(payload).encode() if payload is not None else None
        hdrs = {**(headers or {}), **({"Content-Type": "application/json"} if data else {})}
        if quick:
            return json.loads(self.fetch(url, hdrs, 4 * 1024 * 1024, data, timeout=6, tries=1))
        return json.loads(self.fetch(url, hdrs, 4 * 1024 * 1024, data) if data else self.fetch(url, hdrs, 4 * 1024 * 1024))

    def _official(self, path):
        """SteamGridDB's documented API with the user's own key."""
        try:
            body = self._json(SGDB + path, {"Authorization": "Bearer " + self._key()})
        except urllib.error.HTTPError as e:
            if e.code in (401, 403):
                raise PermissionError("密钥无效")
            if e.code == 404:
                return []
            raise
        return (body.get("data") or []) if body.get("success", False) else []

    def _sgdb_search(self, term):
        """SteamGridDB games for a name: [{id, name, year, source}]. Without a key: the website's own search."""
        if self._key():
            found = self._official("/search/autocomplete/" + urllib.parse.quote(term))
        else:
            body = self._json(SGDB_PUBLIC + "/search/autocomplete?" + urllib.parse.urlencode({"term": term}))
            found = (body.get("data") or []) if body.get("success") else []
        return [{"id": str(g["id"]), "name": g.get("name", ""), "source": "sgdb",
                 "year": time.gmtime(g["release_date"]).tm_year if g.get("release_date") else None,
                 "onSteam": "steam" in (g.get("types") or [])}
                for g in found if isinstance(g, dict) and g.get("id")]

    def _assets(self, match, kind):
        """One kind of image for a match: [{url, thumb}] (thumb = remote preview url). Cached per match."""
        key = (match["source"], str(match["id"]), kind)
        if key in self._cache:
            return self._cache[key]
        ident = str(match["id"])
        items = []
        if match["source"] == "steam":
            items = self._store_assets(ident).get(kind, [])
        elif self._key():
            path = {"p": f"/grids/game/{ident}?dimensions=600x900,342x482,660x930&types=static&nsfw=false&humor=false",
                    "header": f"/grids/game/{ident}?dimensions=920x430,460x215&types=static&nsfw=false&humor=false",
                    "hero": f"/heroes/game/{ident}?types=static&nsfw=false&humor=false",
                    "logo": f"/logos/game/{ident}?types=static&nsfw=false&humor=false",
                    "icon": f"/icons/game/{ident}?types=static&nsfw=false&humor=false"}[kind]
            items = self._official(path)
        else:
            asset_type = {"p": "grid", "header": "grid", "hero": "hero", "logo": "logo", "icon": "icon"}[kind]
            body = self._json(SGDB_PUBLIC + "/search/assets", payload={"asset_type": asset_type, "game_id": [int(ident)], "page": 0, "limit": 48})
            items = ((body.get("data") or {}).get("assets") or []) if body.get("success") else []
            items = [i for i in items if not i.get("nsfw") and not i.get("humor") and not i.get("is_animated") and not i.get("is_deleted")]
            if kind == "p":
                items = [i for i in items if i.get("height", 0) > i.get("width", 0)]
            elif kind == "header":
                items = [i for i in items if i.get("width", 0) > 1.6 * i.get("height", 1)]
        # w / h: the image's own size, shown under each candidate
        result = [{"url": i["url"], "thumb": i.get("thumb") or i["url"], "w": i.get("width") or i.get("w"), "h": i.get("height") or i.get("h")}
                  for i in items if isinstance(i, dict) and str(i.get("url", "")).startswith("https://")]
        self._cache[key] = result
        return result

    def _store_assets(self, appid):
        """A Steam store app's library images. Newer ones sit under hashed paths, which the store's item API names."""
        request = {"ids": [{"appid": int(appid)}], "context": {"language": "schinese", "country_code": "CN"},
                   "data_request": {"include_assets": True}}
        url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?" + urllib.parse.urlencode(
            {"input_json": json.dumps(request, separators=(",", ":"))})
        try:
            items = self._json(url).get("response", {}).get("store_items") or [{}]
        except (OSError, ValueError):
            items = [{}]
        assets = items[0].get("assets") or {}
        fmt = assets.get("asset_url_format") or ""

        # The store's fixed sizes (the logo's varies, so it has none)
        sizes = {"library_capsule_2x": (600, 900), "library_capsule": (300, 450), "library_hero_2x": (3840, 1240),
                 "library_hero": (1920, 620), "header_2x": (920, 430), "header": (460, 215)}

        def asset(*names):
            for n in names:
                if assets.get(n) and fmt:
                    u = "https://shared.cloudflare.steamstatic.com/store_item_assets/" + fmt.replace("${FILENAME}", assets[n])
                    w, h = sizes.get(n, (None, None))
                    return [{"url": u, "thumb": u, "w": w, "h": h}]
            return []
        return {"p": asset("library_capsule_2x", "library_capsule"), "hero": asset("library_hero_2x", "library_hero"),
                "logo": asset("library_logo_2x", "library_logo"), "header": asset("header_2x", "header"), "icon": []}

    def _steam_search(self, term):
        url = "https://store.steampowered.com/api/storesearch/?" + urllib.parse.urlencode({"term": term, "l": "schinese", "cc": "CN"})
        try:
            items = self._json(url, quick=True).get("items") or []
        except (OSError, ValueError):
            return []
        return [{"id": str(i["id"]), "name": i.get("name", ""), "source": "steam", "year": None, "steamAppId": str(i["id"])}
                for i in items[:6] if i.get("type", "app") == "app"]

    def _steam_appid_for(self, name):
        """The store app with exactly this (English) name, or None: SteamGridDB says a game is on Steam but not which app."""
        key = ("appid", name)
        if key not in self._cache:
            url = "https://store.steampowered.com/api/storesearch/?" + urllib.parse.urlencode({"term": name, "l": "english", "cc": "US"})
            try:
                items = self._json(url, quick=True).get("items") or []
            except (OSError, ValueError):
                return None
            plain = lambda x: re.sub(r"[\s\W_]+", "", x).lower()
            self._cache[key] = next((str(i["id"]) for i in items if plain(i.get("name", "")) == plain(name)), None)
        return self._cache[key]

    def _steam_english_name(self, appid):
        request = {"ids": [{"appid": int(appid)}], "context": {"language": "english", "country_code": "US"}}
        url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?" + urllib.parse.urlencode(
            {"input_json": json.dumps(request, separators=(",", ":"))})
        try:
            items = self._json(url, quick=True).get("response", {}).get("store_items") or [{}]
            return items[0].get("name") or None
        except (OSError, ValueError):
            return None

    def _download(self, url, folder, stem=None):
        if not isinstance(url, str) or not url.startswith("https://"):
            raise ValueError("图片地址格式错误")
        ext = os.path.splitext(urllib.parse.urlsplit(url).path)[1].lower()
        ext = ext if ext in (".png", ".jpg", ".jpeg", ".webp", ".ico") else ".jpg"
        stem = stem or hashlib.sha1(url.encode()).hexdigest()[:20]
        folder.mkdir(parents=True, exist_ok=True)
        target = folder / (stem + ext)
        if folder == self.thumbs and target.is_file() and target.stat().st_size > 0:
            return str(target)
        body = self.fetch(url)
        if not body:
            raise ValueError("empty image")
        tmp = target.with_suffix(".part")
        tmp.write_bytes(body)
        for old in folder.glob(stem + ".*"):
            if old != tmp:
                old.unlink(missing_ok=True)
        os.replace(tmp, target)
        return str(target)

    def _preview(self, item):
        try:
            return {**item, "thumb": self._download(item["thumb"], self.thumbs)}
        except (OSError, ValueError):
            try:
                return {**item, "thumb": self._download(item["url"], self.thumbs)}
            except (OSError, ValueError):
                return None

    # ---- searching ----
    def search(self, name):
        """Games that may be this one, each with a cover preview: SteamGridDB's matches for the name, then (for
        Chinese names, which SteamGridDB rarely knows) the Steam store's Chinese-name matches looked up on
        SteamGridDB by their English name, then those store entries themselves (store artwork)."""
        term = clean_title(str(name or ""))[:120]
        if not term:
            return {"query": term, "matches": [], "status": "未找到"}
        cjk = re.search(r"[぀-ヿ㐀-鿿]", term) is not None

        def store_chain():
            # The store knows Chinese names, but not with edition suffixes ("樱之诗10周年"): shorten until something matches
            store = []
            for q in dict.fromkeys([term, re.sub(r"\s*[0-9０-９]+\s*周年.*$", "", term), re.sub(r"\s*[0-9０-９].*$", "", term)]):
                if q and len(q) >= 2:
                    store = self._steam_search(q)
                    if store:
                        break

            def english(app):
                found_name = self._steam_english_name(app["id"])
                if not found_name:
                    return []
                try:
                    # Remember which store app it came from: the page offers that app's official images too
                    return [{**m, "steamAppId": app["id"]} for m in self._sgdb_search(found_name)[:2]]
                except (PermissionError, OSError, ValueError):
                    return []
            with ThreadPoolExecutor(3) as inner:
                return store, [m for found in inner.map(english, store[:3]) for m in found]

        # SteamGridDB and the store side by side; the store gets a few seconds and is left out when slower
        pool = ThreadPoolExecutor(2)
        side = pool.submit(store_chain)
        matches, status = [], ""
        try:
            matches = self._sgdb_search(term)[:8]
        except PermissionError as e:
            status = str(e)
        except (OSError, ValueError) as e:
            self.log(f"local games: sgdb search failed: {type(e).__name__}")
            status = "连不上"
        try:
            store, via_store = side.result(timeout=10)
        except Exception:
            store, via_store = [], []
        pool.shutdown(wait=False)
        if status:
            via_store = []
        if cjk:
            # SteamGridDB's own search on a Chinese name returns loose fuzzy hits: keep only names that contain it
            def norm(x):
                return re.sub(r"[\s\W_]+", "", x).lower()
            matches = [m for m in matches if norm(term) in norm(m["name"]) or norm(m["name"]) in norm(term)]
        ordered = (via_store + matches if cjk else matches + via_store) + store
        seen, result = set(), []
        for m in ordered:
            if (m["source"], m["id"]) not in seen:
                seen.add((m["source"], m["id"]))
                result.append(m)
        result = result[:10]

        def with_thumb(m):
            try:
                covers = self._assets(m, "p")
            except (PermissionError, OSError, ValueError):
                covers = []
            if m.get("onSteam") and not m.get("steamAppId"):
                appid = self._steam_appid_for(m["name"])
                m = {**m, "steamAppId": appid} if appid else m
            # The page loads previews itself (remote url), so the list comes back without waiting for downloads
            return {**m, "thumb": covers[0]["thumb"] if covers else None, "covers": len(covers)}
        # A preview for each, but never more than a few seconds: SteamGridDB sometimes answers one game very slowly.
        # Late lists still land in the cache for the image page.
        pool = ThreadPoolExecutor(6)
        futures = [pool.submit(with_thumb, m) for m in result]
        wait(futures, timeout=7)
        result = [f.result() if f.done() else {**m, "thumb": None, "covers": None} for f, m in zip(futures, result)]
        pool.shutdown(wait=False)
        # A store entry that a SteamGridDB match already stands for is the same game twice
        linked = {m.get("steamAppId") for m in result if m["source"] == "sgdb"}
        result = [m for m in result if not (m["source"] == "steam" and m["id"] in linked)]
        # Games with covers first (SteamGridDB has many empty entries), the exact name first among those
        def plain(x):
            return re.sub(r"[\s\W_]+", "", x).lower()
        result.sort(key=lambda m: (0 if m.get("covers") else 1, 0 if plain(m["name"]) == plain(term) else 1))
        if not result and not status:
            status = "未找到"
        return {"query": term, "matches": result, "status": status}

    def official(self, appid):
        """A Steam store app's own library images (remote urls): {p, header, hero, logo}."""
        if not str(appid).isdigit():
            raise ValueError("appid 格式错误")
        lists = self._store_assets(str(appid))
        return {k: v for k, v in lists.items() if k != "icon"}

    def candidates(self, match, only=None, previews=True):
        """Images for one match, with local previews: {p, header, hero, logo, icon: [{url, thumb}]}. only = one kind
        (the page asks for the tab on screen first, then the rest, so the first previews show up quickly)."""
        if not isinstance(match, dict) or match.get("source") not in ("sgdb", "steam") or not str(match.get("id", "")).isdigit():
            raise ValueError("匹配格式错误")
        if only is not None and only not in KINDS:
            raise ValueError("图种格式错误")
        limits = {"p": 30, "header": 12, "hero": 12, "logo": 12, "icon": 12}
        lists = {}
        for kind in [only] if only else KINDS:
            try:
                lists[kind] = self._assets(match, kind)[:limits[kind]]
            except (PermissionError, OSError, ValueError) as e:
                self.log(f"local games: {kind} list failed: {type(e).__name__}")
                lists[kind] = []
        if previews:
            with ThreadPoolExecutor(8) as pool:
                for kind in lists:
                    lists[kind] = [i for i in pool.map(self._preview, lists[kind]) if i]
        return lists

    # ---- games ----
    def state(self, user=None):
        user = user or steam_user(steam_root())
        games = self._load()
        collections = live_or_file_collections(user)
        members = {c["id"]: set(c["added"]) for c in collections}
        present = {}
        if user:
            try:
                for _, _, entry in shortcut_entries(read_shortcuts(user)):
                    present[str((Vdf.get(entry, "appid") or 0) & 0xFFFFFFFF)] = entry
            except (OSError, ValueError):
                pass
        for g in games:
            g["collections"] = self._collections_of(g)
            g["inSteam"] = g["id"] in present
            # After a sync: Steam's cloud copy of the collections may have replaced ours
            g["collectionOk"] = not g["inSteam"] or all(g["id"] in members.get(c, set()) for c in g["collections"])
        # Local games also means the non-Steam games already added in Steam itself (not through us): listed after ours,
        # with Steam's name, images and collections; editing one makes it ours (same appid, nothing added twice)
        ours_ids = {g["id"] for g in games}
        ours_exes = {exe_key(g["exe"]) for g in games}
        for appid, entry in present.items():
            exe = unquote(Vdf.get(entry, "Exe") or "")
            name = str(Vdf.get(entry, "AppName") or Vdf.get(entry, "appname") or "").strip()
            if appid in ours_ids or exe_key(exe) in ours_exes or not name or not exe:
                continue
            games.append({"id": appid, "name": name, "exe": exe, "dir": unquote(Vdf.get(entry, "StartDir") or ""),
                          "args": Vdf.get(entry, "LaunchOptions") or "", "steam": True, "fromSteam": True,
                          "collections": [c["id"] for c in collections if appid in set(c["added"])], "newCollections": [],
                          "art": self._grid_art(user, appid, Vdf.get(entry, "icon")), "inSteam": True, "collectionOk": True})
        return {"games": games, "key": bool(self._key()), "collections": [{"id": c["id"], "name": c["name"]} for c in collections],
                "pending": sum(1 for g in games if self._pending(g)),
                "job": dict(self.job), "lastSync": self.last_sync, "steam": user is not None}

    @staticmethod
    def _grid_art(user, appid, icon):
        """Images Steam already has for a shortcut: the grid folder's files, and the shortcut's own icon."""
        art = {}
        grid = user / "config" / "grid"
        for kind, suffix in KINDS.items():
            if kind == "icon":
                continue
            found = sorted(grid.glob(appid + suffix + ".*"), key=lambda p: p.stat().st_mtime, reverse=True)
            found = [p for p in found if p.suffix.lower() in (".png", ".jpg", ".jpeg", ".webp")]
            if found:
                art[kind] = str(found[0])
        icon = unquote(icon or "")
        if icon and os.path.isfile(icon):
            art["icon"] = icon
        return art

    @staticmethod
    def _collections_of(g):
        return list(g.get("collections") or ([g["collection"]] if g.get("collection") else []))

    @staticmethod
    def _pending(g):
        return bool(g.get("steam")) and not g.get("fromSteam") and (not g.get("inSteam") or not g.get("collectionOk") or bool(g.get("newCollections"))
                                         or g.get("artSynced") is False or g.get("collectionSynced") is False)

    def set_key(self, key):
        key = str(key or "").strip()
        if key and not re.fullmatch(r"[0-9a-fA-F]{32}", key):
            raise ValueError("密钥格式不对")
        self.dir.mkdir(parents=True, exist_ok=True)
        if key:
            self.key_path.write_text(key, encoding="utf-8")
        else:
            self.key_path.unlink(missing_ok=True)
        self.log("local games: SteamGridDB key " + ("saved" if key else "removed"))
        return {"key": bool(key)}

    def _art_files(self, ident, choice):
        """Download the chosen full-size images (urls) into art\\<appid>…; keeps kinds left out of choice."""
        files = {}
        for kind, suffix in KINDS.items():
            url = (choice or {}).get(kind)
            if url == "":
                for old in self.art.glob(ident + suffix + ".*"):
                    old.unlink(missing_ok=True)
            elif isinstance(url, str) and url.startswith("file:"):
                # Picked from the user's own disk (the icon's 从本地选择): copied, not downloaded
                src = Path(url[5:])
                ext = src.suffix.lower()
                if ext not in (".png", ".ico", ".jpg", ".jpeg", ".webp") or not src.is_file() or src.stat().st_size > MAX_IMAGE:
                    raise ValueError("图片不能用")
                self.art.mkdir(parents=True, exist_ok=True)
                for old in self.art.glob(ident + suffix + ".*"):
                    old.unlink(missing_ok=True)
                target = self.art / (ident + suffix + ext)
                shutil.copyfile(src, target)
                files[kind] = str(target)
            elif url:
                files[kind] = self._download(url, self.art, ident + suffix)
        return files

    def save(self, game):
        """Add or update one game: {id?, name, exe, steam, collections: [id], newCollections: [name], art: {kind: url}}."""
        if not isinstance(game, dict):
            raise ValueError("游戏格式错误")
        name = str(game.get("name", "")).strip()[:128]
        exe = unquote(str(game.get("exe", "")))
        if not name or re.search(r"[\x00-\x1f]", name):
            raise ValueError("名称不对")
        if not exe.lower().endswith(".exe") or not os.path.isfile(exe):
            raise ValueError("找不到程序")
        collections = [str(c) for c in (game.get("collections") or []) if isinstance(c, str) and c][:32]
        new_collections = []
        for n in game.get("newCollections") or []:
            n = str(n).strip()[:128]
            if n and not re.search(r"[\x00-\x1f]", n) and n not in new_collections:
                new_collections.append(n)
        with self._lock:
            games = self._load()
            old = next((g for g in games if g["id"] == game.get("id")), None) if game.get("id") else None
            if old is None:
                old = next((g for g in games if exe_key(g["exe"]) == exe_key(exe)), None)
            if old is None:
                # Already a Steam shortcut (added in Steam itself): use its appid, so nothing is added twice
                ident = None
                user = steam_user(steam_root())
                if user:
                    try:
                        for _, _, entry in shortcut_entries(read_shortcuts(user)):
                            if exe_key(Vdf.get(entry, "Exe")) == exe_key(exe):
                                ident = str((Vdf.get(entry, "appid") or 0) & 0xFFFFFFFF)
                                break
                    except (OSError, ValueError):
                        pass
                old = {"id": ident or shortcut_appid(f'"{exe}"', name), "added": int(time.time())}
                games.append(old)
            if (sorted(self._collections_of(old)), old.get("newCollections") or []) != (sorted(collections), new_collections):
                old["collectionSynced"] = False
            old.pop("collection", None)
            old.pop("newCollection", None)
            old.update(name=name, exe=exe, dir=os.path.dirname(exe), args=str(game.get("args", ""))[:500],
                       steam=bool(game.get("steam", True)), collections=collections, newCollections=new_collections,
                       sgdb=game.get("sgdb"), steamAppId=str(game["steamAppId"]) if str(game.get("steamAppId") or "").isdigit() else None)
            self._save(games)
        art = self._art_files(old["id"], game.get("art"))
        with self._lock:
            games = self._load()
            entry = next((g for g in games if g["id"] == old["id"]), None)
            if entry is not None:
                entry["art"] = {**entry.get("art", {}), **art}
                for kind, url in (game.get("art") or {}).items():
                    if url == "":
                        entry["art"].pop(kind, None)
                        entry["artCleared"] = sorted(set(entry.get("artCleared") or []) | {kind})
                    elif url:
                        entry["artCleared"] = [k for k in (entry.get("artCleared") or []) if k != kind]
                entry["artVersion"] = int(time.time())
                if art or any(url == "" for url in (game.get("art") or {}).values()):
                    entry["artSynced"] = False
                self._save(games)
        self.log(f"local games: saved {old['id']}")
        return entry

    def remove(self, ident):
        with self._lock:
            games = [g for g in self._load() if g["id"] != str(ident)]
            self._save(games)
        for old in self.art.glob(str(ident) + "*"):
            old.unlink(missing_ok=True)
        return {"removed": str(ident)}

    def auto_art(self, game):
        """Best match's first image of each kind: {kind: url} (empty when nothing is found)."""
        found = self.search(game.get("name", ""))
        match = next((m for m in found["matches"] if m.get("covers")), None)
        if match is None:
            return {}, None
        art = {}
        for kind in KINDS:
            try:
                first = self._assets(match, kind)[:1]
            except (PermissionError, OSError, ValueError):
                first = []
            if first:
                art[kind] = first[0]["url"]
        return art, match

    def add_many(self, items, collections=None, new_collections=None):
        """Folder games taken in at once; art is filled in one by one (job shows progress)."""
        if not isinstance(items, list) or len(items) > 500:
            raise ValueError("列表格式错误")
        self.job = {"phase": "补封面", "done": 0, "total": len(items)}
        added = 0
        for item in items:
            try:
                game = {"name": item.get("name"), "exe": item.get("exe"), "steam": True,
                        "collections": collections or [], "newCollections": new_collections or []}
                try:
                    art, match = self.auto_art(game)
                    game["art"], game["sgdb"] = art, match and match["id"]
                except (PermissionError, OSError, ValueError):
                    pass
                self.save(game)
                added += 1
            except (OSError, ValueError) as e:
                self.log(f"local games: skipped one ({type(e).__name__}: {e})")
            self.job["done"] += 1
        self.job = {"phase": "", "done": 0, "total": 0}
        return {"added": added}

    def fill_missing(self):
        """Every game without a portrait: best match from SteamGridDB / the store."""
        missing = [g for g in self._load() if not (g.get("art") or {}).get("p")]
        self.job = {"phase": "补封面", "done": 0, "total": len(missing)}
        filled = 0
        for g in missing:
            try:
                art, match = self.auto_art(g)
                if art:
                    self.save({**g, "collections": self._collections_of(g), "art": art, "sgdb": match and match["id"]})
                    filled += 1
            except (PermissionError, OSError, ValueError) as e:
                self.log(f"local games: no art for {g['id']} ({type(e).__name__})")
            self.job["done"] += 1
        self.job = {"phase": "", "done": 0, "total": 0}
        return {"filled": filled, "missing": len(missing)}

    # ---- Steam ----
    def sync(self, restart=True):
        """Write every pending game into Steam:
        Steam running with its UI interface → changed live (shortcut, images, collections), nothing restarts;
        Steam closed → the files are written directly and Steam reads them when it starts;
        Steam running without that interface → only with restart: shut down, write the files, start again."""
        root = steam_root()
        user = steam_user(root)
        if root is None or user is None:
            raise RuntimeError("找不到 Steam")
        with self._lock:
            games = self._load()
            view = self.state(user)
            pending = [g for g in view["games"] if self._pending(g)]
            if not pending:
                return {"synced": 0, "restarted": False, "live": False}
            was_running = steam_running()
            if was_running and cdp.available():
                self.job = {"phase": "写入 Steam", "done": 0, "total": len(pending)}
                try:
                    written = self._live_steam(user, games, pending)
                finally:
                    self._save(games)
                    self.job = {"phase": "", "done": 0, "total": 0}
                self.last_sync = {"time": int(time.time()), "count": written}
                self.log(f"local games: synced {written} to Steam live")
                return {"synced": written, "restarted": False, "live": True}
            if was_running and not restart:
                return {"synced": 0, "restarted": False, "live": False, "needsRestart": True}
            if was_running and self.game_running():
                raise RuntimeError("游戏运行中")
            if was_running:
                self.job = {"phase": "关闭 Steam", "done": 0, "total": len(pending)}
                subprocess.run([str(root / "steam.exe"), "-shutdown"], capture_output=True, creationflags=NO_WINDOW, timeout=30)
                for _ in range(80):
                    if not steam_running():
                        break
                    time.sleep(0.5)
                else:
                    self.job = {"phase": "", "done": 0, "total": 0}
                    raise RuntimeError("Steam 没关掉")
                time.sleep(1.0)
            try:
                self.job = {"phase": "写入 Steam", "done": 0, "total": len(pending)}
                written = self._write_steam(user, games, pending)
                self._save(games)
            finally:
                if was_running:
                    self.job = {"phase": "启动 Steam", "done": 0, "total": 0}
                    self._start_steam(root)
                self.job = {"phase": "", "done": 0, "total": 0}
        self.last_sync = {"time": int(time.time()), "count": written}
        self.log(f"local games: synced {written} to Steam (restart {was_running})")
        return {"synced": written, "restarted": was_running}

    def _rekey(self, entry, new_id):
        """Steam gave the shortcut another appid than ours: move our entry and its art files onto it."""
        old_id = entry["id"]
        for old in self.art.glob(old_id + "*"):
            os.replace(old, old.with_name(old.name.replace(old_id, new_id, 1)))
        entry["art"] = {k: v.replace(old_id, new_id, 1) for k, v in (entry.get("art") or {}).items()}
        entry["id"] = new_id

    def _live_steam(self, user, games, pending):
        """Through the running Steam client: add / rename the shortcut, set its images and icon, put it in collections."""
        try:
            by_exe = {exe_key(Vdf.get(e, "Exe")): str((Vdf.get(e, "appid") or 0) & 0xFFFFFFFF)
                      for _, _, e in shortcut_entries(read_shortcuts(user))}
        except (OSError, ValueError):
            by_exe = {}
        grid = user / "config" / "grid"
        written = 0
        for g in pending:
            entry = next(x for x in games if x["id"] == g["id"])
            fresh = not cdp.exists(entry["id"])
            if fresh:
                same = by_exe.get(exe_key(entry["exe"]))
                if same and cdp.exists(same):
                    new_id = same
                else:
                    new_id = str(int(cdp.add_shortcut(entry["name"], entry["exe"], entry["dir"], entry.get("args", ""))) & 0xFFFFFFFF)
                if new_id != entry["id"]:
                    self._rekey(entry, new_id)
                    g["id"] = new_id
            ident = entry["id"]
            cdp.update_shortcut(ident, entry["name"])
            if fresh or entry.get("artSynced") is not True:
                art = entry.get("art") or {}
                for kind in cdp.ASSET_TYPES:
                    src = art.get(kind)
                    if src and os.path.isfile(src):
                        cdp.set_artwork(ident, kind, src)
                    elif kind in (entry.get("artCleared") or []):
                        cdp.set_artwork(ident, kind, None)
                entry["artCleared"] = []
                icon = art.get("icon")
                if icon and os.path.isfile(icon):
                    grid.mkdir(parents=True, exist_ok=True)
                    for old in grid.glob(ident + "_icon.*"):
                        old.unlink(missing_ok=True)
                    target = grid / (ident + "_icon" + os.path.splitext(icon)[1])
                    shutil.copyfile(icon, target)
                    cdp.set_icon(ident, str(target))
                entry["artSynced"] = True
            ids, names = self._collections_of(entry), list(entry.get("newCollections") or [])
            if ids or names:
                made = cdp.add_to_collections(ident, ids, names) or {}
                for n in names:
                    if made.get(n) and made[n] not in ids:
                        ids.append(made[n])
                entry.pop("collection", None)
                entry["collections"] = ids
                entry["newCollections"] = []
            entry["collectionSynced"] = True
            written += 1
            self.job["done"] = written
        return written

    def _write_steam(self, user, games, pending):
        cfg = user / "config"
        tree = read_shortcuts(user)
        shortcuts = Vdf.get(tree, "shortcuts")
        if shortcuts is None:
            shortcuts = []
            tree.append([0x00, "shortcuts", shortcuts])
        by_exe = {exe_key(Vdf.get(e, "Exe")): str((Vdf.get(e, "appid") or 0) & 0xFFFFFFFF) for _, _, e in shortcuts}
        by_id = {str((Vdf.get(e, "appid") or 0) & 0xFFFFFFFF): e for _, _, e in shortcuts}
        template = shortcuts[0][2] if shortcuts else None
        entries = []
        for g in pending:
            entry = next(x for x in games if x["id"] == g["id"])
            same = by_exe.get(exe_key(entry["exe"]))
            if entry["id"] not in by_id and same:
                # Added in Steam in the meantime: keep Steam's entry and move ours (and its art) onto that id
                self._rekey(entry, same)
                g["id"] = same
            entries.append(entry)
        grid = cfg / "grid"
        grid.mkdir(parents=True, exist_ok=True)
        icons = {}
        for entry in entries:
            for kind, suffix in KINDS.items():
                src = (entry.get("art") or {}).get(kind)
                # Replace the kinds we have, clear the ones taken out; images Steam had that we never touched stay
                if (src and os.path.isfile(src)) or kind in (entry.get("artCleared") or []):
                    for old in grid.glob(entry["id"] + suffix + ".*"):
                        old.unlink(missing_ok=True)
                if src and os.path.isfile(src):
                    target = grid / (entry["id"] + suffix + os.path.splitext(src)[1])
                    shutil.copyfile(src, target)
                    if kind == "icon":
                        icons[entry["id"]] = str(target)
            entry["artSynced"] = True
            entry["artCleared"] = []
        changed = False
        for entry in entries:
            existing = by_id.get(entry["id"])
            if existing is None:
                index = max([int(k) for _, k, _ in shortcuts if k.isdigit()] + [-1]) + 1
                fields = new_shortcut(entry, template)
                shortcuts.append([0x00, str(index), fields])
                by_id[entry["id"]] = existing = fields
                changed = True
            for field in existing:
                if field[0] != 0x01 or field[1].lower() != "icon":
                    continue
                # Our icon when there is one; one we set earlier and the user since dropped is cleared; any other is left
                ours = os.path.basename(field[2]).lower().startswith(entry["id"] + "_icon.")
                want = icons.get(entry["id"], "" if ours else field[2])
                if field[2] != want:
                    field[2] = want
                    changed = True
        if changed:
            path = cfg / "shortcuts.vdf"
            if path.is_file():
                shutil.copy2(path, cfg / "shortcuts.vdf.claw-bak")
            tmp = path.with_suffix(".claw-tmp")
            tmp.write_bytes(Vdf.dump(tree))
            os.replace(tmp, path)
        wanted = {}
        for entry in entries:
            for c in self._collections_of(entry):
                wanted.setdefault(c, []).append(entry["id"])
            for n in entry.get("newCollections") or []:
                wanted.setdefault("new:" + n, []).append(entry["id"])
            entry["collectionSynced"] = True
        if wanted:
            created = write_collections(user, wanted)
            names = {c["name"]: c["id"] for c in read_collections(user)}
            for entry in entries:
                ids = self._collections_of(entry)
                for n in entry.get("newCollections") or []:
                    ident = created.get("new:" + n) or names.get(n)
                    if ident and ident not in ids:
                        ids.append(ident)
                entry.pop("collection", None)
                entry["collections"] = ids
                entry["newCollections"] = []
        return len(entries)

    @staticmethod
    def _start_steam(root):
        """The way Steam starts at sign-in: the toolbox's elevated logon task if it exists, else plain Steam."""
        task = subprocess.run(["schtasks", "/query", "/tn", STEAM_TASK], capture_output=True, creationflags=NO_WINDOW)
        if task.returncode == 0 and subprocess.run(["schtasks", "/run", "/tn", STEAM_TASK], capture_output=True,
                                                   creationflags=NO_WINDOW).returncode == 0:
            return
        subprocess.Popen([str(root / "steam.exe")], cwd=str(root), creationflags=subprocess.DETACHED_PROCESS)
