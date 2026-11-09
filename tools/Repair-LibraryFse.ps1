# Repackage the currently installed library with its missing FSE declarations.
# Preserve the installed executable/resources (including changes from other worktrees).
param([switch]$Install, [string]$Version = '0.1.68.0')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPackage = Get-AppxPackage ClawToolbox.Library
if (-not $taskPackage) { throw 'Claw library is not installed.' }
if ([version]$Version -le [version]$taskPackage.Version) { throw 'Use a version above the currently installed library.' }
$taskCert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=ClawToolbox' | Select-Object -First 1
if (-not $taskCert) { throw 'Existing ClawToolbox certificate is missing.' }
$taskSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64'
$taskOut = Join-Path $taskRoot ('artifacts\library-fse-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$taskStage = Join-Path $taskOut 'package'
[IO.Directory]::CreateDirectory($taskStage) | Out-Null
$taskExcluded = @('AppxSignature.p7x','AppxBlockMap.xml','[Content_Types].xml','AppxMetadata')
Get-ChildItem -LiteralPath $taskPackage.InstallLocation -Force | Where-Object Name -notin $taskExcluded |
    Copy-Item -Destination $taskStage -Recurse
$taskManifestPath = Join-Path $taskStage 'AppxManifest.xml'
Copy-Item -LiteralPath $taskManifestPath -Destination (Join-Path $taskOut 'original-AppxManifest.xml')
$taskManifest = [xml](Get-Content -LiteralPath $taskManifestPath -Raw)
$taskNs3 = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/3'
$taskNs4 = 'http://schemas.microsoft.com/appx/manifest/uap/windows10/4'
$taskCapability = 'Microsoft.appCategory.gamingHome_8wekyb3d8bbwe'
$taskManifest.Package.SetAttribute('xmlns:uap3', $taskNs3)
$taskManifest.Package.SetAttribute('xmlns:uap4', $taskNs4)
$taskManifest.Package.SetAttribute('IgnorableNamespaces', (($taskManifest.Package.IgnorableNamespaces.Split(' ') + @('uap3','uap4') | Select-Object -Unique) -join ' '))
$taskManifest.Package.Identity.Version = $Version
$taskApp = @($taskManifest.Package.Applications.Application) | Where-Object Id -eq 'App'
if (-not $taskApp) { throw 'Expected library application identity was not found.' }
if (-not (@($taskApp.Extensions.ChildNodes | ForEach-Object { $_.AppExtension.Name }) -contains 'windows.gamingApp')) {
    $taskExtension = $taskManifest.CreateElement('uap3','Extension',$taskNs3)
    $taskExtension.SetAttribute('Category','windows.appExtension')
    $taskHome = $taskManifest.CreateElement('uap3','AppExtension',$taskNs3)
    foreach ($taskPair in @{Name='windows.gamingApp';Id='App';DisplayName='Claw 游戏库';Description='Claw 全屏游戏库';PublicFolder='Assets'}.GetEnumerator()) {
        $taskHome.SetAttribute($taskPair.Key,$taskPair.Value)
    }
    [void]$taskExtension.AppendChild($taskHome)
    [void]$taskApp.Extensions.AppendChild($taskExtension)
}
if (-not (@($taskManifest.Package.Capabilities.CustomCapability.Name) -contains $taskCapability)) {
    $taskCustom = $taskManifest.CreateElement('uap4','CustomCapability',$taskNs4)
    $taskCustom.SetAttribute('Name',$taskCapability)
    [void]$taskManifest.Package.Capabilities.AppendChild($taskCustom)
}
foreach ($taskComment in @($taskManifest.SelectNodes('//comment()'))) {
    if ($taskComment.Value -match '暂不注册为 Xbox 全屏主页') { [void]$taskComment.ParentNode.RemoveChild($taskComment) }
}
$taskManifest.Save($taskManifestPath)
Copy-Item -LiteralPath (Join-Path $taskRoot 'frontend\library-native\ClawLibrary\CustomCapability.SCCD') -Destination $taskStage
$taskExeHash = (Get-FileHash -LiteralPath (Join-Path $taskPackage.InstallLocation 'ClawLibrary.exe')).Hash
$taskMsix = Join-Path $taskOut ('ClawLibrary_'+$Version+'_x64.msix')
& (Join-Path $taskSdk 'makeappx.exe') pack /d $taskStage /p $taskMsix /o | Out-File (Join-Path $taskOut 'pack.log')
if ($LASTEXITCODE) { throw ('Package validation failed: '+$taskOut) }
& (Join-Path $taskSdk 'signtool.exe') sign /fd SHA256 /sha1 $taskCert.Thumbprint $taskMsix | Out-File (Join-Path $taskOut 'sign.log')
if ($LASTEXITCODE) { throw 'Package signing failed.' }
$taskReport = @{previous=$taskPackage.PackageFullName;version=$Version;package=$taskMsix;executableHash=$taskExeHash;installed=$false}
if ($Install) {
    if ((Get-AppxPackage ClawToolbox.Library).PackageFullName -ne $taskPackage.PackageFullName) { throw 'Installed library changed during packaging; rebuild against the latest package.' }
    Add-AppxPackage -Path $taskMsix -ForceApplicationShutdown
    $taskNew = Get-AppxPackage ClawToolbox.Library
    if ($taskNew.Version.ToString() -ne $Version -or (Get-FileHash -LiteralPath (Join-Path $taskNew.InstallLocation 'ClawLibrary.exe')).Hash -ne $taskExeHash) {
        throw 'Installed version or executable does not match the repair.'
    }
    $taskReport.installed = $true
}
$taskReport | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOut 'deployment.json') -Encoding utf8
$taskReport | ConvertTo-Json
