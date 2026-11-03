param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$taskInstance='USB\VID_0DB0&PID_1901&MI_00\6&2B02AE9F&1&0000'
$taskResult=@{instance=$taskInstance}
try {
    $taskDevice=Get-PnpDevice -InstanceId $taskInstance -ErrorAction Stop
    $taskResult.before=$taskDevice.Status
    $taskLog=[IO.Path]::ChangeExtension($OutputPath,'.log')
    $taskProcess=Start-Process pnputil.exe -ArgumentList @('/restart-device',('"'+$taskInstance+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $taskLog
    if (-not $taskProcess.WaitForExit(20000)) { throw 'Controller reconnect did not finish within 20 seconds' }
    $taskResult.exit=$taskProcess.ExitCode
    $taskResult.after=(Get-PnpDevice -InstanceId $taskInstance).Status
    $taskResult.ok=$taskProcess.ExitCode -eq 0
    if ((Get-Service GameInputRedistService).Status -eq 'Stopped') { Start-Service GameInputRedistService }
    $taskResult.gameInput=(Get-Service GameInputRedistService).Status.ToString()
} catch { $taskResult.ok=$false;$taskResult.error=$_.Exception.Message }
$taskResult | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
