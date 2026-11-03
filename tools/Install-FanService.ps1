# Installs only the independent fan controller and its agent client. No firmware updates.
param(
    [string]$AgentHome = (Join-Path $env:LOCALAPPDATA 'ClawToolbox'),
    [string]$OwnerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $taskAdmin) {
    $taskArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"'),'-AgentHome',('"'+$AgentHome+'"'),'-OwnerSid',$OwnerSid,'-Elevated')
    $taskProcess = Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $taskArguments
    if ($taskProcess.ExitCode -ne 0) { throw ('Fan service installation failed: '+$taskProcess.ExitCode) }
    Get-Service ClawFanService | Select-Object Name,Status,StartType
    return
}
$taskLogFolder = Join-Path $taskRoot 'diagnostics\fan-service'
[IO.Directory]::CreateDirectory($taskLogFolder) | Out-Null
$taskLog = Join-Path $taskLogFolder 'install.log'
Start-Transcript -LiteralPath $taskLog -Force | Out-Null
try {
    $taskBinary = Join-Path $taskRoot 'plugin\bin\ClawFanService.exe'
    $taskModule = Join-Path $taskRoot 'plugin\bin\pawnio\IntelMSR.bin'
    $taskEcModule = Join-Path $taskRoot 'plugin\bin\pawnio\LpcACPIEC.bin'
    $taskApp = Join-Path $AgentHome 'app'
    $taskData = Join-Path $AgentHome 'data'
    $taskServiceFolder = Join-Path $env:ProgramFiles 'ClawToolbox\FanService'
    $taskConfigFolder = Join-Path $env:ProgramData 'ClawToolbox\FanService'
    $taskConfigPath = Join-Path $taskConfigFolder 'service.json'
    $taskBackupPath = Join-Path $taskData 'fan-backup.json'
    foreach ($taskRequired in @($taskBinary,$taskModule,$taskEcModule,$taskBackupPath,(Join-Path $taskApp 'claw_power.py'),(Join-Path $AgentHome 'current.json'))) {
        if (-not (Test-Path -LiteralPath $taskRequired -PathType Leaf)) { throw ('Missing required file: '+$taskRequired) }
    }
    $taskOriginal = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskBackupPath | ConvertFrom-Json
    foreach ($taskTable in @($taskOriginal.cpu,$taskOriginal.gpu)) {
        if ($taskTable.Count -ne 6 -or @($taskTable | Where-Object { $_ -lt 1 -or $_ -gt 150 }).Count) { throw 'Factory fan backup is invalid.' }
    }
    function Protect-Folder([string]$Path) {
        # Refuse redirects before installing a SYSTEM executable/configuration.
        $taskCheck = [IO.Path]::GetFullPath($Path)
        while ($taskCheck) {
            if (Test-Path -LiteralPath $taskCheck) {
                if ((Get-Item -LiteralPath $taskCheck -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Refusing redirected service path: '+$taskCheck) }
            }
            $taskParent = Split-Path -Parent $taskCheck
            if ($taskParent -eq $taskCheck) { break }; $taskCheck = $taskParent
        }
        [IO.Directory]::CreateDirectory($Path) | Out-Null
        $taskAcl = New-Object Security.AccessControl.DirectorySecurity
        $taskAcl.SetAccessRuleProtection($true,$false)
        $taskAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
        foreach ($taskPair in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))) {
            $taskRule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $taskPair[0]),$taskPair[1],'ContainerInherit,ObjectInherit','None','Allow')
            $taskAcl.AddAccessRule($taskRule)
        }
        Set-Acl -LiteralPath $Path -AclObject $taskAcl
    }
    $taskBackupFolder = Join-Path $taskLogFolder ('backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    [IO.Directory]::CreateDirectory($taskBackupFolder) | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskApp 'claw_power.py') -Destination (Join-Path $taskBackupFolder 'claw_power.py')
    Copy-Item -LiteralPath (Join-Path $taskData 'power.json') -Destination (Join-Path $taskBackupFolder 'power.json')
    if (Test-Path -LiteralPath $taskConfigPath) { Copy-Item -LiteralPath $taskConfigPath -Destination (Join-Path $taskBackupFolder 'service.json') }
    # Deploy the client before asking the old agent to exit, so reconnecting frontends
    # cannot relaunch an old writer during the handoff.
    foreach ($taskFile in @('claw_power.py','claw_fan_service.py')) {
        Copy-Item -LiteralPath (Join-Path $taskRoot ('plugin\'+$taskFile)) -Destination (Join-Path $taskApp $taskFile) -Force
    }
    $taskEndpointPath = Join-Path $AgentHome 'agent.json'
    if (Test-Path -LiteralPath $taskEndpointPath) {
        $taskEndpoint = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskEndpointPath | ConvertFrom-Json
        $taskTcp = New-Object Net.Sockets.TcpClient('127.0.0.1', [int]$taskEndpoint.port)
        try {
            $taskStream = $taskTcp.GetStream(); $taskStream.ReadTimeout = 10000
            $taskReader = New-Object IO.StreamReader($taskStream)
            $taskWriter = New-Object IO.StreamWriter($taskStream, (New-Object Text.UTF8Encoding($false)))
            $taskWriter.AutoFlush = $true
            $taskWriter.WriteLine((@{hello=$taskEndpoint.token;client='fan-service-installer'} | ConvertTo-Json -Compress))
            $taskHello = $taskReader.ReadLine() | ConvertFrom-Json
            if ($taskHello.hello -ne 'ok') { throw 'Agent authentication failed.' }
            $taskWriter.WriteLine('{"id":1,"method":"shutdown","args":[]}')
            do { $taskReply = $taskReader.ReadLine() | ConvertFrom-Json } while ($taskReply -and $taskReply.id -ne 1)
        } finally { $taskTcp.Dispose() }
        $taskOldAgent = Get-Process -Id $taskEndpoint.pid -ErrorAction SilentlyContinue
        if ($taskOldAgent -and -not $taskOldAgent.WaitForExit(20000)) { throw 'Old toolbox agent did not exit; handoff aborted.' }
    }
    $taskOldService = Get-Service ClawFanService -ErrorAction SilentlyContinue
    if ($taskOldService) { Stop-Service ClawFanService; $taskOldService.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20)) }
    Protect-Folder $taskServiceFolder
    Protect-Folder $taskConfigFolder
    Protect-Folder (Join-Path $taskServiceFolder 'pawnio')
    Copy-Item -LiteralPath $taskBinary -Destination (Join-Path $taskServiceFolder 'ClawFanService.exe') -Force
    Copy-Item -LiteralPath $taskModule -Destination (Join-Path $taskServiceFolder 'pawnio\IntelMSR.bin') -Force
    Copy-Item -LiteralPath $taskEcModule -Destination (Join-Path $taskServiceFolder 'pawnio\LpcACPIEC.bin') -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'plugin\bin\pawnio\LpcACPIEC-COPYING.txt') -Destination (Join-Path $taskServiceFolder 'pawnio\LpcACPIEC-COPYING.txt') -Force
    if (-not (Test-Path -LiteralPath $taskConfigPath)) {
        $taskPower = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskData 'power.json') | ConvertFrom-Json
        $taskFan = $taskPower.fan
        if (-not $taskFan.control) { $taskFan | Add-Member -NotePropertyName control -NotePropertyValue 'custom' -Force }
        $taskConfig = @{schema=1;revision=0;ownerSid=$OwnerSid;fan=$taskFan;factoryCpu=$taskOriginal.cpu;factoryGpu=$taskOriginal.gpu}
        [IO.File]::WriteAllText($taskConfigPath,($taskConfig | ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
    }
    $taskImage = '"'+(Join-Path $taskServiceFolder 'ClawFanService.exe')+'"'
    if ($taskOldService) { Set-Service ClawFanService -StartupType Automatic }
    else { New-Service -Name ClawFanService -BinaryPathName $taskImage -DisplayName 'Claw 独立风扇服务' -Description '开机读取自定义三档曲线；退出工具箱界面后继续调速。' -StartupType Automatic -DependsOn @('Winmgmt','PawnIO') | Out-Null }
    & sc.exe failure ClawFanService reset= 86400 actions= restart/2000/restart/5000/restart/15000
    if ($LASTEXITCODE -ne 0) { throw 'Service recovery configuration failed.' }
    & sc.exe failureflag ClawFanService 1
    if ($LASTEXITCODE -ne 0) { throw 'Service recovery flag configuration failed.' }
    Start-Service ClawFanService
    (Get-Service ClawFanService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    Start-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName Agent
    Get-CimInstance Win32_Service -Filter "Name='ClawFanService'" | Select-Object Name,State,StartMode,StartName,ProcessId
    Write-Output 'Installed. Firmware and BIOS were not modified.'
} catch {
    Write-Error $_ -ErrorAction Continue
    # Bring the UI agent back even if service setup failed; its client will report a
    # service error instead of silently starting a competing fan writer.
    Start-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName Agent -ErrorAction SilentlyContinue
    exit 1
} finally { Stop-Transcript | Out-Null }
