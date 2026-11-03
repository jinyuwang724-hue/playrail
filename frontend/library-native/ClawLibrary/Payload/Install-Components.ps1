# 首次启动引导「正在准备」用的组件安装：后台、PawnIO 驱动、风扇服务、Game Bar 小组件。
# 游戏库以管理员身份运行它一次（服务、驱动、开机任务都要管理员）。每一步先看本机已有的版本，
# 已经装好且不旧就跳过；进度逐步写进 <AgentHome>\setup\status.json，游戏库读这个文件画节点。
# 安装来源都在本脚本旁边（游戏库安装包里的 Payload 文件夹），不联网。
param(
    [Parameter(Mandatory)][string]$AgentHome,
    [Parameter(Mandatory)][string]$User,
    [Parameter(Mandatory)][string]$UserSid,
    [string]$Steps = 'fonts,agent,sleep,pawnio,fan,widget',
    [string]$Run = '',
    [switch]$SkipTask
)
$ErrorActionPreference = 'Stop'
$payload = $PSScriptRoot
$utf8 = New-Object Text.UTF8Encoding($false)
$statusFolder = Join-Path $AgentHome 'setup'
[IO.Directory]::CreateDirectory($statusFolder) | Out-Null
$statusPath = Join-Path $statusFolder $(if ($Run) { 'status-' + $Run + '.json' } else { 'status.json' })
$status = [ordered]@{}
foreach ($name in $Steps.Split(',')) { $status[$name] = @{ state = 'pending'; detail = '' } }
$status['finished'] = $false

function Save-Status {
    $tmp = $statusPath + '.tmp'
    [IO.File]::WriteAllText($tmp, ($status | ConvertTo-Json -Depth 4), $utf8)
    Move-Item -LiteralPath $tmp -Destination $statusPath -Force
}
function Set-Step([string]$Name, [string]$State, [string]$Detail = '') {
    $status[$Name] = @{ state = $State; detail = $Detail }
    Save-Status
}
function Read-Json([string]$Path) { [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json }
Start-Transcript -LiteralPath (Join-Path $statusFolder 'install.log') -Force | Out-Null
Save-Status

# ---- 和后台说话：agent.json 里的端口和令牌，JSON 一行一条 ----
function Invoke-Agent([string]$Method, [object[]]$Arguments = @(), [int]$TimeoutMs = 15000) {
    $endpoint = Read-Json (Join-Path $AgentHome 'agent.json')
    $tcp = New-Object Net.Sockets.TcpClient('127.0.0.1', [int]$endpoint.port)
    try {
        $stream = $tcp.GetStream(); $stream.ReadTimeout = $TimeoutMs
        $reader = New-Object IO.StreamReader($stream, $utf8)
        $writer = New-Object IO.StreamWriter($stream, $utf8); $writer.AutoFlush = $true
        $writer.WriteLine((@{ hello = $endpoint.token; client = 'oobe-installer' } | ConvertTo-Json -Compress))
        if (($reader.ReadLine() | ConvertFrom-Json).hello -ne 'ok') { throw 'Agent authentication failed.' }
        $writer.WriteLine((@{ id = 1; method = $Method; args = $Arguments } | ConvertTo-Json -Compress -Depth 20))
        do { $reply = $reader.ReadLine() | ConvertFrom-Json } while ($reply -and $reply.id -ne 1)
        if ($reply.error) { throw $reply.error }
        return $reply.result
    } finally { $tcp.Dispose() }
}
function Test-Agent {
    try { $null = Invoke-Agent 'get_settings_state' -TimeoutMs 4000; return $true } catch { return $false }
}
function Wait-Agent([int]$Seconds) {
    $until = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $until) { if (Test-Agent) { return $true }; Start-Sleep -Milliseconds 700 }
    return $false
}
function Stop-Agent {
    $endpointPath = Join-Path $AgentHome 'agent.json'
    if (-not (Test-Path -LiteralPath $endpointPath)) { return }
    $endpoint = Read-Json $endpointPath
    try { $null = Invoke-Agent 'shutdown' } catch { }
    $old = Get-Process -Id $endpoint.pid -ErrorAction SilentlyContinue
    if ($old -and -not $old.WaitForExit(20000)) { throw 'Old toolbox agent did not exit.' }
}
function Start-Agent {
    if ($SkipTask) { return }
    Start-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName Agent
}

# 只拷内容变了的文件：正在用的文件内容没变就不碰
function Copy-Tree([string]$From, [string]$To) {
    foreach ($file in Get-ChildItem -LiteralPath $From -Recurse -File) {
        $relative = $file.FullName.Substring($From.TrimEnd('\').Length + 1)
        $target = Join-Path $To $relative
        if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -eq $file.Length -and
            (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) { continue }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

# ---------------- 0 字体 ----------------
# 游戏库随包的字体（SF Pro、苹方、Noto Serif Display、Cormorant、Motiva）装进系统字体目录。
# 装好以后游戏库按字体名直接用系统字体：按文件路径加载的随包字体每排一段文字要 2.7 ms，系统字体 0.05 ms。
if (-not ('ClawFontApi' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ClawFontApi {
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern int AddFontResourceW(string file);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
}
'@
}
function Install-FontsStep {
    $source = Join-Path (Split-Path -Parent $payload) 'Assets\Fonts'
    $target = Join-Path $env:WINDIR 'Fonts'
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts'
    $added = 0
    foreach ($font in Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.otf', '.ttf' }) {
        $dest = Join-Path $target $font.Name
        $same = (Test-Path -LiteralPath $dest) -and (Get-FileHash -LiteralPath $dest).Hash -eq (Get-FileHash -LiteralPath $font.FullName).Hash
        $name = $font.BaseName + $(if ($font.Extension -eq '.otf') { ' (OpenType)' } else { ' (TrueType)' })
        $registered = (Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue) -ne $null
        if ($same -and $registered) { continue }
        if (-not $same) { Copy-Item -LiteralPath $font.FullName -Destination $dest -Force }
        Set-ItemProperty -Path $key -Name $name -Value $font.Name
        [void][ClawFontApi]::AddFontResourceW($dest)
        $added++
    }
    if ($added) {
        $r = [IntPtr]::Zero
        [void][ClawFontApi]::SendMessageTimeoutW([IntPtr]0xffff, 0x001D, [IntPtr]::Zero, [IntPtr]::Zero, 2, 1000, [ref]$r)   # WM_FONTCHANGE
        return "installed $added"
    }
    return 'kept'
}

# ---------------- 1 后台 ----------------
function Install-AgentStep {
    $version = ([IO.File]::ReadAllText((Join-Path $payload 'agent\version.txt'))).Trim()
    $currentPath = Join-Path $AgentHome 'current.json'
    $installed = if (Test-Path -LiteralPath $currentPath) { (Read-Json $currentPath).version } else { $null }
    $task = Get-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName Agent -ErrorAction SilentlyContinue
    if ($installed -and $task -and ([version]$installed -ge [version]$version)) {
        if (-not (Test-Agent)) { Start-Agent; if (-not (Wait-Agent 30)) { throw 'Agent did not start.' } }
        return "kept $installed"
    }
    # 安装包里是一个 zip（app\ + runtime\python-x.y.z\），先解到临时目录再只拷变了的文件
    $source = Join-Path $env:TEMP ('claw-agent-' + [guid]::NewGuid().ToString('N'))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $payload 'agent\agent.zip'), $source)
    if ($installed) { Stop-Agent }
    $runtimeSource = Get-ChildItem -LiteralPath (Join-Path $source 'runtime') -Directory | Select-Object -First 1
    $runtime = Join-Path $AgentHome ('runtime\' + $runtimeSource.Name)
    Copy-Tree $runtimeSource.FullName $runtime
    $app = Join-Path $AgentHome 'app'
    Copy-Tree (Join-Path $source 'app') $app
    Remove-Item -LiteralPath $source -Recurse -Force -ErrorAction SilentlyContinue
    $python = Join-Path $runtime 'pythonw.exe'
    $agent = Join-Path $app 'claw_agent.py'
    [IO.File]::WriteAllText($currentPath, (@{ version = $version; python = $python; agent = $agent } | ConvertTo-Json), $utf8)
    if (-not $SkipTask) {
        $action = New-ScheduledTaskAction -Execute $python -Argument ('"' + $agent + '"') -WorkingDirectory $app
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
        $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
        # 掌机常在电池上：默认设置会拒绝启动，或拔电时杀掉后台
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
            -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances Parallel -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
        Register-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName 'Agent' -Action $action -Trigger $trigger -Principal $principal `
            -Settings $settings -Description 'Claw handheld toolbox agent: power, fan, OSD, rumble and buttons without Steam.' -Force | Out-Null
        Start-Agent
        if (-not (Wait-Agent 40)) { throw 'Agent did not start.' }
    }
    return "installed $version"
}

# ---------------- 2 PawnIO（功耗和风扇读写 MSR / EC 用的签名驱动） ----------------
function Install-PawnIOStep {
    if (Get-Service PawnIO -ErrorAction SilentlyContinue) { return 'kept' }
    $setup = Join-Path $payload 'pawnio\PawnIO_setup.exe'
    if (-not (Test-Path -LiteralPath $setup)) { throw 'PawnIO setup is not in this package.' }
    $process = Start-Process -FilePath $setup -ArgumentList '-install', '-silent' -Wait -PassThru -WindowStyle Hidden
    if (-not (Get-Service PawnIO -ErrorAction SilentlyContinue)) { throw ('PawnIO setup exited ' + $process.ExitCode + ' without a service.') }
    return 'installed'
}

# ---------------- 3 风扇服务 ----------------
function Protect-Folder([string]$Path) {
    # 装 SYSTEM 服务前拒绝被重定向的目录
    $check = [IO.Path]::GetFullPath($Path)
    while ($check) {
        if ((Test-Path -LiteralPath $check) -and ((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Refusing redirected service path: ' + $check) }
        $parent = Split-Path -Parent $check
        if ($parent -eq $check) { break }; $check = $parent
    }
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'))
    foreach ($pair in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-32-545', 'ReadAndExecute'))) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $pair[0]), $pair[1], 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Install-FanStep {
    $binary = Join-Path $payload 'fan\ClawFanService.exe'
    $serviceFolder = Join-Path $env:ProgramFiles 'ClawToolbox\FanService'
    $installedExe = Join-Path $serviceFolder 'ClawFanService.exe'
    $old = Get-Service ClawFanService -ErrorAction SilentlyContinue
    if ($old -and (Test-Path -LiteralPath $installedExe) -and (Get-FileHash -LiteralPath $installedExe).Hash -eq (Get-FileHash -LiteralPath $binary).Hash) {
        if ($old.Status -ne 'Running') { Start-Service ClawFanService }
        return 'kept'
    }
    if (-not (Get-Service PawnIO -ErrorAction SilentlyContinue)) { throw 'PawnIO is not installed.' }
    if (-not (Test-Agent)) { throw 'Agent is not running.' }
    $data = Join-Path $AgentHome 'data'
    $backupPath = Join-Path $data 'fan-backup.json'
    if (-not (Test-Path -LiteralPath $backupPath)) {
        # 出厂曲线只在后台第一次接管风扇时备份：切到自定义一次，让后台先把固件曲线存下来
        $state = Invoke-Agent 'get_power_state' @($false) 30000
        $settings = $state.settings
        $settings.fan.control = 'custom'
        $null = Invoke-Agent 'save_power' @($settings) 30000
        $until = (Get-Date).AddSeconds(15)
        while (-not (Test-Path -LiteralPath $backupPath) -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 500 }
        if (-not (Test-Path -LiteralPath $backupPath)) { throw 'Factory fan curve was not saved.' }
    }
    $original = Read-Json $backupPath
    foreach ($table in @($original.cpu, $original.gpu)) {
        if ($table.Count -ne 6 -or @($table | Where-Object { $_ -lt 1 -or $_ -gt 150 }).Count) { throw 'Factory fan backup is invalid.' }
    }
    $configFolder = Join-Path $env:ProgramData 'ClawToolbox\FanService'
    $configPath = Join-Path $configFolder 'service.json'
    Stop-Agent
    if ($old) { Stop-Service ClawFanService; $old.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
    try {
        Protect-Folder $serviceFolder
        Protect-Folder $configFolder
        Protect-Folder (Join-Path $serviceFolder 'pawnio')
        Copy-Item -LiteralPath $binary -Destination $installedExe -Force
        Copy-Item -LiteralPath (Join-Path $payload 'fan\IntelMSR.bin') -Destination (Join-Path $serviceFolder 'pawnio\IntelMSR.bin') -Force
        if (-not (Test-Path -LiteralPath $configPath)) {
            $fan = (Read-Json (Join-Path $data 'power.json')).fan
            if (-not $fan.control) { $fan | Add-Member -NotePropertyName control -NotePropertyValue 'custom' -Force }
            $config = @{ schema = 1; revision = 0; ownerSid = $UserSid; fan = $fan; factoryCpu = $original.cpu; factoryGpu = $original.gpu }
            [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10), $utf8)
        }
        if ($old) { Set-Service ClawFanService -StartupType Automatic }
        else {
            New-Service -Name ClawFanService -BinaryPathName ('"' + $installedExe + '"') -DisplayName 'Claw 独立风扇服务' `
                -Description '开机读取自定义三档曲线；退出工具箱界面后继续调速。' -StartupType Automatic -DependsOn @('Winmgmt', 'PawnIO') | Out-Null
        }
        & sc.exe failure ClawFanService reset= 86400 actions= restart/2000/restart/5000/restart/15000 | Out-Null
        & sc.exe failureflag ClawFanService 1 | Out-Null
        Start-Service ClawFanService
        (Get-Service ClawFanService).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
    } finally {
        # 服务装没装成，都把后台拉回来；后台会自己报告风扇服务的错误
        Start-Agent
        $null = Wait-Agent 30
    }
    return 'installed'
}

# ---------------- 4 Game Bar 小组件 ----------------
function Install-WidgetStep {
    $msix = Get-ChildItem -LiteralPath (Join-Path $payload 'widget') -Filter *.msix | Select-Object -First 1
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($msix.FullName)
    try {
        $entry = $zip.GetEntry('AppxManifest.xml')
        $reader = New-Object IO.StreamReader($entry.Open())
        [xml]$manifest = $reader.ReadToEnd(); $reader.Dispose()
    } finally { $zip.Dispose() }
    $version = [version]$manifest.Package.Identity.Version
    $existing = Get-AppxPackage -Name $manifest.Package.Identity.Name
    if (-not $existing -or [version]$existing.Version -lt $version) {
        Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
        $existing = Get-AppxPackage -Name $manifest.Package.Identity.Name
        if (-not $existing) { throw 'Widget package did not register.' }
        $result = 'installed ' + $existing.Version
    } else { $result = 'kept ' + $existing.Version }
    # Game Bar 掌机模式的第一个小组件位：换成我们的
    $slot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\GamingConfiguration'
    if (-not (Test-Path $slot)) { New-Item -Path $slot -Force | Out-Null }
    $family = $existing.PackageFamilyName
    if ((Get-ItemProperty -Path $slot -Name OEMGameBarWidget -ErrorAction SilentlyContinue).OEMGameBarWidget -ne $family) {
        Set-ItemProperty -Path $slot -Name OEMGameBarWidget -Value $family
    }
    return $result
}


function Install-SleepStep {
    $source = Join-Path $payload 'sleep\app'
    $marker = Read-Json (Join-Path $source 'enhanced-bundle.json')
    $state = Join-Path $env:ProgramData ('ClawToolbox\SleepAnimation\' + $UserSid)
    $record = Join-Path $state 'installation.json'
    if (Test-Path -LiteralPath $record) {
        $installed = Read-Json $record
        $oldMarker = Join-Path (Split-Path -Parent $installed.executable) 'enhanced-bundle.json'
        if ((Test-Path -LiteralPath $oldMarker) -and (Read-Json $oldMarker).bundle -eq $marker.bundle) { return 'kept' }
    }
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $UserSid) {
        throw 'Sleep installation must run as the same interactive account.'
    }
    & (Join-Path $payload 'sleep\Install-SleepAnimation.ps1') -Source $source -Report (Join-Path $statusFolder 'sleep-installation.json')
    return 'installed'
}

$code = 0
foreach ($name in $Steps.Split(',')) {
    Set-Step $name 'running'
    try {
        $detail = switch ($name) {
            'fonts' { Install-FontsStep }
            'agent' { Install-AgentStep }
            'sleep' { Install-SleepStep }
            'pawnio' { Install-PawnIOStep }
            'fan' { Install-FanStep }
            'widget' { Install-WidgetStep }
        }
        Set-Step $name 'done' ([string]$detail)
        Write-Output ($name + ': ' + $detail)
    } catch {
        Set-Step $name 'failed' $_.Exception.Message
        Write-Output ($name + ' failed: ' + $_.Exception.Message)
        $code = 1
    }
}
$status['finished'] = $true
Save-Status
Stop-Transcript | Out-Null
exit $code
