"""Adopt this machine's already-applied repair and its original recovery records."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil

args = argparse.ArgumentParser()
args.add_argument('--settings', type=Path, required=True)
options = args.parse_args()
root = Path(__file__).resolve().parents[1]
backup = root / 'artifacts/repair-20261008-1655'
target = options.settings / 'compatibility'
record = target / 'state.json'
if record.exists():
    raise SystemExit('Existing managed repair records preserved; migration not repeated.')
read = lambda path: json.loads(path.read_text(encoding='utf-8-sig'))
original = (backup / 'XboxGamingBarHelper.exe').read_bytes()
assert hashlib.sha256(original).hexdigest() == 'dbe62a3ea97a6dc30f069adce3d16bb5ae2bbfd0a44cf09ad45d18c7cf141088'
package = Path(os.environ['LOCALAPPDATA']) / 'Packages/MSIClaw.ClawTweaks_7eszav2039cvc'
helper = package / 'LocalCache/ClawTweaks/Helper/XboxGamingBarHelper.exe'
patched_hash = 'a53c57719fe81888550ba29cc6e85af3c15ffdd50efe0d1b3f0d8c9b47566a83'
assert hashlib.sha256(helper.read_bytes()).hexdigest() == patched_hash
assert read(package / 'LocalState/settings.json')['GameBarFirstSlot_PreserveHome'] is True
cfg = read(Path(os.environ['APPDATA']) / 'GamingMode/config.json')
apps = [entry for entry in cfg['gaming']['customStartupApps'] if entry['name'] == 'Steam Administrator Launcher']
assert len(apps) == 1 and apps[0]['enabled'] and Path(apps[0]['path']).name == 'SteamAdminLauncher.exe'
old_cfg = read(backup / 'GamingMode-config.json')
records = {}
def own(key, before, after):
    records[key] = dict(before=before, after=after)
own('steam.launcher', [app for app in old_cfg['gaming'].get('customStartupApps', []) if app['name'] == 'Steam Administrator Launcher'], apps)
own('steam.run', read(backup / 'Steam-windows-startup-before.json')['value'], None)
own('steam.layers', read(backup / 'Steam-compat-before.json')['value'], None)
for slot, file in [('user', 'Steam-shortcut-0.lnk'), ('common', 'Steam-common-shortcut.lnk')]:
    own('steam.link.' + slot, bool((backup / file).read_bytes()[0x15] & 0x20), True)
own('gamebar.helper', hashlib.sha256(original).hexdigest(), patched_hash)
own('gamebar.flag', read(backup / 'ClawTweaks-settings.json').get('GameBarFirstSlot_PreserveHome'), True)
own('gamebar.home', read(backup / 'GameBar-home-before.json')['InvokeToHomeWidget'], True)
files = []
for folder, saved in [('ZenlessZoneZero Game', 'zzz-steam_appid.txt'), ('Wuthering Waves Game', 'wuwa-steam_appid.txt')]:
    content = (backup / saved).read_bytes()
    assert content.strip(b'\r\n\t \0') in (b'11857803', b'14637647')
    files.append({'path': str(Path('C:/Game') / folder / 'steam_appid.txt'), 'bytes': base64.b64encode(content).decode()})
state = dict(enabled={'steam': True, 'gamebar': True}, records=records, appidFiles=files,
             items={}, phase='已接管之前的修复，等待插件检查', busy=False, importedFrom='repair-20261008-1655')
target.mkdir(parents=True, exist_ok=True)
shutil.copyfile(backup / 'XboxGamingBarHelper.exe', target / 'XboxGamingBarHelper.original.exe')
temp = record.with_suffix('.tmp')
temp.write_text(json.dumps(state, ensure_ascii=False), encoding='utf-8')
os.replace(temp, record)
print('Original repair records adopted; both independent fixes enabled.')
