$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\gamebar-power-handoff'
$packages = Join-Path $env:USERPROFILE '.nuget\packages'
$properties = @('/p:Configuration=Release','/p:Platform=x64','/p:AppxBundle=Never',
    "/p:CPPWINRT_DIR=$packages\microsoft.windows.cppwinrt\2.0.250303.1",
    "/p:GAMEBAR_DIR=$(Join-Path $root 'artifacts\nuget-gamebar\pkg')",
    "/p:MUX_DIR=$packages\microsoft.ui.xaml\2.8.7",
    "/p:WEBVIEW2_DIR=$packages\microsoft.web.webview2\1.0.3351.48",
    "/p:OutDir=$out\bin\", "/p:IntDir=$out\obj\")
& 'C:\BuildTools2022\MSBuild\Current\Bin\MSBuild.exe' (Join-Path $out 'src\native\GameBarWidget\ClawWidget.vcxproj') /nologo /v:m @properties *> (Join-Path $out 'build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $out 'build.log') -Tail 35; throw 'Widget build failed.' }
$msix = Get-ChildItem (Join-Path $out 'bin\ClawWidget') -Recurse -Filter 'ClawWidget_0.4.43.0_x64.msix' | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $msix) { throw 'Widget package not found.' }
$signed = Join-Path $out 'ClawWidget-0.4.43.0.msix'
Copy-Item -LiteralPath $msix.FullName -Destination $signed
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=ClawToolbox' } | Select-Object -First 1
if (-not $cert) { throw 'ClawToolbox signing certificate missing.' }
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $signed | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
if ((Get-AuthenticodeSignature -LiteralPath $signed).Status -ne 'Valid') { throw 'Invalid signature.' }
Write-Output $signed
