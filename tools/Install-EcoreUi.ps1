# Upgrade the prepared UI packages in place; retain LocalState and signed rollback packages.
param([string]$SourceRoot='C:\Users\13304\.codex\worktrees\native-gamebar-osd\Claw-Playhub-ButtonRouter-Handoff')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskReportDir=Join-Path $taskRoot 'artifacts\ecore'
$taskDeployment=Get-Content -Raw -LiteralPath (Join-Path $taskReportDir 'deployment.json')|ConvertFrom-Json
$taskBackup=$taskDeployment.backup
$taskChanges=@(
    @{Name='ClawToolbox.Library';Before='0.1.105.0';After='0.1.106.0';
      Old=Join-Path $SourceRoot 'frontend\library-native\AppPackages\ClawLibrary_0.1.105.0_x64_Test\ClawLibrary_0.1.105.0_x64.msix';
      New=Join-Path $taskReportDir 'integration\frontend\library-native\AppPackages\ClawLibrary_0.1.106.0_x64_Test\ClawLibrary_0.1.106.0_x64.msix'},
    @{Name='ClawToolbox.GameBarWidget';Before='0.4.40.0';After='0.4.41.0';
      Old=Join-Path $SourceRoot 'artifacts\gamebar-signing\ClawWidget.msix';
      New=Join-Path $taskReportDir 'gamebar\bin\ClawWidget\AppPackages\ClawWidget_0.4.41.0_x64_Test\ClawWidget_0.4.41.0_x64.msix'}
)
$taskVerify='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach($taskChange in $taskChanges) {
    $taskInstalled=Get-AppxPackage -Name $taskChange.Name
    if($taskInstalled.Version.ToString() -ne $taskChange.Before){throw ('Installed version changed: '+$taskChange.Name)}
    foreach($taskPackageFile in @($taskChange.Old,$taskChange.New)) {
        $taskZip=[IO.Compression.ZipFile]::OpenRead($taskPackageFile)
        try {
            $taskReader=[IO.StreamReader]::new($taskZip.GetEntry('AppxManifest.xml').Open())
            try{$taskXml=[xml]$taskReader.ReadToEnd()}finally{$taskReader.Dispose()}
            $taskExpected=if($taskPackageFile -eq $taskChange.Old){$taskChange.Before}else{$taskChange.After}
            if($taskXml.Package.Identity.Name -ne $taskChange.Name -or $taskXml.Package.Identity.Version -ne $taskExpected){throw 'Unexpected package identity'}
        } finally {$taskZip.Dispose()}
        & $taskVerify verify /pa $taskPackageFile *> $null
        if($LASTEXITCODE -ne 0){throw ('Package signature verification failed: '+$taskPackageFile)}
    }
    $taskChange.Rollback=Join-Path $taskBackup ($taskChange.Name+'.msix')
    Copy-Item -LiteralPath $taskChange.Old -Destination $taskChange.Rollback
}
$taskApplied=@()
try {
    foreach($taskChange in $taskChanges) {
        Add-AppxPackage -Path $taskChange.New -ForceApplicationShutdown
        $taskApplied+=$taskChange
        if((Get-AppxPackage -Name $taskChange.Name).Version.ToString() -ne $taskChange.After){throw 'Upgrade version verification failed'}
    }
} catch {
    foreach($taskChange in $taskApplied){Add-AppxPackage -Path $taskChange.Rollback -ForceUpdateFromAnyVersion -ForceApplicationShutdown}
    throw
}
$taskResult=@{packages=@($taskChanges|ForEach-Object {@{name=$_.Name;version=$_.After}});backup=$taskBackup;localStatePreserved=$true}
$taskResult|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $taskReportDir 'ui-deployment.json') -Encoding utf8
$taskResult|ConvertTo-Json -Depth 5
