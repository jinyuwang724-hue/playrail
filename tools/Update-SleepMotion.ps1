param([ValidatePattern('^motion-v[0-9]+$')][string]$ReleaseName='motion-v5', [switch]$Elevated, [switch]$Verify)
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    $taskArguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Elevated','-ReleaseName',$ReleaseName)
    if($Verify){$taskArguments+='-Verify'}
    $taskChild=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $taskArguments
    if(-not $taskChild.WaitForExit(180000)){throw 'Motion update timed out.'}
    if($taskChild.ExitCode -ne 0){throw ('Motion update failed: '+$taskChild.ExitCode)}
    return
}
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOutput=Join-Path $taskRoot ('artifacts\sleep-animation\'+$ReleaseName)
$taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskState=Join-Path $env:ProgramData ('ClawToolbox\SleepAnimation\'+$taskSid)
$taskBefore=Get-Content -Raw -LiteralPath (Join-Path $taskState 'installation.json')|ConvertFrom-Json
$taskSettings=Get-Content -Raw -LiteralPath (Join-Path $taskState 'settings.json')
Copy-Item -LiteralPath (Join-Path $taskState 'installation.json') -Destination (Join-Path $taskOutput 'previous-installation.json')
Start-Transcript -Path (Join-Path $taskOutput 'installation.log') -Force
try{
    & (Join-Path $PSScriptRoot 'Install-SleepAnimation.ps1') -Source (Join-Path $taskOutput 'app') -Report (Join-Path $taskOutput 'installation.json') -Verify:$Verify
    if($taskSettings -ne (Get-Content -Raw -LiteralPath (Join-Path $taskState 'settings.json'))){throw 'Existing animation choices changed.'}
    [ordered]@{passed=$true;settingsPreserved=$true;previousExecutable=$taskBefore.executable;currentExecutable=(Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'installation.json')|ConvertFrom-Json).executable;startupAnimationAdded=$false;shutdownAnimationAdded=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskOutput 'update-verification.json') -Encoding UTF8
}finally{Stop-Transcript}
