$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskStage=Join-Path $taskRoot 'artifacts\sleep-animation\settings-v2\integration-r2'
$taskBeforeVersion=(Get-AppxPackage ClawToolbox.Library).Version.ToString()
if($taskBeforeVersion -ne '0.1.110.0'){throw 'Installed library changed; prepare a new UI integration.'}
$taskPackage=Join-Path $taskStage 'frontend\library-native\AppPackages\ClawLibrary_0.1.111.0_x64_Test\ClawLibrary_0.1.111.0_x64.msix'
$taskRollback=Join-Path $taskRoot 'artifacts\library-toggle\src\frontend\library-native\AppPackages\ClawLibrary_0.1.110.0_x64_Test\ClawLibrary_0.1.110.0_x64.msix'
$taskSignTool='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
foreach($taskFile in @($taskPackage,$taskRollback)){
    & $taskSignTool verify /pa $taskFile *> $null
    if($LASTEXITCODE -ne 0){throw ('Package signature verification failed: '+$taskFile)}
}
$taskPython='C:\Users\13304\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
& $taskPython (Join-Path $PSScriptRoot 'Deploy-SleepAnimationSettings.py')
if($LASTEXITCODE -ne 0){throw 'Backend deployment failed; UI was not changed.'}
$taskBackend=Get-Content -Raw (Join-Path $taskStage '..\backend-deployment.json')|ConvertFrom-Json
Copy-Item -LiteralPath $taskRollback -Destination (Join-Path $taskBackend.backup 'ClawLibrary-0.1.110.0.msix')
try {
    Add-AppxPackage -Path $taskPackage -ForceApplicationShutdown
    if((Get-AppxPackage ClawToolbox.Library).Version.ToString() -ne '0.1.111.0'){throw 'Package upgrade did not verify.'}
} catch {
    if((Get-AppxPackage ClawToolbox.Library).Version.ToString() -ne $taskBeforeVersion){Add-AppxPackage -Path $taskRollback -ForceUpdateFromAnyVersion -ForceApplicationShutdown}
    throw
}
@{version='0.1.111.0';package=$taskPackage;backup=$taskBackend.backup;localStatePreserved=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskStage '..\ui-deployment.json') -Encoding UTF8
Write-Output 'Animation switches installed in System > Battery and power.'
