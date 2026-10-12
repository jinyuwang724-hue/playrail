"""Owned, reversible compatibility repairs. Reconcile on events, never idle polling."""
import asyncio
import base64
import ctypes
from ctypes import wintypes
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import uuid
import winreg


def atomic(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + '.tmp')
    temp.write_bytes(value if isinstance(value, bytes) else json.dumps(value, ensure_ascii=False).encode())
    os.replace(temp, path)


def digest(value):
    return hashlib.sha256(value).hexdigest()


def patch_bytes(value, patch, forward):
    source, target, direction = ('original', 'patched', 'forward') if forward else ('patched', 'original', 'reverse')
    if digest(value) != patch[source + 'Hash']:
        raise ValueError('ClawTweaks 已更新或文件不匹配，未覆盖；需要适配新版本')
    size = patch[target + 'Length']
    result = bytearray(value[:size])
    result.extend(b'\0' * (size - len(result)))
    for offset, encoded in patch[direction]:
        block = base64.b64decode(encoded, validate=True)
        result[offset:offset + len(block)] = block
    result = bytes(result)
    if digest(result) != patch[target + 'Hash']:
        raise ValueError('修补资源校验失败，未覆盖')
    return result


class Utility:
    def __init__(self, root, data):
        self.exe = root / 'bin/CompatibilityUtility.exe'
        self.jobs = data / 'jobs'

    @staticmethod
    def elevated(exe, request):
        # Only the fixed utility can elevate; payload operations have no executable/path input.
        class ShellExecuteInfo(ctypes.Structure):
            _fields_ = [('cbSize', wintypes.DWORD), ('fMask', wintypes.ULONG), ('hwnd', wintypes.HWND),
                        ('lpVerb', wintypes.LPCWSTR), ('lpFile', wintypes.LPCWSTR), ('lpParameters', wintypes.LPCWSTR),
                        ('lpDirectory', wintypes.LPCWSTR), ('nShow', ctypes.c_int), ('hInstApp', wintypes.HINSTANCE),
                        ('lpIDList', ctypes.c_void_p), ('lpClass', wintypes.LPCWSTR), ('hkeyClass', wintypes.HKEY),
                        ('dwHotKey', wintypes.DWORD), ('hIcon', wintypes.HANDLE), ('hProcess', wintypes.HANDLE)]
        shell, kernel = ctypes.WinDLL('shell32', use_last_error=True), ctypes.WinDLL('kernel32', use_last_error=True)
        shell.ShellExecuteExW.argtypes = [ctypes.POINTER(ShellExecuteInfo)]
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        info = ShellExecuteInfo(cbSize=ctypes.sizeof(ShellExecuteInfo), fMask=0x40 | 0x400,
                                lpVerb='runas', lpFile=str(exe), lpParameters=subprocess.list2cmdline([str(request)]), nShow=0)
        if not shell.ShellExecuteExW(ctypes.byref(info)):
            raise OSError(ctypes.get_last_error(), '管理员修复未执行')
        try:
            if kernel.WaitForSingleObject(info.hProcess, 60000) != 0:
                raise RuntimeError('管理员修复仍在等待，请完成 Windows 提示后重新检查')
        finally:
            kernel.CloseHandle(info.hProcess)

    async def __call__(self, operation, **values):
        request = self.jobs / (uuid.uuid4().hex + '.json')
        output = Path(str(request) + '.result')
        atomic(request, dict(op=operation, **values))
        try:
            process = await asyncio.create_subprocess_exec(str(self.exe), str(request), creationflags=subprocess.CREATE_NO_WINDOW)
            await asyncio.wait_for(process.wait(), 15)
            result = json.loads(output.read_text(encoding='utf-8'))
            if not result['ok'] and result.get('requiresAdmin'):
                output.unlink()
                await asyncio.to_thread(self.elevated, self.exe, request)
                result = json.loads(output.read_text(encoding='utf-8'))
            if not result['ok']:
                raise RuntimeError(result.get('error', '修复操作失败'))
            return result['data']
        finally:
            # A timed-out elevated job retains its input until a later cleanup.
            if output.exists():
                request.unlink(missing_ok=True)
                output.unlink(missing_ok=True)


class Compatibility:
    RUN = r'Software\Microsoft\Windows\CurrentVersion\Run'
    LAYERS = r'Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'

    def __init__(self, root, data, maintenance):
        self.root, self.data, self.maintenance = root, data / 'compatibility', maintenance
        self.path = self.data / 'state.json'
        self.config = Path(os.environ['APPDATA']) / 'GamingMode/config.json'
        package = Path(os.environ['LOCALAPPDATA']) / 'Packages/MSIClaw.ClawTweaks_7eszav2039cvc'
        self.helper = package / 'LocalCache/ClawTweaks/Helper/XboxGamingBarHelper.exe'
        self.settings = package / 'LocalState/settings.json'
        self.utility = Utility(root, self.data)
        self.lock = asyncio.Lock()
        self.preserved = set()
        self.state = {'enabled': {'steam': False, 'gamebar': False}, 'records': {}, 'appidFiles': [],
                      'items': {}, 'phase': '等待检查', 'busy': False}
        try:
            saved = json.loads(self.path.read_text(encoding='utf-8'))
            if (not isinstance(saved, dict) or set(saved.get('enabled', {})) != {'steam', 'gamebar'}
                    or any(type(v) is not bool for v in saved['enabled'].values()) or not isinstance(saved.get('records'), dict)):
                raise ValueError('恢复记录格式错误')
            self.state.update(saved)
            self.state['busy'] = False
        except FileNotFoundError:
            pass
        except (OSError, ValueError) as ex:
            # Do not silently discard the only recovery record.
            self.state['error'] = '修复记录无法读取：' + str(ex)
        with gzip.open(root / 'resources/clawtweaks-home.patch.gz', 'rt', encoding='utf-8') as stream:
            self.patch = json.load(stream)

    def save(self):
        atomic(self.path, self.state)

    def view(self):
        return {key: self.state.get(key) for key in ('enabled', 'items', 'phase', 'busy', 'error')}

    def record(self, key, before, after):
        old = self.state['records'].get(key)
        if old is None:
            self.state['records'][key] = {'before': before, 'after': after}
        elif old['after'] != after:
            old['after'] = after
        self.save()  # Before every change, including interrupted retries.

    @staticmethod
    def registry(path, name, value=...):
        with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_READ | winreg.KEY_WRITE) as key:
            if value is ...:
                try:
                    result, kind = winreg.QueryValueEx(key, name)
                    if kind != winreg.REG_SZ:
                        raise ValueError('Steam 注册表入口格式不匹配，未修改')
                    return result
                except FileNotFoundError:
                    return None
            if value is None:
                try:
                    winreg.DeleteValue(key, name)
                except FileNotFoundError:
                    pass
            else:
                winreg.SetValueEx(key, name, 0, winreg.REG_SZ, value)

    async def owned(self, key, current, wanted, write, enabled):
        record = self.state['records'].get(key)
        if enabled:
            if current == wanted:
                return False
            self.record(key, current, wanted)
            await write(wanted)
            return True
        if not record:
            return False
        if current == record['after'] and current != record['before']:
            await write(record['before'])
        elif current != record['before']:
            self.preserved.add(key)
        # Changed values belong to the user or another app, so leave them alone.
        del self.state['records'][key]
        self.save()
        return current != record['before'] and current != record['after']

    async def steam(self, inspection, enabled):
        steam = inspection['steamPath']
        desired = {'name': 'Steam Administrator Launcher', 'path': str(self.root / 'bin/SteamAdminLauncher.exe'),
                   'workingDirectory': str(self.root / 'bin'), 'arguments': '', 'processName': 'steam',
                   'startMinimized': True, 'delayAfterStartMs': 1500, 'enabled': True}
        # Read/write only this entry; never restore the whole Playhub configuration.
        cfg = json.loads(self.config.read_text(encoding='utf-8-sig'))
        apps = cfg['gaming'].get('customStartupApps', [])
        current = [entry for entry in apps if entry.get('name') == desired['name']]
        async def startup(value):
            latest = json.loads(self.config.read_text(encoding='utf-8-sig'))
            entries = latest['gaming'].get('customStartupApps', [])
            if [entry for entry in entries if entry.get('name') == desired['name']] != current:
                raise RuntimeError('Playhub 启动配置刚刚改变，请重新检查')
            latest['gaming']['customStartupApps'] = value + [entry for entry in entries if entry.get('name') != desired['name']]
            atomic(self.config, latest)
        await self.owned('steam.launcher', current, [desired], startup, enabled)
        if enabled and current == [desired] and apps and apps[0] != desired:
            # An existing launcher still must win the race against Playhub's Steam launch.
            self.record('steam.launcher', current, [desired])
            await startup([desired])
        run = self.registry(self.RUN, 'Steam')
        expected = run is None or bool(re.match(r'^"?' + re.escape(steam) + r'"?\s+-silent\s*$', run, re.I))
        async def set_run(value):
            self.registry(self.RUN, 'Steam', value)
        if expected or not enabled:
            await self.owned('steam.run', run, None, set_run, enabled)
        elif enabled:
            raise RuntimeError('Steam Windows 自启动入口已自定义，未覆盖；请检查是否会抢先启动普通权限 Steam')
        compat = self.registry(self.LAYERS, steam)
        stripped = ' '.join(part for part in (compat or '').split() if part.upper() != 'RUNASADMIN')
        stripped = stripped if stripped.strip('~ ') else None
        async def set_compat(value):
            self.registry(self.LAYERS, steam, value)
        await self.owned('steam.layers', compat, stripped, set_compat, enabled)
        for slot, item in inspection['shortcuts'].items():
            if item['present'] and item['matches']:
                async def set_link(value, slot=slot):
                    await self.utility('shortcuts', values={slot: value})
                await self.owned('steam.link.' + slot, item['admin'], True, set_link, enabled)
            elif not enabled and 'steam.link.' + slot in self.state['records']:
                self.preserved.add('steam.link.' + slot)
                del self.state['records']['steam.link.' + slot]
                self.save()
        for item in self.state['appidFiles']:
            file = Path(item['path'])
            # Imported records are restricted to the two known erroneous SDK IDs.
            bad = base64.b64decode(item['bytes'], validate=True)
            if file.name != 'steam_appid.txt' or bad.strip(b'\r\n\t \0') not in (b'11857803', b'14637647'):
                raise ValueError('游戏监控备份格式不匹配')
            current_bytes = file.read_bytes() if file.exists() else None
            if enabled and current_bytes == bad:
                file.unlink()
            elif not enabled and current_bytes is None:
                atomic(file, bad)
        processes = inspection['steamProcesses']
        normal = any(p['elevation'] != 'Elevated' for p in processes)
        self.state['items']['steam'] = ('已启用；当前 Steam 需退出后重新从管理员入口打开' if normal else '已启用，Steam 启动入口正常') if enabled else ('已关闭；你后来更改的设置已保留' if any(k.startswith('steam.') for k in self.preserved) else '已关闭，原设置已恢复')

    async def gamebar(self, inspection, enabled):
        value = self.helper.read_bytes()
        current = digest(value)
        known = current in (self.patch['originalHash'], self.patch['patchedHash'])
        wanted = self.patch['patchedHash'] if enabled else self.patch['originalHash']
        records = self.state['records']
        if not known:
            # Preserve records to allow a later compatible restoration.
            raise ValueError('ClawTweaks 已更新或文件不匹配，未覆盖；需要适配新版本')
        cfg = json.loads(self.settings.read_text(encoding='utf-8-sig'))
        flag = cfg.get('GameBarFirstSlot_PreserveHome')
        home = inspection['home']
        if home is None:
            raise RuntimeError('暂时无法读取 Game Bar 主页设置：' + str(inspection.get('homeError', '')))
        change = (current != wanted or flag is not True or home is not True) if enabled else bool(any(key.startswith('gamebar.') for key in records))
        token = None
        try:
            if change:
                token = await self.maintenance(True, None)
                # Recheck the exact executable after the cooperative shutdown.
                if digest(self.helper.read_bytes()) != current:
                    raise RuntimeError('ClawTweaks 文件刚刚改变，未覆盖')
            async def set_helper(target):
                before = self.helper.read_bytes()
                result = patch_bytes(before, self.patch, target == self.patch['patchedHash'])
                backup = self.data / 'XboxGamingBarHelper.original.exe'
                if not backup.exists():
                    atomic(backup, before if digest(before) == self.patch['originalHash'] else patch_bytes(before, self.patch, False))
                atomic(self.helper, result)
            await self.owned('gamebar.helper', current, wanted, set_helper, enabled)
            async def set_flag(value):
                latest = json.loads(self.settings.read_text(encoding='utf-8-sig'))
                if latest.get('GameBarFirstSlot_PreserveHome') != flag:
                    raise RuntimeError('ClawTweaks 设置刚刚改变，请重新检查')
                if value is None:
                    latest.pop('GameBarFirstSlot_PreserveHome', None)
                else:
                    latest['GameBarFirstSlot_PreserveHome'] = value
                atomic(self.settings, latest)
            await self.owned('gamebar.flag', flag, True, set_flag, enabled)
            async def set_home(value):
                await self.utility('home', value=value)
            await self.owned('gamebar.home', home, True, set_home, enabled)
        finally:
            if token:
                await self.maintenance(False, token)
        self.state['items']['gamebar'] = '已启用，Xbox 键打开 Game Bar 主页' if enabled else ('已关闭；你后来更改的设置已保留' if any(k.startswith('gamebar.') for k in self.preserved) else '已关闭，原设置已恢复')

    async def check(self, action='check', feature=None, enabled=None):
        if action not in ('check', 'set', 'restore') or (action == 'set' and (feature not in ('steam', 'gamebar') or type(enabled) is not bool)):
            raise ValueError('修复设置格式错误')
        async with self.lock:
            self.preserved.clear()
            if self.state.get('error'):
                raise RuntimeError(self.state['error'])
            if action == 'set':
                self.state['enabled'][feature] = enabled
            elif action == 'restore':
                self.state['enabled'] = {'steam': False, 'gamebar': False}
            self.state['busy'], self.state['phase'] = True, '正在检查修复状态'
            self.save()
            errors = []
            try:
                inspection = await self.utility('inspect')
                for name, operation in (('steam', self.steam), ('gamebar', self.gamebar)):
                    if self.state['enabled'][name] or any(key.startswith(name + '.') for key in self.state['records']):
                        try:
                            await operation(inspection, self.state['enabled'][name])
                        except Exception as ex:
                            errors.append(str(ex))
                            self.state['items'][name] = '需要处理：' + str(ex)
                    else:
                        self.state['items'][name] = '未启用'
                self.state['phase'] = '；'.join(errors) if errors else '修复状态已检查'
            except Exception as ex:
                self.state['phase'] = '检查未完成：' + str(ex)
            finally:
                self.state['busy'] = False
                self.save()
            return self.view()

    async def close(self):
        async with self.lock:
            pass
