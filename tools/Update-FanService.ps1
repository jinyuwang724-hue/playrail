# Replace only the existing fan service. Keep agent, power settings and fan configuration intact.
param([switch]$Elevated, [ValidateSet(0,4700)][int]$RpmLimit = 0)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskReportFolder = Join-Path $taskRoot 'diagnostics\fan-rpm-ceiling-20261022'
[IO.Directory]::CreateDirectory($taskReportFolder) | Out-Null
$taskReportPath = Join-Path $taskReportFolder 'deployment.json'
$taskAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $taskAdmin) {
    $taskArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Elevated','-RpmLimit',$RpmLimit)
    $taskChild = Start-Process powershell.exe -ArgumentList $taskArgs -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if (Test-Path -LiteralPath $taskReportPath) { Get-Content -LiteralPath $taskReportPath -Raw -Encoding UTF8 }
    if ($taskChild.ExitCode -ne 0) { throw 'Fan service update failed; see deployment.json.' }
    return
}
Start-Transcript -LiteralPath (Join-Path $taskReportFolder 'deployment.log') -Force | Out-Null
$taskResult = [ordered]@{passed=$false;settingsPreserved=$false;rolledBack=$false}
$taskServiceExe = Join-Path $env:ProgramFiles 'ClawToolbox\FanService\ClawFanService.exe'
$taskCandidate = Join-Path $taskRoot 'plugin\bin\ClawFanService.exe'
$taskConfig = Join-Path $env:ProgramData 'ClawToolbox\FanService\service.json'
$taskPower = Join-Path $env:LOCALAPPDATA 'ClawToolbox\data\power.json'
$taskBackup = Join-Path $taskReportFolder ('backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$taskStopped = $false
function Read-FanState {
    $taskReply = '{"command":"get"}' | & $taskServiceExe --client | ConvertFrom-Json
    if (-not $taskReply.ok) { throw $taskReply.error }
    return $taskReply.fan
}
try {
    $taskPath = [IO.Path]::GetFullPath($taskServiceExe)
    while ($taskPath) {
        if ((Get-Item -LiteralPath $taskPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a redirected service path.' }
        $taskParent = Split-Path -Parent $taskPath
        if ($taskParent -eq $taskPath) { break }; $taskPath = $taskParent
    }
    [IO.Directory]::CreateDirectory($taskBackup) | Out-Null
    $taskResult.before = Read-FanState
    $taskConfigHash = (Get-FileHash -LiteralPath $taskConfig).Hash
    $taskPowerHash = (Get-FileHash -LiteralPath $taskPower).Hash
    Copy-Item -LiteralPath $taskServiceExe -Destination (Join-Path $taskBackup 'ClawFanService.exe')
    Copy-Item -LiteralPath $taskConfig -Destination (Join-Path $taskBackup 'service.json')
    Copy-Item -LiteralPath $taskPower -Destination (Join-Path $taskBackup 'power.json')
    Stop-Service ClawFanService
    (Get-Service ClawFanService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
    $taskStopped = $true
    if ($RpmLimit -eq 4700) {
        $taskConfigValue = Get-Content -LiteralPath $taskConfig -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($taskConfigValue.rpmLimit -ne 4700) {
            $taskConfigValue | Add-Member -NotePropertyName rpmLimit -NotePropertyValue 4700 -Force
            $taskConfigValue.revision++
            $taskTemporaryConfig = $taskConfig + '.rpm-limit.tmp'
            [IO.File]::WriteAllText($taskTemporaryConfig,($taskConfigValue|ConvertTo-Json -Depth 12 -Compress),(New-Object Text.UTF8Encoding($false)))
            [IO.File]::Replace($taskTemporaryConfig,$taskConfig,(Join-Path $taskBackup 'service.replace-backup.json'))
            $taskResult.rpmPolicyUpdated = $true
            $taskConfigHash = (Get-FileHash -LiteralPath $taskConfig).Hash
        }
    }
    # UI status clients run from the same executable and may still hold its image open.
    # Rename the old image atomically; existing readers finish against that image.
    $taskPreviousImage = Join-Path (Split-Path -Parent $taskServiceExe) ('ClawFanService.previous-'+[Guid]::NewGuid().ToString('N')+'.exe')
    Move-Item -LiteralPath $taskServiceExe -Destination $taskPreviousImage
    Copy-Item -LiteralPath $taskCandidate -Destination $taskServiceExe -Force
    Start-Service ClawFanService
    (Get-Service ClawFanService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    $taskAfter = Read-FanState
    if ($taskAfter.serviceVersion -ne '1.0.6') { throw 'Wrong service version after update.' }
    if ($RpmLimit -eq 4700 -and $taskAfter.rpmLimit -ne 4700) { throw 'RPM ceiling was not loaded.' }
    if ($taskAfter.error) { throw ('Fan service reports: '+$taskAfter.error) }
    $taskResult.after = $taskAfter
    $taskResult.settingsPreserved = (Get-FileHash -LiteralPath $taskConfig).Hash -eq $taskConfigHash -and (Get-FileHash -LiteralPath $taskPower).Hash -eq $taskPowerHash
    $taskResult.fanSettingsPreserved = ($taskAfter.settings|ConvertTo-Json -Depth 12 -Compress) -eq ($taskResult.before.settings|ConvertTo-Json -Depth 12 -Compress)
    if (-not $taskResult.fanSettingsPreserved) { throw 'Saved fan curves changed unexpectedly.' }
    if (-not $taskResult.settingsPreserved) { throw 'Settings changed unexpectedly.' }
    $taskResult.binaryHash = (Get-FileHash -LiteralPath $taskServiceExe).Hash
    if ($taskResult.binaryHash -ne (Get-FileHash -LiteralPath $taskCandidate).Hash) { throw 'Installed binary differs from build.' }
    $taskResult.passed = $true
} catch {
    $taskResult.error = $_.Exception.Message
    if ($taskStopped -and (Test-Path -LiteralPath (Join-Path $taskBackup 'ClawFanService.exe'))) {
        try {
            Stop-Service ClawFanService -ErrorAction SilentlyContinue
            (Get-Service ClawFanService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
            if (Test-Path -LiteralPath $taskServiceExe) {
                $taskFailedImage = Join-Path (Split-Path -Parent $taskServiceExe) ('ClawFanService.failed-'+[Guid]::NewGuid().ToString('N')+'.exe')
                Move-Item -LiteralPath $taskServiceExe -Destination $taskFailedImage
            }
            Copy-Item -LiteralPath (Join-Path $taskBackup 'ClawFanService.exe') -Destination $taskServiceExe -Force
            Copy-Item -LiteralPath (Join-Path $taskBackup 'service.json') -Destination $taskConfig -Force
            Start-Service ClawFanService
            $taskResult.rolledBack = $true
        } catch { $taskResult.rollbackError = $_.Exception.Message }
    }
} finally {
    [IO.File]::WriteAllText($taskReportPath,($taskResult|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
    Stop-Transcript | Out-Null
}
if (-not $taskResult.passed) { exit 1 }
