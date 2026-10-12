$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskSetup = Join-Path $taskWorkspace 'artifacts\css-loader-setup'
$taskPluginRoot = Join-Path $env:USERPROFILE 'homebrew\plugins'
$taskThemeRoot = Join-Path $env:USERPROFILE 'homebrew\themes'
$taskDestination = Join-Path $taskPluginRoot 'SDH-CssLoader'
$taskZip = Join-Path $taskSetup 'SDH-CSSLoader-Decky-v2.1.2.zip'
$taskPinnedHash = '0105b29cba25eacbfb8574b8a72e24b82cd1a8fb1ea6a9b3460597c9583e79ed'
$taskPlayhubRoot = 'C:\Software\Playhub'
$taskThemeZip = Join-Path $taskPlayhubRoot 'Assets\CssLoaderThemes\themes.zip'
$taskManifest = Join-Path $env:APPDATA 'Playhub\playhub-css-themes.txt'
$taskBackup = Join-Path $taskSetup ('backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$taskStage = Join-Path $taskSetup ('stage-' + [guid]::NewGuid().ToString('N'))

function Test-TaskArchive([string] $archivePath, [string] $extractRoot) {
    $taskBoundary = [IO.Path]::GetFullPath($extractRoot).TrimEnd('\') + '\'
    $taskArchive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($taskArchive.Entries.Count -gt 10000) { throw 'Too many archive entries.' }
        $taskExpanded = 0L
        foreach ($taskEntry in $taskArchive.Entries) {
            $taskEntryTarget = [IO.Path]::GetFullPath((Join-Path $extractRoot $taskEntry.FullName))
            if (-not $taskEntryTarget.StartsWith($taskBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escapes the destination.' }
            if ((($taskEntry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Archive contains a symbolic link.' }
            $taskExpanded += $taskEntry.Length
        }
        if ($taskExpanded -gt 250MB) { throw 'Expanded archive is too large.' }
    } finally { $taskArchive.Dispose() }
}

if ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskPinnedHash) { throw 'Official package checksum does not match Playhub.' }
if (Test-Path -LiteralPath $taskDestination) { throw 'CSS Loader is already present; inspect before replacing it.' }
New-Item -ItemType Directory -Path $taskStage, $taskBackup -Force | Out-Null
Test-TaskArchive $taskZip $taskStage
Test-TaskArchive $taskThemeZip $taskThemeRoot
Expand-Archive -LiteralPath $taskZip -DestinationPath $taskStage
$taskStagedPlugin = Join-Path $taskStage 'SDH-CssLoader'
$taskOriginalHookHash = (Get-FileHash -LiteralPath (Join-Path $taskStagedPlugin 'css_browserhook.py')).Hash.ToLowerInvariant()
$taskAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $taskPlayhubRoot 'Playhub.dll'))
$taskType = $taskAssembly.GetType('Playhub.Services.CssLoaderInstallService', $true)
$taskPatch = $taskType.GetMethod('ApplyWindowsBrowserHookFix', [Reflection.BindingFlags]'NonPublic,Static')
if ($null -eq $taskPatch) { throw 'Installed Playhub Windows compatibility routine is unavailable.' }
$taskIL = $taskPatch.GetMethodBody().GetILAsByteArray()
$taskLiterals = [Collections.Generic.List[string]]::new()
for ($taskOffset = 0; $taskOffset -lt $taskIL.Length - 4; $taskOffset++) {
    if ($taskIL[$taskOffset] -eq 0x72 -and $taskIL[$taskOffset + 4] -eq 0x70) {
        try { $taskLiterals.Add($taskPatch.Module.ResolveString([BitConverter]::ToInt32($taskIL, $taskOffset + 1))) } catch { }
    }
}
$taskLiteralPath = Join-Path $taskSetup 'playhub-browser-hook-literals.json'
$taskLiterals | ConvertTo-Json | Set-Content -LiteralPath $taskLiteralPath -Encoding utf8
& node (Join-Path $PSScriptRoot 'patch-playhub-css-loader.mjs') (Join-Path $taskStagedPlugin 'css_browserhook.py') $taskLiteralPath
if ($LASTEXITCODE -ne 0) { throw 'Playhub compatibility patch failed.' }
& node (Join-Path $PSScriptRoot 'patch-css-loader-utf8.mjs') $taskStagedPlugin
if ($LASTEXITCODE -ne 0) { throw 'Windows theme encoding patch failed.' }
$taskPackage = Get-Content -LiteralPath (Join-Path $taskStagedPlugin 'package.json') -Raw | ConvertFrom-Json
$taskPlugin = Get-Content -LiteralPath (Join-Path $taskStagedPlugin 'plugin.json') -Raw | ConvertFrom-Json
if ($taskPackage.version -ne '2.1.2' -or $taskPlugin.name -ne 'CSS Loader' -or -not (Test-Path -LiteralPath (Join-Path $taskStagedPlugin 'dist\index.js'))) { throw 'Staged plugin verification failed.' }

$taskThemes = [IO.Compression.ZipFile]::OpenRead($taskThemeZip)
try {
    $taskThemeNames = @($taskThemes.Entries | ForEach-Object { ($_.FullName.Replace('\', '/') -split '/')[0] } | Where-Object { $_ } | Sort-Object -Unique)
} finally { $taskThemes.Dispose() }
foreach ($taskThemeName in $taskThemeNames) {
    $taskExistingTheme = Join-Path $taskThemeRoot $taskThemeName
    if (Test-Path -LiteralPath $taskExistingTheme) {
        $taskThemeBackup = Join-Path $taskBackup 'themes'
        New-Item -ItemType Directory -Path $taskThemeBackup -Force | Out-Null
        Copy-Item -LiteralPath $taskExistingTheme -Destination $taskThemeBackup -Recurse
    }
}
if (Test-Path -LiteralPath $taskManifest) { Copy-Item -LiteralPath $taskManifest -Destination (Join-Path $taskBackup 'playhub-css-themes.txt') }
New-Item -ItemType Directory -Path $taskPluginRoot, $taskThemeRoot -Force | Out-Null
$taskResolvedSource = [IO.Path]::GetFullPath($taskStagedPlugin)
$taskWorkspaceBoundary = [IO.Path]::GetFullPath($taskSetup).TrimEnd('\') + '\'
$taskResolvedDestination = [IO.Path]::GetFullPath($taskDestination)
$taskPluginBoundary = [IO.Path]::GetFullPath($taskPluginRoot).TrimEnd('\') + '\'
if (-not $taskResolvedSource.StartsWith($taskWorkspaceBoundary, [StringComparison]::OrdinalIgnoreCase) -or
    -not $taskResolvedDestination.StartsWith($taskPluginBoundary, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($taskResolvedDestination) -ne 'SDH-CssLoader') { throw 'Plugin move boundaries are invalid.' }
Move-Item -LiteralPath $taskResolvedSource -Destination $taskResolvedDestination
[IO.Compression.ZipFile]::ExtractToDirectory($taskThemeZip, $taskThemeRoot, $true)
[IO.File]::WriteAllLines($taskManifest, [string[]] $taskThemeNames, [Text.UTF8Encoding]::new($false))
$taskService = [Activator]::CreateInstance($taskType)
$taskStatus = $taskService.GetStatus($taskPluginRoot)
if (-not $taskStatus.Installed -or $taskStatus.Version -ne '2.1.2') { throw 'Installed plugin verification failed.' }
$taskReport = [ordered]@{
    InstalledAt = (Get-Date).ToString('o')
    PackageUrl = 'https://github.com/DeckThemes/SDH-CssLoader/releases/download/v2.1.2/SDH-CSSLoader-Decky.zip'
    PackageSha256 = $taskPinnedHash
    PlayhubAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $taskPlayhubRoot 'Playhub.dll')).Hash.ToLowerInvariant()
    OriginalBrowserHookSha256 = $taskOriginalHookHash
    PatchedBrowserHookSha256 = (Get-FileHash -LiteralPath (Join-Path $taskDestination 'css_browserhook.py')).Hash.ToLowerInvariant()
    PluginPath = $taskDestination
    ThemeCount = $taskThemeNames.Count
    ThemeNames = $taskThemeNames
    ThemeManifest = $taskManifest
    BackupPath = $taskBackup
    ActiveProfile = 'Playhub'
}
$taskReport | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskSetup 'install-report.json') -Encoding utf8
[pscustomobject]@{Installed=$taskStatus.Installed;Version=$taskStatus.Version;ThemeCount=$taskThemeNames.Count;PluginPath=$taskDestination;BackupPath=$taskBackup} | ConvertTo-Json
