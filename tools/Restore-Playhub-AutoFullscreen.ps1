$ErrorActionPreference = 'Stop'
$taskConfigPath = 'C:\Users\13304\AppData\Roaming\GamingMode\config.json'
$taskSource = [IO.File]::ReadAllText($taskConfigPath)
$taskPattern = '"borderlessFullscreenWindowsInGamingMode"\s*:\s*(true|false)'
if ([regex]::Matches($taskSource, $taskPattern).Count -ne 1) { throw 'Expected exactly one fullscreen policy setting.' }
$taskUpdated = [regex]::Replace($taskSource, $taskPattern, '"borderlessFullscreenWindowsInGamingMode": true')
[IO.File]::WriteAllText($taskConfigPath, $taskUpdated, [Text.UTF8Encoding]::new($false))
Write-Output 'Playhub 自动无边框全屏已恢复；重新切换 GamingMode 后应用。'
