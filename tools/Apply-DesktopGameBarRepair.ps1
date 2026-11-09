param([Parameter(Mandatory=$true)][string]$BackupDirectory)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskHelper=Join-Path $env:LOCALAPPDATA 'Packages\MSIClaw.ClawTweaks_7eszav2039cvc\LocalCache\ClawTweaks\Helper\XboxGamingBarHelper.exe'
$taskSettings=Join-Path $env:LOCALAPPDATA 'Packages\MSIClaw.ClawTweaks_7eszav2039cvc\LocalState\settings.json'
$taskHandover=Join-Path $env:LOCALAPPDATA 'Packages\MSIClaw.ClawTweaks_7eszav2039cvc\LocalCache\Local'
[IO.Directory]::CreateDirectory($BackupDirectory) | Out-Null
$taskRestart=$false; $taskReplaced=$false
try {
 if ((Get-FileHash -LiteralPath $taskHelper).Hash -ne 'DBE62A3EA97A6DC30F069ADCE3D16BB5AE2BBFD0A44CF09AD45D18C7CF141088') { throw 'Helper build changed; repair cancelled.' }
 Copy-Item -LiteralPath $taskHelper -Destination (Join-Path $BackupDirectory 'XboxGamingBarHelper.exe')
 Copy-Item -LiteralPath $taskSettings -Destination (Join-Path $BackupDirectory 'ClawTweaks-settings.json')
 Add-Type -Path (Join-Path $taskRoot 'native\GameBarHomePreference.cs')
 $taskBefore=[GameBarHomePreference]::Read()
 @{InvokeToHomeWidget=$taskBefore} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $BackupDirectory 'GameBar-home-before.json') -Encoding utf8
 $taskVersion=(Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'plugin\plugin.json') | ConvertFrom-Json).version
 [Reflection.Assembly]::LoadFile((Join-Path $taskRoot ('plugin\bin\ClawRouter-'+$taskVersion+'.exe'))) | Out-Null
 $taskControllers=[ClawRouter.Native]::ControllerCount()
 if ($taskControllers -lt 1) { throw 'No visible controller; helper handover cancelled.' }
 foreach ($taskWidget in @(Get-Process XboxGamingBar -ErrorAction SilentlyContinue)) {
  if ([ClawRouter.Native]::PackageFamily($taskWidget.Id) -eq 'MSIClaw.ClawTweaks_7eszav2039cvc') { [ClawRouter.Native]::CloseWidget($taskWidget.Id) }
 }
 foreach ($taskProcess in @(Get-Process XboxGamingBarHelper -ErrorAction SilentlyContinue)) {
  if ($taskProcess.Path -ne $taskHelper) { throw 'Unexpected helper instance.' }
  $taskRequest=Join-Path $taskHandover 'shutdown-request.json'; $taskAck=Join-Path $taskHandover 'shutdown-ack.json'
  if (Test-Path -LiteralPath $taskRequest) { throw 'Existing helper handover request; repair cancelled.' }
  $taskToken=[Guid]::NewGuid().ToString('N'); $taskRestart=$true
  try {
   @{TargetPid=$taskProcess.Id;Token=$taskToken;RequestedAtUtc=[DateTime]::UtcNow.ToString('o');Reason='claw-button-router';RequesterPid=$PID} | ConvertTo-Json -Compress | Set-Content -LiteralPath $taskRequest -Encoding utf8
   if (-not $taskProcess.WaitForExit(9000)) { throw 'Helper did not exit normally; it will not be terminated.' }
  } finally {
   foreach ($taskOwned in @($taskRequest,$taskAck)) {
    if ((Test-Path -LiteralPath $taskOwned) -and ((Get-Content -Raw -LiteralPath $taskOwned | ConvertFrom-Json).Token -eq $taskToken)) { Remove-Item -LiteralPath $taskOwned }
   }
  }
 }
 Start-Sleep -Milliseconds 300
 if (Get-Process XboxGamingBarHelper -ErrorAction SilentlyContinue) { throw 'Helper restarted during repair.' }
 $taskJson=Get-Content -Raw -LiteralPath $taskSettings | ConvertFrom-Json
 $taskJson | Add-Member -NotePropertyName GameBarFirstSlot_PreserveHome -NotePropertyValue $true -Force
 $taskJson | ConvertTo-Json -Depth 50 -Compress | Set-Content -LiteralPath $taskSettings -Encoding utf8NoBOM
 Copy-Item -LiteralPath (Join-Path $taskRoot 'artifacts\XboxGamingBarHelper-home.exe') -Destination $taskHelper -Force
 $taskReplaced=$true
 [GameBarHomePreference]::Set($true) | Out-Null
 if (-not [GameBarHomePreference]::Read()) { throw 'Game Bar Home setting verification failed.' }
 if ([ClawRouter.Native]::ControllerCount() -lt $taskControllers) { throw 'Visible controller count fell during repair.' }
 @{ok=$true;homeBefore=$taskBefore;homeAfter=$true;controllersBefore=$taskControllers;controllersAfter=[ClawRouter.Native]::ControllerCount();helperHash=(Get-FileHash -LiteralPath $taskHelper).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $BackupDirectory 'result.json') -Encoding utf8
} catch {
 if ($taskReplaced -and -not (Get-Process XboxGamingBarHelper -ErrorAction SilentlyContinue)) {
  Copy-Item -LiteralPath (Join-Path $BackupDirectory 'XboxGamingBarHelper.exe') -Destination $taskHelper -Force
  Copy-Item -LiteralPath (Join-Path $BackupDirectory 'ClawTweaks-settings.json') -Destination $taskSettings -Force
  [GameBarHomePreference]::Set($taskBefore) | Out-Null
 }
 @{ok=$false;error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $BackupDirectory 'result.json') -Encoding utf8
 throw
} finally {
 if ($taskRestart -and -not (Get-Process XboxGamingBarHelper -ErrorAction SilentlyContinue)) { Start-ScheduledTask -TaskPath '\ClawTweaks\' -TaskName 'ClawTweaksHelper' }
}
