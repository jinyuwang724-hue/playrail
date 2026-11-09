$ErrorActionPreference = 'Stop'
$taskSdk = Join-Path $PSScriptRoot 'artifacts\tooling\dotnet8\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskSdk)) {
    $taskSdk = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $taskSdk publish (Join-Path $PSScriptRoot 'native\Scaling\ClawScaling.csproj') -c Release --self-contained false -o (Join-Path $PSScriptRoot 'plugin\bin\scaling') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Scaling backend build failed.' }
