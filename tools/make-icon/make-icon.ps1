<#
  mongdock app icon generator (no external tools/packages; System.Drawing only).

  Usage (repo root):
    powershell -ExecutionPolicy Bypass -File tools\make-icon\make-icon.ps1

  Output:
    src\mongdock\Assets\mongdock.ico   16,20,24,32,40,48,64,128 (32-bit BMP entries) + 256 (PNG entry)
    docs\icon-preview.png              all sizes side by side, on light and dark backgrounds (+ x4 zoom of 16-48)
    docs\icon.png                      256 px (README)
    (PNG per size are written to tools\make-icon\out\ for inspection; not committed)

  Design "mong cloud" (concept 1 in tools\make-icon\concepts): a soft white cloud with dot eyes and blush
  sitting on a translucent dock bar, sky-blue -> lavender squircle.
  - 64..256: detailed master on the macOS Big Sur grid (1024: plate 824, corner ~185, soft shadow), downscaled.
  - 16..48: drawn separately at the target size: plate fills the cell, simpler cloud, and the eyes/blush
    are placed on whole pixels so the face stays crisp.
#>
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function C([string]$hex, [int]$a = 255) {
    $h = $hex.TrimStart('#')
    [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($h.Substring(0, 2), 16), [Convert]::ToInt32($h.Substring(2, 2), 16), [Convert]::ToInt32($h.Substring(4, 2), 16))
}
function Brush($c) { New-Object System.Drawing.SolidBrush($c) }

$SkyTop    = C '8FD0FF'
$SkyBottom = C 'C6B6FF'
$Ink       = C '3A3768'
$Blush     = 'FF9DB5'

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2); $d = $r * 2
    if ($d -le 0) { $p.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h))); return $p }
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

# Squircle (continuous corner): superellipse corners as a polyline.
function New-Squircle([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $c = [Math]::Min($r * 1.25, [Math]::Min($w, $h) / 2); $n = 4.2; $steps = 24
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    $corners = @(@(($x + $w - $c), ($y + $c), 270), @(($x + $w - $c), ($y + $h - $c), 0), @(($x + $c), ($y + $h - $c), 90), @(($x + $c), ($y + $c), 180))
    foreach ($k in $corners) {
        for ($i = 0; $i -le $steps; $i++) {
            $t = ($k[2] + 90.0 * $i / $steps) * [Math]::PI / 180.0
            $ct = [Math]::Cos($t); $st = [Math]::Sin($t)
            $px = [Math]::Sign($ct) * [Math]::Pow([Math]::Abs($ct), 2.0 / $n) * $c
            $py = [Math]::Sign($st) * [Math]::Pow([Math]::Abs($st), 2.0 / $n) * $c
            $pts.Add((New-Object System.Drawing.PointF([float]($k[0] + $px), [float]($k[1] + $py))))
        }
    }
    $p.AddPolygon($pts.ToArray()); $p
}

function Set-Quality($g) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
}

function Fill-Ell($g, $c, [float]$cx, [float]$cy, [float]$rx, [float]$ry) {
    $b = Brush $c; $g.FillEllipse($b, $cx - $rx, $cy - $ry, 2 * $rx, 2 * $ry); $b.Dispose()
}
function Fill-RR($g, $c, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $b = Brush $c; $p = New-RoundRect $x $y $w $h $r; $g.FillPath($b, $p); $p.Dispose(); $b.Dispose()
}

# Cloud body in the 1024 design space. big = chunkier cloud for 16/20 px.
function New-Cloud([bool]$big) {
    $cloud = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cloud.FillMode = [System.Drawing.Drawing2D.FillMode]::Winding
    if ($big) {
        $cloud.AddEllipse(190, 390, 300, 300); $cloud.AddEllipse(360, 260, 360, 360); $cloud.AddEllipse(560, 400, 280, 280)
        $cloud.AddPath((New-RoundRect 190 520 650 240 120), $false)
    } else {
        $cloud.AddEllipse(250, 440, 250, 250); $cloud.AddEllipse(370, 320, 320, 320); $cloud.AddEllipse(560, 430, 230, 230)
        $cloud.AddPath((New-RoundRect 250 540 540 210 105), $false)
    }
    $cloud
}

# Plate + dock bar + cloud (no face) in the 1024 design space.
# level 0 = detailed, 1 = 40/48/32/24, 2 = 20/16
function Draw-Body($g, [int]$level) {
    $plate = New-Squircle 100 100 824 824 185
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.RectangleF(99, 99, 826, 826)), $SkyTop, $SkyBottom, [single]75)
    $grad.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    $g.FillPath($grad, $plate); $grad.Dispose()
    $g.SetClip($plate)
    switch ($level) {
        0 { Fill-RR $g (C 'FFFFFF' 110) 180 700 664 130 56 }
        1 { Fill-RR $g (C 'FFFFFF' 150) 150 690 724 150 70 }
        2 { Fill-RR $g (C 'FFFFFF' 170) 120 700 784 170 85 }
    }
    $cloud = New-Cloud ($level -eq 2)
    if ($level -eq 0) {
        $m = New-Object System.Drawing.Drawing2D.Matrix; $m.Translate(0, 14)
        $sh = $cloud.Clone(); $sh.Transform($m)
        $sb = Brush (C '7A6CD8' 60); $g.FillPath($sb, $sh); $sb.Dispose(); $sh.Dispose()
    }
    $wb = Brush (C 'FFFFFF'); $g.FillPath($wb, $cloud); $wb.Dispose()
    if ($level -eq 0) {
        # soft lavender belly shading
        $g.SetClip($cloud, [System.Drawing.Drawing2D.CombineMode]::Intersect)
        $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.RectangleF(0, 500, 1024, 260)), (C 'E9E4FF' 0), (C 'E2DCFF' 255), [single]90)
        $g.FillRectangle($lg, 0, 501, 1024, 258); $lg.Dispose()
        $g.SetClip($plate)
    }
    $cloud.Dispose()
    $g.ResetClip(); $plate.Dispose()
}

# ---------- detailed master (1024) ----------
function New-DetailedMaster {
    $S = 1024
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); Set-Quality $g
    $g.Clear([System.Drawing.Color]::Transparent)
    # soft shadow below the plate
    for ($i = 12; $i -ge 1; $i--) {
        $gr = $i * 2.2
        $sp = New-Squircle (100 - $gr) (114 - $gr + $i * 1.2) (824 + 2 * $gr) (824 + 2 * $gr) (185 + $gr)
        $sb = Brush (C '141432' 7); $g.FillPath($sb, $sp); $sb.Dispose(); $sp.Dispose()
    }
    Draw-Body $g 0
    Fill-Ell $g $Ink 458 600 17 21; Fill-Ell $g $Ink 590 600 17 21
    Fill-Ell $g (C $Blush 150) 400 650 36 22; Fill-Ell $g (C $Blush 150) 648 650 36 22
    $pp = New-Object System.Drawing.Pen($Ink, 12)
    $pp.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $pp.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($pp, 504, 618, 40, 30, 20, 140); $pp.Dispose()
    $g.Dispose()
    return $bmp
}

function Resize-Bitmap($src, [int]$size) {
    $cur = $src; $own = $false
    while ($cur.Width / 2 -ge $size * 2) {
        $half = [int]($cur.Width / 2)
        $nb = New-Object System.Drawing.Bitmap($half, $half, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($nb); Set-Quality $g
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($cur, 0, 0, $half, $half); $g.Dispose()
        if ($own) { $cur.Dispose() }
        $cur = $nb; $own = $true
    }
    $out = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($out); Set-Quality $g
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($cur, 0, 0, $size, $size); $g.Dispose()
    if ($own) { $cur.Dispose() }
    return $out
}

# ---------- small sizes (16..48), drawn at size ----------
# per size: plate margin, cloud level, eye w/h (px), eye half-distance & eye centre y (design units), blush on/off
$SmallSpec = @{
    16 = @{ M = 0; L = 2; EW = 2; EH = 2; DX = 120; EY = 570; Blush = $false }
    20 = @{ M = 0; L = 2; EW = 2; EH = 3; DX = 115; EY = 570; Blush = $false }
    24 = @{ M = 1; L = 1; EW = 2; EH = 3; DX = 85;  EY = 590; Blush = $false }
    32 = @{ M = 1; L = 1; EW = 2; EH = 3; DX = 80;  EY = 590; Blush = $true }
    40 = @{ M = 2; L = 1; EW = 3; EH = 4; DX = 72;  EY = 600; Blush = $true }
    48 = @{ M = 2; L = 1; EW = 3; EH = 4; DX = 70;  EY = 600; Blush = $true }
}

function New-Small([int]$s) {
    $sp = $SmallSpec[$s]
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); Set-Quality $g
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = ($s - 2 * $sp.M) / 824.0
    $st = $g.Save()
    $g.TranslateTransform($sp.M, $sp.M); $g.ScaleTransform($k, $k); $g.TranslateTransform(-100, -100)
    Draw-Body $g $sp.L
    $g.Restore($st)

    # face on whole pixels, symmetric around the centre
    $cx = $s / 2.0
    $dx = $sp.DX * $k
    $lx = [int][Math]::Round($cx - $dx - $sp.EW / 2.0)
    $rx = $s - $lx - $sp.EW
    $ey = [int][Math]::Round($sp.M + ($sp.EY - 100) * $k - $sp.EH / 2.0)
    $ib = Brush $Ink
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    foreach ($x in $lx, $rx) {
        # solid pixel block: anti-aliased dots blur or turn into "+" shapes at these sizes
        $g.FillRectangle($ib, $x, $ey, $sp.EW, $sp.EH)
    }
    Set-Quality $g
    $ib.Dispose()
    if ($sp.Blush) {
        $bw = [Math]::Max(2, [int][Math]::Round($s / 10.0)); $bh = [Math]::Max(1, [int][Math]::Round($s / 20.0))
        $by = $ey + $sp.EH + [Math]::Max(0, [int][Math]::Round($s / 48.0))
        $bb = Brush (C $Blush 200)
        $g.FillEllipse($bb, [float]($lx - $bw + 1), [float]$by, [float]$bw, [float]$bh)
        $g.FillEllipse($bb, [float]($rx + $sp.EW - 1), [float]$by, [float]$bw, [float]$bh)
        $bb.Dispose()
    }
    $g.Dispose()
    return $bmp
}

# ---------- ICO writer ----------
function Get-BmpEntryBytes($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write([int]($w * $h * 4 + $maskStride * $h))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    # XOR (BGRA, bottom-up)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    # AND mask: 1 = transparent
    for ($y = $h - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $w; $x++) {
            if ($bmp.GetPixel($x, $y).A -eq 0) { $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) }
        }
        $bw.Write($row)
    }
    $bw.Flush()
    return $ms.ToArray()
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

function Write-Ico([string]$path, $images) {
    $entries = @()
    foreach ($img in $images) {
        $bytes = if ($img.Width -ge 256) { Get-PngBytes $img } else { Get-BmpEntryBytes $img }
        $entries += , @{ Size = $img.Width; Bytes = $bytes }
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]$e.Bytes.Length); $bw.Write([int]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $bw.Write([byte[]]$e.Bytes) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

# ---------- preview ----------
function Write-Preview([string]$path, $images) {
    $pad = 24; $gapX = 20
    $rowW = $pad * 2 + ($images | ForEach-Object { $_.Width } | Measure-Object -Sum).Sum + $gapX * ($images.Count - 1)
    # extra row: 4x nearest-neighbour zoom of the small sizes for pixel inspection
    $small = @($images | Where-Object { $_.Width -le 48 })
    $zoom = 4
    $zoomW = $pad * 2 + ($small | ForEach-Object { $_.Width * $zoom } | Measure-Object -Sum).Sum + $gapX * ($small.Count - 1)
    $W = [Math]::Max($rowW, $zoomW)
    $rowH = 256 + $pad * 2
    $zoomH = 48 * $zoom + $pad * 2
    $H = ($rowH + $zoomH) * 2
    $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $bgs = @([System.Drawing.Color]::FromArgb(255, 242, 242, 246), [System.Drawing.Color]::FromArgb(255, 30, 30, 34))
    for ($b = 0; $b -lt 2; $b++) {
        $top = $b * ($rowH + $zoomH)
        $br = New-Object System.Drawing.SolidBrush($bgs[$b])
        $g.FillRectangle($br, 0, $top, $W, $rowH + $zoomH); $br.Dispose()
        $x = $pad
        foreach ($img in $images) {
            $y = $top + $pad + (256 - $img.Height)
            $g.DrawImage($img, $x, $y, $img.Width, $img.Height)
            $x += $img.Width + $gapX
        }
        $x = $pad
        foreach ($img in $small) {
            $s = $img.Width * $zoom
            $y = $top + $rowH + $pad + (48 * $zoom - $s)
            $g.DrawImage($img, $x, $y, $s, $s)
            $x += $s + $gapX
        }
    }
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# ---------- main ----------
$outDir = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $outDir | Out-Null
$assetDir = Join-Path $RepoRoot 'src\mongdock\Assets'
New-Item -ItemType Directory -Force $assetDir | Out-Null
$docsDir = Join-Path $RepoRoot 'docs'
New-Item -ItemType Directory -Force $docsDir | Out-Null

$master = New-DetailedMaster
$images = @()
foreach ($s in $Sizes) {
    $img = if ($s -le 48) { New-Small $s } else { Resize-Bitmap $master $s }
    $img.Save((Join-Path $outDir "mongdock-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $images += $img
}
$master.Save((Join-Path $outDir 'mongdock-1024.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$master.Dispose()

$icoPath = Join-Path $assetDir 'mongdock.ico'
Write-Ico $icoPath $images
Write-Preview (Join-Path $docsDir 'icon-preview.png') $images
$images[-1].Save((Join-Path $docsDir 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
foreach ($img in $images) { $img.Dispose() }
Write-Host "wrote $icoPath"
