# Capture the whole primary screen in physical pixels (declares per-monitor DPI awareness first,
# otherwise only the top-left DIP-sized block is captured).
param([Parameter(Mandatory)][string]$Path)
if (-not ('DpiCap' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DpiCap {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
'@
}
[void][DpiCap]::SetProcessDpiAwarenessContext([IntPtr]-4)
Add-Type -AssemblyName System.Drawing
$w = [DpiCap]::GetSystemMetrics(0); $h = [DpiCap]::GetSystemMetrics(1)
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size $w, $h))
$bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"$Path ${w}x${h}"
