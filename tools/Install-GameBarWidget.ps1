# Registers the toolbox Game Bar widget from its build layout (developer mode, no signing).
# Run through Invoke-OutsideApp.ps1 from a packaged app: files under %LOCALAPPDATA% must be real.
param([string]$Layout = (Join-Path $PSScriptRoot '..\artifacts\gamebar-widget\bin\ClawWidget'))
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'ClawToolbox\widget'
$files = 'AppxManifest.xml','ClawWidget.exe','ClawWidget.winmd','App.xbf','WidgetPage.xbf','resources.pri',
         'Microsoft.Gaming.XboxGameBar.dll','Microsoft.Gaming.XboxGameBar.pri','Microsoft.Gaming.XboxGameBar.winmd'
$existing = Get-AppxPackage -Name 'ClawToolbox.GameBarWidget'
if ($existing) { Remove-AppxPackage -Package $existing.PackageFullName }
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $target 'Assets') | Out-Null
foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $Layout $file) -Destination (Join-Path $target $file) }
Copy-Item -Path (Join-Path $Layout 'Assets\*') -Destination (Join-Path $target 'Assets')
Add-AppxPackage -Register (Join-Path $target 'AppxManifest.xml') -ForceApplicationShutdown
$package = Get-AppxPackage -Name 'ClawToolbox.GameBarWidget'
Write-Output ($package.PackageFamilyName + ' ' + $package.Version)
