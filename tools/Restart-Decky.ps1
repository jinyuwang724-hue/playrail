# Restarts the standalone Decky backend so plugin changes load.
# Run it through Invoke-OutsideApp.ps1 when calling from a packaged app.
$ErrorActionPreference = 'Stop'
Get-Process PluginLoader_noconsole -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process powershell.exe -ArgumentList '-NoProfile','-WindowStyle','Hidden','-ExecutionPolicy','Bypass','-File',(Join-Path $env:USERPROFILE 'homebrew\services\Start-Decky.ps1') -WindowStyle Hidden
