param([string]$Output=(Join-Path $PSScriptRoot '..\artifacts\sleep-animation\sleep-diagnostics-20261026'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $Output -Force|Out-Null
& powercfg /requests | Set-Content -LiteralPath (Join-Path $Output 'requests.txt') -Encoding UTF8
& powercfg /waketimers | Set-Content -LiteralPath (Join-Path $Output 'wake-timers.txt') -Encoding UTF8
& powercfg /devicequery wake_armed | Set-Content -LiteralPath (Join-Path $Output 'wake-armed.txt') -Encoding UTF8
& powercfg /sleepstudy /duration 28 /xml /output (Join-Path $Output 'sleepstudy.xml') *> (Join-Path $Output 'sleepstudy-generation.txt')
$taskSince=(Get-Date).AddDays(-21)
$taskEvents=Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power';Id=42,107,506,507;StartTime=$taskSince} -MaxEvents 700
$taskEvents|ForEach-Object {
    $taskXml=[xml]$_.ToXml();$taskFields=@{}
    foreach($taskData in $taskXml.Event.EventData.Data){$taskFields[$taskData.Name]=$taskData.'#text'}
    [pscustomobject]@{at=$_.TimeCreated.ToUniversalTime().ToString('o');id=$_.Id;message=$_.Message;data=$taskFields}
}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Output 'power-events.json') -Encoding UTF8
Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-USB-USBHUB3';Id=196,205;StartTime=$taskSince} -MaxEvents 250 -ErrorAction SilentlyContinue|
    ForEach-Object {[pscustomobject]@{at=$_.TimeCreated.ToUniversalTime().ToString('o');id=$_.Id;message=$_.Message;xml=$_.ToXml()}}|
    ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $Output 'usb-events.json') -Encoding UTF8
@{at=[DateTime]::UtcNow.ToString('o');readOnly=$true;output=[IO.Path]::GetFullPath($Output)}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Output 'complete.json') -Encoding UTF8
