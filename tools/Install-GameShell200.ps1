$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOut=Join-Path $taskRoot 'artifacts\game-shell-200'
$taskBefore=Get-AppxPackage ClawToolbox.Library
if ($taskBefore.Version.ToString() -ne '0.1.199.0') { throw 'Merge the changed installed version before updating' }
$taskExpected=(Get-Content -Raw (Join-Path $taskRoot 'artifacts\special-keys-197\installation.json') | ConvertFrom-Json).exeSha256
if ((Get-FileHash -LiteralPath (Join-Path $taskBefore.InstallLocation 'ClawLibrary.exe')).Hash -ne $taskExpected) { throw 'Installed source baseline changed' }
$taskMsix=Join-Path $taskOut 'src\frontend\library-native\AppPackages\ClawLibrary_0.1.200.0_x64_Test\ClawLibrary_0.1.200.0_x64.msix'
& 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' verify /pa $taskMsix *> (Join-Path $taskOut 'signature.log')
if ($LASTEXITCODE) { throw 'Signature verification failed' }
$taskStatePath=Join-Path $env:LOCALAPPDATA 'ClawToolbox\shell\state.json'
$taskMode=Get-Content -Raw -LiteralPath $taskStatePath | ConvertFrom-Json
$taskStable=Join-Path $env:LOCALAPPDATA 'ClawToolbox\shell\ClawShell.exe'
Copy-Item -LiteralPath $taskStable -Destination (Join-Path $taskOut 'ClawShell-before.exe')
$taskCache=Join-Path $env:LOCALAPPDATA 'Packages\ClawToolbox.Library_ba3r68zr466za\LocalCache'
$taskPrefs=@{}
Get-ChildItem -LiteralPath $taskCache -Filter *.ini | ForEach-Object { $taskPrefs[$_.Name]=(Get-FileHash -LiteralPath $_.FullName).Hash;Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskOut ($_.Name+'.before')) }
function Invoke-SurfaceShell([string]$Executable,[string[]]$ShellArguments) {
    $taskChild=Start-Process -FilePath $Executable -ArgumentList $ShellArguments -WindowStyle Hidden -PassThru
    if (-not $taskChild.WaitForExit(25000) -or $taskChild.ExitCode -ne 0) { throw 'Shell operation failed' }
}
if ($taskMode.mode -eq 'game') { Invoke-SurfaceShell $taskStable @('to-desktop') }
try {
    Add-AppxPackage -Path $taskMsix -ForceApplicationShutdown
    Invoke-SurfaceShell (Join-Path $taskOut 'ClawShell.exe') @('status')
    $taskShells=@(Get-CimInstance Win32_Process -Filter "Name='ClawShell.exe'")
    $taskLoginShells=@($taskShells | Where-Object CommandLine -Match '\s+shell\s*$')
    if ($taskLoginShells.Count -eq 0) {
        foreach ($taskWatch in @($taskShells | Where-Object CommandLine -Match '\s+watch\s*$')) {
            if ((Split-Path -Parent $taskWatch.ExecutablePath) -ne (Split-Path -Parent $taskStable)) { throw 'Unexpected shell supervisor path' }
            $taskProcess=Get-Process -Id $taskWatch.ProcessId
            Stop-Process -Id $taskProcess.Id -Force
            $taskProcess.WaitForExit(5000) | Out-Null
        }
        Start-Process -FilePath $taskStable -ArgumentList 'watch' -WindowStyle Hidden | Out-Null
    }
}
finally {
    if ($taskMode.mode -eq 'game') { Invoke-SurfaceShell $taskStable @('to-game') }
    $taskNow=Get-Content -Raw -LiteralPath $taskStatePath | ConvertFrom-Json
    if ($taskNow.default -ne $taskMode.default) { Invoke-SurfaceShell $taskStable @('set-default',$taskMode.default) }
}
$taskAfter=Get-AppxPackage ClawToolbox.Library
$taskBuilt=Join-Path $taskOut 'src\frontend\library-native\b\x64\Release\win-x64\publish\ClawLibrary.exe'
if ((Get-FileHash -LiteralPath $taskBuilt).Hash -ne (Get-FileHash -LiteralPath (Join-Path $taskAfter.InstallLocation 'ClawLibrary.exe')).Hash) { throw 'Installed executable mismatch' }
$taskChanged=@($taskPrefs.Keys | Where-Object { (Get-FileHash -LiteralPath (Join-Path $taskCache $_)).Hash -ne $taskPrefs[$_] })
@{beforeVersion=$taskBefore.Version.ToString();afterVersion=$taskAfter.Version.ToString();changedPreferences=$taskChanged;exeSha256=(Get-FileHash -LiteralPath $taskBuilt).Hash;shellBefore=$taskMode;shellAfter=(Get-Content -Raw -LiteralPath $taskStatePath | ConvertFrom-Json);runningLoginShellCount=$taskLoginShells.Count} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOut 'installation.json') -Encoding utf8
if ($taskChanged.Count) { throw 'Preferences changed' }
Write-Output ('Installed '+$taskAfter.Version.ToString()+'; preferences preserved')
