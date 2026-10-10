# Generates src\Korty.App\Assets\korty.ico (multi-size, PNG-compressed) and a 512 px preview PNG. Pure System.Drawing, no downloads.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Korty.App\Assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = $size * 0.04; $r = $size * 0.22; $w = $size - 2 * $pad
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $r, $r, 180, 90); $path.AddArc($pad + $w - $r, $pad, $r, $r, 270, 90)
    $path.AddArc($pad + $w - $r, $pad + $w - $r, $r, $r, 0, 90); $path.AddArc($pad, $pad + $w - $r, $r, $r, 90, 90); $path.CloseFigure()
    $bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new([single]$size, [single]$size), [System.Drawing.Color]::FromArgb(255, 32, 35, 43), [System.Drawing.Color]::FromArgb(255, 8, 9, 11))
    $g.FillPath($bg, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 90, 31)), ([single][Math]::Max(1, $size * 0.035))
    $g.DrawPath($pen, $path)
    # keycap row (RGB glow hint)
    $kw = $size * 0.16; $kh = $size * 0.10; $gap = $size * 0.045; $x0 = ($size - (3 * $kw + 2 * $gap)) / 2; $y0 = $size * 0.72
    $cols = @([System.Drawing.Color]::FromArgb(255, 255, 90, 31), [System.Drawing.Color]::FromArgb(255, 255, 176, 32), [System.Drawing.Color]::FromArgb(255, 61, 220, 151))
    for ($i = 0; $i -lt 3; $i++) {
        $b = New-Object System.Drawing.SolidBrush $cols[$i]
        $g.FillRectangle($b, [single]($x0 + $i * ($kw + $gap)), [single]$y0, [single]$kw, [single]$kh); $b.Dispose()
    }
    # letter K
    $font = New-Object System.Drawing.Font 'Segoe UI', ([single]($size * 0.50)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $tb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.DrawString('K', $font, $tb, (New-Object System.Drawing.RectangleF 0, ([single]($size * 0.02)), $size, ([single]($size * 0.70))), $sf)
    $g.Dispose(); $bg.Dispose(); $pen.Dispose(); $font.Dispose(); $tb.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) { $ms = New-Object System.IO.MemoryStream; (New-Frame $s).Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += , $ms.ToArray(); $ms.Dispose() }
$ico = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset); $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush(); [IO.File]::WriteAllBytes((Join-Path $OutDir 'korty.ico'), $ico.ToArray())
(New-Frame 512).Save((Join-Path $OutDir 'korty-512.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"icon written: $(Join-Path $OutDir 'korty.ico')"
