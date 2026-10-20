# Deploys only the updated button/input components; retains all fan service configuration.
param([string]$AgentHome = (Join-Path $env:LOCALAPPDATA 'ClawToolbox'))
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskApp = Join-Path $AgentHome 'app'
$taskSource = Join-Path $taskRoot 'plugin'
$taskVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskSource 'plugin.json') | ConvertFrom-Json).version
$taskCurrentPath = Join-Path $AgentHome 'current.json'
$taskCurrent = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskCurrentPath | ConvertFrom-Json
$taskFiles = @('claw_agent.py','claw_service.py','claw_input_layer.py','claw_button_mappings.py','claw_power.py','claw_fan_service.py','plugin.json')
$taskFiles += @('ClawRouter','ClawInput','ClawPower','ClawTouchBridge') | ForEach-Object { 'bin\'+$_+'-'+$taskVersion+'.exe' }
foreach ($taskFile in $taskFiles) { if (-not (Test-Path -LiteralPath (Join-Path $taskSource $taskFile))) { throw ('Build output missing: '+$taskFile) } }
$taskBackup = Join-Path $AgentHome ('repair-backups\button-mappings-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
[IO.Directory]::CreateDirectory($taskBackup) | Out-Null
$taskSettingsPath = Join-Path $AgentHome 'data\settings.json'
$taskSettings = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskSettingsPath | ConvertFrom-Json
$taskLegacyBefore = @($taskSettings.enabled,$taskSettings.msiAction,$taskSettings.qamAction,$taskSettings.hapticsEnabled) | ConvertTo-Json -Compress
Copy-Item -LiteralPath $taskSettingsPath -Destination (Join-Path $taskBackup 'settings.json')
Copy-Item -LiteralPath $taskCurrentPath -Destination (Join-Path $taskBackup 'current.json')
foreach ($taskFile in $taskFiles) {
    $taskOld = Join-Path $taskApp $taskFile
    if (Test-Path -LiteralPath $taskOld) {
        $taskSaved = Join-Path $taskBackup $taskFile
        [IO.Directory]::CreateDirectory((Split-Path -Parent $taskSaved)) | Out-Null
        Copy-Item -LiteralPath $taskOld -Destination $taskSaved
    }
}
$taskEndpointPath = Join-Path $AgentHome 'agent.json'
$taskOldPid = 0
if (Test-Path -LiteralPath $taskEndpointPath) {
    $taskEndpoint = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskEndpointPath | ConvertFrom-Json
    $taskOldPid = [int]$taskEndpoint.pid
    $taskTcp = New-Object Net.Sockets.TcpClient('127.0.0.1',[int]$taskEndpoint.port)
    try {
        $taskStream = $taskTcp.GetStream(); $taskStream.ReadTimeout = 10000
        $taskReader = New-Object IO.StreamReader($taskStream)
        $taskWriter = New-Object IO.StreamWriter($taskStream,(New-Object Text.UTF8Encoding($false))); $taskWriter.AutoFlush = $true
        $taskWriter.WriteLine((@{hello=$taskEndpoint.token;client='button-mapping-deployer'} | ConvertTo-Json -Compress))
        $taskHello = $taskReader.ReadLine() | ConvertFrom-Json
        if ($taskHello.hello -ne 'ok') { throw 'Agent authentication failed.' }
        $taskWriter.WriteLine('{"id":1,"method":"shutdown","args":[]}')
        do { $taskReply = $taskReader.ReadLine() | ConvertFrom-Json } while ($taskReply -and $taskReply.id -ne 1)
    } finally { $taskTcp.Dispose() }
    $taskOldAgent = Get-Process -Id $taskEndpoint.pid -ErrorAction SilentlyContinue
    if ($taskOldAgent -and -not $taskOldAgent.WaitForExit(25000)) { throw 'Old agent did not exit; deployment paused before changing button settings.' }
}
# Release the running executables before replacing files, including same-version repair builds.
foreach ($taskFile in $taskFiles) {
    $taskOld = Join-Path $taskApp $taskFile
    $taskNew = Join-Path $taskSource $taskFile
    [IO.Directory]::CreateDirectory((Split-Path -Parent $taskOld)) | Out-Null
    if (-not (Test-Path -LiteralPath $taskOld) -or (Get-FileHash -LiteralPath $taskNew).Hash -ne (Get-FileHash -LiteralPath $taskOld).Hash) {
        Copy-Item -LiteralPath $taskNew -Destination $taskOld -Force
    }
}
$taskCurrent.version = $taskVersion
[IO.File]::WriteAllText($taskCurrentPath,($taskCurrent | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
# Only initialise the new context. The four pre-existing Steam/haptics settings are retained verbatim.
$taskSettings = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskSettingsPath | ConvertFrom-Json
if (-not ($taskSettings.PSObject.Properties.Name -contains 'buttonMappings')) {
    $taskSettings | Add-Member -NotePropertyName buttonMappings -NotePropertyValue @{
        QS=@{type='system';action='gamebar'};Claw=@{type='system';action='library'}
    }
}
if (-not ($taskSettings.PSObject.Properties.Name -contains 'steamBindings')) { $taskSettings | Add-Member -NotePropertyName steamBindings -NotePropertyValue @{} }
$taskLegacyAfter = @($taskSettings.enabled,$taskSettings.msiAction,$taskSettings.qamAction,$taskSettings.hapticsEnabled) | ConvertTo-Json -Compress
if ($taskLegacyAfter -ne $taskLegacyBefore) { throw 'Legacy settings changed during handoff; inspect the backup before proceeding.' }
$taskTemporary = $taskSettingsPath+'.tmp'
[IO.File]::WriteAllText($taskTemporary,($taskSettings | ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
[IO.File]::Replace($taskTemporary,$taskSettingsPath,[NullString]::Value)
$taskDecky = Join-Path $env:USERPROFILE 'homebrew\plugins\claw-button-router'
foreach ($taskFile in @('main.py','package.json','plugin.json','dist\index.js')) {
    $taskDestination = Join-Path $taskDecky $taskFile
    if (Test-Path -LiteralPath $taskDestination) {
        $taskSaved = Join-Path $taskBackup ('decky\'+$taskFile)
        [IO.Directory]::CreateDirectory((Split-Path -Parent $taskSaved)) | Out-Null
        Copy-Item -LiteralPath $taskDestination -Destination $taskSaved
        Copy-Item -LiteralPath (Join-Path $taskSource $taskFile) -Destination $taskDestination -Force
    }
}
Start-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName Agent
$taskDeadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 250
    try { $taskLive = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskEndpointPath | ConvertFrom-Json } catch { $taskLive = $null }
} while (($taskLive -eq $null -or $taskLive.version -ne $taskVersion -or $taskLive.pid -eq $taskOldPid) -and (Get-Date) -lt $taskDeadline)
if ($taskLive -eq $null -or $taskLive.version -ne $taskVersion -or $taskLive.pid -eq $taskOldPid) { throw 'Updated agent did not become ready.' }
$taskReport = @{version=$taskVersion;legacySettingsPreserved=$true;backup=$taskBackup;agentPid=$taskLive.pid}
$taskReportFolder = Join-Path $taskRoot 'diagnostics\button-mappings'
[IO.Directory]::CreateDirectory($taskReportFolder) | Out-Null
[IO.File]::WriteAllText((Join-Path $taskReportFolder 'deployment.json'),($taskReport | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
$taskReport | ConvertTo-Json
