$ErrorActionPreference = 'Stop'
$taskSource = Join-Path $PSScriptRoot 'plugin'
$taskVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskSource 'plugin.json') | ConvertFrom-Json).version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
$taskStage = Join-Path $PSScriptRoot ('artifacts\package-' + $taskVersion + '\claw-button-router')
$taskFiles = @('plugin.json','package.json','main.py','claw_compatibility.py','claw_navigation_haptics.py','claw_clash.py','claw_subscriptions.py','bin\ClashBridge.exe',('bin\ClawTouchBridge-'+$taskVersion+'.exe'),'bin\SteamAdminLauncher.exe','bin\CompatibilityUtility.exe','resources\clawtweaks-home.patch.gz','dist\index.js',('bin\ClawRouter-'+$taskVersion+'.exe'))
$taskFiles += 'claw_service.py','claw_agent.py','claw_agent_client.py','claw_magpie.py','claw_overlay_frames.py','claw_power.py','claw_rumble.py','claw_gamepad_fw.py',('bin\ClawPower-'+$taskVersion+'.exe'),'bin\pawnio\IntelMSR.bin'
$taskFiles += @(Get-ChildItem -LiteralPath (Join-Path $taskSource 'bin\magpie') -Recurse -File | ForEach-Object { $_.FullName.Substring($taskSource.TrimEnd('\').Length+1) })
$taskFiles += @(Get-ChildItem -LiteralPath (Join-Path $taskSource 'runtime\python') -Recurse -File | ForEach-Object { $_.FullName.Substring($taskSource.TrimEnd('\').Length+1) })
foreach ($taskFile in $taskFiles) {
    $taskOriginal = Join-Path $taskSource $taskFile
    if (-not (Test-Path -LiteralPath $taskOriginal)) { throw ('Build output missing: ' + $taskFile) }
    $taskCopy = Join-Path $taskStage $taskFile
    [IO.Directory]::CreateDirectory((Split-Path -Parent $taskCopy)) | Out-Null
    Copy-Item -LiteralPath $taskOriginal -Destination $taskCopy -Force
}
$taskZip = Join-Path $PSScriptRoot ('artifacts\claw-button-router-' + $taskVersion + '.zip')
Compress-Archive -LiteralPath $taskStage -DestinationPath $taskZip -Force
Write-Output $taskZip
