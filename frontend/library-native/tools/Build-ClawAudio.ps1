# Builds Native\ClawAudio\bin\clawaudio.dll (miniaudio + claw_audio.c) with MSVC x64.
# Called by ClawLibrary.csproj before build when the sources are newer than the DLL.
$ErrorActionPreference = 'Stop'
$src = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\Native\ClawAudio'
$src = (Resolve-Path $src).Path
$bin = Join-Path $src 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -products * -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC x64 build tools not found' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
$cmd = "`"$vcvars`" >nul && cl /nologo /O2 /MT /LD /W3 /utf-8 /D_CRT_SECURE_NO_WARNINGS `"$src\claw_audio.c`" /Fo`"$bin\\`" /Fe`"$bin\clawaudio.dll`" /link /NOLOGO ole32.lib"
cmd /c $cmd
if ($LASTEXITCODE) { throw "cl failed ($LASTEXITCODE)" }
Remove-Item "$bin\*.obj", "$bin\*.exp" -ErrorAction SilentlyContinue
"built $bin\clawaudio.dll"
