param([switch]$Elevated)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskV2=Join-Path $taskRoot 'artifacts\sleep-animation\settings-v2'
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    $taskChild=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Elevated')
    if(-not $taskChild.WaitForExit(180000)){throw 'Elevated final verification timed out.'}
    if($taskChild.ExitCode -ne 0){throw ('Elevated final verification failed: '+$taskChild.ExitCode)}
    return
}
Start-Transcript -Path (Join-Path $taskV2 'final-verification.log') -Force
try{
& (Join-Path $PSScriptRoot 'Install-SleepAnimation.ps1') -Source (Join-Path $taskV2 'app') -Report (Join-Path $taskV2 'installation.json') -Verify
& (Join-Path $PSScriptRoot 'Verify-SleepAnimation.ps1') -Installation (Join-Path $taskV2 'installation.json') -Report (Join-Path $taskV2 'runtime-verification.json')
$taskPython='C:\Users\13304\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
& $taskPython (Join-Path $taskV2 'Finalize-PowerView.py')
if($LASTEXITCODE -ne 0){throw 'Power-view refinement failed.'}
}finally{Stop-Transcript}
