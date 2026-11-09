# Side-by-side (or stacked) comparison of two screenshots, optionally cropped: web left/top, native right/bottom.
param([Parameter(Mandatory)][string]$Web, [Parameter(Mandatory)][string]$Native, [Parameter(Mandatory)][string]$Path,
      [int]$X = 0, [int]$Y = 0, [int]$W = 0, [int]$H = 0, [switch]$Stack, [string]$LabelA = 'web', [string]$LabelB = 'native')
Add-Type -AssemblyName System.Drawing
$a = [System.Drawing.Image]::FromFile($Web); $b = [System.Drawing.Image]::FromFile($Native)
if ($W -le 0) { $W = $a.Width - $X }; if ($H -le 0) { $H = $a.Height - $Y }
$gap = 8
$outW = if ($Stack) { $W } else { $W * 2 + $gap }
$outH = if ($Stack) { $H * 2 + $gap } else { $H }
$bmp = New-Object System.Drawing.Bitmap $outW, $outH
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Magenta)
$src = New-Object System.Drawing.Rectangle $X, $Y, $W, $H
$g.DrawImage($a, (New-Object System.Drawing.Rectangle 0, 0, $W, $H), $src, [System.Drawing.GraphicsUnit]::Pixel)
$bx = if ($Stack) { 0 } else { $W + $gap }; $by = if ($Stack) { $H + $gap } else { 0 }
$g.DrawImage($b, (New-Object System.Drawing.Rectangle $bx, $by, $W, $H), $src, [System.Drawing.GraphicsUnit]::Pixel)
$font = New-Object System.Drawing.Font 'Segoe UI', 14, ([System.Drawing.FontStyle]::Bold)
$g.FillRectangle([System.Drawing.Brushes]::Black, 0, 0, 90, 26); $g.DrawString($LabelA, $font, [System.Drawing.Brushes]::Yellow, 4, 2)
$g.FillRectangle([System.Drawing.Brushes]::Black, $bx, $by, 90, 26); $g.DrawString($LabelB, $font, [System.Drawing.Brushes]::Yellow, $bx + 4, $by + 2)
$bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $a.Dispose(); $b.Dispose()
"$Path ${outW}x${outH}"
