# Generates assets/Drowsy.ico (PNG-compressed, multi-size) using System.Drawing.
# Run with Windows PowerShell 5.1:  powershell -File assets/make-icon.ps1
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square background
    $r = [math]::Max(2, [int]($s * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($s - 1 - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - 1 - $r * 2, $s - 1 - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $s - 1 - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $bg = [System.Drawing.Color]::FromArgb(255, 30, 42, 74)
    $g.FillPath((New-Object System.Drawing.SolidBrush $bg), $path)

    # Crescent moon: full disc, then a background-coloured disc offset up-right
    $moon = [System.Drawing.Color]::FromArgb(255, 245, 197, 66)
    $d = $s * 0.62; $x = $s * 0.16; $y = $s * 0.20
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $moon), [single]$x, [single]$y, [single]$d, [single]$d)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $bg), [single]($x + $d * 0.36), [single]($y - $d * 0.14), [single]($d * 0.86), [single]($d * 0.86))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = Join-Path $PSScriptRoot 'Drowsy.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose()
"Wrote $out"
