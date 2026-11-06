# 编译游戏模式外壳启动器 ClawShell.exe 和它的护栏测试，然后跑测试。
param([string]$Out, [switch]$NoTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'plugin\bin' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force $Out | Out-Null
$src = Join-Path $root 'native\ClawShell'
$core = Join-Path $src 'ShellGuard.cs'
$system = Join-Path $src 'WindowsShellSystem.cs'
# 外壳的全部源码（WindowsShellSystem.*.cs 是同一个类的分部：保留 explorer、游戏界面）。以前只列了三个文件，漏编分部会少功能
$sources = @(Get-ChildItem -LiteralPath $src -Filter *.cs | Sort-Object Name | ForEach-Object FullName)
& $csc /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /reference:System.Core.dll /reference:Microsoft.CSharp.dll ("/out:" + (Join-Path $Out 'ClawShell.exe')) $sources
if ($LASTEXITCODE -ne 0) { throw 'ClawShell compile failed.' }
if ($NoTests) { return }
& $csc /nologo /codepage:65001 /target:exe /platform:x64 /reference:System.Core.dll ("/reference:" + (Join-Path $Out 'ClawShell.exe')) ("/out:" + (Join-Path $Out 'ClawShellTests.exe')) (Join-Path $root 'tests\ClawShellTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'ClawShell test compile failed.' }
& (Join-Path $Out 'ClawShellTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'ClawShell tests failed.' }
