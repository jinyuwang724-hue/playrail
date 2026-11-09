param([switch]$SkipUi, [switch]$SkipInput)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOut = Join-Path $taskRoot 'artifacts\overlay-focus-fix-191'
$taskSrc = Join-Path $taskOut 'src'
$taskBin = Join-Path $taskSrc 'plugin\bin'
[IO.Directory]::CreateDirectory($taskBin) | Out-Null
$taskWinmd = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata" -Directory | Where-Object Name -Match '^10\.' | Sort-Object { [version]$_.Name } | Select-Object -Last 1 | ForEach-Object { Join-Path $_.FullName 'Windows.winmd' }
if (-not $SkipInput) {
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Runtime.dll /reference:System.Numerics.dll /reference:System.Numerics.Vectors.dll /reference:System.Runtime.WindowsRuntime.dll ("/reference:"+$taskWinmd) ("/out:"+(Join-Path $taskBin 'ClawInput-0.17.58-overlay-r2.exe')) (Get-ChildItem -LiteralPath (Join-Path $taskSrc 'native\InputLayer') -Filter *.cs | ForEach-Object FullName)
    if ($LASTEXITCODE) { throw 'Overlay input build failed' }
}
if (-not $SkipUi) {
    & (Join-Path $taskSrc 'frontend\library-native\tools\Build-Msix.ps1')
    if ($LASTEXITCODE) { throw 'Library build failed' }
}
