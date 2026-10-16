# Restarts Decky's backend so plugin changes load (restarting Steam does not reload it on Playhub).
# Run it through Invoke-OutsideApp.ps1 when calling from a packaged app.
$ErrorActionPreference = 'Stop'
Get-Process PluginLoader_noconsole -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process powershell.exe -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $env:USERPROFILE 'homebrew\services\Playhub-StartDecky.ps1'),'-Loader','PluginLoader_noconsole.exe' -WindowStyle Hidden
