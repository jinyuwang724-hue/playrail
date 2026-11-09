$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskBackup = Join-Path $taskWorkspace 'artifacts\playhub-haptics-backup\index.js'
$taskInfo = Get-Content (Join-Path $taskWorkspace 'repairs\playhub-haptics\patch-info.json') -Raw | ConvertFrom-Json
$taskTarget = Join-Path $env:USERPROFILE 'homebrew\plugins\gaming-mode\dist\index.js'
$taskPlugin = Split-Path -Parent (Split-Path -Parent $taskTarget)
$taskMain = Join-Path $taskPlugin 'main.py'
$taskRoots = @($taskPlugin)
$taskReceiptPath = Join-Path $taskWorkspace 'diagnostics\playhub-haptics-persistence.json'
if (Test-Path -LiteralPath $taskReceiptPath) {
    $taskReceipt = Get-Content -LiteralPath $taskReceiptPath -Raw | ConvertFrom-Json
    $taskManifest = Get-Content -LiteralPath (Join-Path $taskReceipt.bundleRoot 'plugin.json') -Raw | ConvertFrom-Json
    if ($taskManifest.name -ne 'Playhub' -or $taskManifest.version -ne $taskInfo.version) {
        throw 'Bundled Playhub version changed; inspect before restoring.'
    }
    $taskRoots += $taskReceipt.bundleRoot
}
if ((Get-FileHash -LiteralPath $taskBackup).Hash.ToLowerInvariant() -ne $taskInfo.sourceHash) { throw 'Backup hash mismatch.' }
$taskMainBackup = Join-Path $taskWorkspace 'artifacts\playhub-haptics-backup\main.py'
if ((Get-FileHash -LiteralPath $taskMainBackup).Hash.ToLowerInvariant() -ne $taskInfo.sourceMainHash) { throw 'Backend backup hash mismatch.' }
$taskOperations = @()
foreach ($taskRoot in $taskRoots) {
    foreach ($taskFile in @(
        @{ Relative = 'dist\index.js'; Backup = $taskBackup; Original = $taskInfo.sourceHash; Patched = $taskInfo.outputHash },
        @{ Relative = 'main.py'; Backup = $taskMainBackup; Original = $taskInfo.sourceMainHash; Patched = $taskInfo.outputMainHash }
    )) {
        $taskFilePath = Join-Path $taskRoot $taskFile.Relative
        if ((Get-FileHash -LiteralPath $taskFilePath).Hash.ToLowerInvariant() -notin @($taskFile.Original, $taskFile.Patched)) {
            throw ('Playhub changed after repair; inspect before restoring: ' + $taskFilePath)
        }
        $taskOperations += @{ Target = $taskFilePath; Backup = $taskFile.Backup
            Bytes = [IO.File]::ReadAllBytes($taskFilePath); Modified = [IO.File]::GetLastWriteTimeUtc($taskFilePath) }
    }
}
try {
    foreach ($taskOperation in $taskOperations) {
        Copy-Item -LiteralPath $taskOperation.Backup -Destination $taskOperation.Target -Force
    }
} catch {
    foreach ($taskOperation in $taskOperations) {
        [IO.File]::WriteAllBytes($taskOperation.Target, $taskOperation.Bytes)
        [IO.File]::SetLastWriteTimeUtc($taskOperation.Target, $taskOperation.Modified)
    }
    throw
}
Write-Output 'Original feedback restored in the installed plugin and recorded bundle; reload the Playhub backend to apply. The unused feedback module may be retained.'
