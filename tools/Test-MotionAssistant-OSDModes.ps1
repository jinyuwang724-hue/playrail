$ErrorActionPreference = 'Stop'
$taskOriginalMode = (Invoke-RestMethod 'http://127.0.0.1:47991/mode/current' -TimeoutSec 4).currentMode
if ($taskOriginalMode -notin @('Desktop','Gaming')) { throw 'Cannot identify the current Playhub mode.' }
try {
    foreach ($taskMode in @('Gaming','Desktop')) {
        $taskResponse = Invoke-RestMethod ('http://127.0.0.1:47991/mode/' + $taskMode.ToLowerInvariant()) -Method Post -TimeoutSec 45
        Start-Sleep -Seconds 3
        $taskSnapshot = & (Join-Path $PSScriptRoot 'Get-MotionAssistant-OSD.ps1') | ConvertFrom-Json
        $taskSnapshot | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $PSScriptRoot ('..\diagnostics\motion-osd-wuwa-' + $taskMode.ToLowerInvariant() + '-20261006.json')) -Encoding UTF8
        if ($taskSnapshot.Playhub.currentMode -ne $taskMode) { throw ('Mode switch did not reach ' + $taskMode) }
        $taskGame = @($taskSnapshot.Applications | Where-Object { $_.Name -match 'Client-Win64-Shipping\.exe$' })
        $taskOverlay = @($taskSnapshot.OsdSlots | Where-Object { $_.Owner -eq 'MotionAssistant' -and $_.Text -match '<FR>' })
        if ($taskGame.Count -ne 1 -or $taskGame[0].Framerate -le 0 -or $taskOverlay.Count -ne 1 -or -not $taskSnapshot.OsdVisible) { throw ('OSD data incomplete in ' + $taskMode) }
        Write-Output ($taskMode + ': game=' + $taskGame[0].ProcessId + ', fps=' + [math]::Round($taskGame[0].Framerate,1) + ', MotionAssistant OSD present, rendered OSD frame=' + $taskGame[0].OsdFrame)
    }
} finally {
    $taskCurrent = (Invoke-RestMethod 'http://127.0.0.1:47991/mode/current' -TimeoutSec 4).currentMode
    if ($taskCurrent -ne $taskOriginalMode) { Invoke-RestMethod ('http://127.0.0.1:47991/mode/' + $taskOriginalMode.ToLowerInvariant()) -Method Post -TimeoutSec 45 | Out-Null }
}
