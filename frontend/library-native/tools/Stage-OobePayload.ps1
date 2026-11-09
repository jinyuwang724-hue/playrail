# Collects what the first-run setup installs into ClawLibrary\Payload (packed into the MSIX).
# Sources are the components that already run on this machine, so the package carries the same
# versions: the agent in %LOCALAPPDATA%\ClawToolbox, the fan service in Program Files, the signed
# widget msix from its build folder, and PawnIO's official setup.
param(
    [string]$AgentHome = (Join-Path $env:LOCALAPPDATA 'ClawToolbox'),
    [string]$Widget = '',
    [string]$PawnIOSetup = ''
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $Widget) {
    $version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'native\GameBarWidget\Package.appxmanifest') -Raw)).Package.Identity.Version
    $Widget = Join-Path $repo "artifacts\gamebar-qam\ClawWidget-$version.msix"
}
if (-not (Test-Path -LiteralPath $Widget -PathType Leaf)) { throw "Game Bar package is missing: $Widget" }
$payload = Join-Path $PSScriptRoot '..\ClawLibrary\Payload'
foreach ($folder in 'agent', 'fan', 'widget', 'pawnio') {
    $path = Join-Path $payload $folder
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}
$current = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $AgentHome 'current.json') | ConvertFrom-Json
$version = $current.version
$app = Split-Path -Parent $current.agent
$helperVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $app 'plugin.json') | ConvertFrom-Json).version
# The agent goes in as one zip (app\ + runtime\python-x.y.z\): thousands of runtime files and deep
# Magpie paths would otherwise blow MAX_PATH in this worktree and slow the MSIX down.
# The agent folder keeps every older helper build next to the current one; take only this version's.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open((Join-Path $payload 'agent\agent.zip'), 'Create')
try {
    $runtime = Split-Path -Parent $current.python
    foreach ($root in @(@($app, 'app'), @($runtime, ('runtime/' + (Split-Path -Leaf $runtime))))) {
        foreach ($file in Get-ChildItem -LiteralPath $root[0] -Recurse -File) {
            $relative = $file.FullName.Substring($root[0].Length + 1)
            if ($relative -match '(^|\\)__pycache__\\') { continue }
            if ($file.Name -match '-(\d+\.\d+\.\d+)(-r\d+)?\.exe$' -and $Matches[1] -ne $helperVersion) { continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, ($root[1] + '/' + $relative.Replace('\', '/')), 'Optimal')
        }
    }
} finally { $zip.Dispose() }
[IO.File]::WriteAllText((Join-Path $payload 'agent\version.txt'), $version)
$service = Join-Path $env:ProgramFiles 'ClawToolbox\FanService'
Copy-Item -LiteralPath (Join-Path $service 'ClawFanService.exe') -Destination (Join-Path $payload 'fan')
Copy-Item -LiteralPath (Join-Path $service 'pawnio\IntelMSR.bin') -Destination (Join-Path $payload 'fan')
Copy-Item -LiteralPath $Widget -Destination (Join-Path $payload 'widget')
if ($PawnIOSetup) { Copy-Item -LiteralPath $PawnIOSetup -Destination (Join-Path $payload 'pawnio\PawnIO_setup.exe') }
$size = (Get-ChildItem -LiteralPath $payload -Recurse -File | Measure-Object Length -Sum).Sum
Write-Output ('payload: agent ' + $version + ', ' + [math]::Round($size / 1MB, 1) + ' MB')
