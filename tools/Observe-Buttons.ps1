param([int]$Seconds = 90)
$ErrorActionPreference = 'Stop'
$taskLog = Join-Path $PSScriptRoot '..\artifacts\button-observation.jsonl'
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $taskLog)) | Out-Null
$taskWatcher = New-Object System.Management.ManagementEventWatcher
$taskWatcher.Scope = New-Object System.Management.ManagementScope('\\.\root\WMI')
$taskWatcher.Query = New-Object System.Management.WqlEventQuery('SELECT * FROM MSI_Event')
$taskWatcher.Options.Timeout = [TimeSpan]::FromSeconds(1)
$taskWatcher.Start()
Write-Output 'READY: MSI front-button observation; no actions or settings are changed.'
$taskEnd = [DateTime]::UtcNow.AddSeconds($Seconds)
try {
    while ([DateTime]::UtcNow -lt $taskEnd) {
        try {
            $taskEvent = $taskWatcher.WaitForNextEvent()
            $taskRaw = [Convert]::ToInt32($taskEvent.Properties['MSIEvt'].Value)
            $taskCode = $taskRaw -band 255
            if ($taskCode -eq 41 -or $taskCode -eq 88) {
                $taskRecord = @{timestamp=[DateTime]::UtcNow.ToString('o'); source='MSI_Event'; rawCode=$taskRaw; code=$taskCode; button= $(if ($taskCode -eq 41) {'MSI'} else {'QuickSettings'})} | ConvertTo-Json -Compress
                Add-Content -LiteralPath $taskLog -Value $taskRecord -Encoding UTF8
                Write-Output $taskRecord
            }
        } catch [System.Management.ManagementException] {
            if ($_.Exception.ErrorCode -ne [System.Management.ManagementStatus]::Timedout) { throw }
        }
    }
} finally { $taskWatcher.Stop(); $taskWatcher.Dispose() }
