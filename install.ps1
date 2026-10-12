param([string]$PluginRoot = (Join-Path $env:USERPROFILE 'homebrew\plugins'))
$ErrorActionPreference = 'Stop'
$taskTarget = [IO.Path]::GetFullPath((Join-Path $PluginRoot 'claw-button-router'))
$taskAllowed = [IO.Path]::GetFullPath($PluginRoot).TrimEnd('\') + '\'
if (-not $taskTarget.StartsWith($taskAllowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Target is outside plugin root.' }
$taskSource = Join-Path $PSScriptRoot 'plugin'
$taskVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskSource 'plugin.json') | ConvertFrom-Json).version
$taskFiles = @(('bin\ClawRouter-'+$taskVersion+'.exe'),'bin\ClashBridge.exe',('bin\ClawTouchBridge-'+$taskVersion+'.exe'),'bin\SteamAdminLauncher.exe','bin\CompatibilityUtility.exe','resources\clawtweaks-home.patch.gz','dist\index.js','main.py','claw_compatibility.py','claw_navigation_haptics.py','claw_clash.py','claw_subscriptions.py','claw_magpie.py','claw_overlay_frames.py','claw_power.py','claw_rumble.py',('bin\ClawPower-'+$taskVersion+'.exe'),'bin\pawnio\IntelMSR.bin','package.json','plugin.json')
$taskMagpieRoot = Join-Path $taskSource 'bin\magpie'
$taskFiles += @(Get-ChildItem -LiteralPath $taskMagpieRoot -Recurse -File | ForEach-Object { $_.FullName.Substring($taskSource.TrimEnd('\').Length+1) })
foreach ($taskFile in $taskFiles) { if (-not (Test-Path -LiteralPath (Join-Path $taskSource $taskFile))) { throw ('Build output missing: '+$taskFile) } }
if (Test-Path -LiteralPath $taskTarget) {
    $taskBackup=Join-Path $PSScriptRoot ('artifacts\plugin-backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    [IO.Directory]::CreateDirectory($taskBackup) | Out-Null
    foreach ($taskFile in $taskFiles) {
        $taskOld=Join-Path $taskTarget $taskFile
        if (Test-Path -LiteralPath $taskOld) { $taskCopy=Join-Path $taskBackup $taskFile; [IO.Directory]::CreateDirectory((Split-Path -Parent $taskCopy)) | Out-Null; Copy-Item -LiteralPath $taskOld -Destination $taskCopy }
    }
}
foreach ($taskFile in $taskFiles) {
    $taskNew = Join-Path $taskTarget $taskFile
    if ((Test-Path -LiteralPath $taskNew) -and ((Get-FileHash -LiteralPath $taskNew).Hash -eq (Get-FileHash -LiteralPath (Join-Path $taskSource $taskFile)).Hash)) { continue }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $taskNew)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskSource $taskFile) -Destination $taskNew -Force
}
Write-Output $taskTarget
