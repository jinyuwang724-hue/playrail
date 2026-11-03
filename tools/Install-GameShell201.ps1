# 游戏库 0.1.203 → 0.1.204 原位升级（不先卸载，LocalCache 里的偏好保留），外壳换成 1.0.2 并重启 watch 监视进程。
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'artifacts\game-shell-201'
$before=Get-AppxPackage ClawToolbox.Library
if ([version]$before.Version -ge [version]'0.1.204.0') { throw ('Installed '+$before.Version+' is not older than 0.1.204.0') }
$msix=Join-Path $root 'frontend\library-native\AppPackages\ClawLibrary_0.1.204.0_x64_Test\ClawLibrary_0.1.204.0_x64.msix'
& 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' verify /pa $msix *> (Join-Path $out 'signature.log')
if ($LASTEXITCODE) { throw 'Signature verification failed' }
$statePath=Join-Path $env:LOCALAPPDATA 'ClawToolbox\shell\state.json'
$mode=Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
$stable=Join-Path $env:LOCALAPPDATA 'ClawToolbox\shell\ClawShell.exe'
$cache=Join-Path $env:LOCALAPPDATA 'Packages\ClawToolbox.Library_ba3r68zr466za\LocalCache'
$prefs=@{}
Get-ChildItem -LiteralPath $cache -Filter *.ini | ForEach-Object { $prefs[$_.Name]=(Get-FileHash -LiteralPath $_.FullName).Hash; Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $out ($_.Name+'.before')) }
function Invoke-Shell([string]$exe,[string[]]$shellArgs) {
    $child=Start-Process -FilePath $exe -ArgumentList $shellArgs -WindowStyle Hidden -PassThru
    if (-not $child.WaitForExit(25000) -or $child.ExitCode -ne 0) { throw ('Shell '+($shellArgs -join ' ')+' failed') }
}
if ($mode.mode -eq 'game') { Invoke-Shell $stable @('to-desktop') }
try {
    Add-AppxPackage -Path $msix -ForceApplicationShutdown
    # 新外壳跑一次 status：InstallSelf 把自己装到稳定位置
    Invoke-Shell (Join-Path $out 'ClawShell.exe') @('status')
    # 2026-11-03 实测第一次 status 偶尔没换掉稳定位置的旧外壳：核对哈希，不一致再跑一次
    if ((Get-FileHash $stable).Hash -ne (Get-FileHash (Join-Path $out 'ClawShell.exe')).Hash) { Invoke-Shell (Join-Path $out 'ClawShell.exe') @('status') }
    if ((Get-FileHash $stable).Hash -ne (Get-FileHash (Join-Path $out 'ClawShell.exe')).Hash) { throw 'Stable ClawShell.exe was not replaced' }
    $shells=@(Get-CimInstance Win32_Process -Filter "Name='ClawShell.exe'")
    $loginShells=@($shells | Where-Object CommandLine -Match '\s+shell\s*$')
    if ($loginShells.Count -eq 0) {
        foreach ($w in @($shells | Where-Object CommandLine -Match '\s+watch\s*$')) {
            if ((Split-Path -Parent $w.ExecutablePath) -ne (Split-Path -Parent $stable)) { throw 'Unexpected shell supervisor path' }
            $p=Get-Process -Id $w.ProcessId -ErrorAction SilentlyContinue
            Stop-Process -Id $w.ProcessId -Force
            if ($p) { $p.WaitForExit(5000) | Out-Null }
        }
        Start-Process -FilePath $stable -ArgumentList 'watch' -WindowStyle Hidden | Out-Null
    }
}
finally {
    if ($mode.mode -eq 'game') { Invoke-Shell $stable @('to-game') }
    $now=Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    if ($now.default -ne $mode.default) { Invoke-Shell $stable @('set-default',$mode.default) }
}
$after=Get-AppxPackage ClawToolbox.Library
$changed=@($prefs.Keys | Where-Object { (Get-FileHash -LiteralPath (Join-Path $cache $_)).Hash -ne $prefs[$_] })
@{beforeVersion=$before.Version.ToString();afterVersion=$after.Version.ToString();changedPreferences=$changed;shellBefore=$mode;shellAfter=(Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json);loginShells=$loginShells.Count} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'installation.json') -Encoding utf8
if ($changed.Count) { throw 'Preferences changed' }
Write-Output ('Installed '+$after.Version+'; preferences preserved')
