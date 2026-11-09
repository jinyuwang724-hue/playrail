# 首次启动引导「正在准备」用的组件安装：后台、PawnIO 驱动、风扇服务、Game Bar 小组件。
# 游戏库以管理员身份运行它一次（服务、驱动、开机任务都要管理员）。每一步先看本机已有的版本，
# 已经装好且不旧就跳过；进度逐步写进 <AgentHome>\setup\status.json，游戏库读这个文件画节点。
# 安装来源都在本脚本旁边（游戏库安装包里的 Payload 文件夹），不联网。
param(
    [Parameter(Mandatory)][string]$AgentHome,
    [Parameter(Mandatory)][string]$User,
    [Parameter(Mandatory)][string]$UserSid,
    [string]$Steps,
    [ValidatePattern('^[a-zA-Z0-9-]*$')][string]$Run = '',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$payload = $PSScriptRoot
$utf8 = New-Object Text.UTF8Encoding($false)
$taskPath = '\ClawToolbox\'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$bios = Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
$isClaw = $bios.SystemManufacturer -match 'Micro-Star|^MSI' -and $bios.SystemProductName -match 'Claw'
$fullInstall = -not $Steps
$order = @('fonts', 'pawnio', 'agent', 'sleep', 'fan', 'widget', 'edge-swipe', 'shell')
if ($fullInstall) {
    $requested = @($order | Where-Object { $isClaw -or $_ -notin 'pawnio', 'fan' })
} else {
    $requested = @($Steps.Split(',') | ForEach-Object { $_.Trim() })
    foreach ($step in $requested) {
        if ($step -notin $order) { throw "Unknown component: $step" }
    }
}
$selected = @($order | Where-Object { $_ -in $requested })
$manifest = Join-Path (Split-Path -Parent $payload) 'AppxManifest.xml'
$release = if (Test-Path -LiteralPath $manifest) {
    ([xml][IO.File]::ReadAllText($manifest)).Package.Identity.Version
}

function Assert-Payload {
    $files = @{
        agent = @('agent\agent.zip', 'agent\version.txt')
        sleep = @('sleep\Install-SleepAnimation.ps1', 'sleep\app\ClawSleepAnimation.exe', 'sleep\app\enhanced-bundle.json')
        fan = @('fan\ClawFanService.exe', 'fan\IntelMSR.bin')
        pawnio = @('pawnio\PawnIO_setup.exe')
        'edge-swipe' = @('shell\ClawShell.exe')
        shell = @('shell\ClawShell.exe')
    }
    foreach ($step in $selected) {
        foreach ($file in $files[$step]) {
            if (-not (Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) { throw "Missing package file: $file" }
        }
    }
    if ('widget' -in $selected -and @(Get-ChildItem -LiteralPath (Join-Path $payload 'widget') -Filter '*.msix' -File).Count -ne 1) {
        throw 'The package must contain exactly one Game Bar widget MSIX.'
    }
    if ('agent' -in $selected) {
        $null = [version]([IO.File]::ReadAllText((Join-Path $payload 'agent\version.txt'))).Trim()
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $payload 'agent\agent.zip'))
        try {
            if (-not $zip.GetEntry('app/claw_agent.py') -or -not $zip.GetEntry('app/plugin.json') -or
                @($zip.Entries | Where-Object FullName -match '^runtime/[^/]+/pythonw\.exe$').Count -ne 1) {
                throw 'Agent archive has an invalid app or Python runtime layout.'
            }
        } finally { $zip.Dispose() }
    }
    if ('sleep' -in $selected) {
        $source = Join-Path $payload 'sleep\app'
        $bundle = Get-Content -Raw -LiteralPath (Join-Path $source 'enhanced-bundle.json') | ConvertFrom-Json
        foreach ($file in $bundle.files.PSObject.Properties) {
            $path = Join-Path $source $file.Name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.Value) {
                throw "Power animation bundle failed verification: $($file.Name)"
            }
        }
    }
}
if ($ValidateOnly) { Assert-Payload; return }
if ($identity.User.Value -ne $UserSid -or $identity.Name -ne $User -or
    -not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run setup elevated as the interactive account that owns Claw Toolbox.'
}

$statusFolder = Join-Path $AgentHome 'setup'
[IO.Directory]::CreateDirectory($statusFolder) | Out-Null
$statusPath = Join-Path $statusFolder $(if ($Run) { 'status-' + $Run + '.json' } else { 'status.json' })
$status = [ordered]@{ schema = 1; release = $release; finished = $false; success = $false }
foreach ($name in $selected) { $status[$name] = @{ state = 'pending'; detail = '' } }

function Write-Json([string]$Path, $Value) {
    $tmp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($tmp, ($Value | ConvertTo-Json -Depth 10), $utf8)
    try {
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($tmp, $Path, [NullString]::Value) }
        else { [IO.File]::Move($tmp, $Path) }
    } finally {
        if ([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) }
    }
}
function Save-Status { Write-Json $statusPath $status }
function Set-Step([string]$Name, [string]$State, [string]$Detail = '') {
    $status[$Name] = @{ state = $State; detail = $Detail }
    Save-Status
}
function Read-Json([string]$Path) { [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json }

# ---- 和后台说话：agent.json 里的端口和令牌，JSON 一行一条 ----
function Invoke-Agent([string]$Method, [object[]]$Arguments = @(), [int]$TimeoutMs = 15000) {
    $endpoint = Read-Json (Join-Path $AgentHome 'agent.json')
    $tcp = New-Object Net.Sockets.TcpClient('127.0.0.1', [int]$endpoint.port)
    try {
        $stream = $tcp.GetStream(); $stream.ReadTimeout = $TimeoutMs
        $reader = New-Object IO.StreamReader($stream, $utf8)
        $writer = New-Object IO.StreamWriter($stream, $utf8); $writer.AutoFlush = $true
        $writer.WriteLine((@{ hello = $endpoint.token; client = 'oobe-installer' } | ConvertTo-Json -Compress))
        $hello = $reader.ReadLine() | ConvertFrom-Json
        if ($hello.hello -ne 'ok' -or $hello.pid -ne $endpoint.pid) { throw 'Agent authentication failed.' }
        $writer.WriteLine((@{ id = 1; method = $Method; args = $Arguments } | ConvertTo-Json -Compress -Depth 20))
        do { $reply = $reader.ReadLine() | ConvertFrom-Json } while ($reply -and $reply.id -ne 1)
        if (-not $reply) { throw 'Agent disconnected without replying.' }
        if ($reply.error) { throw $reply.error }
        return $reply.result
    } finally { $tcp.Dispose() }
}
function Test-Agent([string]$ExpectedScript) {
    try {
        $running = Invoke-Agent 'agent_info' -TimeoutMs 4000
        if (-not $ExpectedScript) { return $true }
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $($running.pid)"
        return $process.CommandLine -and $process.CommandLine.Contains('"' + $ExpectedScript + '"')
    } catch { return $false }
}
function Wait-Agent([int]$Seconds, [string]$ExpectedScript) {
    $until = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $until) { if (Test-Agent $ExpectedScript) { return $true }; Start-Sleep -Milliseconds 700 }
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
    Start-ScheduledTask -TaskPath $taskPath -TaskName Agent
}

function Restore-Task([string]$Name, [string]$Xml) {
    if ($Xml) {
        Register-ScheduledTask -TaskPath $taskPath -TaskName $Name -Xml $Xml -Force | Out-Null
    } elseif (Get-ScheduledTask -TaskPath $taskPath -TaskName $Name -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskPath $taskPath -TaskName $Name -Confirm:$false
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
    $version = [version]([IO.File]::ReadAllText((Join-Path $payload 'agent\version.txt'))).Trim()
    $currentPath = Join-Path $AgentHome 'current.json'
    $previous = if (Test-Path -LiteralPath $currentPath) { Read-Json $currentPath }
    $task = Get-ScheduledTask -TaskPath $taskPath -TaskName Agent -ErrorAction SilentlyContinue
    $archive = Join-Path $payload 'agent\agent.zip'
    $hash = (Get-FileHash -LiteralPath $archive).Hash
    $keep = $previous -and ([version]$previous.version -gt $version -or $previous.archiveHash -eq $hash)
    if ($keep -and $task -and $task.Actions.Execute -eq $previous.python -and
        $task.Actions.Arguments -eq ('"' + $previous.agent + '"')) {
        if (-not (Test-Agent $previous.agent)) {
            Start-Agent
            if (-not (Wait-Agent 30 $previous.agent)) { throw 'Installed agent did not start.' }
        }
        return "kept $($previous.version)"
    }
    $next = $previous
    if (-not $keep) {
        # Stage a complete release alongside the old one so a failed start can reuse the old files.
        $releases = Join-Path $env:ProgramFiles 'ClawToolbox\Agent\releases'
        Protect-Folder $releases
        $directory = Join-Path $releases "$version-$($hash.Substring(0, 16))"
        if (-not (Test-Path -LiteralPath $directory)) {
            $staging = Join-Path $releases ('stage-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            try {
                [IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
                [IO.Directory]::Move($staging, $directory)
            } finally {
                if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
            }
        }
        $runtime = @(Get-ChildItem -LiteralPath (Join-Path $directory 'runtime') -Directory)
        if ($runtime.Count -ne 1) { throw 'Agent package must contain one Python runtime.' }
        $next = @{
            version = "$version"; archiveHash = $hash
            python = Join-Path $runtime[0].FullName 'pythonw.exe'
            agent = Join-Path $directory 'app\claw_agent.py'
        }
    }
    foreach ($file in @($next.python, $next.agent)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Agent release file is missing: $file" }
    }
    $oldTask = if ($task) { Export-ScheduledTask -TaskPath $taskPath -TaskName Agent }
    Stop-Agent
    try {
        $action = New-ScheduledTaskAction -Execute $next.python -Argument ('"' + $next.agent + '"') -WorkingDirectory (Split-Path -Parent $next.agent)
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
        $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
        # 掌机常在电池上：默认设置会拒绝启动，或拔电时杀掉后台
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
            -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
        Register-ScheduledTask -TaskPath $taskPath -TaskName Agent -Action $action -Trigger $trigger -Principal $principal `
            -Settings $settings -Description 'Claw handheld toolbox agent: power, fan, OSD, rumble and buttons without Steam.' -Force | Out-Null
        Start-Agent
        if (-not (Wait-Agent 40 $next.agent)) { throw 'New agent did not start.' }
        Write-Json $currentPath $next
    } catch {
        $failure = $_
        try {
            Stop-ScheduledTask -TaskPath $taskPath -TaskName Agent -ErrorAction SilentlyContinue
            Restore-Task Agent $oldTask
            if ($oldTask) {
                Start-Agent
                if (-not (Wait-Agent 30)) { throw 'Previous agent did not start.' }
            }
        } catch { throw "Agent update failed: $failure. Agent rollback failed: $_" }
        throw $failure
    }
    return "active $($next.version)"
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
function Assert-LocalPath([string]$Path) {
    $check = [IO.Path]::GetFullPath($Path)
    while ($check) {
        if ((Test-Path -LiteralPath $check) -and ((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Refusing redirected service path: ' + $check) }
        $parent = Split-Path -Parent $check
        if ($parent -eq $check) { break }; $check = $parent
    }
}

function Protect-Folder([string]$Path) {
    Assert-LocalPath $Path
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
    $module = Join-Path $payload 'fan\IntelMSR.bin'
    $installedModule = Join-Path $serviceFolder 'pawnio\IntelMSR.bin'
    $old = Get-Service ClawFanService -ErrorAction SilentlyContinue
    if ($old -and (Test-Path -LiteralPath $installedExe) -and (Test-Path -LiteralPath $installedModule) -and
        (Get-FileHash -LiteralPath $installedExe).Hash -eq (Get-FileHash -LiteralPath $binary).Hash -and
        (Get-FileHash -LiteralPath $installedModule).Hash -eq (Get-FileHash -LiteralPath $module).Hash) {
        if ($old.Status -ne 'Running') { Start-Service ClawFanService }
        $old.WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
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
    Protect-Folder $serviceFolder
    Protect-Folder $configFolder
    Protect-Folder (Split-Path -Parent $installedModule)
    $saved = @{}
    foreach ($file in @($installedExe, $installedModule)) {
        $saved[$file] = $null
        if (Test-Path -LiteralPath $file) {
            $backup = $file + '.' + [guid]::NewGuid().ToString('N') + '.backup'
            Copy-Item -LiteralPath $file -Destination $backup
            $saved[$file] = $backup
        }
    }
    $hadConfig = Test-Path -LiteralPath $configPath
    Stop-Agent
    $changed = $false
    $restored = $false
    try {
        if ($old) { Stop-Service ClawFanService; $old.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
        $changed = $true
        Copy-Item -LiteralPath $binary -Destination $installedExe -Force
        Copy-Item -LiteralPath $module -Destination $installedModule -Force
        if (-not $hadConfig) {
            $fan = (Read-Json (Join-Path $data 'power.json')).fan
            if (-not $fan.control) { $fan | Add-Member -NotePropertyName control -NotePropertyValue 'custom' -Force }
            $config = @{ schema = 1; revision = 0; ownerSid = $UserSid; fan = $fan; factoryCpu = $original.cpu; factoryGpu = $original.gpu }
            [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10), $utf8)
        }
        if (-not $old) {
            New-Service -Name ClawFanService -BinaryPathName ('"' + $installedExe + '"') -DisplayName 'Claw 独立风扇服务' `
                -Description '开机读取自定义三档曲线；退出工具箱界面后继续调速。' -StartupType Automatic -DependsOn @('Winmgmt', 'PawnIO') | Out-Null
        }
        & sc.exe failure ClawFanService reset= 86400 actions= restart/2000/restart/5000/restart/15000 | Out-Null
        if ($LASTEXITCODE) { throw 'Could not set fan service recovery actions.' }
        & sc.exe failureflag ClawFanService 1 | Out-Null
        if ($LASTEXITCODE) { throw 'Could not enable fan service recovery actions.' }
        Start-Service ClawFanService
        (Get-Service ClawFanService).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
        $restored = $true
    } catch {
        $failure = $_
        try {
            if ($changed) {
                $service = Get-Service ClawFanService -ErrorAction SilentlyContinue
                if ($service) {
                    Stop-Service ClawFanService
                    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
                    if (-not $old) {
                        & sc.exe delete ClawFanService | Out-Null
                        if ($LASTEXITCODE) { throw 'Could not remove incomplete fan service registration.' }
                    }
                }
                foreach ($file in $saved.Keys) {
                    if ($saved[$file]) { Copy-Item -LiteralPath $saved[$file] -Destination $file -Force }
                    elseif (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
                }
                if (-not $hadConfig -and (Test-Path -LiteralPath $configPath)) { Remove-Item -LiteralPath $configPath -Force }
            }
            if ($old) { Start-Service ClawFanService; $old.WaitForStatus('Running', [TimeSpan]::FromSeconds(15)) }
            $restored = $true
        } catch { throw "Fan update failed: $failure. Fan rollback failed: $_" }
        throw $failure
    } finally {
        if ($restored) {
            foreach ($backup in $saved.Values) {
                if ($backup) { Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue }
            }
        }
        Start-Agent
        if (-not (Wait-Agent 30)) { throw 'Agent did not reconnect after fan service setup.' }
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
        $monitor = Get-ScheduledTask -TaskPath $taskPath -TaskName SleepAnimation -ErrorAction SilentlyContinue
        $recovery = Get-ScheduledTask -TaskPath $taskPath -TaskName SleepAnimationRecovery -ErrorAction SilentlyContinue
        if ($monitor -and $recovery -and $monitor.Actions.Execute -eq $installed.executable -and
            $recovery.Actions.Execute -eq $installed.executable -and (Test-Path -LiteralPath $installed.executable) -and
            (Test-Path -LiteralPath $oldMarker) -and (Read-Json $oldMarker).bundle -eq $marker.bundle) {
            if ($monitor.State -ne 'Running') { Start-ScheduledTask -TaskPath $taskPath -TaskName SleepAnimation }
            $until = (Get-Date).AddSeconds(15)
            do {
                try {
                    $ready = Read-Json (Join-Path $state 'monitor.json')
                    if ((Get-Process -Id $ready.pid -ErrorAction Stop).Path -eq $installed.executable) { return 'ready' }
                } catch { }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $until)
            throw 'Installed power animation monitor did not start.'
        }
    }
    & (Join-Path $payload 'sleep\Install-SleepAnimation.ps1') -Source $source -Report (Join-Path $statusFolder 'sleep-installation.json')
    return 'installed'
}

function Install-EdgeSwipeStep {
    $taskName = 'GameModeEdgeSwipe'
    $folder = Join-Path $env:ProgramFiles 'ClawToolbox\GameModeEdgeSwipe'
    $source = Join-Path $payload 'shell\ClawShell.exe'
    $version = (Get-Item -LiteralPath $source).VersionInfo.FileVersion
    $hash = (Get-FileHash -LiteralPath $source).Hash
    $binary = Join-Path $folder "ClawShell-$version-$($hash.Substring(0, 12)).exe"
    $task = Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task -and $task.Actions.Execute -eq $binary -and (Test-Path -LiteralPath $binary) -and
        (Get-FileHash -LiteralPath $binary).Hash -eq $hash -and
        (Get-ScheduledTaskInfo -TaskPath $taskPath -TaskName $taskName).LastTaskResult -eq 0) { return 'kept' }
    Protect-Folder $folder
    Copy-Item -LiteralPath $source -Destination $binary -Force
    $action = New-ScheduledTaskAction -Execute $binary -Argument 'edge-apply $(Arg0)'
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $UserSid
    $principal = New-ScheduledTaskPrincipal -UserId $UserSid -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances Queue -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
    $oldTask = if ($task) { Export-ScheduledTask -TaskPath $taskPath -TaskName $taskName }
    try {
        Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
        $started = (Get-Date).AddSeconds(-1)
        Start-ScheduledTask -TaskPath $taskPath -TaskName $taskName
        $until = (Get-Date).AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 250
            $info = Get-ScheduledTaskInfo -TaskPath $taskPath -TaskName $taskName
            if ($info.LastRunTime -ge $started -and (Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName).State -eq 'Ready') {
                if ($info.LastTaskResult -ne 0) { throw "Edge swipe task exited with $($info.LastTaskResult)." }
                return 'installed'
            }
        } while ((Get-Date) -lt $until)
        throw 'Edge swipe task did not complete.'
    } catch {
        $failure = $_
        try {
            Stop-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
            Restore-Task $taskName $oldTask
        } catch { throw "Edge swipe update failed: $failure. Task rollback failed: $_" }
        throw $failure
    }
}

function Install-ShellStep {
    $stable = Join-Path $AgentHome 'shell\ClawShell.exe'
    # Game mode installs its shell on first use. For existing users, stage the new
    # shell at the path Winlogon already uses and let the current session exit normally.
    if (-not (Test-Path -LiteralPath $stable -PathType Leaf)) { return 'available on first use' }
    Assert-LocalPath $stable
    $source = Join-Path $payload 'shell\ClawShell.exe'
    if ((Get-FileHash -LiteralPath $stable).Hash -eq (Get-FileHash -LiteralPath $source).Hash) { return 'kept' }
    $staged = $stable + '.' + [guid]::NewGuid().ToString('N') + '.new'
    $backup = Join-Path (Split-Path -Parent $stable) "ClawShell.old-$([DateTime]::UtcNow.Ticks)"
    try {
        Copy-Item -LiteralPath $source -Destination $staged
        [IO.File]::Replace($staged, $stable, $backup)
    } finally {
        if ([IO.File]::Exists($staged)) { [IO.File]::Delete($staged) }
    }
    return 'updated for the next shell start'
}

$lock = [Threading.Mutex]::new($false, 'Global\ClawToolbox.BackendSetup')
$locked = $false
$transcript = $false
try {
    try {
        $locked = $lock.WaitOne(0)
    } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another Claw backend installation is running.' }
    Start-Transcript -LiteralPath (Join-Path $statusFolder 'install.log') -Force | Out-Null
    $transcript = $true
    Save-Status
    Assert-Payload

    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $root = $scheduler.GetFolder('\')
    if (-not @($root.GetFolders(0) | Where-Object Name -eq 'ClawToolbox').Count) {
        $null = $root.CreateFolder('ClawToolbox')
    }

    foreach ($name in $selected) {
        Set-Step $name 'running'
        try {
            $detail = switch ($name) {
                'fonts' { Install-FontsStep }
                'agent' { Install-AgentStep }
                'sleep' { Install-SleepStep }
                'edge-swipe' { Install-EdgeSwipeStep }
                'shell' { Install-ShellStep }
                'pawnio' { Install-PawnIOStep }
                'fan' {
                    if ($status.agent.state -eq 'failed' -or $status.pawnio.state -eq 'failed') {
                        throw 'Fan setup requires working Agent and PawnIO installations.'
                    }
                    Install-FanStep
                }
                'widget' { Install-WidgetStep }
            }
            Set-Step $name 'done' ([string]$detail)
            Write-Output ($name + ': ' + $detail)
        } catch {
            Set-Step $name 'failed' $_.Exception.Message
            Write-Output ($name + ' failed: ' + $_.Exception.Message)
        }
    }
    $status.finished = $true
    $status.success = @($selected | Where-Object { $status[$_].state -ne 'done' }).Count -eq 0
    if ($fullInstall -and $status.success) {
        $status.completedAt = [DateTime]::UtcNow.ToString('o')
        Write-Json (Join-Path $statusFolder 'backend.json') $status
    }
} catch {
    $status.finished = $true
    $status.success = $false
    $status.error = $_.Exception.Message
    Write-Output ("Claw backend setup failed: " + $status.error)
} finally {
    try {
        if ($locked -or $Run) { Save-Status }
    } finally {
        if ($transcript) { Stop-Transcript | Out-Null }
        if ($locked) { $lock.ReleaseMutex() }
        $lock.Dispose()
    }
}
if (-not $status.success) { exit 1 }
exit 0
