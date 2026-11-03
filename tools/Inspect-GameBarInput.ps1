param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $root 'artifacts\gamebar-dismiss-diagnosis\input-api-elevated'
if (-not $Elevated) {
    $taskArgs = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Elevated'
    $taskChild = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $taskArgs
    if ($taskChild.ExitCode -ne 0) { throw 'Read-only probe failed.' }
    Get-Content -LiteralPath (Join-Path $taskOutput 'gamebar-inspection.json')
    return
}
$taskExe = Join-Path $root 'artifacts\sleep-animation\motion-v10\app\ClawSleepAnimation.exe'
$taskArgs = '--inspect-gamebar --state-dir "' + $taskOutput + '"'
$taskProbe = Start-Process -FilePath $taskExe -ArgumentList $taskArgs -Wait -PassThru -WindowStyle Hidden
if ($taskProbe.ExitCode -ne 0) { throw 'Read-only probe failed.' }
