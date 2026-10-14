# Burst-capture the screen (physical pixels) N times as fast as possible, cropped to a region; saves numbered PNGs.
param([Parameter(Mandatory)][string]$Dir, [int]$Count = 20, [int]$X = 0, [int]$Y = 0, [int]$W = 1920, [int]$H = 1200, [int]$DelayMs = 0)
if (-not ('DpiCap2' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DpiCap2 { [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v); }
'@
}
[void][DpiCap2]::SetProcessDpiAwarenessContext([IntPtr]-4)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Dir | Out-Null
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$frames = @()
for ($i = 0; $i -lt $Count; $i++) {
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size $W, $H))
    $g.Dispose()
    $frames += ,@($bmp, $sw.ElapsedMilliseconds)
    if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
}
$i = 0
foreach ($f in $frames) { $f[0].Save((Join-Path $Dir ("f{0:D2}_{1}ms.png" -f $i, $f[1])), [System.Drawing.Imaging.ImageFormat]::Png); $f[0].Dispose(); $i++ }
"$Count frames in $($sw.ElapsedMilliseconds) ms"
