# Builds and signs the Game Bar widget package (artifacts\gamebar-signing\ClawWidget.msix).
# Signing uses the CN=ClawToolbox certificate in CurrentUser\My; the device must trust it (TrustedPeople).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packages = Join-Path $env:USERPROFILE '.nuget\packages'
$out = Join-Path $root 'artifacts\gamebar-widget'
$properties = @('/p:Configuration=Release','/p:Platform=x64',
    "/p:CPPWINRT_DIR=$packages\microsoft.windows.cppwinrt\2.0.250303.1",
    "/p:GAMEBAR_DIR=$(Join-Path $root 'artifacts\nuget-gamebar\pkg')",
    "/p:MUX_DIR=$packages\microsoft.ui.xaml\2.8.7",
    "/p:WEBVIEW2_DIR=$packages\microsoft.web.webview2\1.0.3351.48",
    "/p:OutDir=$out\bin\", "/p:IntDir=$out\obj\")
& 'C:\BuildTools2022\MSBuild\Current\Bin\MSBuild.exe' (Join-Path $root 'native\GameBarWidget\ClawWidget.vcxproj') /nologo /v:m @properties
if ($LASTEXITCODE -ne 0) { throw 'Widget build failed.' }
$msix = Get-ChildItem (Join-Path $out 'bin\ClawWidget\AppPackages') -Recurse -Filter *.msix | Sort-Object LastWriteTime | Select-Object -Last 1
$signed = Join-Path $root 'artifacts\gamebar-signing\ClawWidget.msix'
New-Item -ItemType Directory -Force (Split-Path -Parent $signed) | Out-Null
Copy-Item $msix.FullName $signed -Force
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=ClawToolbox' } | Select-Object -First 1
if (-not $cert) { throw 'Signing certificate CN=ClawToolbox not found in CurrentUser\My.' }
& 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' sign /fd SHA256 /sha1 $cert.Thumbprint $signed | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
Write-Output $signed
