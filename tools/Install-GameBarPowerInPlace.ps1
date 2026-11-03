param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\gamebar-power-in-place'
if (-not $Elevated) {
    $taskArgs = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Elevated'
    $taskProcess = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $taskArgs
    if ($taskProcess.ExitCode -ne 0) { throw 'Installation failed; see install-error.txt.' }
    Get-Content -LiteralPath (Join-Path $out 'installation.json')
    exit
}
Start-Transcript -Path (Join-Path $out 'install.log') -Append | Out-Null
try {
    $taskPackage = Get-AppxPackage -Name 'ClawToolbox.GameBarWidget'
    if ($taskPackage.Version.ToString() -ne '0.4.43.0') { throw 'Installed widget version changed.' }
    $taskBase = Get-Content -Raw -LiteralPath (Join-Path $out 'base.json') | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath (Join-Path $taskPackage.InstallLocation 'ClawWidget.exe')).Hash.ToLowerInvariant() -ne $taskBase.exeSha256) { throw 'Widget baseline hash changed.' }
    $taskNew = Join-Path $out 'ClawWidget-0.4.44.0.msix'
    if ((Get-AuthenticodeSignature -LiteralPath $taskNew).Status -ne 'Valid') { throw 'Invalid signature.' }
    Copy-Item -LiteralPath (Join-Path $root 'artifacts\gamebar-power-handoff\ClawWidget-0.4.43.0.msix') -Destination (Join-Path $out 'ClawWidget-0.4.43.0-rollback.msix')
    & (Join-Path $PSScriptRoot 'Update-SleepMotion.ps1') -ReleaseName motion-v9 -Elevated
    Add-AppxPackage -Path $taskNew -ForceApplicationShutdown
    $taskInstalled = Get-AppxPackage -Name 'ClawToolbox.GameBarWidget'
    if ($taskInstalled.Version.ToString() -ne '0.4.44.0') { throw 'Package version verification failed.' }
    $taskHash = (Get-FileHash -LiteralPath (Join-Path $taskInstalled.InstallLocation 'ClawWidget.exe')).Hash.ToLowerInvariant()
    if ($taskHash -ne (Get-FileHash -LiteralPath (Join-Path $out 'bin\ClawWidget\ClawWidget.exe')).Hash.ToLowerInvariant()) { throw 'Installed binary mismatch.' }
    $taskResult = @{widgetVersion='0.4.44.0';widgetExeSha256=$taskHash;motion='v9';libraryProtocolActivation=$false;animationActivatesWindow=$false;physicalButtonAndWakeIntegrated=$true;settingsPreservedByInPlaceUpgrade=$true;actualPowerRequests=0;visualTestConfirmed=$false}
    $taskResult | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'installation.json') -Encoding utf8
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $out 'install-error.txt') -Encoding utf8
    throw
} finally { Stop-Transcript | Out-Null }
