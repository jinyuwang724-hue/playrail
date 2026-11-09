param([Parameter(Mandatory=$true)][int]$GamePid,[Parameter(Mandatory=$true)][string]$ExpectedPath)
$ErrorActionPreference='Stop'
try {
if ($ExpectedPath -notin @('C:\Game\ZenlessZoneZero Game\ZenlessZoneZero.exe','C:\Game\Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe')) { throw 'Expected game path is not a repair target.' }
$taskGame=Get-Process -Id $GamePid -ErrorAction SilentlyContinue
if (-not $taskGame) { return }
if ($taskGame.Path -ne $ExpectedPath) { throw 'Test process identity changed; close cancelled.' }
$taskKnownStarts=@{20380='20261008170625';26076='20261008171104'}
if (-not $taskKnownStarts.ContainsKey($GamePid) -or $taskGame.StartTime.ToString('yyyyMMddHHmmss') -ne $taskKnownStarts[$GamePid]) { throw 'This is not a recorded repair test session.' }
$taskGame.CloseMainWindow() | Out-Null
if (-not $taskGame.WaitForExit(3000)) { Stop-Process -Id $GamePid; $taskGame.WaitForExit(5000) | Out-Null }
@{ok=$true;pid=$GamePid} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot ('..\artifacts\repair-20261008-1655\close-test-'+$GamePid+'.json'))
} catch {
 @{ok=$false;pid=$GamePid;error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot ('..\artifacts\repair-20261008-1655\close-test-'+$GamePid+'.json'))
 throw
}
