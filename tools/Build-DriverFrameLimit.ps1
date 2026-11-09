$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOut = Join-Path $taskRoot 'artifacts\driver-frame-limit'
$taskSource = Join-Path $taskOut 'native'
$taskManifest = Get-Content -Raw -LiteralPath (Join-Path $taskOut 'preparation.json') | ConvertFrom-Json
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskFiles = 'ClawPower.cs','ClawSmallCores.cs','ClawMappedInput.cs','ClawLossless.cs','ClawFanHardware.cs','ClawDisplay.cs','ClawGpu.cs','ClawSystem.cs','ClawUpdate.cs','ClawFrameLimit.cs'
$taskSources = $taskFiles | ForEach-Object { Join-Path $taskSource $_ }
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Management.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:Microsoft.CSharp.dll ("/lib:" + (Join-Path (Split-Path $taskCompiler) 'WPF')) /reference:UIAutomationClient.dll /reference:UIAutomationTypes.dll /reference:WindowsBase.dll ("/out:" + (Join-Path $taskOut $taskManifest.newHelper)) @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Driver limiter helper build failed' }
Write-Output $taskManifest.newHelper
