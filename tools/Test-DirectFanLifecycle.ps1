# Verify a normal service stop restores factory control, then restart in finally.
param([switch]$Elevated)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskReport=Join-Path $taskRoot 'diagnostics\fan-direct-20261023\lifecycle.json'
$taskAdmin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $taskAdmin) {
 $taskChild=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Elevated')
 if(Test-Path -LiteralPath $taskReport){Get-Content -Raw -LiteralPath $taskReport|ConvertFrom-Json|Select-Object passed,stoppedInFactoryAuto,restarted,settingsPreserved,error|ConvertTo-Json}
 exit $taskChild.ExitCode
}
$taskExe=Join-Path $env:ProgramFiles 'ClawToolbox\FanService\ClawFanService.exe'
$taskConfig=Join-Path $env:ProgramData 'ClawToolbox\FanService\service.json'
$taskResult=[ordered]@{passed=$false;stoppedInFactoryAuto=$false;restarted=$false;settingsPreserved=$false}
$taskStopped=$false
function Read-DirectState {
 $taskReply='{"command":"get"}' | & $taskExe --client | ConvertFrom-Json
 if(-not $taskReply.ok){throw $taskReply.error};return $taskReply.fan
}
try {
 $taskBefore=Read-DirectState
 if($taskBefore.serviceVersion -ne '1.1.1' -or $taskBefore.controlStrategy -ne 'ec_output'){throw 'Expected direct controller 1.1.1'}
 $taskResult.before=$taskBefore
 $taskHash=(Get-FileHash -LiteralPath $taskConfig).Hash
 $taskProbe=Join-Path $taskRoot 'diagnostics\fan-direct-20261023\FanReadOnlyProbe.exe'
 $taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
 & $taskCompiler /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Management.dll /reference:System.Web.Extensions.dll ("/out:"+$taskProbe) (Join-Path $taskRoot 'native\ClawFanHardware.cs') (Join-Path $taskRoot 'tools\FanReadOnlyProbe.cs')
 if($LASTEXITCODE -ne 0){throw 'Read-only probe build failed'}
 Stop-Service ClawFanService
 $taskStopped=$true
 (Get-Service ClawFanService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
 $taskAuto=& $taskProbe | ConvertFrom-Json
 if($LASTEXITCODE -ne 0){throw 'Stopped-state read failed'}
 $taskSaved=Get-Content -Raw -LiteralPath $taskConfig|ConvertFrom-Json
 $taskResult.stopped=$taskAuto
 $taskResult.stoppedInFactoryAuto=-not $taskAuto.software -and -not $taskAuto.fullSpeed -and $taskAuto.cpuTable[7] -eq 94 -and $taskAuto.gpuTable[7] -eq 94 -and ($taskAuto.cpu -join ',') -eq ($taskSaved.factoryCpu -join ',') -and ($taskAuto.gpu -join ',') -eq ($taskSaved.factoryGpu -join ',')
 if(-not $taskResult.stoppedInFactoryAuto){throw 'Factory hand-back was not confirmed'}
} catch {$taskResult.error=$_.Exception.Message}
finally {
 if($taskStopped) {
  try {
   Start-Service ClawFanService
   (Get-Service ClawFanService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
   for($taskTry=0;$taskTry -lt 30;$taskTry++){
    $taskAfter=Read-DirectState
    if($taskAfter.target -ne $null -or $taskAfter.error){break}
    Start-Sleep -Milliseconds 300
   }
   $taskResult.after=$taskAfter
   $taskResult.restarted=$taskAfter.serviceVersion -eq '1.1.1' -and $taskAfter.controlStrategy -eq 'ec_output' -and $taskAfter.target -ne $null -and -not $taskAfter.error
   $taskResult.settingsPreserved=(Get-FileHash -LiteralPath $taskConfig).Hash -eq $taskHash
  } catch {$taskResult.restartError=$_.Exception.Message}
 }
 $taskResult.passed=$taskResult.stoppedInFactoryAuto -and $taskResult.restarted -and $taskResult.settingsPreserved -and -not $taskResult.error
 [IO.File]::WriteAllText($taskReport,($taskResult|ConvertTo-Json -Depth 14))
}
if(-not $taskResult.passed){exit 1}
