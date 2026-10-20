# Standalone Decky startup. Installed at %USERPROFILE%\homebrew\services.
[CmdletBinding()]
param(
    [ValidateSet('PluginLoader.exe', 'PluginLoader_noconsole.exe')]
    [string]$Loader = 'PluginLoader_noconsole.exe',
    [ValidateRange(5, 60)]
    [int]$StartupTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$servicesDirectory = Join-Path $env:USERPROFILE 'homebrew\services'
$expectedExecutables = @(
    (Join-Path $servicesDirectory 'PluginLoader.exe'),
    (Join-Path $servicesDirectory 'PluginLoader_noconsole.exe')
)
$startupLog = Join-Path $servicesDirectory 'Decky-startup.log'
$startupMutex = New-Object Threading.Mutex($false, 'Local\ClawToolbox.Decky.Start')
$ownsMutex = $false
$startedProcess = $null

function Test-DeckyListener {
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 1337 -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        $listenerProcess = Get-Process -Id $listener.OwningProcess -ErrorAction Stop
        if ($listenerProcess.Path -notin $expectedExecutables) {
            throw "Port 1337 belongs to another executable (PID $($listenerProcess.Id))."
        }
    }
    return ($listeners.Count -gt 0)
}

function Test-DeckyResponse {
    $response = $null
    try {
        $request = [Net.HttpWebRequest]::Create('http://127.0.0.1:1337/frontend/index.js')
        $request.Proxy = $null
        $request.Timeout = 1000
        $request.ReadWriteTimeout = 1000
        $request.KeepAlive = $false
        $response = $request.GetResponse()
        return ([int]$response.StatusCode -eq 200)
    } catch {
        return $false
    } finally {
        if ($null -ne $response) { $response.Dispose() }
    }
}

try {
    try { $ownsMutex = $startupMutex.WaitOne(30000) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another Decky startup is still in progress.' }

    $listening = Test-DeckyListener
    if ($listening -and (Test-DeckyResponse)) { exit 0 }

    $existingProcesses = @(Get-Process -Name PluginLoader,PluginLoader_noconsole -ErrorAction SilentlyContinue)
    foreach ($existingProcess in $existingProcesses) {
        if ($existingProcess.Path -notin $expectedExecutables) {
            throw "A different Decky executable is already running (PID $($existingProcess.Id))."
        }
    }

    if (-not $listening -and $existingProcesses.Count -eq 0) {
        $executable = Join-Path $servicesDirectory $Loader
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Decky executable is missing: $executable"
        }
        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.FileName = $executable
        $startInfo.WorkingDirectory = $servicesDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $pythonDirectory = Join-Path $servicesDirectory 'python-3.11.9'
        if (Test-Path -LiteralPath (Join-Path $pythonDirectory 'python.exe') -PathType Leaf) {
            $startInfo.EnvironmentVariables['PATH'] = $pythonDirectory + ';' + $env:PATH
        }
        $startedProcess = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $startedProcess) { throw 'Windows did not create a Decky process.' }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        if ((Test-DeckyListener) -and (Test-DeckyResponse)) {
            if ($null -ne $startedProcess) {
                Add-Content -LiteralPath $startupLog -Value ((Get-Date -Format o) + " Ready; launcher PID $($startedProcess.Id).")
            }
            exit 0
        }
        if ($null -ne $startedProcess -and $startedProcess.HasExited) {
            throw "Decky exited during startup ($($startedProcess.ExitCode))."
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Decky did not respond before the timeout. No duplicate process was started.'
} catch {
    try {
        Add-Content -LiteralPath $startupLog -Value ((Get-Date -Format o) + ' ERROR: ' + $_.Exception.Message)
    } catch { }
    exit 1
} finally {
    if ($null -ne $startedProcess) { $startedProcess.Dispose() }
    if ($ownsMutex) { $startupMutex.ReleaseMutex() }
    $startupMutex.Dispose()
}
