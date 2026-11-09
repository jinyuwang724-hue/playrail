$ErrorActionPreference = 'Stop'
$backupPath = Join-Path $PSScriptRoot 'steam-default-admin-before.json'
$backup = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
$expectedKey = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
$expectedExe = 'C:\Software\steam.exe'
if ($backup.RegistryKey -ne $expectedKey -or $backup.Executable -ine $expectedExe) {
    throw '备份内容与本次 Steam 设置不符，未修改注册表。'
}
if ($backup.OriginalValueExisted) {
    if (-not (Test-Path -LiteralPath $expectedKey)) { New-Item -Path $expectedKey -Force | Out-Null }
    New-ItemProperty -LiteralPath $expectedKey -Name $backup.OriginalName -Value $backup.OriginalValue -PropertyType String -Force | Out-Null
} elseif (Test-Path -LiteralPath $expectedKey) {
    Remove-ItemProperty -LiteralPath $expectedKey -Name $expectedExe -ErrorAction SilentlyContinue
}
Write-Output '已恢复 Steam 原来的默认启动权限；下次完全退出并重新启动 Steam 后生效。'
