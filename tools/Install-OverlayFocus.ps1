$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOut = Join-Path $taskRoot 'artifacts\overlay-focus-fix-191'
$taskPkg = Get-AppxPackage ClawToolbox.Library
if ($taskPkg.Version.ToString() -notin @('0.1.190.0','0.1.191.0','0.1.192.0','0.1.193.0','0.1.194.0','0.1.195.0')) { throw 'Installed library changed; merge the newer baseline first.' }
$taskBaseExe = Join-Path $taskRoot '.claude\worktrees\bold-jennings-a19012\artifacts\qp\src\frontend\library-native\b\x64\Release\win-x64\publish\ClawLibrary.exe'
$taskExpectedHash = if ($taskPkg.Version.ToString() -in @('0.1.191.0','0.1.192.0','0.1.193.0','0.1.194.0','0.1.195.0')) { (Get-Content -Raw -LiteralPath (Join-Path $taskOut 'installation.json') | ConvertFrom-Json).exeSha256 } else { (Get-FileHash -LiteralPath $taskBaseExe).Hash }
if ($taskExpectedHash -ne (Get-FileHash -LiteralPath (Join-Path $taskPkg.InstallLocation 'ClawLibrary.exe')).Hash) { throw 'Installed binary does not match the reviewed baseline.' }
$taskCache = Join-Path $env:LOCALAPPDATA 'Packages\ClawToolbox.Library_ba3r68zr466za\LocalCache'
$taskPrefs = @{}
Get-ChildItem -LiteralPath $taskCache -Filter *.ini | ForEach-Object {
    $taskPrefs[$_.Name] = (Get-FileHash -LiteralPath $_.FullName).Hash
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskOut ($_.Name + '.before'))
}
$taskMsix = Join-Path $taskOut 'src\frontend\library-native\AppPackages\ClawLibrary_0.1.196.0_x64_Test\ClawLibrary_0.1.196.0_x64.msix'
& 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' verify /pa $taskMsix *> (Join-Path $taskOut 'signature.log')
if ($LASTEXITCODE) { throw 'MSIX signature verification failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot '.claude\worktrees\bold-jennings-a19012\artifacts\qp\src\frontend\library-native\AppPackages\ClawLibrary_0.1.190.0_x64_Test\ClawLibrary_0.1.190.0_x64.msix') -Destination (Join-Path $taskOut 'ClawLibrary-0.1.190.0-rollback.msix') -Force
$taskModePath = Join-Path $env:LOCALAPPDATA 'ClawToolbox\shell\state.json'
$taskModeBefore = if (Test-Path -LiteralPath $taskModePath) { Get-Content -Raw -LiteralPath $taskModePath | ConvertFrom-Json } else { $null }
function Invoke-OverlayInstallMode([string[]]$ModeArguments) {
    $taskModeProcess = Start-Process -FilePath (Join-Path $env:LOCALAPPDATA 'ClawToolbox\shell\ClawShell.exe') -ArgumentList $ModeArguments -WindowStyle Hidden -PassThru
    if (-not $taskModeProcess.WaitForExit(20000) -or $taskModeProcess.ExitCode -ne 0) { throw 'Mode handoff failed during library update.' }
}
# The existing supervisor counts package replacement as a crash loop. Use its
# supported desktop/game handoff around the update, preserving the login default.
if ($taskModeBefore.mode -eq 'game') { Invoke-OverlayInstallMode -ModeArguments @('to-desktop') }
try { Add-AppxPackage -Path $taskMsix -ForceApplicationShutdown }
finally {
    if ($taskModeBefore) {
        $taskModeNow = Get-Content -Raw -LiteralPath $taskModePath | ConvertFrom-Json
        if ($taskModeNow.default -ne $taskModeBefore.default) { Invoke-OverlayInstallMode -ModeArguments @('set-default', $taskModeBefore.default) }
        if ($taskModeBefore.mode -eq 'game' -and (Get-AppxPackage ClawToolbox.Library)) { Invoke-OverlayInstallMode -ModeArguments @('to-game') }
    }
}
$taskAfter = Get-AppxPackage ClawToolbox.Library
if ($taskAfter.Version.ToString() -ne '0.1.196.0') { throw 'Installed version mismatch.' }
$taskBuiltExe = Join-Path $taskOut 'src\frontend\library-native\b\x64\Release\win-x64\publish\ClawLibrary.exe'
if ((Get-FileHash -LiteralPath $taskBuiltExe).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskAfter.InstallLocation 'ClawLibrary.exe')).Hash) { throw 'Installed executable mismatch.' }
$taskChanged = @($taskPrefs.Keys | Where-Object { (Get-FileHash -LiteralPath (Join-Path $taskCache $_)).Hash -ne $taskPrefs[$_] })
@{beforeVersion=$taskPkg.Version.ToString();afterVersion=$taskAfter.Version.ToString();preferencesBefore=$taskPrefs;changedPreferences=$taskChanged;exeSha256=(Get-FileHash -LiteralPath $taskBuiltExe).Hash;shellBefore=$taskModeBefore;shellAfter=(Get-Content -Raw -LiteralPath $taskModePath | ConvertFrom-Json)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOut 'installation.json') -Encoding utf8
if ($taskChanged.Count) { throw 'Library preferences changed.' }
Write-Output 'Installed 0.1.196.0; preferences preserved.'
