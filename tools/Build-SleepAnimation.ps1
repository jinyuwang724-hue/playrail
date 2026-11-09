param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\sleep-animation\build'))
$ErrorActionPreference = 'Stop'
$taskProject = Join-Path $PSScriptRoot '..\native\SleepAnimation\ClawSleepAnimation.csproj'
& dotnet publish $taskProject -c Release -p:Platform=x64 --no-self-contained -o $OutputDirectory -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Sleep animation build failed.' }
Write-Output (Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'ClawSleepAnimation.exe')
