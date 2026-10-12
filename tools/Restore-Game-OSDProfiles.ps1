$ErrorActionPreference = 'Stop'
$taskBackupRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\diagnostics\motion-osd-backup-20261006'))
$taskProfilesRoot = [IO.Path]::GetFullPath('C:\Software\RivaTuner Statistics Server\Profiles')
$taskManifest = @(Get-Content -Raw -LiteralPath (Join-Path $taskBackupRoot 'game-profiles.json') | ConvertFrom-Json)
foreach ($taskEntry in $taskManifest) {
    if ([IO.Path]::GetFileName($taskEntry.Name) -ne $taskEntry.Name -or $taskEntry.Name -notmatch '\.exe$') { throw 'Invalid profile filename in backup manifest.' }
    $taskDestination = [IO.Path]::GetFullPath((Join-Path $taskProfilesRoot $taskEntry.Name))
    if (-not $taskDestination.StartsWith($taskProfilesRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Profile path is outside RTSS Profiles.' }
    if ($taskEntry.PreviouslyExisted) {
        Copy-Item -LiteralPath (Join-Path $taskBackupRoot $taskEntry.Name) -Destination $taskDestination -Force
    } elseif (Test-Path -LiteralPath $taskDestination) {
        Remove-Item -LiteralPath $taskDestination
    }
}
Write-Output 'Game-specific OSD profiles restored. Restart RTSS and the game to apply.'
