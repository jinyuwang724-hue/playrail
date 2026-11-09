# ClawShell 1.0.2 + 游戏库 0.1.201：编译外壳、跑外壳测试、把外壳放进游戏库 Payload，-Ui 时再出游戏库 MSIX。
param([switch]$Ui)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$out=Join-Path $root 'artifacts\game-shell-201'
New-Item -ItemType Directory -Force $out | Out-Null
$csc="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$shell=Join-Path $out 'ClawShell.exe'
& $csc /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /reference:System.Core.dll /reference:Microsoft.CSharp.dll ("/out:"+$shell) (Get-ChildItem -LiteralPath (Join-Path $root 'native\ClawShell') -Filter *.cs | ForEach-Object FullName)
if ($LASTEXITCODE) { throw 'Shell compile failed' }
foreach ($test in @('ClawShellTests','GameShellTests')) {
    $files=@((Join-Path $root 'tests\ClawShellTests.cs'))
    if ($test -eq 'GameShellTests') { $files+=Join-Path $root 'tests\SpecialKeys197\GameShellTests.cs' }
    & $csc /nologo /codepage:65001 /reference:System.Core.dll ("/reference:"+$shell) ("/main:"+$test) ("/out:"+(Join-Path $out ($test+'.exe'))) $files
    if ($LASTEXITCODE) { throw 'Shell test compile failed' }
    & (Join-Path $out ($test+'.exe'))
    if ($LASTEXITCODE) { throw 'Shell tests failed' }
}
Copy-Item -LiteralPath $shell -Destination (Join-Path $root 'frontend\library-native\ClawLibrary\Payload\shell\ClawShell.exe')
if ($Ui) {
    $env:PATH='C:\Program Files\dotnet;'+$env:PATH
    & (Join-Path $root 'frontend\library-native\tools\Build-Msix.ps1')
    if ($LASTEXITCODE) { throw 'Library build failed' }
}
