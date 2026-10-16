# Runs a PowerShell script through a one-off scheduled task, outside the process tree of the
# calling app. Needed from packaged apps (e.g. the Claude desktop app): their writes under
# %LOCALAPPDATA% are redirected into the package, so installs and Decky restarts made from
# there are invisible to the sign-in task and to everything else.
param([Parameter(Mandatory)][string]$Script, [string[]]$Arguments = @(), [int]$TimeoutSeconds = 300)
$ErrorActionPreference = 'Stop'
$log = Join-Path (Split-Path -Parent $Script) ('outside-' + [IO.Path]::GetFileNameWithoutExtension($Script) + '.log')
Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
$argumentText = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
$command = '-NoProfile -ExecutionPolicy Bypass -Command "& { & ''' + $Script + ''' ' + $argumentText.Replace('"', "'") + ' *> ''' + $log + '''; exit $LASTEXITCODE }"'
# conhost --headless: no console window at all. With Windows Terminal as the default console a plain
# powershell.exe opens a terminal tab even with -WindowStyle Hidden, takes the focus, and on a
# handheld that focus on a text window pops the touch keyboard.
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument ('--headless powershell.exe ' + $command) -WorkingDirectory (Split-Path -Parent $Script)
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Seconds $TimeoutSeconds)
$name = 'Outside-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
Register-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName $name -Action $action -Settings $settings | Out-Null
try {
    Start-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName $name
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do { Start-Sleep -Milliseconds 500; $task = Get-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName $name } while ($task.State -eq 'Running' -and (Get-Date) -lt $deadline)
    $result = (Get-ScheduledTaskInfo -TaskPath '\ClawToolbox\' -TaskName $name).LastTaskResult
} finally { Unregister-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName $name -Confirm:$false }
if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log }
if ($result -ne 0) { throw ('Outside run failed: ' + $result) }
