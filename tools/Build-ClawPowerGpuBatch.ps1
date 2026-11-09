param(
    [Parameter(Mandatory)][string]$Original,
    [Parameter(Mandatory)][string]$Output
)

# The deployed r30 has features absent from the main checkout's ClawPower.cs.
# Apply the sampler to that exact binary while retaining its GPU aggregation
# and every unrelated executor method. The regular source build uses the same sampler.
$ErrorActionPreference = 'Stop'
$originalPath = (Resolve-Path -LiteralPath $Original).Path
$outputPath = [IO.Path]::GetFullPath($Output)
if ($outputPath -eq $originalPath) { throw 'Choose a separate output executable.' }
if ((Get-FileHash -LiteralPath $originalPath).Hash -ne '852143A089834F0CF7FCC2843FDB2F9903DC603672DF4480D62CFE03EC94154C') {
    throw 'The original is not the supported ClawPower r30 build.'
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64\ildasm.exe'
$source = Join-Path $PSScriptRoot '..\native\ClawGpuEngineSampler.cs'
$tmpRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$work = Join-Path $tmpRoot ('claw-gpu-batch-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($work)

function Replace-Block([string]$Text, [string]$Start, [string]$End, [string]$Replacement) {
    $startIndex = $Text.IndexOf($Start, [StringComparison]::Ordinal)
    $endIndex = if ($startIndex -ge 0) { $Text.IndexOf($End, $startIndex + $Start.Length, [StringComparison]::Ordinal) } else { -1 }
    if ($startIndex -lt 0 -or $endIndex -lt 0) { throw "Unexpected r30 IL near $Start" }
    return $Text.Substring(0, $startIndex) + $Replacement + $Text.Substring($endIndex)
}

try {
    $sampler = Join-Path $work 'sampler.dll'
    $samplerIl = Join-Path $work 'sampler.il'
    $originalIl = Join-Path $work 'original.il'
    $updatedIl = Join-Path $work 'updated.il'
    $candidate = Join-Path $work 'ClawPower-0.17.58-r31.exe'
    & (Join-Path $framework 'csc.exe') /nologo /optimize+ /target:library "/out:$sampler" $source
    if ($LASTEXITCODE) { throw 'GPU sampler compilation failed.' }
    & $sdk /text /utf8 /nobar "/out=$samplerIl" $sampler
    if ($LASTEXITCODE) { throw 'GPU sampler disassembly failed.' }
    & $sdk /text /utf8 /nobar "/out=$originalIl" $originalPath
    if ($LASTEXITCODE) { throw 'r30 disassembly failed.' }

    $text = [IO.File]::ReadAllText($originalIl).Replace("`r`n", "`n")
    $donor = [IO.File]::ReadAllText($samplerIl).Replace("`r`n", "`n")
    $samplerType = [regex]::Match($donor, '(?ms)^\.class[^\n]*\bClawGpuEngineSampler\n.*?^} // end of class ClawGpuEngineSampler').Value
    $stats = [regex]::Match($text, '(?ms)^\.class[^\n]*\bSystemStats\n.*?^} // end of class SystemStats').Value
    if (-not $stats -or -not $samplerType) { throw 'Could not identify the two types.' }
    $updated = [regex]::Replace($stats, '(?m)^  \.field private .* (gpuEngines|gpuPrimed)\n', '')
    $updated = $updated.Replace('  .field private valuetype SystemStats/ProcessorTimes[] previousCores', "  .field private initonly class ClawGpuEngineSampler gpuBatch`n  .field private valuetype SystemStats/ProcessorTimes[] previousCores")
    $oldInit = "    IL_000b:  ldarg.0`n    IL_000c:  call       class [mscorlib]System.StringComparer"
    $newInit = @'
    IL_000b:  ldarg.0
    newobj instance void ClawGpuEngineSampler::.ctor()
    stfld class ClawGpuEngineSampler SystemStats::gpuBatch
'@
    $updated = Replace-Block $updated $oldInit '    IL_002b:  ldarg.0' ($newInit + "`n")
    $gpuEnumeration = "    .try`n    {`n      IL_004c:  ldstr      `"GPU Engine`""
    $memoryEnumeration = "    .try`n    {`n      IL_0181:  ldstr      `"GPU Adapter Memory`""
    $updated = Replace-Block $updated $gpuEnumeration $memoryEnumeration ''
    $newRead = @'
    IL_01a9:  ldarg.0
    ldfld class ClawGpuEngineSampler SystemStats::gpuBatch
    callvirt instance class [mscorlib]System.Collections.Generic.List`1<valuetype [mscorlib]System.Collections.Generic.KeyValuePair`2<string,float64>> ClawGpuEngineSampler::Read()
'@
    $updated = Replace-Block $updated '    IL_01a9:  newobj' '    IL_0216:  call       float64 SystemStats::GpuBusy' ($newRead + "`n")
    if ($updated -match 'SystemStats::(gpuEngines|gpuPrimed)') { throw 'Legacy per-instance GPU reads are still present.' }
    $text = $text.Replace($stats, $updated + "`n`n" + $samplerType)
    $text = $text.Replace(".assembly 'ClawPower-0.17.58-r30'", ".assembly 'ClawPower-0.17.58-r31'").Replace(".module 'ClawPower-0.17.58-r30.exe'", ".module 'ClawPower-0.17.58-r31.exe'")
    $text = $text.Replace('ldc.r8     -nan(ind)', 'ldc.r8     (00 00 00 00 00 00 F8 FF)')
    [IO.File]::WriteAllText($updatedIl, $text, [Text.UTF8Encoding]::new($true))
    $options = @('/nologo', '/quiet', '/exe', '/pe64', '/x64', "/output=$candidate")
    $resource = Join-Path $work 'original.res'
    if (Test-Path -LiteralPath $resource) { $options += "/resource=$resource" }
    & (Join-Path $framework 'ilasm.exe') @options $updatedIl
    if ($LASTEXITCODE) { throw 'Updated ClawPower assembly failed.' }
    $null = [IO.Directory]::CreateDirectory((Split-Path -Parent $outputPath))
    Copy-Item -LiteralPath $candidate -Destination $outputPath -Force
    Get-Item -LiteralPath $outputPath
} finally {
    $resolved = (Resolve-Path -LiteralPath $work).Path
    if ((Split-Path -Parent $resolved) -eq $tmpRoot -and (Split-Path -Leaf $resolved) -match '^claw-gpu-batch-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
