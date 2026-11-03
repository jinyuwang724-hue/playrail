# Release build: NativeAOT, MSIX signed with the existing self-signed cert CN=ClawToolbox (same as the Game Bar widget).
#   .\Build-Msix.ps1 [-Install]
# Needs: .NET 9 SDK, VS Build Tools with MSVC x64 (NativeAOT linker), the cert in CurrentUser\My.
param([switch]$Install)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here '..\ClawLibrary\ClawLibrary.csproj'
$out = Join-Path $here '..\AppPackages\'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=ClawToolbox' | Select-Object -First 1
if (-not $cert) { throw 'certificate CN=ClawToolbox not found in CurrentUser\My' }
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -products * -latest -property installationPath
$msvc = Get-ChildItem "$vs\VC\Tools\MSVC" -Directory | Sort-Object Name | Select-Object -Last 1
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$($msvc.FullName)\bin\Hostx64\x64;$env:PATH"
dotnet publish $proj -c Release -p:Platform=x64 -r win-x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($cert.Thumbprint) -p:AppxBundle=Never -p:AppxPackageDir=$out
if ($LASTEXITCODE) { throw "publish failed ($LASTEXITCODE)" }
$msix = Get-ChildItem $out -Recurse -Filter *.msix | Sort-Object LastWriteTime | Select-Object -Last 1
"package: $($msix.FullName) ($([math]::Round($msix.Length / 1MB, 1)) MB)"
if ($Install) {
    # Upgrade in place so LocalState, art kits and user preferences survive. Increment the
    # manifest version for a new build; never uninstall the package to bypass version checks.
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
    Get-AppxPackage ClawToolbox.Library | Select-Object Name, Version, PackageFamilyName
}


