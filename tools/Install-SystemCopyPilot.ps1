param([string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\system-copy-pilot'))
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($Directory)
$taskIntegration = Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'integration.json') | ConvertFrom-Json
$taskInstalled = Get-AppxPackage ClawToolbox.Library
if ($taskInstalled.Version -ne $taskIntegration.baseVersion) { throw 'The installed application changed; merge that version before installing this pilot.' }
$taskBaseline = Join-Path $taskIntegration.sourceBaseline 'b\x64\Release\win-x64\publish\ClawLibrary.exe'
$taskExpectedExecutable = if ($taskIntegration.baseExecutableSha256) { $taskIntegration.baseExecutableSha256 } else { (Get-FileHash -LiteralPath $taskBaseline).Hash }
if ((Get-FileHash -LiteralPath (Join-Path $taskInstalled.InstallLocation 'ClawLibrary.exe')).Hash -ne $taskExpectedExecutable) { throw 'The source baseline does not match the installed executable.' }
foreach ($taskEntry in $taskIntegration.changes.PSObject.Properties) {
    $taskCandidate = Join-Path $taskRoot ('src\frontend\library-native\' + $taskEntry.Name)
    if ((Get-FileHash -LiteralPath $taskCandidate).Hash.ToLowerInvariant() -ne $taskEntry.Value.candidateSha256) { throw ('Unreviewed source change: ' + $taskEntry.Name) }
}
$taskPackage = Join-Path $taskRoot ('src\frontend\library-native\AppPackages\ClawLibrary_' + $taskIntegration.version + '_x64_Test\ClawLibrary_' + $taskIntegration.version + '_x64.msix')
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskPackage
if ($taskSignature.Status -ne 'Valid') { throw 'The pilot package signature is invalid.' }
$taskCache = Join-Path $env:LOCALAPPDATA ('Packages\' + $taskInstalled.PackageFamilyName + '\LocalCache')
$taskPreferences = @{}
Get-ChildItem -LiteralPath $taskCache -File | Where-Object Extension -In '.ini','.json' | ForEach-Object { $taskPreferences[$_.Name] = (Get-FileHash -LiteralPath $_.FullName).Hash }
$taskReport = [ordered]@{ beforeVersion=[string]$taskInstalled.Version; beforeExecutable=(Get-FileHash -LiteralPath (Join-Path $taskInstalled.InstallLocation 'ClawLibrary.exe')).Hash; package=$taskPackage; signature=[string]$taskSignature.Status; preferencesBefore=$taskPreferences }
$taskReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRoot 'installation-before.json') -Encoding utf8
Add-AppxPackage -Path $taskPackage -ForceApplicationShutdown
$taskAfter = Get-AppxPackage ClawToolbox.Library
if ($taskAfter.Version -ne $taskIntegration.version) { throw 'Unexpected installed version.' }
$taskChanged = @($taskPreferences.Keys | Where-Object { (Get-FileHash -LiteralPath (Join-Path $taskCache $_)).Hash -ne $taskPreferences[$_] })
$taskReport.afterVersion = [string]$taskAfter.Version
$taskReport.changedPreferences = $taskChanged
$taskReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRoot 'installation.json') -Encoding utf8
if ($taskChanged.Count) { throw ('Preferences changed: ' + ($taskChanged -join ', ')) }
Write-Output ('Installed ' + $taskAfter.Version + '; existing UI preferences retained.')
