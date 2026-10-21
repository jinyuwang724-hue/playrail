"""Local (non-Steam) games added from ClawLibrary: artwork from SteamGridDB, and the same game written
into Steam as a non-Steam shortcut with its artwork and collection.

Everything lives in data\\local-games: games.json (read directly by ClawLibrary at load), art\\ (the
chosen images, named the way Steam's grid folder names them: <appid>p, <appid>_hero, <appid>_logo,
<appid>), thumbs\\ (candidate previews) and the user's own SteamGridDB API key (never logged).

A game's id is the Steam shortcut appid it will get (crc32 of exe + name with the top bit set, the way
Steam ROM Manager does it), fixed when the game is added. So once it is in shortcuts.vdf, the library's
automatic import of new shortcuts sees the same id and the game appears only once.

Steam is not touched while adding. sync() writes every pending game in one go: Steam is shut down
(Steam rewrites shortcuts.vdf and its collections from memory, so they cannot be changed while it
runs), shortcuts.vdf gets the new entries, the grid folder the images, the collection store the new
members, and Steam is started again the way it was started at sign-in. Collections are cloud-synced,
so state() checks afterwards whether Steam kept them.
"""
from concurrent.futures import ThreadPoolExecutor
import hashlib
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
MAX_IMAGE = 40 * 1024 * 1024
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
STEAM_TASK = r"\ClawToolbox\SteamAdmin"
# Art kinds, as Steam names the files in userdata\<id>\config\grid
KINDS = {"p": "p", "hero": "_hero", "logo": "_logo", "header": ""}
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
    def _fetch(url, headers=None, limit=MAX_IMAGE):
        request = urllib.request.Request(url, headers={"User-Agent": "ClawToolbox", **(headers or {})})
        with urllib.request.urlopen(request, timeout=20) as r:
            body = r.read(limit + 1)
        if len(body) > limit:
            raise ValueError("too large")
        return body

    def _sgdb(self, path):
        key = self._key()
        if not key:
            raise PermissionError("无密钥")
        try:
            body = json.loads(self.fetch(SGDB + path, {"Authorization": "Bearer " + key}, 4 * 1024 * 1024))
        except urllib.error.HTTPError as e:
            if e.code in (401, 403):
                raise PermissionError("密钥无效")
            if e.code == 404:
                return []
            raise
        if not body.get("success", False):
            return []
        return body.get("data") or []

    def _steam_search(self, term):
        url = "https://store.steampowered.com/api/storesearch/?" + urllib.parse.urlencode({"term": term, "l": "schinese", "cc": "CN"})
        try:
            items = json.loads(self.fetch(url, None, 2 * 1024 * 1024)).get("items") or []
        except (OSError, ValueError):
            return []
        return [{"id": str(i["id"]), "name": i.get("name", ""), "source": "steam"} for i in items[:8] if i.get("type", "app") == "app"]

    def _download(self, url, folder, stem=None):
        if not isinstance(url, str) or not url.startswith("https://"):
            raise ValueError("图片地址格式错误")
        ext = os.path.splitext(urllib.parse.urlsplit(url).path)[1].lower()
        ext = ext if ext in (".png", ".jpg", ".jpeg", ".webp") else ".jpg"
        stem = stem or hashlib.sha1(url.encode()).hexdigest()[:20]
        folder.mkdir(parents=True, exist_ok=True)
        target = folder / (stem + ext)
        if stem and target.is_file() and folder == self.thumbs:
            return str(target)
        body = self.fetch(url)
        tmp = target.with_suffix(".part")
        tmp.write_bytes(body)
        for old in folder.glob(stem + ".*"):
            if old != tmp:
                old.unlink(missing_ok=True)
        os.replace(tmp, target)
        return str(target)

    # ---- searching ----
    def search(self, name):
        """Matches for a game name: SteamGridDB games (with a key), else Steam store apps."""
        term = clean_title(str(name or ""))[:120]
        if not term:
            return {"query": term, "matches": [], "status": "未找到"}
        key = self._key()
        matches = []
        status = ""
        if key:
            try:
                matches = [{"id": str(g["id"]), "name": g.get("name", ""), "year": time.gmtime(g["release_date"]).tm_year
                            if g.get("release_date") else None, "source": "sgdb"}
                           for g in self._sgdb("/search/autocomplete/" + urllib.parse.quote(term))[:10]]
            except PermissionError as e:
                status = str(e)
            except (OSError, ValueError) as e:
                self.log(f"local games: sgdb search failed: {type(e).__name__}")
                status = "连不上"
        if not matches:
            # Many galgames are on the Steam store under their Chinese name; with a key their SteamGridDB
            # entry is found through the store's appid, without one the store's own artwork is used.
            for app in self._steam_search(term):
                if key and status == "":
                    try:
                        found = self._sgdb("/games/steam/" + app["id"])
                        if isinstance(found, dict) and found.get("id"):
                            matches.append({"id": str(found["id"]), "name": found.get("name") or app["name"], "source": "sgdb"})
                            continue
                    except (PermissionError, OSError, ValueError):
                        pass
                matches.append(app)
        if not matches and not status:
            status = "未找到" if key else "无密钥"
        return {"query": term, "matches": matches, "status": status}

    def candidates(self, match):
        """Images for one match, with local previews: {p, hero, logo, header: [{url, thumb}]}."""
        if not isinstance(match, dict) or match.get("source") not in ("sgdb", "steam") or not str(match.get("id", "")).isdigit():
            raise ValueError("匹配格式错误")
        ident = str(match["id"])
        if match["source"] == "steam":
            # Newer store images sit under hashed paths; the store's own item API names them (no key needed)
            request = {"ids": [{"appid": int(ident)}], "context": {"language": "schinese", "country_code": "CN"},
                       "data_request": {"include_assets": True}}
            url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?" + urllib.parse.urlencode(
                {"input_json": json.dumps(request, separators=(",", ":"))})
            try:
                items = json.loads(self.fetch(url, None, 2 * 1024 * 1024)).get("response", {}).get("store_items") or [{}]
            except (OSError, ValueError):
                items = [{}]
            assets = items[0].get("assets") or {}
            fmt = assets.get("asset_url_format") or ""

            def asset(*names):
                return [{"url": "https://shared.cloudflare.steamstatic.com/store_item_assets/" + fmt.replace("${FILENAME}", assets[n]),
                         "thumb": "https://shared.cloudflare.steamstatic.com/store_item_assets/" + fmt.replace("${FILENAME}", assets[n])}
                        for n in names if assets.get(n) and fmt][:1]
            lists = {"p": asset("library_capsule_2x", "library_capsule"), "hero": asset("library_hero_2x", "library_hero"),
                     "logo": asset("library_logo_2x", "library_logo"), "header": asset("header_2x", "header")}
        else:
            def images(path, n):
                return [{"url": i["url"], "thumb": i.get("thumb") or i["url"]} for i in self._sgdb(path)[:n]
                        if isinstance(i, dict) and str(i.get("url", "")).startswith("https://")]
            lists = {
                "p": images(f"/grids/game/{ident}?dimensions=600x900,342x482,660x930&types=static&nsfw=false&humor=false", 16),
                "hero": images(f"/heroes/game/{ident}?types=static&nsfw=false&humor=false", 10),
                "logo": images(f"/logos/game/{ident}?types=static&nsfw=false&humor=false", 10),
                "header": images(f"/grids/game/{ident}?dimensions=920x430,460x215&types=static&nsfw=false&humor=false", 4),
            }

        def preview(item):
            try:
                item["thumb"] = self._download(item["thumb"], self.thumbs)
                return item
            except (OSError, ValueError):
                return None

        with ThreadPoolExecutor(6) as pool:
            for kind in ("p", "hero", "logo"):
                lists[kind] = [i for i in pool.map(preview, lists[kind]) if i]
        lists["header"] = lists["header"][:1]
        return lists

    # ---- games ----
    def state(self, user=None):
        user = user or steam_user(steam_root())
        games = self._load()
        collections = read_collections(user) if user else []
        members = {c["id"]: set(c["added"]) for c in collections}
        present = {}
        if user:
            try:
                for _, _, entry in shortcut_entries(read_shortcuts(user)):
                    present[str((Vdf.get(entry, "appid") or 0) & 0xFFFFFFFF)] = exe_key(Vdf.get(entry, "Exe"))
            except (OSError, ValueError):
                pass
        for g in games:
            g["inSteam"] = g["id"] in present
            # After a sync: Steam's cloud copy of the collections may have replaced ours
            g["collectionOk"] = not g.get("collection") or not g["inSteam"] or g["id"] in members.get(g["collection"], set())
        return {"games": games, "key": bool(self._key()), "collections": [{"id": c["id"], "name": c["name"]} for c in collections],
                "pending": sum(1 for g in games if self._pending(g)),
                "job": dict(self.job), "lastSync": self.last_sync, "steam": user is not None}

    @staticmethod
    def _pending(g):
        return bool(g.get("steam")) and (not g.get("inSteam") or not g.get("collectionOk") or bool(g.get("newCollection"))
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
            elif url:
                files[kind] = self._download(url, self.art, ident + suffix)
        return files

    def save(self, game):
        """Add or update one game: {id?, name, exe, steam, collection, newCollection, art: {kind: url}}."""
        if not isinstance(game, dict):
            raise ValueError("游戏格式错误")
        name = str(game.get("name", "")).strip()[:128]
        exe = unquote(str(game.get("exe", "")))
        if not name or re.search(r"[\x00-\x1f]", name):
            raise ValueError("名称不对")
        if not exe.lower().endswith(".exe") or not os.path.isfile(exe):
            raise ValueError("找不到程序")
        collection = game.get("collection") or None
        new_collection = str(game.get("newCollection") or "").strip()[:128] or None
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
            if (old.get("collection"), old.get("newCollection")) != (collection if not new_collection else None, new_collection) and (collection or new_collection):
                old["collectionSynced"] = False
            old.update(name=name, exe=exe, dir=os.path.dirname(exe), args=str(game.get("args", ""))[:500],
                       steam=bool(game.get("steam", True)), collection=collection if not new_collection else None,
                       newCollection=new_collection, sgdb=game.get("sgdb"))
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
        if not found["matches"]:
            return {}, None
        match = found["matches"][0]
        lists = self.candidates(match)
        return {k: v[0]["url"] for k, v in lists.items() if v}, match

    def add_many(self, items, collection=None, new_collection=None):
        """Folder games taken in at once; art is filled in one by one (job shows progress)."""
        if not isinstance(items, list) or len(items) > 500:
            raise ValueError("列表格式错误")
        self.job = {"phase": "补封面", "done": 0, "total": len(items)}
        added = 0
        for item in items:
            try:
                game = {"name": item.get("name"), "exe": item.get("exe"), "steam": True, "collection": collection, "newCollection": new_collection}
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
                    self.save({**g, "art": art, "sgdb": match and match["id"]})
                    filled += 1
            except (PermissionError, OSError, ValueError) as e:
                self.log(f"local games: no art for {g['id']} ({type(e).__name__})")
            self.job["done"] += 1
        self.job = {"phase": "", "done": 0, "total": 0}
        return {"filled": filled, "missing": len(missing)}

    # ---- Steam ----
    def sync(self):
        """Write every pending game into Steam. Steam is shut down first and started again afterwards."""
        root = steam_root()
        user = steam_user(root)
        if root is None or user is None:
            raise RuntimeError("找不到 Steam")
        if self.game_running():
            raise RuntimeError("游戏运行中")
        with self._lock:
            games = self._load()
            view = self.state(user)
            pending = [g for g in view["games"] if self._pending(g)]
            if not pending:
                return {"synced": 0, "restarted": False}
            was_running = steam_running()
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

    def _write_steam(self, user, games, pending):
        cfg = user / "config"
        tree = read_shortcuts(user)
        shortcuts = Vdf.get(tree, "shortcuts")
        if shortcuts is None:
            shortcuts = []
            tree.append([0x00, "shortcuts", shortcuts])
        by_exe = {exe_key(Vdf.get(e, "Exe")): str((Vdf.get(e, "appid") or 0) & 0xFFFFFFFF) for _, _, e in shortcuts}
        ids = {str((Vdf.get(e, "appid") or 0) & 0xFFFFFFFF) for _, _, e in shortcuts}
        template = shortcuts[0][2] if shortcuts else None
        changed = False
        for g in pending:
            entry = next(x for x in games if x["id"] == g["id"])
            if entry["id"] in ids:
                continue
            same = by_exe.get(exe_key(entry["exe"]))
            if same:
                # Added in Steam in the meantime: keep Steam's entry and move ours (and its art) onto that id
                for old in self.art.glob(entry["id"] + "*"):
                    os.replace(old, old.with_name(old.name.replace(entry["id"], same, 1)))
                entry["art"] = {k: v.replace(entry["id"], same, 1) for k, v in (entry.get("art") or {}).items()}
                entry["id"] = g["id"] = same
                continue
            index = max([int(k) for _, k, _ in shortcuts if k.isdigit()] + [-1]) + 1
            shortcuts.append([0x00, str(index), new_shortcut(entry, template)])
            ids.add(entry["id"])
            changed = True
        if changed:
            path = cfg / "shortcuts.vdf"
            if path.is_file():
                shutil.copy2(path, cfg / "shortcuts.vdf.claw-bak")
            tmp = path.with_suffix(".claw-tmp")
            tmp.write_bytes(Vdf.dump(tree))
            os.replace(tmp, path)
        grid = cfg / "grid"
        grid.mkdir(parents=True, exist_ok=True)
        for g in pending:
            entry = next(x for x in games if x["id"] == g["id"])
            for kind, suffix in KINDS.items():
                src = (entry.get("art") or {}).get(kind)
                if src and os.path.isfile(src):
                    for old in grid.glob(entry["id"] + suffix + ".*"):
                        old.unlink(missing_ok=True)
                    shutil.copyfile(src, grid / (entry["id"] + suffix + os.path.splitext(src)[1]))
            entry["artSynced"] = True
        wanted = {}
        for g in pending:
            entry = next(x for x in games if x["id"] == g["id"])
            if entry.get("newCollection"):
                wanted.setdefault("new:" + entry["newCollection"], []).append(entry["id"])
            elif entry.get("collection"):
                wanted.setdefault(entry["collection"], []).append(entry["id"])
        for g in pending:
            next(x for x in games if x["id"] == g["id"])["collectionSynced"] = True
        if wanted:
            created = write_collections(user, wanted)
            for entry in games:
                if entry.get("newCollection"):
                    entry["collection"] = created.get("new:" + entry["newCollection"]) or next(
                        (c["id"] for c in read_collections(user) if c["name"] == entry["newCollection"]), None)
                    entry["newCollection"] = None
        return len(pending)

    @staticmethod
    def _start_steam(root):
        """The way Steam starts at sign-in: the toolbox's elevated logon task if it exists, else plain Steam."""
        task = subprocess.run(["schtasks", "/query", "/tn", STEAM_TASK], capture_output=True, creationflags=NO_WINDOW)
        if task.returncode == 0 and subprocess.run(["schtasks", "/run", "/tn", STEAM_TASK], capture_output=True,
                                                   creationflags=NO_WINDOW).returncode == 0:
            return
        subprocess.Popen([str(root / "steam.exe")], cwd=str(root), creationflags=subprocess.DETACHED_PROCESS)
