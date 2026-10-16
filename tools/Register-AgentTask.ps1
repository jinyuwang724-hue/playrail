# Registers (or updates) the sign-in task that starts the toolbox agent. Needs administrator
# rights for a logon trigger; install.ps1 runs it elevated. The task itself runs as the signed-in
# user without elevation; helpers that need administrator rights elevate themselves as before.
param([Parameter(Mandatory)][string]$Python, [Parameter(Mandatory)][string]$Agent, [Parameter(Mandatory)][string]$User)
$ErrorActionPreference = 'Stop'
$action = New-ScheduledTaskAction -Execute $Python -Argument ('"' + $Agent + '"') -WorkingDirectory (Split-Path -Parent $Agent)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
# Handhelds live on battery: the defaults would refuse to start, or kill the agent, off the charger.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances Parallel -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskPath '\ClawToolbox\' -TaskName 'Agent' -Action $action -Trigger $trigger -Principal $principal `
    -Settings $settings -Description 'Claw handheld toolbox agent: power, fan, OSD, rumble and buttons without Steam.' -Force | Out-Null
