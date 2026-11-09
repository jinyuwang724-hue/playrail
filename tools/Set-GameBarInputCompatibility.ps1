param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$taskKey = 'HKLM:\SOFTWARE\Microsoft\GameInputRedist\Exe\GameBar.exe'
$taskBackup = Join-Path $env:LOCALAPPDATA 'ClawToolbox\repair-backups\gamebar-20261018\background-input.json'
$taskResult = Join-Path $env:LOCALAPPDATA 'ClawToolbox\repair-backups\gamebar-20261018\background-input-result.json'
try {
    if ($Restore) {
        $taskBefore = Get-Content -Raw -LiteralPath $taskBackup | ConvertFrom-Json
        if ($taskBefore.hadValue) {
            New-ItemProperty -LiteralPath $taskKey -Name BackgroundInput -PropertyType $taskBefore.kind -Value $taskBefore.value -Force | Out-Null
        } elseif (Test-Path -LiteralPath $taskKey) {
            Remove-ItemProperty -LiteralPath $taskKey -Name BackgroundInput -ErrorAction SilentlyContinue
            $taskOpened = Get-Item -LiteralPath $taskKey
            if (-not $taskBefore.hadKey -and $taskOpened.ValueCount -eq 0 -and $taskOpened.SubKeyCount -eq 0) { Remove-Item -LiteralPath $taskKey }
        }
    } else {
        if (-not (Test-Path -LiteralPath $taskBackup)) {
            $taskBefore = @{hadKey=(Test-Path -LiteralPath $taskKey);hadValue=$false}
            if ($taskBefore.hadKey) {
                $taskOpened = Get-Item -LiteralPath $taskKey
                if ($taskOpened.GetValueNames() -contains 'BackgroundInput') {
                    $taskBefore.hadValue=$true
                    $taskBefore.value=$taskOpened.GetValue('BackgroundInput')
                    $taskBefore.kind=$taskOpened.GetValueKind('BackgroundInput').ToString()
                }
            }
            $taskBefore | ConvertTo-Json | Set-Content -LiteralPath $taskBackup -Encoding UTF8
        }
        New-Item -Path $taskKey -Force | Out-Null
        New-ItemProperty -LiteralPath $taskKey -Name BackgroundInput -PropertyType DWord -Value 1 -Force | Out-Null
    }
    @{ok=$true;restored=[bool]$Restore;value=(Get-ItemProperty -LiteralPath $taskKey -Name BackgroundInput -ErrorAction SilentlyContinue).BackgroundInput} | ConvertTo-Json | Set-Content -LiteralPath $taskResult -Encoding UTF8
} catch {
    @{ok=$false;error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath $taskResult -Encoding UTF8
    throw
}
