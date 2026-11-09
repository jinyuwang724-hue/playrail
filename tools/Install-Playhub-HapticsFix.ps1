$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskRepair = Join-Path $taskWorkspace 'repairs\playhub-haptics\index.js'
$taskBackup = Join-Path $taskWorkspace 'artifacts\playhub-haptics-backup\index.js'
$taskInfo = Get-Content (Join-Path $taskWorkspace 'repairs\playhub-haptics\patch-info.json') -Raw | ConvertFrom-Json
$taskTarget = Join-Path $env:USERPROFILE 'homebrew\plugins\gaming-mode\dist\index.js'
$taskPlugin = Split-Path -Parent (Split-Path -Parent $taskTarget)
$taskMain = Join-Path $taskPlugin 'main.py'
$taskModule = Join-Path $taskPlugin 'claw_navigation_haptics.py'
$taskCurrentHash = (Get-FileHash -LiteralPath $taskTarget).Hash.ToLowerInvariant()
$taskAllowedHashes = @($taskInfo.sourceHash, $taskInfo.outputHash)
$taskAllowedModuleHashes = @($taskInfo.moduleHash)
foreach ($taskPrior in @('patch-info-first-attempt.json','patch-info-second-attempt.json','patch-info-xinput-long-pulses.json','patch-info-xinput-official-durations.json')) {
    $taskPriorPath = Join-Path $taskWorkspace ('repairs\playhub-haptics\' + $taskPrior)
    if (Test-Path -LiteralPath $taskPriorPath) {
        $taskPriorInfo = Get-Content $taskPriorPath -Raw | ConvertFrom-Json
        $taskAllowedHashes += $taskPriorInfo.outputHash
        if ($taskPriorInfo.moduleHash) { $taskAllowedModuleHashes += $taskPriorInfo.moduleHash }
    }
}
if ($taskCurrentHash -notin $taskAllowedHashes) { throw 'Playhub changed since backup; inspect before installing.' }
if ((Get-FileHash -LiteralPath $taskMain).Hash.ToLowerInvariant() -notin @($taskInfo.sourceMainHash,$taskInfo.outputMainHash)) { throw 'Playhub backend changed since backup.' }
if ((Get-FileHash -LiteralPath $taskBackup).Hash.ToLowerInvariant() -ne $taskInfo.sourceHash) { throw 'Backup hash mismatch.' }
if ((Get-FileHash -LiteralPath $taskRepair).Hash.ToLowerInvariant() -ne $taskInfo.outputHash) { throw 'Repair hash mismatch.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskWorkspace 'artifacts\playhub-haptics-backup\main.py')).Hash.ToLowerInvariant() -ne $taskInfo.sourceMainHash) { throw 'Backend backup hash mismatch.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskWorkspace 'repairs\playhub-haptics\main.py')).Hash.ToLowerInvariant() -ne $taskInfo.outputMainHash) { throw 'Backend repair hash mismatch.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskWorkspace 'repairs\playhub-haptics\claw_navigation_haptics.py')).Hash.ToLowerInvariant() -ne $taskInfo.moduleHash) { throw 'Feedback module hash mismatch.' }
if ((Test-Path -LiteralPath $taskModule) -and (Get-FileHash -LiteralPath $taskModule).Hash.ToLowerInvariant() -notin $taskAllowedModuleHashes) { throw 'Feedback module name is already in use.' }
Copy-Item -LiteralPath (Join-Path $taskWorkspace 'repairs\playhub-haptics\claw_navigation_haptics.py') -Destination $taskModule -Force
Copy-Item -LiteralPath (Join-Path $taskWorkspace 'repairs\playhub-haptics\main.py') -Destination $taskMain -Force
Copy-Item -LiteralPath $taskRepair -Destination $taskTarget -Force
if ((Get-FileHash -LiteralPath $taskTarget).Hash.ToLowerInvariant() -ne $taskInfo.outputHash) { throw 'Installed hash mismatch.' }
Write-Output 'MSI Claw XInput feedback repair installed; reload the Playhub backend to apply.'
