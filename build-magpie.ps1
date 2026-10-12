param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskSource = Join-Path $taskRoot 'native\upstream\Magpie-0.12.1'
$taskPython = Join-Path $taskRoot 'artifacts\tooling\magpie-python\Scripts'
$env:PATH = $taskPython + ';' + $env:PATH
$taskMSBuild = 'C:\BuildTools2022\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $taskMSBuild)) { throw 'Install Visual Studio 2022 Build Tools with C++ desktop, UWP C++ and Windows SDK 26100.' }
$taskOut = Join-Path $taskRoot 'artifacts\magpie-build\'
[IO.Directory]::CreateDirectory($taskOut) | Out-Null
if (-not $SkipBuild) {
    Push-Location $taskSource
    try {
        & $taskMSBuild Magpie.sln -m:4 -restore '-p:RestorePackagesConfig=true;Configuration=Release;Platform=x64;UseClangCL=false;MajorVersion=0;MinorVersion=12;PatchVersion=1;VersionTag=claw;CommitId=664e0f4' ("-p:OutDir="+$taskOut) -v:minimal '-flp:logfile=build-claw.log;verbosity=normal'
        if ($LASTEXITCODE -ne 0) { throw 'Magpie build failed; inspect native/upstream/Magpie-0.12.1/build-claw.log.' }
    } finally { Pop-Location }
}
$taskBundle = Join-Path $taskRoot 'plugin\bin\magpie'
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 ("/out:"+(Join-Path $taskBundle 'MagpieLauncher.exe')) (Join-Path $taskRoot 'native\MagpieLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Magpie privilege bridge build failed.' }
foreach ($taskName in @('Magpie.exe','Microsoft.UI.Xaml.dll','resources.pri')) {
    Copy-Item -LiteralPath (Join-Path $taskOut $taskName) -Destination (Join-Path $taskBundle $taskName) -Force
}
Copy-Item -LiteralPath (Join-Path $taskSource 'LICENSE') -Destination (Join-Path $taskBundle 'LICENSE') -Force
@'
Magpie 0.12.1-claw, based on https://github.com/Blinue/Magpie/tree/v0.12.1
Copyright (c) Xu and contributors. GPL-3.0-or-later.
Claw modifications: verified elevation pipe bridge, target HWND, cover crop, original effect chains/parameters and capture controls.
Corresponding patched source and build instructions: magpie-source.zip in this directory.
Effects and signed TouchHelper originate from the verified official v0.12.1 release.
'@ | Set-Content -LiteralPath (Join-Path $taskBundle 'NOTICE.txt') -Encoding utf8
Write-Output $taskBundle
