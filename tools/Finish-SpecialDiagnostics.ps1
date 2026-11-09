param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$taskResult=@{stoppedHelpers=@()}
try {
    foreach ($taskProcess in @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'")) {
        if ($taskProcess.CommandLine -like '*tools\Restart-GameBarInput.ps1*' -and $taskProcess.ProcessId -ne $PID) {
            Stop-Process -Id $taskProcess.ProcessId -Force
            $taskResult.stoppedHelpers+=$taskProcess.ProcessId
        }
    }
    $taskCli='C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe'
    $taskResult.hidhide=(& $taskCli --cloak-state | Out-String).Trim()
    $taskResult.hiddenDevices=(& $taskCli --dev-list | Out-String).Trim()
    $taskResult.services=@(Get-CimInstance Win32_Service -Filter "Name='GameInputSvc' OR Name='GameInputRedistService'" | Select-Object Name,State,ProcessId)
    $taskResult.ok=$true
} catch { $taskResult.ok=$false;$taskResult.error=$_.Exception.Message }
$taskResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8
