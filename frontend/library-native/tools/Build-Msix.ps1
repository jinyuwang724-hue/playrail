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

# Payload (first-run components + ClawShell for game mode) is git-ignored, so a worktree checkout has
# almost none of it. Fill missing files from the main checkout's Payload (never overwrite or delete),
# then refuse to package if a key file is still missing: without Payload\shell\ClawShell.exe the
# library hides every game-mode entry (ShellMode.Available), and the first-run setup can't install.
$payload = [IO.Path]::GetFullPath((Join-Path $here '..\ClawLibrary\Payload'))
$common = (& git -C $here rev-parse --path-format=absolute --git-common-dir 2>$null)
if ($common) {
    $mainPayload = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $common) 'frontend\library-native\ClawLibrary\Payload'))
    if ($mainPayload -ne $payload -and (Test-Path -LiteralPath $mainPayload)) {
        robocopy $mainPayload $payload /E /XC /XN /XO /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "payload sync from $mainPayload failed ($LASTEXITCODE)" }
        "payload: filled missing files from $mainPayload"
    }
}
$required = 'shell\ClawShell.exe', 'session\ClawGameSession.exe', 'Install-Components.ps1', 'pawnio\PawnIO_setup.exe',
    'agent\agent.zip', 'agent\version.txt', 'fan\ClawFanService.exe', 'fan\IntelMSR.bin',
    'sleep\Install-SleepAnimation.ps1', 'sleep\app\ClawSleepAnimation.exe', 'sleep\app\enhanced-bundle.json'
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $payload $_)) })
if (-not (Get-ChildItem -LiteralPath (Join-Path $payload 'widget') -Filter *.msix -ErrorAction SilentlyContinue)) { $missing += 'widget\*.msix' }
if ($missing.Count) { throw "Payload incomplete, not packaging (run tools\Stage-OobePayload.ps1): $($missing -join ', ')" }
# The shell launcher must be the version in native\ClawShell: 0.1.232/0.1.233 shipped a stale 1.0.1 that
# overwrote the installed 1.0.9 (keep-explorer game mode silently gone). Program.Version is stamped as file version.
$shellSource = [IO.Path]::GetFullPath((Join-Path $here '..\..\..\native\ClawShell\Program.cs'))
$shellWant = [regex]::Match([IO.File]::ReadAllText($shellSource), 'const string Version = "([\d.]+)"').Groups[1].Value
$shellHave = (Get-Item -LiteralPath (Join-Path $payload 'shell\ClawShell.exe')).VersionInfo.FileVersion
if (-not $shellWant -or $shellHave -ne $shellWant) {
    throw "Payload\shell\ClawShell.exe is '$shellHave' but native\ClawShell is '$shellWant'; run tools\Build-ClawShell.ps1 -Out `"$payload\shell`" -NoTests (and copy it to the main checkout's Payload)"
}
"shell: ClawShell $shellHave"

dotnet publish $proj -c Release -p:Platform=x64 -r win-x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($cert.Thumbprint) -p:AppxBundle=Never -p:AppxPackageDir=$out
if ($LASTEXITCODE) { throw "publish failed ($LASTEXITCODE)" }
$msix = Get-ChildItem $out -Recurse -Filter *.msix | Sort-Object LastWriteTime | Select-Object -Last 1
"package: $($msix.FullName) ($([math]::Round($msix.Length / 1MB, 1)) MB)"
if ($Install) {
    # Upgrade in place so LocalState, art kits and user preferences survive. Increment the
    # manifest version for a new build; never uninstall the package to bypass version checks.
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
    $installed = Get-AppxPackage ClawToolbox.Library
    $installed | Select-Object Name, Version, PackageFamilyName
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $homePath = Join-Path $env:LOCALAPPDATA 'ClawToolbox'
    $installer = Join-Path $installed.InstallLocation 'Payload\Install-Components.ps1'
    $run = [guid]::NewGuid().ToString('N')
    $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -AgentHome "{1}" -User "{2}" -UserSid {3} -Run {4}' -f $installer, $homePath, $identity.Name, $identity.User.Value, $run
    $process = Start-Process -FilePath powershell.exe -Verb RunAs -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Backend setup failed ($($process.ExitCode)). See $homePath\setup\status-$run.json." }
}


