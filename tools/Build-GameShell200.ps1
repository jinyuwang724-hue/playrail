param([switch]$Ui)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOut=Join-Path $taskRoot 'artifacts\game-shell-200'
$taskNative=Join-Path $taskOut 'src\native\ClawShell'
$taskCsc="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $taskCsc /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /reference:System.Core.dll /reference:Microsoft.CSharp.dll ("/out:"+(Join-Path $taskOut 'ClawShell.exe')) (Get-ChildItem -LiteralPath $taskNative -Filter *.cs | ForEach-Object FullName)
if ($LASTEXITCODE) { throw 'Shell compile failed' }
foreach ($taskTest in @('ClawShellTests','GameShellTests')) {
    $taskFiles=@((Join-Path $taskRoot 'tests\ClawShellTests.cs'))
    if ($taskTest -eq 'GameShellTests') { $taskFiles+=Join-Path $taskRoot 'tests\SpecialKeys197\GameShellTests.cs' }
    & $taskCsc /nologo /codepage:65001 /reference:System.Core.dll ("/reference:"+(Join-Path $taskOut 'ClawShell.exe')) ("/main:"+$taskTest) ("/out:"+(Join-Path $taskOut ($taskTest+'.exe'))) $taskFiles
    if ($LASTEXITCODE) { throw 'Shell test compile failed' }
    & (Join-Path $taskOut ($taskTest+'.exe'))
    if ($LASTEXITCODE) { throw 'Shell tests failed' }
}
Copy-Item -LiteralPath (Join-Path $taskOut 'ClawShell.exe') -Destination (Join-Path $taskOut 'src\frontend\library-native\ClawLibrary\Payload\shell\ClawShell.exe')
if ($Ui) {
    $env:PATH='C:\Program Files\dotnet;'+$env:PATH
    & (Join-Path $taskOut 'src\frontend\library-native\tools\Build-Msix.ps1')
    if ($LASTEXITCODE) { throw 'Library build failed' }
}
