$ErrorActionPreference = 'Stop'
$steamExe = 'C:\Software\steam.exe'
$compatKey = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
$backupPath = Join-Path $PSScriptRoot 'steam-default-admin-before.json'
if (-not (Test-Path -LiteralPath $steamExe)) { throw 'Steam 主程序不存在。' }
$keyExisted = Test-Path -LiteralPath $compatKey
$oldProperty = if ($keyExisted) {
    (Get-ItemProperty -LiteralPath $compatKey).PSObject.Properties |
        Where-Object { $_.Name -ieq $steamExe } | Select-Object -First 1
}
if (-not (Test-Path -LiteralPath $backupPath)) {
    [ordered]@{
        SavedAt = (Get-Date).ToString('o')
        RegistryKey = $compatKey
        Executable = $steamExe
        OriginalKeyExisted = [bool]$keyExisted
        OriginalValueExisted = [bool]$oldProperty
        OriginalName = if ($oldProperty) { $oldProperty.Name } else { $steamExe }
        OriginalValue = if ($oldProperty) { [string]$oldProperty.Value } else { $null }
    } | ConvertTo-Json | Set-Content -LiteralPath $backupPath -Encoding utf8
}
$flags = if ($oldProperty) { [string]$oldProperty.Value } else { '~' }
if (($flags -split '\s+') -notcontains 'RUNASADMIN') { $flags = ($flags.Trim() + ' RUNASADMIN').Trim() }
if (-not $keyExisted) { New-Item -Path $compatKey -Force | Out-Null }
New-ItemProperty -LiteralPath $compatKey -Name $steamExe -Value $flags -PropertyType String -Force | Out-Null
$saved = Get-ItemPropertyValue -LiteralPath $compatKey -Name $steamExe
if (($saved -split '\s+') -notcontains 'RUNASADMIN') { throw '未能确认管理员兼容性设置。' }
[pscustomobject]@{ Executable = $steamExe; DefaultAdmin = $true; Flags = $saved; OriginalSettingsBackup = $backupPath } | ConvertTo-Json
