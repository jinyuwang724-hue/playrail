$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskPython='C:\Users\13304\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
$taskModule=Join-Path $taskRoot 'plugin\claw_sleep_animation.py'
$taskCode="import importlib.util,asyncio,json,sys;s=importlib.util.spec_from_file_location('a',sys.argv[1]);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);print(json.dumps(asyncio.run(m.view()),ensure_ascii=True))"
& $taskPython -c $taskCode $taskModule
if($LASTEXITCODE -ne 0){throw 'Animation pipe probe failed'}
