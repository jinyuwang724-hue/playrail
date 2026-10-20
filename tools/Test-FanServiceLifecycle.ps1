param([switch]$Exercise,[switch]$Elevated)
$ErrorActionPreference = 'Stop'
if (-not $Exercise) { Get-Service ClawFanService | Select-Object Name,Status,StartType; return }
$taskAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $taskAdmin) {
    $taskArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Exercise','-Elevated')
    $taskChild = Start-Process powershell.exe -ArgumentList $taskArgs -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if ($taskChild.ExitCode -ne 0) { throw 'Fan service lifecycle verification failed; see diagnostics/fan-service/lifecycle.log.' }
    Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'diagnostics\fan-service\lifecycle.json')
    return
}
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskLog = Join-Path $taskRoot 'diagnostics\fan-service\lifecycle.log'
$taskReportPath = Join-Path $taskRoot 'diagnostics\fan-service\lifecycle.json'
Start-Transcript -LiteralPath $taskLog -Force | Out-Null
$taskReport = [ordered]@{passed=$false;rebootTested=$false;sleepTested=$false;firmwareFlashed=$false}
try {
    [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
    $taskClient = Join-Path $env:ProgramFiles 'ClawToolbox\FanService\ClawFanService.exe'
    function Get-FanServiceState {
        $taskResult = '{"command":"get"}' | & $taskClient --client | ConvertFrom-Json
        if (-not $taskResult.ok) { throw $taskResult.error }; return $taskResult.fan
    }
    $taskBefore = Get-FanServiceState
    $taskReport.before = $taskBefore
    Stop-Service ClawFanService
    (Get-Service ClawFanService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
    $taskAuto = & (Join-Path $taskRoot 'artifacts\FanReadOnlyProbe.exe') | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $taskAuto.software -or $taskAuto.fullSpeed) { throw 'Normal service stop did not release factory Auto.' }
    $taskReport.normalStopAuto = $taskAuto
    Start-Service ClawFanService
    (Get-Service ClawFanService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    $taskDeadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 250; $taskRestarted = Get-FanServiceState } while (($taskRestarted.error -or $taskRestarted.target -eq $null) -and (Get-Date) -lt $taskDeadline)
    if ($taskRestarted.error -or $taskRestarted.settings.mode -ne $taskBefore.settings.mode -or $taskRestarted.revision -ne $taskBefore.revision) { throw 'Restart did not reload saved mode.' }
    $taskReport.restart = $taskRestarted
    $taskService = Get-CimInstance Win32_Service -Filter "Name='ClawFanService'"
    $taskServicePid = [int]$taskService.ProcessId
    $taskProcess = Get-Process -Id $taskServicePid
    if ($taskProcess.ProcessName -ne 'ClawFanService') { throw 'Unexpected process identity; crash test aborted.' }
    Stop-Process -Id $taskServicePid -Force
    $taskDeadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $taskService = Get-CimInstance Win32_Service -Filter "Name='ClawFanService'"
    } while (($taskService.State -ne 'Running' -or $taskService.ProcessId -eq $taskServicePid) -and (Get-Date) -lt $taskDeadline)
    if ($taskService.State -ne 'Running' -or $taskService.ProcessId -eq $taskServicePid) { throw 'Service crash recovery failed.' }
    $taskDeadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 250; $taskRecovered = Get-FanServiceState } while (($taskRecovered.error -or $taskRecovered.target -eq $null) -and (Get-Date) -lt $taskDeadline)
    if ($taskRecovered.error -or $taskRecovered.settings.mode -ne $taskBefore.settings.mode -or $taskRecovered.revision -ne $taskBefore.revision) { throw 'Recovered service did not reload saved mode.' }
    $taskReport.crashRecovery = @{oldPid=$taskServicePid;newPid=$taskService.ProcessId;state=$taskRecovered}
    $taskReport.passed = $true
} catch {
    $taskReport.error = $_.Exception.Message
    Write-Error $_ -ErrorAction Continue
} finally {
    if ((Get-Service ClawFanService).Status -ne 'Running') { Start-Service ClawFanService }
    [IO.File]::WriteAllText($taskReportPath,($taskReport | ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
    Stop-Transcript | Out-Null
}
if (-not $taskReport.passed) { exit 1 }
