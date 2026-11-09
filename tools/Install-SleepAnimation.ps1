param(
    [string]$Source = (Join-Path $PSScriptRoot '..\artifacts\sleep-animation\verified-app'),
    [string]$Report = (Join-Path $PSScriptRoot '..\artifacts\sleep-animation\installation.json'),
    [switch]$Remove, [switch]$Verify
)
$ErrorActionPreference = 'Stop'
$taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$taskIdentity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run the installer as administrator.' }
$taskSid = $taskIdentity.User.Value
$taskState = Join-Path $env:ProgramData ('ClawToolbox\SleepAnimation\' + $taskSid)
$taskPath = '\ClawToolbox\'
$taskName = 'SleepAnimation'; $taskRecovery = 'SleepAnimationRecovery'
$taskConfig = Join-Path $taskState 'installation.json'
$taskChecks = [Collections.Generic.List[object]]::new()
$taskBackup = @{}
$taskPreviousInstallation = if(Test-Path -LiteralPath $taskConfig){[IO.File]::ReadAllText($taskConfig)}else{$null}

function Get-ButtonPolicy {
    $taskText = (& powercfg /qh SCHEME_CURRENT SUB_BUTTONS PBUTTONACTION) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Power-button query failed.' }
    $taskValues = [regex]::Matches($taskText, '0x([0-9a-fA-F]{8})')
    if ($taskValues.Count -ne 2) { throw 'Unexpected power-button query result.' }
    [pscustomobject]@{ac=[Convert]::ToInt32($taskValues[0].Groups[1].Value,16);dc=[Convert]::ToInt32($taskValues[1].Groups[1].Value,16)}
}
function Assert-Policy([int]$Ac, [int]$Dc, [string]$Label) {
    $taskActual = Get-ButtonPolicy
    if ($taskActual.ac -ne $Ac -or $taskActual.dc -ne $Dc) { throw ($Label + ': unexpected policy: ' + ($taskActual|ConvertTo-Json -Compress)) }
    $taskChecks.Add([pscustomobject]@{check=$Label;passed=$true;ac=$Ac;dc=$Dc})
}
function Wait-Restored {
    $taskUntil=[DateTime]::UtcNow.AddSeconds(10)
    do {
        $taskPolicy=Get-ButtonPolicy
        if ($taskPolicy.ac -eq $taskOriginal.ac -and $taskPolicy.dc -eq $taskOriginal.dc) { return }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $taskUntil)
    throw 'Power-button restoration timed out.'
}
function Invoke-Helper([string]$Executable, [string]$Mode, [string]$State) {
    $taskChild=Start-Process -FilePath $Executable -ArgumentList ($Mode+' --state-dir "'+$State+'"') -PassThru -WindowStyle Hidden
    if (-not $taskChild.WaitForExit(5000)) { throw 'Helper did not complete.' }
    if ($taskChild.ExitCode -notin 0,2) { throw ('Helper failed: '+$Mode+' '+$taskChild.ExitCode) }
}
function Wait-Monitor([string]$Executable, [string]$State) {
    $taskUntil=[DateTime]::UtcNow.AddSeconds(12)
    while ([DateTime]::UtcNow -lt $taskUntil) {
        try {
            $taskReady=Get-Content -Raw -LiteralPath (Join-Path $State 'monitor.json') -ErrorAction Stop|ConvertFrom-Json
            $taskProcess=Get-Process -Id $taskReady.pid -ErrorAction Stop
            if ($taskProcess.Path -eq $Executable) { return $taskReady }
        } catch { }
        Start-Sleep -Milliseconds 150
    }
    throw ('Monitor did not report ready: '+$State)
}
function Stop-Installed {
    if (Test-Path -LiteralPath $taskConfig) {
        $taskOld=Get-Content -Raw -LiteralPath $taskConfig|ConvertFrom-Json
        Invoke-Helper $taskOld.executable '--stop' $taskState
    }
    $taskExisting=Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
    if ($taskExisting) {
        $taskUntil=[DateTime]::UtcNow.AddSeconds(4)
        while ((Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName).State -eq 'Running' -and [DateTime]::UtcNow -lt $taskUntil) { Start-Sleep -Milliseconds 100 }
        if ((Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName).State -eq 'Running') { Stop-ScheduledTask -TaskPath $taskPath -TaskName $taskName }
        $taskLease=Join-Path $taskState 'active-lease.json'
        $taskUntil=[DateTime]::UtcNow.AddSeconds(10)
        while((Test-Path -LiteralPath $taskLease) -and [DateTime]::UtcNow -lt $taskUntil){Start-Sleep -Milliseconds 150}
        if(Test-Path -LiteralPath $taskLease){throw 'Installed monitor did not release the power-button policy.'}
    }
}
try {
    New-Item -ItemType Directory -Path $taskState -Force|Out-Null
    # SYSTEM recovery executes only a protected Program Files binary and reads
    # protected ProgramData state, never a user-writable startup executable.
    $taskAclOutput = & icacls.exe $taskState /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' ('*'+$taskSid+':(OI)(CI)RX') 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('State folder protection failed: '+($taskAclOutput -join ' ')) }
    foreach ($taskTask in @($taskName,$taskRecovery)) {
        $taskExisting=Get-ScheduledTask -TaskName $taskTask -TaskPath $taskPath -ErrorAction SilentlyContinue
        if ($taskExisting) {
            if ($taskExisting.Description -notlike 'Claw power-button animation*') { throw ('Unrelated task: '+$taskTask) }
            $taskBackup[$taskTask]=Export-ScheduledTask -TaskName $taskTask -TaskPath $taskPath
        }
    }
    Stop-Installed
    if ($Remove) {
        if (Test-Path -LiteralPath $taskConfig) {
            $taskOld=Get-Content -Raw -LiteralPath $taskConfig|ConvertFrom-Json
            Invoke-Helper $taskOld.executable '--recover-only' $taskState
        }
        foreach ($taskTask in @($taskName,$taskRecovery)) {
            if (Get-ScheduledTask -TaskName $taskTask -TaskPath $taskPath -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $taskTask -TaskPath $taskPath -Confirm:$false }
        }
        $taskRemovedPolicy=Get-ButtonPolicy
        $taskChecks.Add([pscustomobject]@{check='removed-policy-preserved';passed=$true;ac=$taskRemovedPolicy.ac;dc=$taskRemovedPolicy.dc})
        @{removed=$true;checks=$taskChecks}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $Report -Encoding UTF8
        return
    }
    $taskOriginal=Get-ButtonPolicy
    # Existing choices can be sleep, hibernate, shutdown or do nothing. The
    # controller remains available with capture inactive for unsupported actions.
    $taskChecks.Add([pscustomobject]@{check='original-policy-preserved';passed=$true;ac=$taskOriginal.ac;dc=$taskOriginal.dc})
    $taskSource=[IO.Path]::GetFullPath($Source)
    if (-not (Test-Path -LiteralPath (Join-Path $taskSource 'ClawSleepAnimation.exe'))) { throw 'Build missing.' }
    $taskStamp=Get-Date -Format 'yyyyMMdd-HHmmss'
    $taskRelease=Join-Path $env:ProgramFiles ('ClawToolbox Sleep Animation\releases\'+$taskStamp)
    New-Item -ItemType Directory -Path $taskRelease -Force|Out-Null
    Get-ChildItem -LiteralPath $taskSource|Copy-Item -Destination $taskRelease -Recurse
    $taskExe=Join-Path $taskRelease 'ClawSleepAnimation.exe'
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskSource -File -Recurse) {
        $taskRelative=$taskFile.FullName.Substring($taskSource.TrimEnd('\').Length+1)
        if ((Get-FileHash -LiteralPath $taskFile.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskRelease $taskRelative)).Hash) { throw ('Copied build differs: '+$taskRelative) }
    }
    $taskCanCapture=$taskOriginal.ac -in 1,2 -and $taskOriginal.dc -in 1,2
    if ($Verify -and $taskCanCapture) {
        foreach ($taskMode in @('normal-stop','owner-crash')) {
            $taskTestState=Join-Path $taskState ('validation\'+$taskStamp+'-'+$taskMode)
            New-Item -ItemType Directory -Path $taskTestState -Force|Out-Null
            $taskMonitor=Start-Process -FilePath $taskExe -ArgumentList ('--monitor --state-dir "'+$taskTestState+'"') -WindowStyle Hidden -PassThru
            $taskReady=Wait-Monitor $taskExe $taskTestState
            Assert-Policy 0 0 ($taskMode+'-armed')
            if ($taskMode -eq 'normal-stop') { Invoke-Helper $taskExe '--stop' $taskTestState } else { Stop-Process -Id $taskReady.pid -Force }
            if (-not $taskMonitor.WaitForExit(5000)) { throw 'Test monitor did not exit.' }
            Wait-Restored
            Assert-Policy $taskOriginal.ac $taskOriginal.dc ($taskMode+'-restored')
            Start-Sleep -Milliseconds 600
        }
    }
    $taskPrincipal=New-ScheduledTaskPrincipal -UserId $taskIdentity.Name -LogonType Interactive -RunLevel Highest
    $taskTrigger=New-ScheduledTaskTrigger -AtLogOn -User $taskIdentity.Name
    $taskAction=New-ScheduledTaskAction -Execute $taskExe -Argument ('--monitor --state-dir "'+$taskState+'"')
    $taskSettings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal -Settings $taskSettings -Description 'Claw power-button animation: interactive monitor with independent policy recovery.' -Force|Out-Null
    $taskRecoveryAction=New-ScheduledTaskAction -Execute $taskExe -Argument ('--recover-only --state-dir "'+$taskState+'"')
    $taskRecoveryPrincipal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $taskRecoverySettings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskPath $taskPath -TaskName $taskRecovery -Action $taskRecoveryAction -Trigger (New-ScheduledTaskTrigger -AtStartup) -Principal $taskRecoveryPrincipal -Settings $taskRecoverySettings -Description 'Claw power-button animation: restore an abandoned policy before logon.' -Force|Out-Null
    $taskInfo=[pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');executable=$taskExe;state=$taskState;task=$taskPath+$taskName;recoveryTask=$taskPath+$taskRecovery}
    $taskInfo|ConvertTo-Json|Set-Content -LiteralPath $taskConfig -Encoding UTF8
    Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName
    $taskReady=Wait-Monitor $taskExe $taskState
    $taskOptionsFile=Join-Path $taskState 'settings.json'
    $taskSuspendEnabled=$true
    if(Test-Path -LiteralPath $taskOptionsFile){$taskSuspendEnabled=(Get-Content -Raw -LiteralPath $taskOptionsFile|ConvertFrom-Json).suspendEnabled -eq $true}
    $taskShouldCapture=$taskCanCapture -and $taskSuspendEnabled
    if($taskShouldCapture){Assert-Policy 0 0 'installed-monitor-armed'}
    else{Assert-Policy $taskOriginal.ac $taskOriginal.dc 'installed-controller-policy-preserved'}
    if ($Verify -and $taskShouldCapture) {
        # Simulate loss of both processes, then exercise the actual boot task.
        foreach ($taskPid in @($taskReady.guardPid,$taskReady.pid)) {
            $taskVictim=Get-Process -Id $taskPid -ErrorAction Stop
            if ($taskVictim.Path -ne $taskExe) { throw 'Unexpected recovery-test process.' }
            Stop-Process -Id $taskPid -Force
        }
        Start-ScheduledTask -TaskPath $taskPath -TaskName $taskRecovery
        Wait-Restored
        Assert-Policy $taskOriginal.ac $taskOriginal.dc 'boot-recovery-after-owner-and-guard-loss'
        Start-Sleep -Milliseconds 500
        Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName
        $taskReady=Wait-Monitor $taskExe $taskState
        Assert-Policy 0 0 'monitor-restarted-after-recovery'
    }
    $taskInfo|Add-Member -NotePropertyName monitor -NotePropertyValue $taskReady
    $taskInfo|Add-Member -NotePropertyName checks -NotePropertyValue $taskChecks
    $taskInfo|Add-Member -NotePropertyName installed -NotePropertyValue $true
    $taskInfo|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $Report -Encoding UTF8
} catch {
    $taskFailure=$_.Exception.ToString()
    try { Stop-Installed } catch { }
    if ($taskExe) { try { Invoke-Helper $taskExe '--recover-only' $taskState } catch { } }
    foreach ($taskTask in @($taskName,$taskRecovery)) {
        if ($taskBackup.ContainsKey($taskTask)) { Register-ScheduledTask -TaskPath $taskPath -TaskName $taskTask -Xml $taskBackup[$taskTask] -Force|Out-Null }
        elseif (Get-ScheduledTask -TaskName $taskTask -TaskPath $taskPath -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $taskTask -TaskPath $taskPath -Confirm:$false }
    }
    if($taskPreviousInstallation){
        [IO.File]::WriteAllText($taskConfig,$taskPreviousInstallation)
        if($taskBackup.ContainsKey($taskName)){try{Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName}catch{}}
    }
    @{installed=$false;error=$taskFailure;checks=$taskChecks;policy=(Get-ButtonPolicy)}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $Report -Encoding UTF8
    throw
}
