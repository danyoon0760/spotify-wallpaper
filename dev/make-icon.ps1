# Draws the app icon (host/app.ico). Re-run only when the design changes:
#   powershell -ExecutionPolicy Bypass -File dev\make-icon.ps1
# A vinyl record: black disc, light rim, a few grooves, red dot in the middle.
# (ASCII only on purpose: Windows PowerShell 5.1 misreads UTF-8 files without a BOM.)
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$out = Join-Path $PSScriptRoot '..\host\app.ico'

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0   # the design is drawn on a 32px grid and scaled

    $g.FillEllipse([System.Drawing.Brushes]::Black, 1 * $s, 1 * $s, 30 * $s, 30 * $s)
    $paper = [System.Drawing.Color]::FromArgb(242, 239, 233)
    $ring = New-Object System.Drawing.Pen $paper, (2.5 * $s)
    $g.DrawEllipse($ring, 2.25 * $s, 2.25 * $s, 27.5 * $s, 27.5 * $s)

    if ($size -ge 24) {
        # grooves, clipped to the inside of the disc
        $clip = New-Object System.Drawing.Drawing2D.GraphicsPath
        $clip.AddEllipse(5 * $s, 5 * $s, 22 * $s, 22 * $s)
        $g.SetClip($clip)
        $groove = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, $paper)), (1.2 * $s)
        foreach ($y in 9, 13, 19, 23) { $g.DrawLine($groove, 6 * $s, $y * $s, 26 * $s, $y * $s) }
        $g.ResetClip()
        $groove.Dispose(); $clip.Dispose()
    }

    $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 70, 90))
    $r = if ($size -ge 24) { 4.0 } else { 5.0 }
    $g.FillEllipse($dot, (16 - $r) * $s, (16 - $r) * $s, 2 * $r * $s, 2 * $r * $s)

    $ring.Dispose(); $dot.Dispose(); $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # 256px: stored as PNG (the standard for large icon frames)
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        # smaller frames: classic 32-bit DIB (BITMAPINFOHEADER, bottom-up BGRA rows, then an empty AND mask).
        # Older loaders, including System.Drawing, cannot read small PNG frames.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]($size * $size * 4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([math]::Ceiling($size / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $size)))
        $bw.Flush()
    }
    $bmp.Dispose()
    return , $ms.ToArray()
}

$images = foreach ($size in $sizes) { [pscustomobject]@{ Size = $size; Data = (New-IconPng $size) } }

# ICO file: header (6 bytes) + one 16-byte entry per image + image data
$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$img.Data.Length); $w.Write([uint32]$offset)
    $offset += $img.Data.Length
}
foreach ($img in $images) { $w.Write($img.Data) }
$w.Flush()
[System.IO.File]::WriteAllBytes($out, $file.ToArray())
"app.ico: $($images.Count) sizes, $([math]::Round($file.Length / 1KB, 1)) KB -> $out"
