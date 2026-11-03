param([string]$FromVersion='0.1.196.0',[string]$Version='0.1.197.0',[switch]$SkipBackend)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOut=Join-Path $taskRoot 'artifacts\special-keys-197'
$taskPackage=Get-AppxPackage ClawToolbox.Library
if ($taskPackage.Version.ToString() -ne $FromVersion) { throw 'Installed version changed; merge it before updating.' }
$taskMsix=Join-Path $taskOut ('src\frontend\library-native\AppPackages\ClawLibrary_'+$Version+'_x64_Test\ClawLibrary_'+$Version+'_x64.msix')
& 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' verify /pa $taskMsix *> (Join-Path $taskOut 'signature.log')
if ($LASTEXITCODE) { throw 'Signature verification failed' }
$taskCache=Join-Path $env:LOCALAPPDATA 'Packages\ClawToolbox.Library_ba3r68zr466za\LocalCache'
$taskPrefs=@{}
Get-ChildItem -LiteralPath $taskCache -Filter *.ini | ForEach-Object { $taskPrefs[$_.Name]=(Get-FileHash -LiteralPath $_.FullName).Hash; Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskOut ($_.Name+'.before')) }
$taskStatePath=Join-Path $env:LOCALAPPDATA 'ClawToolbox\shell\state.json'
$taskMode=Get-Content -Raw -LiteralPath $taskStatePath | ConvertFrom-Json
function Invoke-SpecialShell([string[]]$ShellArguments) {
    $taskProcess=Start-Process -FilePath (Join-Path $taskOut 'ClawShell.exe') -ArgumentList $ShellArguments -WindowStyle Hidden -PassThru
    if (-not $taskProcess.WaitForExit(20000) -or $taskProcess.ExitCode -ne 0) { throw 'Shell handoff failed' }
}
# InstallSelf moves the old executable aside, preserving running login shells.
Invoke-SpecialShell -ShellArguments @('status')
if ($taskMode.mode -eq 'game') { Invoke-SpecialShell -ShellArguments @('to-desktop') }
try {
    Add-AppxPackage -Path $taskMsix -ForceApplicationShutdown
    if (-not $SkipBackend) {
        & 'C:\Users\13304\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' (Join-Path $taskRoot 'tools\Deploy-EnhancedSleep.py') $taskOut
        if ($LASTEXITCODE) { throw 'Backend deployment failed' }
    }
}
finally {
    if ($taskMode.mode -eq 'game') { Invoke-SpecialShell -ShellArguments @('to-game') }
    $taskNow=Get-Content -Raw -LiteralPath $taskStatePath | ConvertFrom-Json
    if ($taskNow.default -ne $taskMode.default) { Invoke-SpecialShell -ShellArguments @('set-default',$taskMode.default) }
}
$taskAfter=Get-AppxPackage ClawToolbox.Library
$taskBuilt=Join-Path $taskOut 'src\frontend\library-native\b\x64\Release\win-x64\publish\ClawLibrary.exe'
if ((Get-FileHash -LiteralPath $taskBuilt).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskAfter.InstallLocation 'ClawLibrary.exe')).Hash) { throw 'Installed executable mismatch' }
$taskChanged=@($taskPrefs.Keys | Where-Object { (Get-FileHash -LiteralPath (Join-Path $taskCache $_)).Hash -ne $taskPrefs[$_] })
@{beforeVersion=$taskPackage.Version.ToString();afterVersion=$taskAfter.Version.ToString();preferencesBefore=$taskPrefs;changedPreferences=$taskChanged;exeSha256=(Get-FileHash -LiteralPath $taskBuilt).Hash;shellBefore=$taskMode;shellAfter=(Get-Content -Raw -LiteralPath $taskStatePath | ConvertFrom-Json)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOut 'installation.json') -Encoding utf8
if ($taskChanged.Count) { throw 'Preferences changed' }
Write-Output ('Installed '+$taskAfter.Version.ToString()+'; preferences preserved.')
