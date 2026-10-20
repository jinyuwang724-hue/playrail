param([switch]$FrontendOnly, [switch]$RouterOnly, [string]$Python = $env:CLAW_PYTHON)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
# The agent's private Python runtime (the device has no system Python). Built once from a local
# Python 3.12; pass -Python or set CLAW_PYTHON to rebuild it.
$taskRuntime = Join-Path $taskRoot 'plugin\runtime\python'
if (-not $FrontendOnly -and -not $RouterOnly -and ($Python -or -not (Test-Path -LiteralPath (Join-Path $taskRuntime 'runtime.json')))) {
    if (-not $Python) { throw 'Agent runtime missing: run build.ps1 -Python <path to python.exe 3.12>.' }
    & $Python (Join-Path $taskRoot 'tools\Build-AgentRuntime.py') $taskRuntime
    if ($LASTEXITCODE -ne 0) { throw 'Agent runtime build failed.' }
}
if (-not $FrontendOnly) {
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Windows .NET Framework 4.x compiler is required.' }
$taskOutput = Join-Path $taskRoot 'plugin\bin'
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Management.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll ("/out:" + (Join-Path $taskOutput 'ClawRouter.exe')) (Join-Path $taskRoot 'native\ClawRouter.cs') (Join-Path $taskRoot 'native\FirmwareShortcutFilter.cs') (Join-Path $taskRoot 'native\GameBarGuard.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 ("/out:" + (Join-Path $taskOutput 'ClawTouchBridge.exe')) (Join-Path $taskRoot 'native\ClawTouchBridge.cs')
if ($LASTEXITCODE -ne 0) { throw 'Touch bridge build failed.' }
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Management.dll ("/out:" + (Join-Path $taskOutput 'ClawPower.exe')) (Join-Path $taskRoot 'native\ClawPower.cs') (Join-Path $taskRoot 'native\ClawMappedInput.cs') (Join-Path $taskRoot 'native\ClawFanHardware.cs') (Join-Path $taskRoot 'native\ClawDisplay.cs') (Join-Path $taskRoot 'native\ClawGpu.cs') (Join-Path $taskRoot 'native\ClawSystem.cs')
if ($LASTEXITCODE -ne 0) { throw 'Power helper build failed.' }
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Management.dll /reference:System.ServiceProcess.dll ("/out:" + (Join-Path $taskOutput 'ClawFanService.exe')) (Join-Path $taskRoot 'native\ClawFanService.cs') (Join-Path $taskRoot 'native\ClawFanHardware.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fan service build failed.' }
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ("/out:" + (Join-Path $taskOutput 'ClawInput.exe')) (Get-ChildItem -LiteralPath (Join-Path $taskRoot 'native\InputLayer') -Filter *.cs | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw 'Input layer build failed.' }
if (-not $RouterOnly) {
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /reference:System.Web.Extensions.dll ("/win32manifest:" + (Join-Path $taskRoot 'native\SteamAdminLauncher.manifest')) ("/out:" + (Join-Path $taskOutput 'SteamAdminLauncher.exe')) (Join-Path $taskRoot 'native\SteamAdminLauncher.cs') (Join-Path $taskRoot 'native\SteamPaths.cs')
if ($LASTEXITCODE -ne 0) { throw 'Steam launcher build failed.' }
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:winexe /platform:x64 /reference:System.Web.Extensions.dll ("/out:" + (Join-Path $taskOutput 'CompatibilityUtility.exe')) (Join-Path $taskRoot 'native\CompatibilityUtility.cs') (Join-Path $taskRoot 'native\SteamPaths.cs') (Join-Path $taskRoot 'native\GameBarHomePreference.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compatibility utility build failed.' }
& $taskCompiler /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Management.dll ("/out:" + (Join-Path $taskOutput 'ClashBridge.exe')) (Join-Path $taskRoot 'native\ClashBridge.cs') (Join-Path $taskRoot 'native\VergeSession.cs')
if ($LASTEXITCODE -ne 0) { throw 'Clash bridge build failed.' }
}
$taskVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'plugin\plugin.json') | ConvertFrom-Json).version
Copy-Item -LiteralPath (Join-Path $taskOutput 'ClawRouter.exe') -Destination (Join-Path $taskOutput ('ClawRouter-'+$taskVersion+'.exe')) -Force
# The bridge runs while a game is scaled; version it like the router so installs never overwrite a running copy.
Copy-Item -LiteralPath (Join-Path $taskOutput 'ClawTouchBridge.exe') -Destination (Join-Path $taskOutput ('ClawTouchBridge-'+$taskVersion+'.exe')) -Force
# The power helper stays elevated while the plugin runs; versioned for the same reason.
Copy-Item -LiteralPath (Join-Path $taskOutput 'ClawPower.exe') -Destination (Join-Path $taskOutput ('ClawPower-'+$taskVersion+'.exe')) -Force
# The input layer engine stays elevated while the layer is on; versioned for the same reason.
Copy-Item -LiteralPath (Join-Path $taskOutput 'ClawInput.exe') -Destination (Join-Path $taskOutput ('ClawInput-'+$taskVersion+'.exe')) -Force
}
[IO.Directory]::CreateDirectory((Join-Path $taskRoot 'plugin\dist')) | Out-Null
$taskRouter = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\menu-router.js'),[Text.Encoding]::UTF8)
$taskHaptics = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\navigation-haptics.js'),[Text.Encoding]::UTF8)
$taskGameAdder = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\game-add.js'),[Text.Encoding]::UTF8)
$taskPanelLifecycle = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\panel-lifecycle.js'),[Text.Encoding]::UTF8)
$taskToolboxUI = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\toolbox-ui.js'),[Text.Encoding]::UTF8)
$taskMagpie = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\magpie.js'),[Text.Encoding]::UTF8)
$taskPower = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\power.js'),[Text.Encoding]::UTF8)
$taskRumble = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\rumble.js'),[Text.Encoding]::UTF8)
$taskSteamContext = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\steam-context.js'),[Text.Encoding]::UTF8)
$taskFrontend = [IO.File]::ReadAllText((Join-Path $taskRoot 'plugin\frontend.js'),[Text.Encoding]::UTF8)
$taskFrontend = $taskFrontend.Replace('import { createMenuRouter } from "./menu-router.js";', '')
$taskFrontend = $taskFrontend.Replace('import { installNavigationHaptics } from "./navigation-haptics.js";', '')
$taskFrontend = $taskFrontend.Replace('import { createGameAdder } from "./game-add.js";', '')
$taskFrontend = $taskFrontend.Replace('import { syncPluginPanel } from "./panel-lifecycle.js";', '')
$taskFrontend = $taskFrontend.Replace('import { createModuleSelection, createToolboxUI, toolboxModules, toolboxStyles } from "./toolbox-ui.js";', '')
$taskFrontend = $taskFrontend.Replace('import { createMagpieController, createMagpiePanel } from "./magpie.js";', '')
$taskFrontend = $taskFrontend.Replace('import { syncOverlayInset, syncQuickAccessSize } from "./magpie.js";', '')
$taskFrontend = $taskFrontend.Replace('import { installSteamContext } from "./steam-context.js";', '')
$taskFrontend = $taskFrontend.Replace('import { createPowerController, createPowerPanel, powerSummaries, powerTiles } from "./power.js";', '')
$taskFrontend = $taskFrontend.Replace('import { createRumbleController, createRumbleRows } from "./rumble.js";', '')
[IO.File]::WriteAllText((Join-Path $taskRoot 'plugin\dist\index.js'),($taskRouter + [Environment]::NewLine + $taskHaptics + [Environment]::NewLine + $taskGameAdder + [Environment]::NewLine + $taskPanelLifecycle + [Environment]::NewLine + $taskToolboxUI + [Environment]::NewLine + $taskMagpie + [Environment]::NewLine + $taskPower + [Environment]::NewLine + $taskRumble + [Environment]::NewLine + $taskSteamContext + [Environment]::NewLine + $taskFrontend),(New-Object Text.UTF8Encoding($false)))
Write-Output 'Build complete.'
