param([Parameter(Mandatory=$true)][string]$BackupDirectory)
$ErrorActionPreference='Stop'
$taskLink='C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Steam\Steam.lnk'
$taskShell=New-Object -ComObject WScript.Shell
if ($taskShell.CreateShortcut($taskLink).TargetPath -ne 'C:\Software\Steam.exe') { throw 'Steam shortcut target changed.' }
$taskData=[IO.File]::ReadAllBytes($taskLink)
if ($taskData[0] -ne 0x4c) { throw 'Unexpected shortcut format.' }
$taskBackup=Join-Path $BackupDirectory 'Steam-common-shortcut.lnk'
if (Test-Path -LiteralPath $taskBackup) { throw 'Original shortcut backup already exists; it will not be overwritten.' }
Copy-Item -LiteralPath $taskLink -Destination $taskBackup
$taskData[0x15]=$taskData[0x15] -bor 0x20
[IO.File]::WriteAllBytes($taskLink,$taskData)
