<#
  mongdock app icon generator (no external tools/packages; System.Drawing only).

  Usage (repo root):
    powershell -ExecutionPolicy Bypass -File tools\make-icon\make-icon.ps1

  Output:
    src\mongdock\Assets\mongdock.ico   16,20,24,32,40,48,64,128 (32-bit BMP entries) + 256 (PNG entry)
    docs\icon-preview.png              all sizes side by side, on light and dark backgrounds
    docs\icon.png                      256 px (README)
    (PNG per size are written to tools\make-icon\out\ for inspection; not committed)

  Design: macOS Big Sur style squircle (1024 grid: 824 plate, corner ~185, soft shadow below),
  indigo -> sky gradient, top bar strip at the top, dock (translucent rounded bar + 4 small tiles) at the bottom.
  16-32 px are drawn separately with fewer details on a pixel grid so they stay crisp.
#>
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

# palette
$GradTop    = [System.Drawing.Color]::FromArgb(255, 112, 96, 232)   # soft indigo-violet
$GradBottom = [System.Drawing.Color]::FromArgb(255, 72, 170, 240)   # sky
$TileColors = @(
    [System.Drawing.Color]::FromArgb(255, 255, 255, 255),
    [System.Drawing.Color]::FromArgb(255, 255, 214, 120),
    [System.Drawing.Color]::FromArgb(255, 255, 150, 160),
    [System.Drawing.Color]::FromArgb(255, 140, 232, 190)
)

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2)
    $d = $r * 2
    if ($d -le 0) { $p.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h))); return $p }
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Squircle-ish (continuous corner) path: superellipse corners via polyline, smoother than circular arcs.
function New-Squircle([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    # corner extends further than r (Apple's continuous curve spans ~1.528r); use superellipse n=5 over 1.25r
    $c = [Math]::Min($r * 1.25, [Math]::Min($w, $h) / 2)
    $n = 4.2
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    $steps = 24
    # corners: (center, quadrant angles) clockwise starting top-right
    $corners = @(
        @{ cx = $x + $w - $c; cy = $y + $c;      a0 = 270; },
        @{ cx = $x + $w - $c; cy = $y + $h - $c; a0 = 0;   },
        @{ cx = $x + $c;      cy = $y + $h - $c; a0 = 90;  },
        @{ cx = $x + $c;      cy = $y + $c;      a0 = 180; }
    )
    foreach ($k in $corners) {
        for ($i = 0; $i -le $steps; $i++) {
            $t = ($k.a0 + 90.0 * $i / $steps) * [Math]::PI / 180.0
            $ct = [Math]::Cos($t); $st = [Math]::Sin($t)
            $px = [Math]::Sign($ct) * [Math]::Pow([Math]::Abs($ct), 2.0 / $n) * $c
            $py = [Math]::Sign($st) * [Math]::Pow([Math]::Abs($st), 2.0 / $n) * $c
            $pts.Add((New-Object System.Drawing.PointF([float]($k.cx + $px), [float]($k.cy + $py))))
        }
    }
    $p.AddPolygon($pts.ToArray())
    return $p
}

function Set-Quality($g) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
}

function Fill-Plate($g, $path, [System.Drawing.RectangleF]$rect) {
    # rect-based ctor covers the whole plate (point-based one tiles and leaves a band in the corner)
    $r2 = New-Object System.Drawing.RectangleF(($rect.X - 1), ($rect.Y - 1), ($rect.Width + 2), ($rect.Height + 2))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($r2, $GradTop, $GradBottom, [single]70.0)
    $grad.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    $g.FillPath($grad, $path)
    $grad.Dispose()
}

# ---------- detailed version (drawn on a 1024 canvas, then downscaled) ----------
function New-DetailedMaster {
    $S = 1024
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    Set-Quality $g
    $g.Clear([System.Drawing.Color]::Transparent)

    $px = 100.0; $py = 92.0; $pw = 824.0; $ph = 824.0; $pr = 185.0

    # soft shadow below: stacked translucent squircles, slightly offset downward
    for ($i = 12; $i -ge 1; $i--) {
        $grow = $i * 2.2
        $sp = New-Squircle ($px - $grow) ($py + 14 - $grow + $i * 1.2) ($pw + 2 * $grow) ($ph + 2 * $grow) ($pr + $grow)
        $sb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(7, 20, 20, 50))
        $g.FillPath($sb, $sp); $sb.Dispose(); $sp.Dispose()
    }

    $plate = New-Squircle $px $py $pw $ph $pr
    $rect = New-Object System.Drawing.RectangleF($px, $py, $pw, $ph)
    Fill-Plate $g $plate $rect

    $g.SetClip($plate)
    # gentle top highlight
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, $py)), (New-Object System.Drawing.PointF(0, ($py + 420))),
        [System.Drawing.Color]::FromArgb(46, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillRectangle($hl, $px, $py, $pw, 420); $hl.Dispose()

    # top bar strip
    $barH = 104.0
    $tb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
    $g.FillRectangle($tb, $px, $py, $pw, $barH); $tb.Dispose()
    $line = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 255, 255, 255))
    $g.FillRectangle($line, $px, ($py + $barH), $pw, 5); $line.Dispose()
    $g.ResetClip()

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
    $cy = $py + $barH / 2 + 4
    $g.FillEllipse($white, 222, ($cy - 20), 40, 40)                      # "logo" dot
    $m1 = New-RoundRect 292 ($cy - 12) 96 24 12; $g.FillPath($white, $m1); $m1.Dispose()   # menu
    $m2 = New-RoundRect 408 ($cy - 12) 70 24 12; $g.FillPath($white, $m2); $m2.Dispose()
    $s1 = New-RoundRect 676 ($cy - 12) 126 24 12; $g.FillPath($white, $s1); $s1.Dispose()  # clock
    $g.FillEllipse($white, 632, ($cy - 12), 24, 24)
    $white.Dispose()

    # dock: translucent rounded bar + tiles
    $dx = 196.0; $dy = 664.0; $dw = 632.0; $dh = 168.0
    $dockPath = New-RoundRect $dx $dy $dw $dh 56
    $db = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(84, 255, 255, 255))
    $g.FillPath($db, $dockPath); $db.Dispose()
    $dp = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 255, 255, 255), 5)
    $g.DrawPath($dp, $dockPath); $dp.Dispose(); $dockPath.Dispose()

    $tile = 104.0; $gap = 36.0
    $total = 4 * $tile + 3 * $gap
    $tx = 512 - $total / 2; $ty = $dy + 26
    for ($i = 0; $i -lt 4; $i++) {
        $x = $tx + $i * ($tile + $gap)
        # tiny tile shadow
        $tsp = New-RoundRect $x ($ty + 6) $tile $tile 28
        $tsb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 30, 30, 90))
        $g.FillPath($tsb, $tsp); $tsb.Dispose(); $tsp.Dispose()
        $tp = New-RoundRect $x $ty $tile $tile 28
        $tb2 = New-Object System.Drawing.SolidBrush($TileColors[$i])
        $g.FillPath($tb2, $tp); $tb2.Dispose(); $tp.Dispose()
    }
    # running-app dots under first two tiles
    $dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230, 255, 255, 255))
    foreach ($i in 0, 1) {
        $x = $tx + $i * ($tile + $gap) + $tile / 2
        $g.FillEllipse($dot, ($x - 9), ($ty + $tile + 12), 18, 18)
    }
    $dot.Dispose()
    $plate.Dispose()
    $g.Dispose()
    return $bmp
}

function Resize-Bitmap($src, [int]$size) {
    # step-down by halves for quality, then final resize
    $cur = $src
    $own = $false
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

# ---------- simple version for 16..32 (drawn directly on the pixel grid) ----------
function New-Simple([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.Clear([System.Drawing.Color]::Transparent)

    # plate fills almost the whole cell (Windows small icons are not inset like macOS)
    $m = if ($s -ge 32) { 1.0 } elseif ($s -ge 24) { 1.0 } else { 0.0 }
    $pw = $s - 2 * $m
    $pr = [Math]::Round($s * 0.23)
    $plate = New-Squircle $m $m $pw $pw $pr
    Fill-Plate $g $plate (New-Object System.Drawing.RectangleF($m, $m, $pw, $pw))

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    # top bar line (whole pixels)
    switch ($s) {
        16 { $bar = @(3, 3, 10, 1);  $dock = @(3, 10, 10, 3);  $tiles = $null }
        20 { $bar = @(4, 4, 12, 1);  $dock = @(3, 12, 14, 4);  $tiles = $null }
        24 { $bar = @(5, 5, 14, 2);  $dock = @(4, 14, 16, 5);  $tiles = @(3, 3, 1) }   # count, tile, gap
        32 { $bar = @(6, 6, 20, 2);  $dock = @(5, 19, 22, 7);  $tiles = @(3, 4, 2) }
    }
    $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 255, 255, 255))), $bar[0], $bar[1], $bar[2], $bar[3])

    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $dp = New-RoundRect $dock[0] $dock[1] $dock[2] $dock[3] ([Math]::Max(1.0, $dock[3] / 2.5))
    $g.FillPath($white, $dp); $dp.Dispose()

    if ($tiles) {
        # tiles cut out of the white dock in the plate's colour so they read as "icons on a dock"
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
        $cnt = $tiles[0]; $t = $tiles[1]; $gp = $tiles[2]
        $tw = $cnt * $t + ($cnt - 1) * $gp
        $tx = $dock[0] + [int](($dock[2] - $tw) / 2)
        $ty = $dock[1] + [int](($dock[3] - $t) / 2)
        $tb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 86, 140, 236))
        for ($i = 0; $i -lt $cnt; $i++) { $g.FillRectangle($tb, ($tx + $i * ($t + $gp)), $ty, $t, $t) }
        $tb.Dispose()
    }
    $white.Dispose(); $plate.Dispose(); $g.Dispose()
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
    # extra row: 2x nearest-neighbour zoom of the small sizes for pixel inspection
    $small = @($images | Where-Object { $_.Width -le 32 })
    $zoom = 4
    $zoomW = $pad * 2 + ($small | ForEach-Object { $_.Width * $zoom } | Measure-Object -Sum).Sum + $gapX * ($small.Count - 1)
    $W = [Math]::Max($rowW, $zoomW)
    $rowH = 256 + $pad * 2
    $zoomH = 32 * $zoom + $pad * 2
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
            $y = $top + $rowH + $pad + (32 * $zoom - $s)
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
    $img = if ($s -le 32) { New-Simple $s } else { Resize-Bitmap $master $s }
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
