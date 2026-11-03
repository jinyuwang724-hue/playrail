param([switch]$Elevated)
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    $taskChild=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Elevated')
    if(-not $taskChild.WaitForExit(180000)){throw 'Startup shield installation timed out.'}
    if($taskChild.ExitCode -ne 0){throw ('Startup shield installation failed: '+$taskChild.ExitCode)}
    return
}
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOut=Join-Path $taskRoot 'artifacts\startup-shield'
$taskNew=Join-Path $taskOut 'src\frontend\library-native\AppPackages\ClawLibrary_0.1.118.0_x64_Test\ClawLibrary_0.1.118.0_x64.msix'
$taskOld=Join-Path $taskRoot 'artifacts\startup-transition\src\frontend\library-native\AppPackages\ClawLibrary_0.1.117.0_x64_Test\ClawLibrary_0.1.117.0_x64.msix'
if((Get-AppxPackage ClawToolbox.Library).Version.ToString() -ne '0.1.117.0'){throw 'Installed library changed; prepare a fresh integration.'}
$taskSigner='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
foreach($taskPackage in @($taskNew,$taskOld)){ & $taskSigner verify /pa $taskPackage *> $null; if($LASTEXITCODE -ne 0){throw 'Library package signature check failed.'} }
$taskPrefs=Join-Path $env:LOCALAPPDATA 'Packages\ClawToolbox.Library_ba3r68zr466za\LocalCache\ui.ini'
$taskPrefsHash=(Get-FileHash -LiteralPath $taskPrefs).Hash
Start-Transcript -Path (Join-Path $taskOut 'installation.log') -Force
try{
    & (Join-Path $PSScriptRoot 'Update-SleepMotion.ps1') -ReleaseName motion-v7 -Elevated
    $taskPython='C:\Users\13304\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    & $taskPython (Join-Path $PSScriptRoot 'Deploy-PowerExitRouting.py')
    if($LASTEXITCODE -ne 0){throw 'Backend animated power routing did not verify.'}
    $taskLegacy=Get-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName StartupAnimation -ErrorAction SilentlyContinue
    if($taskLegacy){
        if($taskLegacy.Description -notlike 'Claw startup loading animation*'){throw 'Unrelated startup task.'}
        Export-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName StartupAnimation|Set-Content -LiteralPath (Join-Path $taskOut 'legacy-startup-task.xml') -Encoding Unicode
        Disable-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName StartupAnimation|Out-Null
    }
    try{Add-AppxPackage -Path $taskNew -ForceApplicationShutdown}
    catch{
        if((Get-AppxPackage ClawToolbox.Library).Version.ToString() -ne '0.1.117.0'){Add-AppxPackage -Path $taskOld -ForceApplicationShutdown -ForceUpdateFromAnyVersion}
        if($taskLegacy){Enable-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName StartupAnimation|Out-Null}
        throw
    }
    if((Get-AppxPackage ClawToolbox.Library).Version.ToString() -ne '0.1.118.0'){throw 'Library shield upgrade failed.'}
    if((Get-FileHash -LiteralPath $taskPrefs).Hash -ne $taskPrefsHash){throw 'Library preferences changed during upgrade.'}
    [ordered]@{installed=$true;libraryVersion='0.1.118.0';libraryPreferencesPreserved=$true;legacyStartupTaskDisabled=$true;shield='inside library';gate='foreground window on active user desktop';minimumVisibleMilliseconds=1100;gameBarPowerRoutingInstalled=$true;actualRestartPerformed=$false;credentialSettingsChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskOut 'installation.json') -Encoding UTF8
}finally{Stop-Transcript}
