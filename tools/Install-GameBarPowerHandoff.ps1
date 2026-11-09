param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\gamebar-power-handoff'
if (-not $Elevated) {
    $taskArgs = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Elevated'
    $taskProcess = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $taskArgs
    if ($taskProcess.ExitCode -ne 0) { throw ('Installation failed; see install.log: ' + $taskProcess.ExitCode) }
    Get-Content -LiteralPath (Join-Path $out 'installation.json')
    exit
}
Start-Transcript -Path (Join-Path $out 'install.log') -Append | Out-Null
try {
    $taskPackage = Get-AppxPackage -Name 'ClawToolbox.GameBarWidget'
    $taskAlreadyUpdated = $taskPackage.Version.ToString() -eq '0.4.43.0'
    if (-not $taskAlreadyUpdated -and $taskPackage.Version.ToString() -ne '0.4.42.0') { throw 'Installed widget version changed.' }
    $taskBase = Get-Content -Raw -LiteralPath (Join-Path $out 'base.json') | ConvertFrom-Json
    if (-not $taskAlreadyUpdated -and (Get-FileHash -LiteralPath (Join-Path $taskPackage.InstallLocation 'ClawWidget.exe')).Hash.ToLowerInvariant() -ne $taskBase.widgetExeSha256) { throw 'Widget baseline hash changed.' }
    $taskNew = Join-Path $out 'ClawWidget-0.4.43.0.msix'
    $taskOld = Join-Path $env:USERPROFILE '.codex\worktrees\native-gamebar-osd\Claw-Playhub-ButtonRouter-Handoff\artifacts\gamebar-signing\ClawWidget.msix'
    $taskSigner = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($taskCandidate in @($taskOld, $taskNew)) {
        & $taskSigner verify /pa $taskCandidate *> $null
        if ($LASTEXITCODE -ne 0) { throw ('Invalid package signature: ' + $taskCandidate) }
        $taskZip = [IO.Compression.ZipFile]::OpenRead($taskCandidate)
        try {
            $taskReader = [IO.StreamReader]::new($taskZip.GetEntry('AppxManifest.xml').Open())
            try { $taskManifest = [xml]$taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
            $taskExpected = if ($taskCandidate -eq $taskOld) { '0.4.42.0' } else { '0.4.43.0' }
            if ($taskManifest.Package.Identity.Version -ne $taskExpected -or $taskManifest.Package.Identity.Name -ne 'ClawToolbox.GameBarWidget') { throw 'Wrong package identity.' }
        } finally { $taskZip.Dispose() }
    }
    $taskState = Join-Path $env:LOCALAPPDATA 'Packages\ClawToolbox.GameBarWidget_ba3r68zr466za\LocalState'
    $taskSettings = @{}
    # gamebar-layout.json is a volatile geometry report, regenerated when the host closes.
    # It is not the user's tile preferences. Preserve LocalSettings by upgrading in place.
    foreach ($taskName in @('preview-layout.json')) {
        $taskSettings[$taskName] = (Get-FileHash -LiteralPath (Join-Path $taskState $taskName)).Hash
    }
    $taskRollback = Join-Path $out 'ClawWidget-0.4.42.0-rollback.msix'
    if (-not (Test-Path -LiteralPath $taskRollback)) { Copy-Item -LiteralPath $taskOld -Destination $taskRollback }
    $taskPython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    if (-not $taskAlreadyUpdated) {
        & $taskPython (Join-Path $root 'tools\Deploy-GameBarPowerHandoff.py')
        if ($LASTEXITCODE -ne 0) { throw 'Backend deployment failed.' }
        Add-AppxPackage -Path $taskNew -ForceApplicationShutdown
    }
    $taskInstalled = Get-AppxPackage -Name 'ClawToolbox.GameBarWidget'
    if ($taskInstalled.Version.ToString() -ne '0.4.43.0') { throw 'Package version verification failed.' }
    foreach ($taskName in $taskSettings.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $taskState $taskName)).Hash -ne $taskSettings[$taskName]) { throw ('Widget settings changed: ' + $taskName) }
    }
    $taskInstalledSha = (Get-FileHash -LiteralPath (Join-Path $taskInstalled.InstallLocation 'ClawWidget.exe')).Hash.ToLowerInvariant()
    if ($taskInstalledSha -ne (Get-FileHash -LiteralPath (Join-Path $out 'bin\ClawWidget\ClawWidget.exe')).Hash.ToLowerInvariant()) { throw 'Installed binary mismatch.' }
    $taskResult = @{ widgetVersion='0.4.43.0';widgetExeSha256=$taskInstalledSha;rollback=$taskRollback;settingsPreservedByInPlaceUpgrade=$true;previewLayoutPreserved=$true;actualPowerRequests=0;gameBarVisualTestConfirmed=$false }
    $taskResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'installation.json') -Encoding utf8
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $out 'install-error.txt') -Encoding utf8
    throw
} finally { Stop-Transcript | Out-Null }
