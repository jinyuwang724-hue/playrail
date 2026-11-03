param([string]$Directory = (Join-Path $PSScriptRoot '..\artifacts\enhanced-sleep\final'), [switch]$SkipUi, [switch]$SkipInput)
$ErrorActionPreference = 'Stop'
$taskOutput = [IO.Path]::GetFullPath($Directory)
$taskSource = Join-Path $taskOutput 'src'
$taskIntegration = Get-Content -Raw -LiteralPath (Join-Path $taskOutput 'integration.json') | ConvertFrom-Json
& dotnet publish (Join-Path $taskSource 'native\SleepAnimation\ClawSleepAnimation.csproj') -c Release -p:Platform=x64 -r win-x64 --self-contained true -o (Join-Path $taskOutput 'app') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Enhanced sleep build failed.' }
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskWinmd = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata" -Directory | Where-Object Name -Match '^10\.' | Sort-Object { [version]$_.Name } | Select-Object -Last 1 | ForEach-Object { Join-Path $_.FullName 'Windows.winmd' }
$taskBin = Join-Path $taskSource 'plugin\bin'
[IO.Directory]::CreateDirectory($taskBin) | Out-Null
$taskInput = Join-Path $taskBin 'ClawInput-0.17.58-enhanced-sleep.exe'
if($SkipInput){
    Copy-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'ClawToolbox\app\bin\ClawInput-0.17.58-enhanced-sleep.exe') -Destination $taskInput -Force
}else{
    & $taskCompiler /nologo /codepage:65001 /optimize+ /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Runtime.dll /reference:System.Numerics.dll /reference:System.Numerics.Vectors.dll /reference:System.Runtime.WindowsRuntime.dll ("/reference:"+$taskWinmd) ("/out:"+$taskInput) (Get-ChildItem -LiteralPath (Join-Path $taskSource 'native\InputLayer') -Filter *.cs | ForEach-Object FullName)
    if ($LASTEXITCODE -ne 0) { throw 'Sleep-aware input engine build failed.' }
}
$taskInputBaseline = Join-Path $env:LOCALAPPDATA 'ClawToolbox\app\bin\ClawInput-0.17.58-enhanced-sleep.exe'
$taskInputHash = if(Test-Path -LiteralPath $taskInputBaseline){(Get-FileHash $taskInputBaseline).Hash.ToLowerInvariant()}else{$null}
$taskInputChange = [pscustomobject]@{ baseSha256=$taskInputHash; candidateSha256=(Get-FileHash $taskInput).Hash.ToLowerInvariant() }
$taskIntegration.changes.PSObject.Properties.Remove('plugin/bin/ClawInput-0.17.58.exe')
$taskIntegration.changes | Add-Member -NotePropertyName 'plugin/bin/ClawInput-0.17.58-enhanced-sleep.exe' -NotePropertyValue $taskInputChange -Force
$taskIntegration | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $taskOutput 'integration.json') -Encoding utf8

# Keep the complete existing OOBE payload, updating just this feature's files.
$taskPayload = Join-Path $taskSource 'frontend\library-native\ClawLibrary\Payload\agent\agent.zip'
$taskPython = Join-Path (Split-Path -Parent (Get-Content -Raw -LiteralPath (Join-Path $env:LOCALAPPDATA 'ClawToolbox\current.json') | ConvertFrom-Json).python) 'python.exe'
& $taskPython (Join-Path $PSScriptRoot 'Pack-EnhancedSleepPayload.py') $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Sleep payload packaging failed.' }
if (-not $SkipUi) {
    & (Join-Path $taskSource 'frontend\library-native\tools\Build-Msix.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Merged library build failed.' }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-SleepAnimation.ps1') -Destination (Join-Path $taskOutput 'Install-SleepAnimation.ps1') -Force
Write-Output $taskOutput
