$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $taskCompiler /nologo /codepage:65001 /target:exe /platform:x64 ("/reference:"+(Join-Path $PSScriptRoot 'plugin\bin\ClawRouter.exe')) ("/out:"+(Join-Path $PSScriptRoot 'plugin\bin\NativePolicyTests.exe')) (Join-Path $PSScriptRoot 'tests\NativePolicyTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compile failed.' }
& (Join-Path $PSScriptRoot 'plugin\bin\NativePolicyTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }
& $taskCompiler /nologo /codepage:65001 /target:exe /platform:x64 ("/reference:"+(Join-Path $PSScriptRoot 'plugin\bin\ClawRouter.exe')) ("/out:"+(Join-Path $PSScriptRoot 'plugin\bin\FirmwareShortcutTests.exe')) (Join-Path $PSScriptRoot 'tests\FirmwareShortcutTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Firmware shortcut test compile failed.' }
& (Join-Path $PSScriptRoot 'plugin\bin\FirmwareShortcutTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Firmware shortcut tests failed.' }
node --test (Join-Path $PSScriptRoot 'tests\frontend.test.mjs') (Join-Path $PSScriptRoot 'tests\menu-router.test.mjs') (Join-Path $PSScriptRoot 'tests\steam-context.test.mjs') (Join-Path $PSScriptRoot 'tests\navigation-haptics.test.mjs') (Join-Path $PSScriptRoot 'tests\compatibility-ui.test.mjs') (Join-Path $PSScriptRoot 'tests\scaling-visibility.test.mjs') (Join-Path $PSScriptRoot 'tests\panel-lifecycle.test.mjs') (Join-Path $PSScriptRoot 'tests\magpie.test.mjs') (Join-Path $PSScriptRoot 'tests\toolbox-ui.test.mjs') (Join-Path $PSScriptRoot 'tests\rumble.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Frontend tests failed.' }
