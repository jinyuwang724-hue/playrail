# Run through Invoke-OutsideApp.ps1 so Windows sees the actual HKCU Run entry.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$startScript = Join-Path $env:USERPROFILE 'homebrew\services\Start-Decky.ps1'
if (-not (Test-Path -LiteralPath $startScript -PathType Leaf)) {
    throw "Decky startup script is missing: $startScript"
}
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$command = '"' + $powershell + '" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $startScript + '"'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$previous = (Get-ItemProperty -LiteralPath $runKey -Name DeckyLoader -ErrorAction SilentlyContinue).DeckyLoader
$reportDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\decky-autostart-fix-20261018'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$beforePath = Join-Path $reportDirectory 'system-autostart-before.txt'
if (-not (Test-Path -LiteralPath $beforePath)) {
    [IO.File]::WriteAllText($beforePath, [string]$previous)
}
Set-ItemProperty -LiteralPath $runKey -Name DeckyLoader -Value $command
$applied = (Get-ItemProperty -LiteralPath $runKey -Name DeckyLoader).DeckyLoader
$systemView = @(Get-CimInstance Win32_StartupCommand -Filter "Name='DeckyLoader'")
$matching = @($systemView | Where-Object { $_.Command -eq $command })
if ($applied -ne $command -or $matching.Count -eq 0) {
    throw 'Windows startup provider does not see the updated Decky command.'
}
$startupApproval = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name DeckyLoader -ErrorAction SilentlyContinue).DeckyLoader
$approvalHex = if ($null -eq $startupApproval) { 'No disable record' } else { [BitConverter]::ToString($startupApproval) }
$report = [pscustomobject]@{
    checkedAt = (Get-Date -Format o)
    previousCommand = $previous
    appliedCommand = $applied
    windowsStartupCommand = $matching[0].Command
    location = $matching[0].Location
    startupApproval = $approvalHex
    windowsVerified = $true
}
[IO.File]::WriteAllText((Join-Path $reportDirectory 'system-autostart-verified.json'), ($report | ConvertTo-Json -Depth 4) + [Environment]::NewLine)
Write-Output 'Windows startup provider verified the real DeckyLoader entry.'
Write-Output $matching[0].Command
Write-Output ('StartupApproved: ' + $approvalHex)
