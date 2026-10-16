param([string]$PluginRoot = (Join-Path $env:USERPROFILE 'homebrew\plugins'), [string]$AgentHome = (Join-Path $env:LOCALAPPDATA 'ClawToolbox'))
$ErrorActionPreference = 'Stop'
$taskSource = Join-Path $PSScriptRoot 'plugin'
$taskVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskSource 'plugin.json') | ConvertFrom-Json).version

# Copies changed files only, so files a running process holds are skipped when unchanged.
function Copy-Changed([string]$From, [string]$To, [string[]]$Files, [string]$BackupName) {
    foreach ($file in $Files) { if (-not (Test-Path -LiteralPath (Join-Path $From $file))) { throw ('Build output missing: '+$file) } }
    if ($BackupName -and (Test-Path -LiteralPath $To)) {
        $backup = Join-Path $PSScriptRoot ('artifacts\'+$BackupName+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
        foreach ($file in $Files) {
            $old = Join-Path $To $file
            if (Test-Path -LiteralPath $old) { $copy = Join-Path $backup $file; [IO.Directory]::CreateDirectory((Split-Path -Parent $copy)) | Out-Null; Copy-Item -LiteralPath $old -Destination $copy }
        }
    }
    foreach ($file in $Files) {
        $new = Join-Path $To $file
        if ((Test-Path -LiteralPath $new) -and ((Get-FileHash -LiteralPath $new).Hash -eq (Get-FileHash -LiteralPath (Join-Path $From $file)).Hash)) { continue }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $new)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $From $file) -Destination $new -Force
    }
}
function Get-Relative([string]$Root, [string]$Folder) {
    @(Get-ChildItem -LiteralPath (Join-Path $Root $Folder) -Recurse -File | ForEach-Object { $_.FullName.Substring($Root.TrimEnd('\').Length+1) })
}

# 1. The agent: everything that runs on the device, independent of Steam and Decky.
$agentApp = Join-Path $AgentHome 'app'
$agentFiles = @(('bin\ClawRouter-'+$taskVersion+'.exe'),('bin\ClawTouchBridge-'+$taskVersion+'.exe'),'bin\SteamAdminLauncher.exe','bin\CompatibilityUtility.exe',('bin\ClawPower-'+$taskVersion+'.exe'),'bin\pawnio\IntelMSR.bin','resources\clawtweaks-home.patch.gz','claw_agent.py','claw_service.py','claw_compatibility.py','claw_navigation_haptics.py','claw_magpie.py','claw_overlay_frames.py','claw_power.py','claw_rumble.py','claw_gamepad_fw.py','claw_input_layer.py',('bin\ClawInput-'+$taskVersion+'.exe'),'plugin.json')
$agentFiles += Get-Relative $taskSource 'bin\magpie'
Copy-Changed $taskSource $agentApp $agentFiles $null

$runtimeSource = Join-Path $taskSource 'runtime\python'
$runtimeVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $runtimeSource 'runtime.json') | ConvertFrom-Json).version
$runtime = Join-Path $AgentHome ('runtime\python-'+$runtimeVersion)
Copy-Changed $runtimeSource $runtime (Get-Relative $runtimeSource '.') $null
$python = Join-Path $runtime 'pythonw.exe'
$agent = Join-Path $agentApp 'claw_agent.py'
$current = @{ version = $taskVersion; python = $python; agent = $agent } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $AgentHome 'current.json'), $current, (New-Object Text.UTF8Encoding($false)))

$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$register = Join-Path $PSScriptRoot 'tools\Register-AgentTask.ps1'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) { & $register -Python $python -Agent $agent -User $user }
else {
    $quoted = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$register+'"'),'-Python',('"'+$python+'"'),'-Agent',('"'+$agent+'"'),'-User',('"'+$user+'"'))
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $quoted -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Agent sign-in task registration failed.' }
}
# The agent is not started here: on first install the old all-in-one plugin still owns the
# hardware until Decky reloads it. The reloaded plugin starts (or upgrades) the agent itself.

# 2. The Decky front end: Steam UI, the agent connection and Clash Verge.
$taskTarget = [IO.Path]::GetFullPath((Join-Path $PluginRoot 'claw-button-router'))
$taskAllowed = [IO.Path]::GetFullPath($PluginRoot).TrimEnd('\') + '\'
if (-not $taskTarget.StartsWith($taskAllowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Target is outside plugin root.' }
$deckyFiles = @('bin\ClashBridge.exe','dist\index.js','main.py','claw_agent_client.py','claw_clash.py','claw_subscriptions.py','package.json','plugin.json')
Copy-Changed $taskSource $taskTarget $deckyFiles 'plugin-backup'
Write-Output $taskTarget
Write-Output $agentApp
