param([string]$PlayhubRoot = 'C:\Software\Playhub')
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskRepairRoot = Join-Path $taskWorkspace 'repairs\playhub-haptics'
$taskInfo = Get-Content -LiteralPath (Join-Path $taskRepairRoot 'patch-info.json') -Raw | ConvertFrom-Json
$taskBundle = Join-Path $PlayhubRoot 'Assets\GamingModeDeckyPlugin\gaming-mode'
$taskInstalled = Join-Path $env:USERPROFILE 'homebrew\plugins\gaming-mode'
$taskBundleManifest = Get-Content -LiteralPath (Join-Path $taskBundle 'plugin.json') -Raw | ConvertFrom-Json
$taskInstalledManifest = Get-Content -LiteralPath (Join-Path $taskInstalled 'plugin.json') -Raw | ConvertFrom-Json
if ($taskBundleManifest.name -ne 'Playhub' -or $taskBundleManifest.version -ne $taskInfo.version -or
    $taskInstalledManifest.name -ne 'Playhub' -or $taskInstalledManifest.version -ne $taskInfo.version) {
    throw 'Playhub version changed; inspect before applying this version-specific repair.'
}
$taskFiles = @(
    @{ Relative = 'dist\index.js'; Repair = 'index.js'; Original = $taskInfo.sourceHash; Patched = $taskInfo.outputHash },
    @{ Relative = 'main.py'; Repair = 'main.py'; Original = $taskInfo.sourceMainHash; Patched = $taskInfo.outputMainHash },
    @{ Relative = 'claw_navigation_haptics.py'; Repair = 'claw_navigation_haptics.py'; Original = $null; Patched = $taskInfo.moduleHash }
)
$taskOperations = @()
foreach ($taskFile in $taskFiles) {
    $taskSource = Join-Path $taskRepairRoot $taskFile.Repair
    if ((Get-FileHash -LiteralPath $taskSource).Hash.ToLowerInvariant() -ne $taskFile.Patched) {
        throw ('Repair hash mismatch: ' + $taskFile.Repair)
    }
    foreach ($taskRoot in @($taskBundle, $taskInstalled)) {
        $taskTarget = Join-Path $taskRoot $taskFile.Relative
        $taskExisted = Test-Path -LiteralPath $taskTarget -PathType Leaf
        if ($taskExisted) {
            $taskCurrentHash = (Get-FileHash -LiteralPath $taskTarget).Hash.ToLowerInvariant()
            if ($taskCurrentHash -notin @($taskFile.Original, $taskFile.Patched)) {
                throw ('File changed; inspect instead of overwriting: ' + $taskTarget)
            }
        } elseif ($taskFile.Original) {
            throw ('Required original file missing: ' + $taskTarget)
        }
        $taskOperations += @{
            Target = $taskTarget; Source = $taskSource; Expected = $taskFile.Patched
            Existed = $taskExisted
            Bytes = $(if ($taskExisted) { [IO.File]::ReadAllBytes($taskTarget) } else { $null })
            Modified = $(if ($taskExisted) { [IO.File]::GetLastWriteTimeUtc($taskTarget) } else { $null })
        }
    }
}
$taskBackupRoot = Join-Path $taskWorkspace ('artifacts\playhub-haptics-persistence-backup\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $taskBackupRoot -Force | Out-Null
foreach ($taskFile in $taskFiles) {
    $taskTarget = Join-Path $taskBundle $taskFile.Relative
    if (Test-Path -LiteralPath $taskTarget -PathType Leaf) {
        Copy-Item -LiteralPath $taskTarget -Destination (Join-Path $taskBackupRoot $taskFile.Repair)
    }
}
try {
    # Patch the source first: Playhub's startup sync must also copy the repaired bytes.
    foreach ($taskOperation in $taskOperations) {
        Copy-Item -LiteralPath $taskOperation.Source -Destination $taskOperation.Target -Force
        if ((Get-FileHash -LiteralPath $taskOperation.Target).Hash.ToLowerInvariant() -ne $taskOperation.Expected) {
            throw ('Installed hash mismatch: ' + $taskOperation.Target)
        }
    }
} catch {
    foreach ($taskOperation in $taskOperations) {
        if ($taskOperation.Existed) {
            [IO.File]::WriteAllBytes($taskOperation.Target, $taskOperation.Bytes)
            [IO.File]::SetLastWriteTimeUtc($taskOperation.Target, $taskOperation.Modified)
        } elseif (Test-Path -LiteralPath $taskOperation.Target) {
            Remove-Item -LiteralPath $taskOperation.Target -Force
        }
    }
    throw
}
$taskReceipt = [ordered]@{
    appliedAt = (Get-Date -Format o); version = $taskInfo.version
    bundleRoot = $taskBundle; installedRoot = $taskInstalled; backupRoot = $taskBackupRoot
    reason = 'Playhub startup SyncDeckyPlugin replaced the installed-only repair with bundled originals.'
    files = @($taskOperations | ForEach-Object { [ordered]@{ path = $_.Target; hash = $_.Expected } })
}
$taskReceipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskWorkspace 'diagnostics\playhub-haptics-persistence.json') -Encoding UTF8
Write-Output 'Repaired Playhub bundled and installed plugins. Reload the Playhub backend to apply.'
