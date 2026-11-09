param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$taskResult=@{}
try {
    $taskService=Get-CimInstance Win32_Service -Filter "Name='GameInputRedistService'"
    $taskResult.before=$taskService.State
    if ($taskService.State -eq 'Stop Pending' -and $taskService.ProcessId -gt 0) {
        $taskProcess=Get-Process -Id $taskService.ProcessId
        if ($taskProcess.Path -ne 'C:\Program Files\Microsoft GameInput\x64\GameInputRedistService.exe') { throw 'Unexpected GameInput service path' }
        $taskResult.recoveredPid=$taskProcess.Id
        $taskProcess.Kill();$taskProcess.WaitForExit(5000) | Out-Null
    }
    Start-Service GameInputRedistService
    (Get-Service GameInputRedistService).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
    $taskResult.after=(Get-Service GameInputRedistService).Status.ToString()
    $taskResult.ok=$true
} catch { $taskResult.ok=$false;$taskResult.error=$_.Exception.Message }
$taskResult | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
