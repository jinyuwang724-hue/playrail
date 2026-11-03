# Replace the fan service only; preserve power and saved curves. Roll back on startup failure.
param([switch]$Elevated)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskReports=Join-Path $taskRoot 'diagnostics\fan-direct-20261023'
$taskReportPath=Join-Path $taskReports 'deployment.json'
$taskAdmin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $taskAdmin) {
 $taskChild=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Elevated')
 if(Test-Path -LiteralPath $taskReportPath){Get-Content -Raw -LiteralPath $taskReportPath|ConvertFrom-Json|Select-Object passed,rolledBack,settingsPreserved,error,backup|ConvertTo-Json}
 exit $taskChild.ExitCode
}
$taskResult=[ordered]@{passed=$false;rolledBack=$false;settingsPreserved=$false}
$taskFolder=Join-Path $env:ProgramFiles 'ClawToolbox\FanService'
$taskExe=Join-Path $taskFolder 'ClawFanService.exe'
$taskConfig=Join-Path $env:ProgramData 'ClawToolbox\FanService\service.json'
$taskPower=Join-Path $env:LOCALAPPDATA 'ClawToolbox\data\power.json'
$taskBackup=Join-Path $taskReports ('direct-backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$taskStopped=$false
function Read-DirectState {
 $taskReply='{"command":"get"}' | & $taskExe --client | ConvertFrom-Json
 if(-not $taskReply.ok){throw $taskReply.error}; return $taskReply.fan
}
try {
 # A SYSTEM service image and its dependencies must not be redirected elsewhere.
 $taskCheck=[IO.Path]::GetFullPath($taskFolder)
 while($taskCheck) {
  if((Get-Item -LiteralPath $taskCheck -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected service directory'}
  $taskParent=Split-Path -Parent $taskCheck;if($taskParent -eq $taskCheck){break};$taskCheck=$taskParent
 }
 if((Get-FileHash -LiteralPath (Join-Path $taskRoot 'plugin\bin\pawnio\LpcACPIEC.bin')).Hash -ne 'C38FD116E7AFF4D1FDB0A494E296BE0A6708E5A22FC72F14587442FB7F8F7906'){throw 'Wrong ACPI EC module'}
 [IO.Directory]::CreateDirectory($taskBackup)|Out-Null;$taskResult.backup=$taskBackup
 $taskResult.before=Read-DirectState
 $taskConfigHash=(Get-FileHash -LiteralPath $taskConfig).Hash
 $taskPowerHash=(Get-FileHash -LiteralPath $taskPower).Hash
 Copy-Item -LiteralPath $taskExe -Destination (Join-Path $taskBackup 'ClawFanService.exe')
 Copy-Item -LiteralPath $taskConfig -Destination (Join-Path $taskBackup 'service.json')
 Stop-Service ClawFanService;(Get-Service ClawFanService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20));$taskStopped=$true
 $taskPrevious=Join-Path $taskFolder ('ClawFanService.previous-'+[Guid]::NewGuid().ToString('N')+'.exe')
 Move-Item -LiteralPath $taskExe -Destination $taskPrevious
 Copy-Item -LiteralPath (Join-Path $taskRoot 'plugin\bin\ClawFanService.exe') -Destination $taskExe
 foreach($taskName in @('LpcACPIEC.bin','LpcACPIEC-COPYING.txt')){Copy-Item -LiteralPath (Join-Path $taskRoot ('plugin\bin\pawnio\'+$taskName)) -Destination (Join-Path $taskFolder ('pawnio\'+$taskName)) -Force}
 Start-Service ClawFanService;(Get-Service ClawFanService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
 for($taskTry=0;$taskTry -lt 30;$taskTry++){
  $taskAfter=Read-DirectState
  if($taskAfter.target -ne $null -or $taskAfter.error){break}
  Start-Sleep -Milliseconds 300
 }
 if($taskAfter.serviceVersion -ne '1.1.1' -or $taskAfter.controlStrategy -ne 'ec_output' -or $taskAfter.target -eq $null -or $taskAfter.error){throw ('New controller not ready: '+$taskAfter.error)}
 $taskResult.after=$taskAfter
 $taskResult.settingsPreserved=(Get-FileHash -LiteralPath $taskConfig).Hash -eq $taskConfigHash -and (Get-FileHash -LiteralPath $taskPower).Hash -eq $taskPowerHash
 if(-not $taskResult.settingsPreserved){throw 'Saved settings changed during service-only update'}
 $taskResult.binaryHash=(Get-FileHash -LiteralPath $taskExe).Hash
 if($taskResult.binaryHash -ne (Get-FileHash -LiteralPath (Join-Path $taskRoot 'plugin\bin\ClawFanService.exe')).Hash){throw 'Installed binary mismatch'}
 $taskResult.passed=$true
} catch {
 $taskResult.error=$_.Exception.Message
 if($taskStopped){
  try {
   Stop-Service ClawFanService -ErrorAction SilentlyContinue;(Get-Service ClawFanService).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
   if(Test-Path -LiteralPath $taskExe){Move-Item -LiteralPath $taskExe -Destination (Join-Path $taskFolder ('ClawFanService.failed-'+[Guid]::NewGuid().ToString('N')+'.exe'))}
   Copy-Item -LiteralPath (Join-Path $taskBackup 'ClawFanService.exe') -Destination $taskExe
   Start-Service ClawFanService;$taskResult.rolledBack=$true
  } catch {$taskResult.rollbackError=$_.Exception.Message}
 }
} finally {[IO.File]::WriteAllText($taskReportPath,($taskResult|ConvertTo-Json -Depth 14))}
if(-not $taskResult.passed){exit 1}
