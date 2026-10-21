# Export the approved monochrome “叠页” artwork for Windows and both app packages.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$brand = Join-Path $root 'assets\branding'
$source = [System.Drawing.Bitmap]::FromFile((Join-Path $brand 'claw-pages-master.png'))

function Save-IconPng([string]$Path, [int]$Width, [int]$Height, [int]$ArtSize) {
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(
            [int](($Width - $ArtSize) / 2), [int](($Height - $ArtSize) / 2), $ArtSize, $ArtSize))
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}

try {
    Save-IconPng (Join-Path $brand 'claw-pages-1024.png') 1024 1024 1024
    $sizes = @(16,20,24,32,40,48,64,128,256)
    $frames = @()
    foreach ($size in $sizes) {
        $path = Join-Path $brand ('claw-pages-{0}.png' -f $size)
        Save-IconPng $path $size $size $size
        $frames += ,([IO.File]::ReadAllBytes($path))
    }
    $stream = [IO.File]::Create((Join-Path $brand 'claw-pages.ico'))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    }
    finally { $writer.Dispose(); $stream.Dispose() }

    $assetDirs = @(
        (Join-Path $root 'frontend\library-native\ClawLibrary\Assets'),
        (Join-Path $root 'native\GameBarWidget\Assets')
    )
    foreach ($dir in $assetDirs) {
        foreach ($spec in @(@('Square44x44Logo',44), @('Square150x150Logo',150), @('StoreLogo',50))) {
            $name = $spec[0]; $baseSize = [int]$spec[1]
            Save-IconPng (Join-Path $dir ($name + '.png')) $baseSize $baseSize $baseSize
            foreach ($scale in @(100,125,150,200,400)) {
                $pixels = [int][math]::Round($baseSize * $scale / 100.0, [MidpointRounding]::AwayFromZero)
                Save-IconPng (Join-Path $dir ($name + '.scale-' + $scale + '.png')) $pixels $pixels $pixels
            }
        }
        foreach ($size in @(16,24,32,48,256)) {
            foreach ($suffix in @('', '_altform-unplated')) {
                Save-IconPng (Join-Path $dir ('Square44x44Logo.targetsize-' + $size + $suffix + '.png')) $size $size $size
            }
        }
        Save-IconPng (Join-Path $dir 'SplashScreen.png') 620 300 240
        foreach ($scale in @(100,125,150,200,400)) {
            Save-IconPng (Join-Path $dir ('SplashScreen.scale-' + $scale + '.png')) ([int](620*$scale/100)) ([int](300*$scale/100)) ([int](240*$scale/100))
        }
    }
    $library = $assetDirs[0]
    Save-IconPng (Join-Path $library 'Wide310x150Logo.png') 310 150 132
    foreach ($scale in @(100,125,150,200,400)) {
        Save-IconPng (Join-Path $library ('Wide310x150Logo.scale-' + $scale + '.png')) ([int][math]::Round(310*$scale/100.0,[MidpointRounding]::AwayFromZero)) ([int][math]::Round(150*$scale/100.0,[MidpointRounding]::AwayFromZero)) ([int][math]::Round(132*$scale/100.0,[MidpointRounding]::AwayFromZero))
    }
    Copy-Item -LiteralPath (Join-Path $brand 'claw-pages.ico') -Destination (Join-Path $library 'ClawIcon.ico') -Force
    # MSIX limits each logo to 200 KiB; use indexed PNG only when lossless compression is insufficient.
    $largePngs = @(Get-ChildItem -LiteralPath $assetDirs -File -Filter '*.png' | Where-Object Length -GT 190000)
    if ($largePngs.Count) {
        & node (Join-Path $PSScriptRoot 'Optimize-IconPng.mjs') @($largePngs.FullName)
        if ($LASTEXITCODE) { throw 'PNG optimization failed.' }
    }
}
finally { $source.Dispose() }
Write-Output 'Exported 叠页 branding for the library and Game Bar widget.'
