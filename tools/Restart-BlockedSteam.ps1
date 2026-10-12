$ErrorActionPreference='Stop'
$taskSteam=Get-Process -Id 15064 -ErrorAction SilentlyContinue
if ($taskSteam) {
 if ($taskSteam.Path -ne 'C:\Software\steam.exe') { throw 'Unexpected Steam process.' }
 if ($taskSteam.StartTime.ToString('yyyyMMddHHmmss') -ne '20261008165404') { throw 'This is not the blocked startup session.' }
 if (Get-Process -Name ZenlessZoneZero,Client-Win64-Shipping -ErrorAction SilentlyContinue) { throw 'Game is running; restart cancelled.' }
 # Steam is blocked at its startup compatibility notice, before account logon.
 Stop-Process -Id $taskSteam.Id
 $taskSteam.WaitForExit(5000) | Out-Null
 Start-Sleep -Milliseconds 700
}
Start-Process -FilePath 'C:\Users\13304\homebrew\plugins\claw-button-router\bin\SteamAdminLauncher.exe' -WindowStyle Hidden
