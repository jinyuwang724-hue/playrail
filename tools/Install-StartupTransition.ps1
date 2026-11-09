param([switch]$Elevated)
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    $taskChild=Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-Elevated')
    if(-not $taskChild.WaitForExit(180000)){throw 'Startup transition installation timed out.'}
    if($taskChild.ExitCode -ne 0){throw ('Startup transition installation failed: '+$taskChild.ExitCode)}
    return
}
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOut=Join-Path $taskRoot 'artifacts\startup-transition'
$taskNew=Join-Path $taskOut 'src\frontend\library-native\AppPackages\ClawLibrary_0.1.117.0_x64_Test\ClawLibrary_0.1.117.0_x64.msix'
$taskOld=Join-Path $taskRoot 'artifacts\tray-hide\src\frontend\library-native\AppPackages\ClawLibrary_0.1.116.0_x64_Test\ClawLibrary_0.1.116.0_x64.msix'
if((Get-AppxPackage ClawToolbox.Library).Version.ToString() -ne '0.1.116.0'){throw 'Installed library changed; prepare a fresh integration.'}
$taskSigner='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
foreach($taskPackage in @($taskNew,$taskOld)){ & $taskSigner verify /pa $taskPackage *> $null; if($LASTEXITCODE -ne 0){throw 'Library package signature check failed.'} }
$taskPrefs=Join-Path $env:LOCALAPPDATA 'Packages\ClawToolbox.Library_ba3r68zr466za\LocalCache\ui.ini'
$taskPrefsHash=(Get-FileHash -LiteralPath $taskPrefs).Hash
Start-Transcript -Path (Join-Path $taskOut 'installation.log') -Force
try{
    try{Add-AppxPackage -Path $taskNew -ForceApplicationShutdown}
    catch{ if((Get-AppxPackage ClawToolbox.Library).Version.ToString() -ne '0.1.116.0'){Add-AppxPackage -Path $taskOld -ForceApplicationShutdown -ForceUpdateFromAnyVersion}; throw }
    if((Get-AppxPackage ClawToolbox.Library).Version.ToString() -ne '0.1.117.0'){throw 'Readiness-enabled library upgrade failed.'}
    & (Join-Path $PSScriptRoot 'Update-SleepMotion.ps1') -ReleaseName motion-v6 -Elevated
    $taskNative=Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'artifacts\sleep-animation\motion-v6\installation.json')|ConvertFrom-Json
    if(-not $taskNative.installed){throw 'Animation update did not verify.'}
    $taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $taskName='StartupAnimation';$taskPath='\ClawToolbox\'
    $taskExisting=Get-ScheduledTask -TaskName $taskName -TaskPath $taskPath -ErrorAction SilentlyContinue
    if($taskExisting -and $taskExisting.Description -notlike 'Claw startup loading animation*'){throw 'An unrelated startup task has the same name.'}
    if($taskExisting){Export-ScheduledTask -TaskName $taskName -TaskPath $taskPath|Set-Content -LiteralPath (Join-Path $taskOut 'previous-task.xml') -Encoding Unicode}
    $taskPrincipal=New-ScheduledTaskPrincipal -UserId $taskIdentity.Name -LogonType Interactive -RunLevel Limited
    $taskTrigger=New-ScheduledTaskTrigger -AtLogOn -User $taskIdentity.Name
    $taskAction=New-ScheduledTaskAction -Execute $taskNative.executable -Argument '--boot-loading'
    $taskSettings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -Priority 4
    Register-ScheduledTask -TaskName $taskName -TaskPath $taskPath -Principal $taskPrincipal -Trigger $taskTrigger -Action $taskAction -Settings $taskSettings -Description 'Claw startup loading animation: user desktop only; wait for the configured Claw home to be ready.' -Force|Out-Null
    if((Get-FileHash -LiteralPath $taskPrefs).Hash -ne $taskPrefsHash){throw 'Library preferences changed during upgrade.'}
    [ordered]@{installed=$true;libraryVersion='0.1.117.0';libraryPreferencesPreserved=$true;executable=$taskNative.executable;task=$taskPath+$taskName;taskRunLevel='Limited';trigger='current-user logon';timeoutSeconds=45;secureDesktopChanged=$false;credentialSettingsChanged=$false;rebootPerformed=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskOut 'installation.json') -Encoding UTF8
}finally{Stop-Transcript}
