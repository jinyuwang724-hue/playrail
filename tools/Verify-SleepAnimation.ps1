param([string]$Installation=(Join-Path $PSScriptRoot '..\artifacts\sleep-animation\installation.json'), [string]$Report=(Join-Path $PSScriptRoot '..\artifacts\sleep-animation\runtime-verification.json'))
$ErrorActionPreference='Stop'
$taskInstall=Get-Content -Raw -LiteralPath $Installation|ConvertFrom-Json
if(-not $taskInstall.installed){throw 'No successful installation record.'}
$taskExe=$taskInstall.executable; $taskState=$taskInstall.state
$taskInitialOptions=Get-Content -Raw -LiteralPath (Join-Path $taskState 'settings.json')|ConvertFrom-Json
if(-not $taskInitialOptions.suspendEnabled){throw 'Enable the suspend animation before running its watchdog test.'}
$taskLease=Get-Content -Raw -LiteralPath (Join-Path $taskState 'active-lease.json')|ConvertFrom-Json
$taskOriginal=@([int]$taskLease.Policy.Ac,[int]$taskLease.Policy.Dc)
function Get-Policy {
    $taskMatches=[regex]::Matches(((& powercfg /qh SCHEME_CURRENT SUB_BUTTONS PBUTTONACTION)-join "`n"),'0x([0-9a-fA-F]{8})')
    if($taskMatches.Count -ne 2){throw 'Unexpected power-button query.'}
    @([Convert]::ToInt32($taskMatches[0].Groups[1].Value,16),[Convert]::ToInt32($taskMatches[1].Groups[1].Value,16))
}
function Wait-Ready {
    $taskUntil=[DateTime]::UtcNow.AddSeconds(12)
    while([DateTime]::UtcNow -lt $taskUntil){
        try{
            $taskInfo=Get-Content -Raw -LiteralPath (Join-Path $taskState 'monitor.json')|ConvertFrom-Json
            $taskProcess=Get-Process -Id $taskInfo.pid -ErrorAction Stop
            if($taskProcess.Path -eq $taskExe){return $taskInfo}
        }catch{}
        Start-Sleep -Milliseconds 150
    }
    throw 'Monitor did not restart.'
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SleepAnimationProcessTest {
 [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
 [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
}
'@
$taskResults=[ordered]@{at=[DateTime]::UtcNow.ToString('o')}
try{
    $taskReady=Wait-Ready
    $taskOwner=Get-Process -Id $taskReady.pid -ErrorAction Stop
    if($taskOwner.Path -ne $taskExe){throw 'Unexpected monitor process.'}
    $taskSuspended=$false
    try{
        if([SleepAnimationProcessTest]::NtSuspendProcess($taskOwner.Handle) -ne 0){throw 'Could not pause own monitor for watchdog verification.'}
        $taskSuspended=$true
        $taskUntil=[DateTime]::UtcNow.AddSeconds(45)
        do{
            Start-Sleep -Milliseconds 200
            $taskPolicy=Get-Policy
            if($taskPolicy[0] -eq $taskOriginal[0] -and $taskPolicy[1] -eq $taskOriginal[1]){break}
        }while([DateTime]::UtcNow -lt $taskUntil)
        if($taskPolicy[0] -ne $taskOriginal[0] -or $taskPolicy[1] -ne $taskOriginal[1]){throw 'Guard did not restore while its owner was paused.'}
        $taskResults.unresponsiveOwnerRestored=$true
    }finally{
        if($taskSuspended){[void][SleepAnimationProcessTest]::NtResumeProcess($taskOwner.Handle)}
    }
    # v2 keeps its settings controller alive and disables capture after a fault.
    $taskUntil=[DateTime]::UtcNow.AddSeconds(7)
    do{
        Start-Sleep -Milliseconds 150
        $taskOptions=Get-Content -Raw -LiteralPath (Join-Path $taskState 'settings.json')|ConvertFrom-Json
    }while($taskOptions.suspendEnabled -and [DateTime]::UtcNow -lt $taskUntil)
    if($taskOptions.suspendEnabled -or $taskOwner.HasExited){throw 'Controller did not recover with capture safely disabled.'}
    $taskResults.controllerAvailableAfterWatchdog=$true
    $taskChild=Start-Process -FilePath $taskExe -ArgumentList ('--set-suspend true --state-dir "'+$taskState+'"') -WindowStyle Hidden -PassThru
    if(-not $taskChild.WaitForExit(10000) -or $taskChild.ExitCode -ne 0){throw 'Could not re-enable animation through its settings controller.'}
    $taskReady=Wait-Ready
    $taskResults.restartAfterWatchdog=$true
    $taskRecovery=Get-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName SleepAnimationRecovery
    $taskMain=Get-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName SleepAnimation
    if($taskRecovery.Principal.UserId -ne 'SYSTEM' -or $taskRecovery.Actions.Execute -ne $taskExe -or $taskMain.State -ne 'Running'){throw 'Unexpected startup/recovery task configuration.'}
    $taskResults.startupTasksVerified=$true
    $taskResults.recoveryTaskLastResult=(Get-ScheduledTaskInfo -TaskPath '\ClawToolbox\' -TaskName SleepAnimationRecovery).LastTaskResult
    $taskBefore=@{}
    foreach($taskPid in @($taskReady.pid,$taskReady.guardPid)){
        $taskProcess=Get-Process -Id $taskPid -ErrorAction Stop
        $taskBefore[$taskPid]=$taskProcess.TotalProcessorTime.TotalMilliseconds
    }
    $taskSample=[Diagnostics.Stopwatch]::StartNew();Start-Sleep -Seconds 12
    $taskCpu=0.0;$taskWorkingSet=0L
    foreach($taskPid in @($taskReady.pid,$taskReady.guardPid)){
        $taskProcess=Get-Process -Id $taskPid -ErrorAction Stop
        $taskCpu+=$taskProcess.TotalProcessorTime.TotalMilliseconds-$taskBefore[$taskPid]
        $taskWorkingSet+=$taskProcess.WorkingSet64
    }
    $taskResults.idleSampleSeconds=[Math]::Round($taskSample.Elapsed.TotalSeconds,2)
    $taskResults.idleCpuPercentAllCores=[Math]::Round(100*$taskCpu/$taskSample.Elapsed.TotalMilliseconds/[Environment]::ProcessorCount,3)
    $taskResults.combinedWorkingSetMiB=[Math]::Round($taskWorkingSet/1MB,1)
    $taskResults.monitor=$taskReady
    $taskResults.activeTraceNames=@((& logman query -ets)|Where-Object{$_ -match 'ClawSleep'})
    $taskResults.finalPolicy=Get-Policy
    if($taskResults.finalPolicy[0] -ne 0 -or $taskResults.finalPolicy[1] -ne 0){throw 'Monitor lost power-button ownership.'}
    $taskResults.passed=$true
}catch{
    $taskResults.passed=$false;$taskResults.error=$_.Exception.ToString()
    throw
}finally{
    $taskResults|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $Report -Encoding UTF8
}
