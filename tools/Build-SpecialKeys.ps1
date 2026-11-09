param([switch]$Ui)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOut=Join-Path $taskRoot 'artifacts\special-keys-197'
$taskSrc=Join-Path $taskOut 'src'
$taskCsc="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$taskNative=Join-Path $taskSrc 'native'
$taskSources=@('ClawRouter.cs','FirmwareShortcutFilter.cs','GameBarGuard.cs','SessionModeProbe.cs','RawKeyboardSource.cs') | ForEach-Object { Join-Path $taskNative $_ }
& $taskCsc /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Management.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll ("/out:"+(Join-Path $taskOut 'ClawRouter-0.17.58-specialkeys.exe')) $taskSources
if ($LASTEXITCODE) { throw 'Router compile failed' }
foreach ($taskTest in @('FirmwareShortcutTests','SpecialKeys197\ShortcutTests')) {
    $taskTestExe=Join-Path $taskOut ((Split-Path $taskTest -Leaf)+'.exe')
    & $taskCsc /nologo /codepage:65001 /reference:System.Windows.Forms.dll ("/reference:"+(Join-Path $taskOut 'ClawRouter-0.17.58-specialkeys.exe')) ("/out:"+$taskTestExe) (Join-Path $taskRoot ('tests\'+$taskTest+'.cs'))
    if ($LASTEXITCODE) { throw 'Shortcut test compile failed' }
    & $taskTestExe
    if ($LASTEXITCODE) { throw 'Shortcut tests failed' }
}
$taskShell=@('ShellGuard.cs','WindowsShellSystem.cs','Program.cs') | ForEach-Object { Join-Path $taskNative ('ClawShell\'+$_) }
& $taskCsc /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /reference:System.Core.dll /reference:Microsoft.CSharp.dll ("/out:"+(Join-Path $taskOut 'ClawShell.exe')) $taskShell
if ($LASTEXITCODE) { throw 'Shell compile failed' }
& $taskCsc /nologo /codepage:65001 /reference:System.Core.dll ("/reference:"+(Join-Path $taskOut 'ClawShell.exe')) ("/out:"+(Join-Path $taskOut 'ClawShellTests.exe')) (Join-Path $taskRoot 'tests\ClawShellTests.cs')
if ($LASTEXITCODE) { throw 'Shell test compile failed' }
& (Join-Path $taskOut 'ClawShellTests.exe')
if ($LASTEXITCODE) { throw 'Shell tests failed' }
if ($Ui) { & (Join-Path $taskSrc 'frontend\library-native\tools\Build-Msix.ps1'); if ($LASTEXITCODE) { throw 'UI build failed' } }
