param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$taskResult=@{stopped=@();services=@()}
try {
    $taskRoots=@((Get-AppxPackage Microsoft.XboxGamingOverlay).InstallLocation,(Get-AppxPackage ClawToolbox.GameBarWidget).InstallLocation) | Where-Object { $_ }
    foreach ($taskProcess in @(Get-Process GameBar,GameBarFTServer,ClawWidget -ErrorAction SilentlyContinue)) {
        $taskPath=$taskProcess.Path
        if (-not ($taskRoots | Where-Object { $taskPath.StartsWith($_+'\',[StringComparison]::OrdinalIgnoreCase) })) { throw 'Unexpected overlay process path' }
        $taskResult.stopped+=@{pid=$taskProcess.Id;name=$taskProcess.ProcessName}
        $taskProcess.Kill();$taskProcess.WaitForExit(5000) | Out-Null
    }
    foreach ($taskName in @('GameInputSvc','GameInputRedistService')) {
        Restart-Service -Name $taskName -Force
        (Get-Service $taskName).WaitForStatus('Running',[TimeSpan]::FromSeconds(10))
        $taskResult.services+=@{name=$taskName;status=(Get-Service $taskName).Status.ToString()}
    }
    $taskResult.ok=$true
} catch { $taskResult.ok=$false;$taskResult.error=$_.Exception.Message }
$taskResult | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8
