$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskTestExe = Join-Path $taskRoot 'artifacts\FanServiceRecoveryTests.exe'
[IO.Directory]::CreateDirectory((Split-Path -Parent $taskTestExe)) | Out-Null
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Management.dll /reference:System.ServiceProcess.dll ("/out:"+$taskTestExe) /main:FanServiceTests (Join-Path $taskRoot 'native\ClawFanService.cs') (Join-Path $taskRoot 'native\ClawFanHardware.cs') (Join-Path $taskRoot 'tests\FanServiceTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fan recovery test build failed.' }
& $taskTestExe
if ($LASTEXITCODE -ne 0) { throw 'Fan recovery regression failed.' }
