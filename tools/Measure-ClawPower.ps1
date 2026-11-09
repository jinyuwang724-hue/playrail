param(
    [ValidateRange(15, 3600)][int]$Seconds = 300,
    [ValidateRange(2, 300)][int]$SettleSeconds = 45,
    [string]$GpuBatchHelper,
    [string]$OsdCandidate,
    [switch]$OsdOnly,
    [ValidateRange(24, 360)][int]$DisplayHz = 120,
    [ValidateRange(0, 100)][int]$DisplayBrightness = 80,
    [switch]$ApplyDisplay,
    [string]$FrameProbe,
    [string]$Output = (Join-Path $PSScriptRoot ('..\artifacts\power-abcd-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
public sealed class AgentBaselineGate : IDisposable {
    readonly ManualResetEvent waiting = new ManualResetEvent(false);
    readonly ManualResetEvent acquired = new ManualResetEvent(false);
    readonly ManualResetEvent release = new ManualResetEvent(false);
    readonly Thread worker;
    public AgentBaselineGate() {
        worker = new Thread(Run) { IsBackground = true };
        worker.Start();
        waiting.WaitOne();
    }
    void Run() {
        using (var mutex = new Mutex(false, @"Local\ClawToolboxAgent")) {
            waiting.Set();
            int result;
            try { result = WaitHandle.WaitAny(new WaitHandle[] { release, mutex }); }
            catch (AbandonedMutexException e) { result = e.MutexIndex; }
            if (result != 1) return;
            try { acquired.Set(); release.WaitOne(); }
            finally { mutex.ReleaseMutex(); }
        }
    }
    public bool WaitForAcquisition(int timeout) { return acquired.WaitOne(timeout); }
    public void Dispose() {
        release.Set();
        worker.Join();
        waiting.Dispose();
        acquired.Dispose();
        release.Dispose();
    }
}
public static class ClawMeasurement {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    [DllImport("powrprof.dll")] static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLen, [Out] byte[] output, uint outputLen);
    public static uint ForegroundPid() {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid;
    }
    public static void Focus(IntPtr window) {
        if (window == IntPtr.Zero || SetForegroundWindow(window)) return;
        uint pid, thread = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        uint own = GetCurrentThreadId();
        if (thread == 0 || thread == own || !AttachThreadInput(own, thread, true)) return;
        try { SetForegroundWindow(window); }
        finally { AttachThreadInput(own, thread, false); }
    }
    public static int[] Battery() {
        byte[] data = new byte[32];
        uint status = CallNtPowerInformation(5, IntPtr.Zero, 0, data, (uint)data.Length);
        if (status != 0 || data[1] == 0) throw new InvalidOperationException("System battery state is unavailable.");
        return new int[] { data[0], data[3], BitConverter.ToInt32(data, 12), BitConverter.ToInt32(data, 16) };
    }
}
'@

$homePath = Join-Path $env:LOCALAPPDATA 'ClawToolbox'
$endpoint = Join-Path $homePath 'agent.json'
$session = (Get-Process -Id $PID).SessionId
$package = Get-AppxPackage -Name ClawToolbox.Library
if (-not $package) { throw 'The Claw Library MSIX is not installed.' }
$preferences = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalCache\ui.ini"
$utf8 = [Text.UTF8Encoding]::new($false)
$outputPath = [IO.Path]::GetFullPath($Output)
$null = [IO.Directory]::CreateDirectory($outputPath)
$samplesPath = Join-Path $outputPath 'samples.csv'
$cpuPath = Join-Path $outputPath 'cpu.csv'
$summaryPath = Join-Path $outputPath 'summary.csv'
foreach ($file in @($samplesPath, $cpuPath, $summaryPath)) {
    if (Test-Path -LiteralPath $file) { throw "A measurement already exists at $file." }
}

function Invoke-Agent([string]$Method, [object[]]$Arguments = @()) {
    $record = Get-Content -LiteralPath $endpoint -Raw | ConvertFrom-Json
    $client = [Net.Sockets.TcpClient]::new('127.0.0.1', [int]$record.port)
    try {
        $stream = $client.GetStream()
        $stream.ReadTimeout = 4000
        $writer = [IO.StreamWriter]::new($stream, $utf8)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($stream, $utf8)
        $writer.WriteLine((@{hello=$record.token; client='power-measurement'} | ConvertTo-Json -Compress))
        $hello = $reader.ReadLine() | ConvertFrom-Json
        if ($hello.hello -ne 'ok' -or $hello.pid -ne $record.pid) { throw 'Agent authentication failed.' }
        $writer.WriteLine((@{id=1; method=$Method; args=$Arguments} | ConvertTo-Json -Depth 8 -Compress))
        do { $reply = $reader.ReadLine() | ConvertFrom-Json } while ($reply -and $reply.id -ne 1)
        if ($reply.error) { throw $reply.error }
        return $reply.result
    } finally { $client.Dispose() }
}

function Get-Library {
    Get-Process ClawLibrary -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session | Select-Object -First 1
}

function Wait-For([string]$Description, [scriptblock]$Condition, [int]$Timeout = 30) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        [Windows.Forms.Application]::DoEvents()
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt $Timeout)
    throw "Timed out waiting for $Description."
}

function Close-Library {
    $library = Get-Library
    if (-not $library) { return }
    if ($library.Path -ne (Join-Path $package.InstallLocation 'ClawLibrary.exe')) {
        throw 'Refusing to stop a game library process from outside the installed MSIX.'
    }
    if ($library.CloseMainWindow() -and $library.WaitForExit(3500)) { return }
    # Closing with "keep running in tray" enabled leaves the frontend alive.
    # Stop only the validated frontend, never its hardware helpers.
    Write-Host 'Closing the game library process for the no-frontend baseline.'
    Stop-Process -InputObject $library
    if (-not $library.WaitForExit(10000)) { throw 'The game library did not stop.' }
}

function Open-Library {
    Start-Process 'claw-library://restore-library'
    Wait-For 'the game library window' {
        $window = Get-Library
        $window -and $window.MainWindowHandle -ne [IntPtr]::Zero
    }
}

function Get-ProcessSnapshot {
    $result = @{}
    foreach ($p in (Get-Process -Name 'Claw*','pythonw','dwm','PresentMon*' -ErrorAction SilentlyContinue)) {
        if ($null -eq $p.CPU) { continue }
        try {
            $key = "$($p.Id)-$($p.StartTime.Ticks)"
            $result[$key] = [pscustomobject]@{name=$p.ProcessName; id=$p.Id; cpu=$p.CPU}
        } catch { }
    }
    return $result
}

function Read-State {
    if ($useBatteryWmi) {
        $reading = Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus | Select-Object -First 1
        $battery = @([int]$reading.PowerOnline, [int]$reading.Discharging, $reading.RemainingCapacity, $reading.DischargeRate)
    } else {
        $battery = [ClawMeasurement]::Battery()
    }
    if ($battery[0] -ne 0 -or $battery[1] -ne 1 -or $battery[2] -le 0) {
        throw 'Keep AC disconnected and the device on battery power for the whole run.'
    }
    return ,$battery
}

function Measure-Phase([string]$Phase, [scriptblock]$Validate, [IntPtr]$Foreground = [IntPtr]::Zero) {
    Write-Host "$Phase settling for $SettleSeconds seconds"
    $settle = [Diagnostics.Stopwatch]::StartNew()
    while ($settle.Elapsed.TotalSeconds -lt $SettleSeconds) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 500
    }
    Write-Host "$Phase sampling for $Seconds seconds"
    $before = Get-ProcessSnapshot
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $samples = [Collections.Generic.List[object]]::new()
    $next = 0
    $focusRestores = 0
    do {
        [Windows.Forms.Application]::DoEvents()
        if ($Foreground -ne [IntPtr]::Zero -and [ClawMeasurement]::GetForegroundWindow() -ne $Foreground) {
            [ClawMeasurement]::Focus($Foreground)
            $focusRestores++
        }
        if ($timer.Elapsed.TotalSeconds -ge $next) {
            if (-not (& $Validate)) {
                $actual = Get-Process -Id ([ClawMeasurement]::ForegroundPid()) -ErrorAction SilentlyContinue
                $router = Get-Process 'ClawRouter*' -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session
                throw "$Phase lost its expected state. Foreground=$($actual.ProcessName)/$($actual.Id), library=$((Get-Library).Id), router=$($router.Id)."
            }
            $battery = Read-State
            $sample = [pscustomobject]@{
                Phase=$Phase; Seconds=[math]::Round($timer.Elapsed.TotalSeconds,3)
                Timestamp=[DateTimeOffset]::Now.ToString('o'); RemainingMilliWh=$battery[2]
                ReportedRateMilliW=$battery[3]; ForegroundPid=[ClawMeasurement]::ForegroundPid()
            }
            if ($OsdOnly -and ($next % 20 -eq 0 -or $next -ge $Seconds)) {
                Confirm-TestDisplay $Phase
            }
            $samples.Add($sample)
            $sample | Export-Csv -Path $samplesPath -NoTypeInformation -Append
            $next += 5
        }
        if ($samples[$samples.Count-1].Seconds -ge ($Seconds - 0.25)) { break }
        Start-Sleep -Milliseconds 200
    } while ($true)
    $after = Get-ProcessSnapshot
    $first = $samples[0]
    $last = $samples[$samples.Count-1]
    $span = $last.Seconds - $first.Seconds
    $drop = $first.RemainingMilliWh - $last.RemainingMilliWh
    $rows = foreach ($key in $after.Keys) {
        if (-not $before.ContainsKey($key)) { continue }
        $used = $after[$key].cpu - $before[$key].cpu
        [pscustomobject]@{Phase=$Phase; Process=$after[$key].name; PID=$after[$key].id
            CPUSeconds=[math]::Round($used,3); OneLogicalCpuPercent=[math]::Round(100*$used/$span,2)}
    }
    $rows | Sort-Object CPUSeconds -Descending | Export-Csv -Path $cpuPath -NoTypeInformation -Append
    $result = [pscustomobject]@{
        Phase=$Phase; Seconds=[math]::Round($span,2); StartMilliWh=$first.RemainingMilliWh; EndMilliWh=$last.RemainingMilliWh
        CapacityUpdates=@($samples.RemainingMilliWh | Sort-Object -Unique).Count - 1
        FocusRestores=$focusRestores
        AverageWatts=if ($drop -gt 0) { [math]::Round(3.6*$drop/$span,2) }
    }
    $result | Export-Csv -Path $summaryPath -NoTypeInformation -Append
    Write-Host ($result | Format-List | Out-String)
}

function Set-TestOsd([bool]$Enabled) {
    $state = Invoke-Agent 'save_osd' @(@{enabled=$Enabled})
    if ($state.settings.enabled -ne $Enabled) { throw 'The OSD did not change state.' }
}

function Confirm-TestDisplay([string]$Phase) {
    $brightness = Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness |
        Where-Object Active | Select-Object -First 1 -ExpandProperty CurrentBrightness
    $refresh = Get-CimInstance Win32_VideoController |
        Where-Object { $_.CurrentHorizontalResolution -and $_.CurrentRefreshRate -gt 1 } |
        Select-Object -First 1 -ExpandProperty CurrentRefreshRate
    [pscustomobject]@{
        Phase=$Phase; Timestamp=[DateTimeOffset]::Now.ToString('o')
        RefreshHz=$refresh; BrightnessPercent=$brightness
    } | Export-Csv -LiteralPath (Join-Path $outputPath 'display.csv') -Append -NoTypeInformation
    if ($null -eq $refresh -or [math]::Abs([int]$refresh - $DisplayHz) -gt 1 -or $brightness -ne $DisplayBrightness) {
        throw "Display expected $DisplayHz Hz and $DisplayBrightness% brightness, read $refresh Hz and $brightness%."
    }
}

function Get-PowerExecutor([int]$Revision, [int]$AgentId) {
    $name = "ClawPower-0.17.58-r$Revision.exe"
    $helpers = @(Get-CimInstance Win32_Process -Filter "Name='$name' AND SessionId=$session")
    $bridge = $helpers | Where-Object ParentProcessId -eq $AgentId | Select-Object -First 1
    if ($bridge) { return $helpers | Where-Object ParentProcessId -eq $bridge.ProcessId | Select-Object -First 1 }
}

function Restart-AgentWith([scriptblock]$Replace) {
    $active = try { Invoke-Agent 'agent_info' } catch { $null }
    if ($active) {
        Wait-For 'duplicate Agent launches to drain' {
            $instances = @(Get-CimInstance Win32_Process -Filter "Name='pythonw.exe' AND SessionId=$session" |
                Where-Object CommandLine -match '[\\/]claw_agent\.py"?(?:\s|$)')
            $instances.Count -eq 1 -and $instances[0].ProcessId -eq $active.pid
        } 45
    }
    $gate = [AgentBaselineGate]::new()
    try {
        Start-Sleep -Milliseconds 200
        if ($active) { $null = Invoke-Agent 'shutdown' }
        if (-not $gate.WaitForAcquisition(20000)) { throw 'The existing Agent did not release its lock.' }
        & $Replace
    } finally {
        $gate.Dispose()
    }
    try { Wait-For 'the waiting Agent to start' { try { [bool](Invoke-Agent 'agent_info') } catch { $false } } 4 }
    catch { Start-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName Agent }
    Wait-For 'the replacement Agent to answer' {
        try { $info = Invoke-Agent 'agent_info'; -not $active -or $info.pid -ne $active.pid } catch { $false }
    } 50
    return Invoke-Agent 'agent_info'
}

function Switch-PowerHelper([string]$ManifestText, [int]$Revision) {
    $info = Restart-AgentWith {
        $pending = $manifestPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::WriteAllText($pending, $ManifestText, $utf8)
            [IO.File]::Replace($pending, $manifestPath, [NullString]::Value)
        } finally { if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) } }
    }
    Wait-For "ClawPower r$Revision" { Get-PowerExecutor $Revision $info.pid } 50
    return $info
}

function Wait-OsdWindow {
    Wait-For 'the OSD window' {
        $window = Get-Process ClawPerformanceOverlay -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session | Select-Object -First 1
        $window -and $window.MainWindowHandle -ne [IntPtr]::Zero -and [ClawMeasurement]::IsWindowVisible($window.MainWindowHandle)
    }
}

function Measure-FrameProbe([string]$Phase) {
    if (-not $FrameProbe) { return }
    $exe = (Resolve-Path -LiteralPath $FrameProbe).Path
    $presentFile = Join-Path $outputPath "$Phase-frame-intervals.csv"
    $cover.Hide()
    $probe = Start-Process -FilePath $exe -ArgumentList ('"' + $presentFile + '"') -PassThru
    try {
        Wait-For 'the frame probe window' { $probe.Refresh(); $probe.MainWindowHandle -ne [IntPtr]::Zero }
        [ClawMeasurement]::Focus($probe.MainWindowHandle)
        Wait-For 'the frame probe to be the foreground target' { [ClawMeasurement]::ForegroundPid() -eq $probe.Id }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.Elapsed.TotalSeconds -lt 35) {
            [Windows.Forms.Application]::DoEvents()
            $state = Invoke-Agent 'get_monitor_state'
            $sample = $state.sample
            [pscustomobject]@{
                Phase=$Phase; Seconds=[math]::Round($watch.Elapsed.TotalSeconds,2)
                Foreground=[ClawMeasurement]::ForegroundPid(); Target=$state.target.pid; Probe=$probe.Id
                Fps=$sample.fps; Displayed=$sample.displayed; Fps01=$sample.fps01; GraphEnd=$sample.graphEnd
                GraphPoints=@($sample.framePoints).Count; Source=$state.frameSource
            } | Export-Csv -LiteralPath (Join-Path $outputPath 'frame-probe.csv') -NoTypeInformation -Append
            Start-Sleep -Seconds 1
        }
        $diagnostic = Join-Path $homePath 'data\overlay-render.json'
        if (Test-Path -LiteralPath $diagnostic) { Copy-Item -LiteralPath $diagnostic -Destination (Join-Path $outputPath "$Phase-game-overlay.json") }
    } finally {
        if (-not $probe.HasExited) {
            $null = $probe.CloseMainWindow()
            if (-not $probe.WaitForExit(5000)) { Stop-Process -InputObject $probe }
        }
        $probe.Dispose()
        $cover.Show()
        [ClawMeasurement]::Focus($cover.Handle)
    }
}

$useBatteryWmi = [bool](Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus -ErrorAction SilentlyContinue)
$null = Read-State
$shellState = Get-Content -LiteralPath (Join-Path $homePath 'shell\state.json') -Raw | ConvertFrom-Json
if ($shellState.mode -ne 'desktop' -or -not (Get-Process explorer -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session)) {
    throw 'Run the measurement in Windows desktop mode.'
}
if (Get-Process ClawSleepAnimation -ErrorAction SilentlyContinue) {
    throw 'Pause power animation through its supported controls before running this session-layer benchmark.'
}
$agent = Invoke-Agent agent_info
$initialLibrary = Get-Library
$initialForeground = [ClawMeasurement]::GetForegroundWindow()
$libraryForeground = $initialLibrary -and ([ClawMeasurement]::ForegroundPid() -eq $initialLibrary.Id)
$initialWindow = if ($initialLibrary) { $initialLibrary.MainWindowHandle } else { [IntPtr]::Zero }
$wasMinimized = $initialWindow -ne [IntPtr]::Zero -and [ClawMeasurement]::IsIconic($initialWindow)
$screensaverEnabled = [IO.File]::Exists($preferences) -and [IO.File]::ReadAllText($preferences) -match '(?im)^screensaver=True(?=\r?$)'
$watchPath = Join-Path $homePath 'shell\ClawShell.exe'
$watch = Get-CimInstance Win32_Process -Filter "Name='ClawShell.exe' AND SessionId=$session" |
    Where-Object { $_.ExecutablePath -eq $watchPath -and $_.CommandLine -match '\s+watch\s*$' } |
    Select-Object -First 1
@{
    version="$($package.Version)"
    scope=if ($OsdOnly) { "Optimized OSD on/off/on with game library closed at $DisplayHz Hz and $DisplayBrightness% brightness" }
        elseif ($OsdCandidate) { 'Original and optimized OSD with game library closed' }
        elseif ($GpuBatchHelper) { 'r30/r31 desktop OSD comparison with game library closed' } else { 'Session background and game library' }
    fixed='System services, third-party processes and any inaccessible orphan helpers remain in all phases.'
    initialProcesses=@(Get-Process -Name 'Claw*','PresentMon*' -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,SessionId)
    batterySource=if ($useBatteryWmi) { 'BatteryStatus WMI' } else { 'SystemBatteryState' }
    screensaverTemporarilyDisabled=$screensaverEnabled
    runStart=[DateTimeOffset]::Now.ToString('o'); settleSeconds=$SettleSeconds; requestedSeconds=$Seconds
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'run.json') -Encoding UTF8

$cover = [Windows.Forms.Form]::new()
$cover.FormBorderStyle = 'None'
$cover.StartPosition = 'Manual'
$cover.Bounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds
$cover.BackColor = [Drawing.Color]::FromArgb(38,38,38)
$cover.TopMost = $true
$cover.ShowInTaskbar = $false
$watchStopped = $false
$agentStopped = $false
$libraryChanged = $false
$agentGate = $null
$screensaverChanged = $false

if ($OsdOnly) {
    $initialOsd = (Invoke-Agent 'get_osd_state').settings.enabled
    $modifiedOsd = $false
    try {
        if (-not [ClawMeasurement]::SetThreadExecutionState(2147483651)) { throw 'Could not keep the display and device awake.' }
        Close-Library
        $cover.Show()
        [ClawMeasurement]::Focus($cover.Handle)
        Wait-For 'the cover window in the foreground' { [ClawMeasurement]::ForegroundPid() -eq $PID }
        if ($ApplyDisplay) {
            $display = Invoke-Agent 'get_display_state'
            if ($display.refresh -ne $DisplayHz) { $null = Invoke-Agent 'set_refresh_rate' @($DisplayHz) }
            if ($display.brightness -ne $DisplayBrightness) { $null = Invoke-Agent 'set_brightness' @($DisplayBrightness) }
            Start-Sleep -Seconds 3
        }
        Confirm-TestDisplay 'setup'
        $modifiedOsd = $true
        Set-TestOsd $true
        Wait-OsdWindow
        $valid = { $cover.Visible -and -not (Get-Library) -and (Get-Process -Id $agent.pid -ErrorAction SilentlyContinue) }
        Measure-Phase 'optimized-on' $valid
        Set-TestOsd $false
        Wait-For 'the disabled OSD to exit' { -not (Get-Process ClawPerformanceOverlay -ErrorAction SilentlyContinue) }
        Measure-Phase 'optimized-off' $valid
        Set-TestOsd $true
        Wait-OsdWindow
        Measure-Phase 'optimized-on-repeat' $valid
        if ($FrameProbe) {
            Confirm-TestDisplay 'probe-on'
            Measure-FrameProbe 'probe-on'
            Confirm-TestDisplay 'probe-on-end'
            Set-TestOsd $false
            Wait-For 'the disabled OSD to exit' { -not (Get-Process ClawPerformanceOverlay -ErrorAction SilentlyContinue) }
            Confirm-TestDisplay 'probe-off'
            Measure-FrameProbe 'probe-off'
            Confirm-TestDisplay 'probe-off-end'
        }
    } finally {
        try { if ($modifiedOsd) { Set-TestOsd $initialOsd } }
        finally {
            $cover.Dispose()
            $null = [ClawMeasurement]::SetThreadExecutionState(2147483648)
            if ($initialLibrary) {
                Open-Library
                if ($wasMinimized) { $null = [ClawMeasurement]::ShowWindowAsync((Get-Library).MainWindowHandle, 6) }
            }
            if (-not $libraryForeground) { [ClawMeasurement]::Focus($initialForeground) }
            Write-Host "Results: $outputPath"
        }
    }
    return
}

if ($OsdCandidate) {
    $candidate = (Resolve-Path -LiteralPath $OsdCandidate).Path
    $current = Get-Content -LiteralPath (Join-Path $homePath 'current.json') -Raw | ConvertFrom-Json
    $app = Split-Path -Parent $current.agent
    $backup = Join-Path $outputPath 'original'
    $files = [ordered]@{'claw_power.py'='claw_power.py'; 'claw_overlay.py'='claw_overlay.py'; 'ClawPerformanceOverlay.exe'='bin\ClawPerformanceOverlay.exe'}
    foreach ($file in $files.Keys) {
        if (-not (Test-Path -LiteralPath (Join-Path $candidate $file) -PathType Leaf)) { throw "Candidate is missing $file." }
        if (-not (Test-Path -LiteralPath (Join-Path $app $files[$file]) -PathType Leaf)) { throw "Active Agent is missing $file." }
    }
    $null = [IO.Directory]::CreateDirectory($backup)
    foreach ($file in $files.Keys) { Copy-Item -LiteralPath (Join-Path $app $files[$file]) -Destination (Join-Path $backup $file) }
    $initialOsd = (Invoke-Agent 'get_osd_state').settings.enabled
    $candidateStarted = $false
    $tested = $false
    try {
        if (-not [ClawMeasurement]::SetThreadExecutionState(2147483651)) { throw 'Could not keep the display and device awake.' }
        Close-Library
        $cover.Show()
        [ClawMeasurement]::Focus($cover.Handle)
        Wait-For 'the cover window in the foreground' { [ClawMeasurement]::ForegroundPid() -eq $PID }
        Set-TestOsd $true
        Wait-OsdWindow
        $valid = { $cover.Visible -and -not (Get-Library) -and (Get-Process -Id $agent.pid -ErrorAction SilentlyContinue) }
        Measure-Phase 'original-on' $valid
        Measure-FrameProbe 'original-on'
        $candidateStarted = $true
        $agent = Restart-AgentWith {
            foreach ($file in $files.Keys) { Copy-Item -LiteralPath (Join-Path $candidate $file) -Destination (Join-Path $app $files[$file]) -Force }
        }
        Wait-OsdWindow
        Measure-Phase 'optimized-on' $valid
        $diagnostic = Join-Path $homePath 'data\overlay-render.json'
        if (Test-Path -LiteralPath $diagnostic) { Copy-Item -LiteralPath $diagnostic -Destination (Join-Path $outputPath 'optimized-desktop-overlay.json') }
        Set-TestOsd $false
        Wait-For 'the disabled OSD to exit' { -not (Get-Process ClawPerformanceOverlay -ErrorAction SilentlyContinue) }
        Measure-Phase 'optimized-off' $valid
        Set-TestOsd $true
        Wait-OsdWindow
        Measure-Phase 'optimized-on-repeat' $valid
        Measure-FrameProbe 'optimized-on'
        $tested = $true
    } finally {
        try {
            if ($candidateStarted -and -not $tested) {
                $null = Restart-AgentWith {
                    foreach ($file in $files.Keys) { Copy-Item -LiteralPath (Join-Path $backup $file) -Destination (Join-Path $app $files[$file]) -Force }
                }
            }
        } finally {
            try { Set-TestOsd $initialOsd }
            finally {
                $cover.Dispose()
                $null = [ClawMeasurement]::SetThreadExecutionState(2147483648)
                if ($initialLibrary) {
                    Open-Library
                    if ($wasMinimized) { $null = [ClawMeasurement]::ShowWindowAsync((Get-Library).MainWindowHandle, 6) }
                }
                if (-not $libraryForeground) { [ClawMeasurement]::Focus($initialForeground) }
                Write-Host "Results: $outputPath"
            }
        }
    }
    return
}

if ($GpuBatchHelper) {
    $candidate = (Resolve-Path -LiteralPath $GpuBatchHelper).Path
    $agentApp = Join-Path $homePath 'app'
    $manifestPath = Join-Path $agentApp 'plugin.json'
    $originalManifest = [IO.File]::ReadAllText($manifestPath)
    $manifest = $originalManifest | ConvertFrom-Json
    if ($manifest.version -ne '0.17.58' -or $manifest.powerHelperRevision -ne 30 -or -not (Get-PowerExecutor 30 $agent.pid)) {
        throw 'This comparison requires the running ClawPower 0.17.58 r30 baseline.'
    }
    $candidatePath = Join-Path $agentApp 'bin\ClawPower-0.17.58-r31.exe'
    if ((Test-Path -LiteralPath $candidatePath) -and (Get-FileHash -LiteralPath $candidatePath).Hash -ne (Get-FileHash -LiteralPath $candidate).Hash) {
        throw 'A different r31 helper is already installed.'
    }
    if (-not (Test-Path -LiteralPath $candidatePath)) { Copy-Item -LiteralPath $candidate -Destination $candidatePath }
    [IO.File]::WriteAllText((Join-Path $outputPath 'original-plugin.json'), $originalManifest, $utf8)
    $manifest.powerHelperRevision = 31
    $initialOsd = (Invoke-Agent 'get_osd_state').settings.enabled
    $tested = $false
    try {
        if (-not [ClawMeasurement]::SetThreadExecutionState(2147483651)) { throw 'Could not keep the display and device awake.' }
        Close-Library
        $cover.Show()
        [ClawMeasurement]::Focus($cover.Handle)
        Wait-For 'the cover window in the foreground' { [ClawMeasurement]::ForegroundPid() -eq $PID }
        Set-TestOsd $true
        $executor = Get-PowerExecutor 30 $agent.pid
        Measure-Phase 'r30-on' { $cover.Visible -and -not (Get-Library) -and (Get-Process -Id $executor.ProcessId -ErrorAction SilentlyContinue) }

        $agent = Switch-PowerHelper ($manifest | ConvertTo-Json -Depth 20) 31
        $executor = Get-PowerExecutor 31 $agent.pid
        $valid = { $cover.Visible -and -not (Get-Library) -and (Get-Process -Id $executor.ProcessId -ErrorAction SilentlyContinue) }
        Wait-For 'a GPU utilization reading from r31' {
            $state = Invoke-Agent 'get_osd_state'
            $state.settings.enabled -and $null -ne $state.readings.gpuUsage -and $state.sources.gpuUsageSource -eq 'busiestPhysicalEngine'
        } 30
        Measure-Phase 'r31-on' $valid
        Set-TestOsd $false
        Measure-Phase 'r31-off' $valid
        Set-TestOsd $true
        Measure-Phase 'r31-on-repeat' $valid
        $tested = $true
    } finally {
        try {
            if (-not $tested -and ([IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json).powerHelperRevision -eq 31) {
                $null = Switch-PowerHelper $originalManifest 30
            }
        } finally {
            try { Set-TestOsd $initialOsd }
            finally {
                $cover.Dispose()
                $null = [ClawMeasurement]::SetThreadExecutionState(2147483648)
                if ($initialLibrary) {
                    Open-Library
                    if ($wasMinimized) { $null = [ClawMeasurement]::ShowWindowAsync((Get-Library).MainWindowHandle, 6) }
                }
                if (-not $libraryForeground) { [ClawMeasurement]::Focus($initialForeground) }
                Write-Host "Results: $outputPath"
            }
        }
    }
    return
}

try {
    if (-not [ClawMeasurement]::SetThreadExecutionState(2147483651)) { throw 'Could not keep the display and device awake.' }
    $libraryChanged = $true
    Close-Library
    $widget = Get-Process ClawWidget -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session
    foreach ($process in $widget) {
        $widgetPackage = Get-AppxPackage -Name ClawToolbox.GameBarWidget
        if (-not $widgetPackage -or $process.Path -ne (Join-Path $widgetPackage.InstallLocation 'ClawWidget.exe')) {
            throw 'Refusing to stop a widget process from outside the installed MSIX.'
        }
        Stop-Process -InputObject $process
        if (-not $process.WaitForExit(10000)) { throw 'The Game Bar widget did not exit.' }
    }
    $cover.Show()
    [ClawMeasurement]::Focus($cover.Handle)
    Wait-For 'the cover window in the foreground' { [ClawMeasurement]::ForegroundPid() -eq $PID }
    if ($watch) {
        $watchProcess = Get-Process -Id $watch.ProcessId -ErrorAction Stop
        Stop-Process -InputObject $watchProcess
        $watchStopped = $true
        if (-not $watchProcess.WaitForExit(10000)) { throw 'The desktop-mode watcher did not exit.' }
    }
    # The scheduled task permits parallel launches. Let any pending instances
    # time out before joining the mutex queue for the baseline.
    Wait-For 'duplicate Agent launches to drain' {
        $instances = @(Get-CimInstance Win32_Process -Filter "Name='pythonw.exe' AND SessionId=$session" |
            Where-Object CommandLine -match '[\\/]claw_agent\.py"?(?:\s|$)')
        $instances.Count -eq 1 -and $instances[0].ProcessId -eq $agent.pid
    } 45
    $agentGate = [AgentBaselineGate]::new()
    Start-Sleep -Milliseconds 200
    $agentStopped = $true
    $null = Invoke-Agent shutdown
    if (-not $agentGate.WaitForAcquisition(20000)) { throw 'Could not reserve the Agent single-instance mutex for the baseline.' }
    Wait-For 'the Agent and its normal helpers to exit' {
        -not (Get-Process -Id $agent.pid -ErrorAction SilentlyContinue) -and
        -not (Get-Process 'ClawRouter*','ClawInput*','ClawPerformanceOverlay' -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session)
    } 50
    Measure-Phase 'A' {
        -not (Get-Library) -and -not (Get-Process -Id $agent.pid -ErrorAction SilentlyContinue) -and
        -not (Get-Process 'ClawRouter*','ClawWidget' -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session) -and
        $cover.Visible
    }

    $agentGate.Dispose()
    $agentGate = $null
    try { Wait-For 'the waiting Agent to start' { try { [bool](Invoke-Agent agent_info) } catch { $false } } 3 }
    catch { Start-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName Agent }
    Wait-For 'the Agent to answer' { try { [bool](Invoke-Agent agent_info) } catch { $false } } 50
    $activeAgent = Invoke-Agent agent_info
    $agentStopped = $false
    if ($watchStopped) { Start-Process -FilePath $watchPath -ArgumentList 'watch' -WindowStyle Hidden; $watchStopped = $false }
    [ClawMeasurement]::Focus($cover.Handle)
    Measure-Phase 'B' { -not (Get-Library) -and $cover.Visible -and (Get-Process -Id $activeAgent.pid -ErrorAction SilentlyContinue) }

    $cover.Hide()
    if ($screensaverEnabled) {
        $prefs = [IO.File]::ReadAllText($preferences)
        [IO.File]::WriteAllText($preferences, ($prefs -replace '(?im)^screensaver=True(?=\r?$)', 'screensaver=False'), $utf8)
        $screensaverChanged = $true
    }
    Open-Library
    $library = Get-Library
    [ClawMeasurement]::Focus($library.MainWindowHandle)
    Wait-For 'the game library in the foreground' { [ClawMeasurement]::ForegroundPid() -eq $library.Id }
    $libraryWindow = [ClawMeasurement]::GetForegroundWindow()
    Measure-Phase 'C' {
        [ClawMeasurement]::ForegroundPid() -eq $library.Id -and (Get-Process -Id $activeAgent.pid -ErrorAction SilentlyContinue)
    } $libraryWindow

    $cover.Show()
    [ClawMeasurement]::Focus($cover.Handle)
    Wait-For 'the cover window in the foreground' { [ClawMeasurement]::ForegroundPid() -eq $PID }
    Measure-Phase 'D-covered' {
        $cover.Visible -and [ClawMeasurement]::ForegroundPid() -ne $library.Id -and
        (Get-Process -Id $activeAgent.pid -ErrorAction SilentlyContinue) -and
        [ClawMeasurement]::IsWindowVisible($libraryWindow) -and -not [ClawMeasurement]::IsIconic($libraryWindow)
    }

    $null = [ClawMeasurement]::ShowWindowAsync($libraryWindow, 6)
    [ClawMeasurement]::Focus($cover.Handle)
    Wait-For 'the game library to minimize' { [ClawMeasurement]::IsIconic($libraryWindow) }
    Measure-Phase 'D-minimized' {
        $cover.Visible -and [ClawMeasurement]::IsIconic($libraryWindow) -and
        (Get-Process -Id $activeAgent.pid -ErrorAction SilentlyContinue)
    }
} finally {
    if ($agentGate) { $agentGate.Dispose() }
    $cover.Dispose()
    $null = [ClawMeasurement]::SetThreadExecutionState(2147483648)
    if ($agentStopped) {
        try { Start-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName Agent }
        catch { Write-Warning "Could not restore the Agent: $_" }
    }
    if ($watchStopped) {
        try { Start-Process -FilePath $watchPath -ArgumentList 'watch' -WindowStyle Hidden }
        catch { Write-Warning "Could not restore the desktop watcher: $_" }
    }
    if ($libraryChanged) {
        try {
            if ($screensaverChanged) {
                try { Close-Library }
                finally {
                    $prefs = [IO.File]::ReadAllText($preferences)
                    [IO.File]::WriteAllText($preferences, ($prefs -replace '(?im)^screensaver=False(?=\r?$)', 'screensaver=True'), $utf8)
                }
            }
            if ($initialLibrary) {
                Open-Library
                if ($wasMinimized) { $null = [ClawMeasurement]::ShowWindowAsync((Get-Library).MainWindowHandle, 6) }
            } else { Close-Library }
            if (-not $libraryForeground) { [ClawMeasurement]::Focus($initialForeground) }
        } catch { Write-Warning "Could not restore the game library window: $_" }
    }
    Write-Host "Results: $outputPath"
}
