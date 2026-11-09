param([string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\enhanced-sleep\final'), [switch]$Elevated)
$ErrorActionPreference = 'Stop'
$taskOutput = [IO.Path]::GetFullPath($Directory)
$taskAdmin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if(-not $taskAdmin){
    $taskChild = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Directory',('"'+$taskOutput+'"'),'-Elevated')
    if(-not $taskChild.WaitForExit(120000)){throw 'Sleep component installation did not finish.'}
    if($taskChild.ExitCode -ne 0){throw ('Sleep component installation failed. See '+(Join-Path $taskOutput 'native-install-error.txt'))}
    return
}
try{
    $taskSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $taskState = Join-Path $env:ProgramData ('ClawToolbox\SleepAnimation\'+$taskSid)
    Copy-Item -LiteralPath (Join-Path $taskState 'installation.json') -Destination (Join-Path $taskOutput 'native-before.json') -Force
    $taskBefore = Get-Content -Raw -LiteralPath (Join-Path $taskState 'settings.json') | ConvertFrom-Json
    & (Join-Path $PSScriptRoot 'Install-SleepAnimation.ps1') -Source (Join-Path $taskOutput 'app') -Report (Join-Path $taskOutput 'native-installation.json')
    $taskAfter = Get-Content -Raw -LiteralPath (Join-Path $taskState 'settings.json') | ConvertFrom-Json
    if($taskBefore.suspendEnabled -ne $taskAfter.suspendEnabled -or $taskBefore.resumeEnabled -ne $taskAfter.resumeEnabled){throw 'Existing animation preferences changed.'}
}catch{
    $_.Exception.ToString() | Set-Content -LiteralPath (Join-Path $taskOutput 'native-install-error.txt') -Encoding UTF8
    throw
}
