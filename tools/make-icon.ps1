# Renders Melora's icon (a dark tile with three accent bars) to src/Melora/Assets/Melora.ico.
# Each size is drawn separately and stored as PNG inside the .ico, so small sizes stay crisp.
#   pwsh tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$out = Join-Path $PSScriptRoot "..\src\Melora\Assets\Melora.ico"
$tile = [System.Drawing.Color]::FromArgb(255, 0x1E, 0x1E, 0x26)
$accent = [System.Drawing.Color]::FromArgb(255, 0x9B, 0xB0, 0xFF)

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $g.FillPath((New-Object System.Drawing.SolidBrush $tile), (RoundedRect 0 0 $s $s ($s * 0.22)))

    # Three bars, like a level meter: medium, tall, short.
    $barW = $s * 0.14; $gap = $s * 0.09
    $left = ($s - (3 * $barW + 2 * $gap)) / 2
    $bottom = $s * 0.76
    $brush = New-Object System.Drawing.SolidBrush $accent
    $i = 0
    foreach ($h in 0.34, 0.52, 0.24) {
        $bh = $s * $h
        $g.FillPath($brush, (RoundedRect ($left + $i * ($barW + $gap)) ($bottom - $bh) $barW $bh ($barW / 2)))
        $i++
    }
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , @($s, $ms.ToArray())
}

# ICO container: header, one directory entry per size, then the PNG data.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
$w.Dispose()
"Wrote $out ($((Get-Item $out).Length) bytes)"
