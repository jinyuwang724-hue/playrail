$ErrorActionPreference = 'Stop'
$taskTargets = @()
foreach ($taskRoot in @('C:\Software\steamapps\common','D:\SteamLibrary\steamapps\common')) {
    if (-not (Test-Path -LiteralPath $taskRoot)) { continue }
    $taskFiles = & rg --files $taskRoot -g '*.exe'
    if ($LASTEXITCODE -ne 0) { throw ('Could not read game library: ' + $taskRoot) }
    foreach ($taskFile in $taskFiles) {
        $taskRelative = $taskFile.Substring($taskRoot.Length + 1)
        if ($taskRelative -match '(?i)(Steamworks Shared|Steam Controller Configs|Gal\s*Master|U-ena|Lossless Scaling)[\\/]') { continue }
        if ($taskRelative -match '(?i)[\\/](?:_?CommonRedist|Redist|Extras|Engine|EasyAntiCheat|AntiCheatExpert|CrashReporter|CrashReporterClient|ThirdParty|backup|bak|patch|lib|renpy|tools|bin32|x86)[\\/]') { continue }
        $taskName = [IO.Path]::GetFileName($taskFile)
        if ($taskName -match '(?i)(crash|unins|uninst|install|setup|launch|start_protected|protocolselector|config|updat|repair|patch|report|7za|python|zsync|directx|redist|benchmark|startup|bootmenu|settings|font|register|check|filechk|loader|GAL PRO MASTER)') { continue }
        $taskTargets += [pscustomobject]@{Name=$taskName;Path=$taskFile}
    }
}
foreach ($taskPath in @(
    'C:\Game\Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe',
    'C:\Game\ZenlessZoneZero Game\ZenlessZoneZero.exe',
    ('D:\gal\Amakano2' + [char]0xff5e + 'PE\Amakano2' + [char]0xff5e + 'PEchs.exe')
)) { if (Test-Path -LiteralPath $taskPath) { $taskTargets += [pscustomobject]@{Name=[IO.Path]::GetFileName($taskPath);Path=$taskPath} } }
$taskTargets = @($taskTargets | Sort-Object Name,Path -Unique)
$taskTargets | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\diagnostics\motion-osd-game-targets-20261006.json') -Encoding UTF8
$taskTargets
