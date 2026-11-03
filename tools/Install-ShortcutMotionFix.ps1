param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\shortcut-motion-fix'
if (-not $Elevated) {
    $taskArguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Elevated'
    $taskProcess = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $taskArguments
    if ($taskProcess.ExitCode -ne 0) { throw 'Library update failed; see install-error.txt.' }
    Get-Content -LiteralPath (Join-Path $out 'installation.json')
    return
}
Start-Transcript -Path (Join-Path $out 'installation.log') -Append | Out-Null
try {
    $taskCurrent = Get-AppxPackage -Name 'ClawToolbox.Library'
    $taskBase = Get-Content -Raw -LiteralPath (Join-Path $out 'base.json') | ConvertFrom-Json
    if ($taskCurrent.Version.ToString() -ne '0.1.118.0') { throw 'Installed library version changed.' }
    if ((Get-FileHash -LiteralPath (Join-Path $taskCurrent.InstallLocation 'ClawLibrary.exe')).Hash.ToLowerInvariant() -ne $taskBase.installedExeSha256) { throw 'Installed library baseline changed.' }
    $taskOld = Join-Path $root 'artifacts\startup-shield\src\frontend\library-native\AppPackages\ClawLibrary_0.1.118.0_x64_Test\ClawLibrary_0.1.118.0_x64.msix'
    $taskNew = Join-Path $out 'src\frontend\library-native\AppPackages\ClawLibrary_0.1.119.0_x64_Test\ClawLibrary_0.1.119.0_x64.msix'
    $taskSigner = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
    foreach ($taskPackage in @($taskOld, $taskNew)) {
        & $taskSigner verify /pa $taskPackage *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Library signature verification failed.' }
    }
    Copy-Item -LiteralPath $taskOld -Destination (Join-Path $out 'ClawLibrary-0.1.118.0-rollback.msix')
    $taskCache = Join-Path $env:LOCALAPPDATA 'Packages\ClawToolbox.Library_ba3r68zr466za\LocalCache'
    $taskPrefs = Join-Path $taskCache 'ui.ini'
    $taskPrefsHash = (Get-FileHash -LiteralPath $taskPrefs).Hash
    Copy-Item -LiteralPath $taskPrefs -Destination (Join-Path $out 'ui-before.ini')
    $taskAgentFile = Join-Path $env:LOCALAPPDATA 'ClawToolbox\agent.json'
    $taskAgentPid = (Get-Content -Raw -LiteralPath $taskAgentFile | ConvertFrom-Json).pid
    $taskSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $taskPower = Join-Path $env:ProgramData ('ClawToolbox\SleepAnimation\' + $taskSid)
    $taskMotionHash = (Get-FileHash -LiteralPath (Join-Path $taskPower 'settings.json')).Hash
    $taskMonitorPid = (Get-Content -Raw -LiteralPath (Join-Path $taskPower 'monitor.json') | ConvertFrom-Json).pid
    Add-AppxPackage -Path $taskNew -ForceApplicationShutdown
    $taskInstalled = Get-AppxPackage -Name 'ClawToolbox.Library'
    if ($taskInstalled.Version.ToString() -ne '0.1.119.0') { throw 'Library update verification failed.' }
    $taskSha = (Get-FileHash -LiteralPath (Join-Path $taskInstalled.InstallLocation 'ClawLibrary.exe')).Hash.ToLowerInvariant()
    $taskBuilt = Join-Path $out 'src\frontend\library-native\b\x64\Release\win-x64\publish\ClawLibrary.exe'
    if ($taskSha -ne (Get-FileHash -LiteralPath $taskBuilt).Hash.ToLowerInvariant()) { throw 'Installed binary does not match the new build.' }
    if ((Get-FileHash -LiteralPath $taskPrefs).Hash -ne $taskPrefsHash) { throw 'Library preferences changed.' }
    if ((Get-Content -Raw -LiteralPath $taskAgentFile | ConvertFrom-Json).pid -ne $taskAgentPid) { throw 'Backend restarted unexpectedly.' }
    if ((Get-FileHash -LiteralPath (Join-Path $taskPower 'settings.json')).Hash -ne $taskMotionHash) { throw 'Power animation settings changed.' }
    if ((Get-Content -Raw -LiteralPath (Join-Path $taskPower 'monitor.json') | ConvertFrom-Json).pid -ne $taskMonitorPid) { throw 'Power animation monitor restarted unexpectedly.' }
    @{installed=$true;version='0.1.119.0';exeSha256=$taskSha;preferencesPreserved=$true;agentPid=$taskAgentPid;powerAnimationMonitorPid=$taskMonitorPid;powerAnimationSettingsPreserved=$true;stateRegressions=15;coveredWindowVisualConfirmed=$false} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'installation.json') -Encoding utf8
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $out 'install-error.txt') -Encoding utf8
    throw
} finally { Stop-Transcript | Out-Null }
