# Scoped repair: replace only the current 0.17.55 router, retaining the agent and all settings.
param([string]$Binary=(Join-Path $PSScriptRoot '..\artifacts\gamebar-flash\ClawRouter.exe'))
$ErrorActionPreference='Stop'
$taskHome=Join-Path $env:LOCALAPPDATA 'ClawToolbox'
$taskCurrent=Get-Content -Raw -LiteralPath (Join-Path $taskHome 'current.json')|ConvertFrom-Json
if($taskCurrent.version -ne '0.17.55'){throw 'The installed agent changed; rebase this router repair first.'}
$taskTarget=Join-Path $taskHome 'app\bin\ClawRouter-0.17.55.exe'
$taskOldHash=(Get-FileHash -LiteralPath $taskTarget).Hash
$taskNewHash=(Get-FileHash -LiteralPath $Binary).Hash
if($taskOldHash -eq $taskNewHash){Write-Output 'Router repair is already installed.';return}
if($taskOldHash -ne '954749427F43E728D6E698269126842B8E0D3E4760016BF389C9BA93298404B9'){throw 'Unexpected installed router; preserve the newer repair before applying this one.'}
$taskBackup=Join-Path $taskHome ('repair-backups\gamebar-router-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $taskBackup -Force|Out-Null
Copy-Item -LiteralPath $taskTarget -Destination (Join-Path $taskBackup 'ClawRouter-0.17.55.exe')
$taskSettings=Join-Path $taskHome 'data\settings.json'
Copy-Item -LiteralPath $taskSettings -Destination (Join-Path $taskBackup 'settings.json')
$taskSettingsHash=(Get-FileHash -LiteralPath $taskSettings).Hash
$taskEndpointPath=Join-Path $taskHome 'agent.json'
$taskEndpoint=Get-Content -Raw -LiteralPath $taskEndpointPath|ConvertFrom-Json
$taskOldProcess=Get-Process -Id $taskEndpoint.pid -ErrorAction Stop
$taskTcp=New-Object Net.Sockets.TcpClient('127.0.0.1',[int]$taskEndpoint.port)
try {
    $taskStream=$taskTcp.GetStream();$taskStream.ReadTimeout=10000
    $taskReader=New-Object IO.StreamReader($taskStream)
    $taskWriter=New-Object IO.StreamWriter($taskStream,(New-Object Text.UTF8Encoding($false)));$taskWriter.AutoFlush=$true
    $taskWriter.WriteLine((@{hello=$taskEndpoint.token;client='gamebar-router-repair'}|ConvertTo-Json -Compress))
    $taskHello=$taskReader.ReadLine()|ConvertFrom-Json
    if($taskHello.hello -ne 'ok'){throw 'Agent authentication failed.'}
    $taskWriter.WriteLine('{"id":1,"method":"shutdown","args":[]}')
    do{$taskReply=$taskReader.ReadLine()|ConvertFrom-Json}while($taskReply -and $taskReply.id -ne 1)
} finally {$taskTcp.Dispose()}
if(-not $taskOldProcess.WaitForExit(30000)){throw 'Agent cleanup is still running; no executable has been replaced.'}
if(Get-Process -Name 'ClawRouter-0.17.55' -ErrorAction SilentlyContinue){throw 'The previous router has not exited; no executable has been replaced.'}
Copy-Item -LiteralPath $Binary -Destination $taskTarget -Force
Start-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName Agent
$taskDeadline=(Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 250
    try{$taskLive=Get-Content -Raw -LiteralPath $taskEndpointPath|ConvertFrom-Json}catch{$taskLive=$null}
}while(($taskLive -eq $null -or $taskLive.pid -eq $taskEndpoint.pid) -and (Get-Date) -lt $taskDeadline)
if($taskLive -eq $null -or $taskLive.pid -eq $taskEndpoint.pid){throw ('Agent did not restart; the original executable is preserved at '+$taskBackup)}
$taskReport=@{at=(Get-Date).ToUniversalTime().ToString('o');backup=$taskBackup;version=$taskCurrent.version;oldHash=$taskOldHash;newHash=$taskNewHash;agentPid=$taskLive.pid;settingsUnchanged=(Get-FileHash -LiteralPath $taskSettings).Hash -eq $taskSettingsHash}
$taskReport|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\artifacts\gamebar-flash\deployment.json') -Encoding UTF8
$taskReport|ConvertTo-Json
if(-not $taskReport.settingsUnchanged){throw 'Settings changed during restart; inspect the backup before continuing.'}
