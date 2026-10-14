# Screenshot one wall state in both versions: native (full screen, real device) and web (headless Edge,
# 1280x800 CSS px at 1.5x = 1920x1200, same data/art via web-preview.mjs on 127.0.0.1:8765).
#   .\Shoot-Pair.ps1 -State home|expand|collection|styleb|tab|nav -Dir <out> [-Exe <ClawLibrary.exe>] [-Wait 5]
param([string]$State = 'home', [Parameter(Mandatory)][string]$Dir, [string]$Exe, [double]$Wait = 5, [switch]$WebOnly, [switch]$NativeOnly)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) { $Exe = Join-Path $here '..\b\x64\Debug\ClawLibrary.exe' }
New-Item -ItemType Directory -Force $Dir | Out-Null
$hash = if ($State -eq 'home') { '' } else { "#$State" }
if (-not $NativeOnly) {
    $node = 'C:\Users\13304\AppData\Local\OpenAI\Codex\runtimes\cua_node\2c9e75c4e9c71beb\bin\node.exe'
    & $node (Join-Path $here 'web-shot.mjs') "http://127.0.0.1:8765/$hash" "$Dir\web-$State.png" ([int]($Wait * 1000)) 2>&1 | Out-Null
}
if (-not $WebOnly) {
    Get-Process ClawLibrary -ErrorAction SilentlyContinue | Stop-Process -Force
    $args = @()
    if ($State -ne 'home') { $args += "--state=$State" }
    $p = if ($args.Count) { Start-Process $Exe -ArgumentList $args -PassThru } else { Start-Process $Exe -PassThru }
    & (Join-Path $here 'Focus-Window.ps1') -ProcessId $p.Id | Out-Null
    Start-Sleep -Milliseconds ([int]($Wait * 1000))
    & (Join-Path $here 'Capture-Screen.ps1') -Path "$Dir\native-$State.png" | Out-Null
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
Get-ChildItem $Dir -Filter "*-$State.png" | ForEach-Object { "$($_.Name) $($_.Length)" }


