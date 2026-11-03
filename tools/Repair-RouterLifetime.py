"""Retire the verified orphan gracefully and install parent-bound button routing."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
HOME = Path(os.environ['LOCALAPPDATA']) / 'ClawToolbox'
OUT = ROOT / 'artifacts/library-toggle/router-recovery'
spec = importlib.util.spec_from_file_location('deploy', Path(__file__).with_name('Deploy-ScalingSettings.py'))
deploy = importlib.util.module_from_spec(spec); spec.loader.exec_module(deploy)


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    binary = HOME / 'app/bin/ClawRouter-0.17.58.exe'
    service = HOME / 'app/claw_service.py'
    if sha(binary) != '3aa65c909b7f21681154721e093fd7a2c9e98611c72ef9a0b81176ba9f5c94fb':
        raise RuntimeError('Installed router changed; preserve the newer version')
    if sha(service) != '8dd1e4250b68d36c4f2a3b33bb30121a05ce42e8256310c91daee7e5cbdddd8e':
        raise RuntimeError('Installed service changed; prepare a fresh integration')
    text = service.read_text(encoding='utf-8-sig')
    source = (ROOT / 'plugin/claw_service.py').read_text(encoding='utf-8-sig')
    function = source[source.index('async def start_router('):source.index('\n\nclass Service:')]
    text = text.replace('class Service:', function + '\n\nclass Service:', 1)
    begin = text.index('        self._process = await asyncio.create_subprocess_exec(')
    end = text.index('        self._started.set()', begin)
    text = text[:begin] + '        self._process = await start_router(\n            PLUGIN_ROOT / "bin" / ("ClawRouter-" + version + ".exe"), self._data, config)\n' + text[end:]
    compile(text, 'claw_service.py', 'exec')
    (OUT / 'claw_service.py').write_text(text, encoding='utf-8')
    backup = HOME / 'repair-backups' / time.strftime('router-lifetime-%Y%m%d-%H%M%S')
    backup.mkdir(parents=True)
    shutil.copy2(binary, backup / binary.name); shutil.copy2(service, backup / service.name)
    for file in ('restore.json', 'settings.json'):
        if (HOME / 'data' / file).exists(): shutil.copy2(HOME / 'data' / file, backup / file)
    preserved = {str(p.relative_to(HOME)): sha(p) for p in (HOME / 'data/settings.json', HOME / 'data/power.json', HOME / 'current.json', HOME / 'app/plugin.json') if p.exists()}
    (backup / 'before.json').write_text(json.dumps({'preserved':preserved}), encoding='utf-8')
    marker = HOME / 'agent-update-until'
    marker.write_text(str(time.time() + 110), encoding='ascii')
    previous = None
    try:
        previous = deploy.stop()
        # WM_QUIT reaches the old router's actual main message loop, allowing its
        # existing using/Dispose cleanup to run. Never terminate a restore in flight.
        script = r'''
$ErrorActionPreference='Stop'
$taskOrphan=Get-CimInstance Win32_Process -Filter 'ProcessId=18320'
if($taskOrphan) {
    $taskExpected=Join-Path $env:LOCALAPPDATA 'ClawToolbox\app\bin\ClawRouter-0.17.58.exe'
    if($taskOrphan.ExecutablePath -ne $taskExpected){throw 'Orphan executable identity changed'}
    $taskParent=Get-CimInstance Win32_Process -Filter ('ProcessId='+$taskOrphan.ParentProcessId)
    if($taskParent -and $taskParent.CreationDate -lt $taskOrphan.CreationDate){throw 'Router still has a living owner'}
    $taskProcess=Get-Process -Id 18320
    if($taskProcess.StartTime -gt [datetime]'2026-10-25T22:31:00'){throw 'Router PID was reused'}
    Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class RouterRecoveryQuit{[DllImport("user32.dll",SetLastError=true)]public static extern bool PostThreadMessage(uint thread,uint message,UIntPtr wp,IntPtr lp);}'
    $taskThreads=foreach($taskItem in $taskProcess.Threads){try{[pscustomobject]@{Id=$taskItem.Id;Started=$taskItem.StartTime}}catch{}}
    $taskThread=$taskThreads|Sort-Object Started|Select-Object -First 1
    if(-not [RouterRecoveryQuit]::PostThreadMessage([uint32]$taskThread.Id,0x12,[UIntPtr]::Zero,[IntPtr]::Zero)){throw 'Could not request graceful orphan exit'}
    if(-not $taskProcess.WaitForExit(5000)){
        # This exact process has no owner and has already ignored a prior 20s
        # graceful exit. Preserve its journal and refuse if a restore child exists.
        $taskChildren=@(Get-CimInstance Win32_Process -Filter 'ParentProcessId=18320'|Where-Object {$_.Name -ne 'conhost.exe'})
        if($taskChildren.Count -gt 0 -or (Get-Process XboxGamingBarHelper -ErrorAction SilentlyContinue)){throw 'A restore helper is still running; no process terminated'}
        $taskProcess.Kill()
        if(-not $taskProcess.WaitForExit(5000)){throw 'The disconnected orphan did not exit'}
    }
}
if(Get-Process -Name 'ClawRouter-0.17.58' -ErrorAction SilentlyContinue){throw 'Another router is still running; no file replaced'}
'''
        subprocess.run(['powershell.exe', '-NoProfile', '-Command', script], check=True, timeout=30)
        shutil.copy2(OUT / 'ClawRouter.exe', binary)
        shutil.copy2(OUT / 'claw_service.py', service)
        marker.unlink(missing_ok=True)
        pid = deploy.start(previous)
        deadline = time.monotonic() + 10
        while True:
            state = deploy.call('refresh')
            status = state['status']
            if status.get('configured') and status.get('canObserve') and status.get('released'): break
            if time.monotonic() > deadline: raise RuntimeError('Physical button listener did not become ready: ' + str(status))
            time.sleep(.2)
        assert all(sha(HOME / p) == digest for p,digest in preserved.items()), 'Saved configuration changed'
    except Exception:
        marker.unlink(missing_ok=True)
        # Do not overwrite a running binary or kill a helper during its cleanup.
        print('Recovery backup: ' + str(backup))
        if previous is not None:
            try: deploy.start(previous)
            except Exception: pass
        raise
    shutil.copy2(service, ROOT / 'artifacts/library-toggle/src/plugin/claw_service.py')
    report = {'backup':str(backup), 'agentPid':pid, 'settingsPreserved':True,
        'routerHash':sha(binary), 'serviceHash':sha(service), 'status':status}
    (OUT / 'deployment.json').write_text(json.dumps(report,ensure_ascii=False,indent=2), encoding='utf-8')
    print(json.dumps(report,ensure_ascii=True))


if __name__ == '__main__': main()
