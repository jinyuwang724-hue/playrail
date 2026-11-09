param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$taskCli='C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe'
$taskPrincipal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$taskOutput=@{elevated=$taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)}
try {
    $taskOutput.state=(& $taskCli --cloak-state 2>&1 | Out-String).Trim()
    $taskOutput.stateExit=$LASTEXITCODE
    $taskOutput.devices=(& $taskCli --dev-list 2>&1 | Out-String).Trim()
    $taskOutput.devicesExit=$LASTEXITCODE
} catch { $taskOutput.error=$_.Exception.Message }
$taskOutput | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $OutputPath -Encoding utf8
