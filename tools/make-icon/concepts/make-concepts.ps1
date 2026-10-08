<#
  mongdock icon concepts (drafts only - NOT applied to the app).
  System.Drawing only, no external tools.

  Usage (repo root):
    powershell -ExecutionPolicy Bypass -File tools\make-icon\concepts\make-concepts.ps1

  Output:
    tools\make-icon\concepts\concept-N-1024.png   detailed master per concept
    docs\icon-concepts.png                       comparison sheet (light/dark, big + 64 + 32 + 16, 16/32 zoomed x4)

  Every concept is drawn in a 1024 design space (macOS Big Sur grid: plate 100..924, corner ~185).
  32/16 px use a separate simplified drawing (bigger shapes, fewer parts, plate fills the cell).
#>
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function C([string]$hex, [int]$a = 255) {
    $h = $hex.TrimStart('#')
    [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($h.Substring(0, 2), 16), [Convert]::ToInt32($h.Substring(2, 2), 16), [Convert]::ToInt32($h.Substring(4, 2), 16))
}
function Brush($c) { New-Object System.Drawing.SolidBrush($c) }
function PenOf($c, [float]$w) {
    $p = New-Object System.Drawing.Pen($c, $w)
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $p
}

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

# crescent = circle A minus circle B, as one antialiased path
function New-Crescent([float]$ax, [float]$ay, [float]$R, [float]$bx, [float]$by, [float]$r2) {
    $dx = $bx - $ax; $dy = $by - $ay; $d = [Math]::Sqrt($dx * $dx + $dy * $dy)
    $a = ($R * $R - $r2 * $r2 + $d * $d) / (2 * $d)
    $alpha = [Math]::Acos($a / $R) * 180 / [Math]::PI
    $phi = [Math]::Atan2($dy, $dx) * 180 / [Math]::PI
    $h = [Math]::Sqrt($R * $R - $a * $a)
    $mx = $ax + $a * $dx / $d; $my = $ay + $a * $dy / $d
    $p1x = $mx + $h * $dy / $d; $p1y = $my - $h * $dx / $d
    $p2x = $mx - $h * $dy / $d; $p2y = $my + $h * $dx / $d
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    # outer arc on A from (phi+alpha) the long way round
    $p.AddArc($ax - $R, $ay - $R, 2 * $R, 2 * $R, [float]($phi + $alpha), [float](360 - 2 * $alpha))
    # back along B through the side facing A
    $endA = $phi + $alpha + 360 - 2 * $alpha    # == phi - alpha (mod 360)
    $ex = $ax + $R * [Math]::Cos($endA * [Math]::PI / 180); $ey = $ay + $R * [Math]::Sin($endA * [Math]::PI / 180)
    $b1 = [Math]::Atan2($ey - $by, $ex - $bx) * 180 / [Math]::PI
    $sx = $ax + $R * [Math]::Cos(($phi + $alpha) * [Math]::PI / 180); $sy = $ay + $R * [Math]::Sin(($phi + $alpha) * [Math]::PI / 180)
    $b2 = [Math]::Atan2($sy - $by, $sx - $bx) * 180 / [Math]::PI
    $psi = $phi + 180
    $s = (($b2 - $b1) % 360 + 360) % 360
    $mid = $b1 + $s / 2
    $diff = [Math]::Abs(((($mid - $psi) % 360) + 540) % 360 - 180)
    if ($diff -gt 90) { $s = $s - 360 }
    $p.AddArc($bx - $r2, $by - $r2, 2 * $r2, 2 * $r2, [float]$b1, [float]$s)
    $p.CloseFigure(); $p
}

function Set-Quality($g) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
}

function Fill-Plate($g, $path, $top, $bottom, [float]$angle = 70) {
    $rect = New-Object System.Drawing.RectangleF(99, 99, 826, 826)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bottom, [single]$angle)
    $grad.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    $g.FillPath($grad, $path); $grad.Dispose()
}

function Fill-Ell($g, $c, [float]$cx, [float]$cy, [float]$rx, [float]$ry) {
    $b = Brush $c; $g.FillEllipse($b, $cx - $rx, $cy - $ry, 2 * $rx, 2 * $ry); $b.Dispose()
}
function Fill-RR($g, $c, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $b = Brush $c; $p = New-RoundRect $x $y $w $h $r; $g.FillPath($b, $p); $p.Dispose(); $b.Dispose()
}

# level: 0 = detailed (>=64), 1 = 32px, 2 = 16px
# ---------------------------------------------------------------- 1. cloud
function Draw-Cloud($g, [int]$level) {
    $plate = New-Squircle 100 100 824 824 185
    Fill-Plate $g $plate (C '8FD0FF') (C 'C6B6FF') 75
    $g.SetClip($plate)
    # dock bar
    if ($level -eq 0) { Fill-RR $g (C 'FFFFFF' 110) 180 700 664 130 56 }
    elseif ($level -eq 1) { Fill-RR $g (C 'FFFFFF' 150) 150 690 724 150 70 }
    else { Fill-RR $g (C 'FFFFFF' 170) 120 700 784 170 85 }

    # cloud body: overlapping circles + flat base
    $cloud = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cloud.FillMode = [System.Drawing.Drawing2D.FillMode]::Winding
    if ($level -eq 2) {
        $cloud.AddEllipse(190, 390, 300, 300); $cloud.AddEllipse(360, 260, 360, 360); $cloud.AddEllipse(560, 400, 280, 280)
        $cloud.AddPath((New-RoundRect 190 520 650 240 120), $false)
    } else {
        $cloud.AddEllipse(250, 440, 250, 250); $cloud.AddEllipse(370, 320, 320, 320); $cloud.AddEllipse(560, 430, 230, 230)
        $cloud.AddPath((New-RoundRect 250 540 540 210 105), $false)
    }
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

    $ink = C '3A3768'
    switch ($level) {
        0 { Fill-Ell $g $ink 458 600 17 21; Fill-Ell $g $ink 590 600 17 21
            Fill-Ell $g (C 'FF9DB5' 150) 400 650 36 22; Fill-Ell $g (C 'FF9DB5' 150) 648 650 36 22
            $pp = PenOf $ink 12; $g.DrawArc($pp, 504, 618, 40, 30, 20, 140); $pp.Dispose() }
        1 { Fill-Ell $g $ink 450 590 30 36; Fill-Ell $g $ink 610 590 30 36
            Fill-Ell $g (C 'FF9DB5' 190) 380 670 50 32; Fill-Ell $g (C 'FF9DB5' 190) 680 670 50 32 }
        2 { Fill-Ell $g $ink 430 560 52 64; Fill-Ell $g $ink 650 560 52 64 }
    }
    $g.ResetClip(); $plate.Dispose()
}

# ---------------------------------------------------------------- 2. dog
function Draw-Dog($g, [int]$level) {
    $plate = New-Squircle 100 100 824 824 185
    Fill-Plate $g $plate (C 'FFF3DF') (C 'FFBE8C') 80
    $g.SetClip($plate)
    $fur = C 'FFFDF7'; $ear = C 'A86A4E'; $ink = C '3B2A22'
    if ($level -eq 2) {
        Fill-Ell $g $ear 285 470 95 165; Fill-Ell $g $ear 739 470 95 165
        Fill-Ell $g $fur 512 520 270 250
        Fill-Ell $g $ink 420 510 48 56; Fill-Ell $g $ink 604 510 48 56
        Fill-RR $g (C 'E07A4F') 100 680 824 260 0
    } else {
        $s = if ($level -eq 1) { 1.08 } else { 1.0 }
        # ears (behind head), drooping
        $st = $g.Save()
        $g.TranslateTransform(318, 470); $g.RotateTransform(18); Fill-Ell $g $ear 0 0 (78 * $s) (150 * $s); $g.Restore($st)
        $st = $g.Save()
        $g.TranslateTransform(706, 470); $g.RotateTransform(-18); Fill-Ell $g $ear 0 0 (78 * $s) (150 * $s); $g.Restore($st)
        # head
        Fill-Ell $g $fur 512 540 (232 * $s) (215 * $s)
        if ($level -eq 0) {
            Fill-Ell $g (C 'F2C9A8') 512 610 92 62          # muzzle
            Fill-Ell $g $ink 512 585 30 22                  # nose
            $pp = PenOf $ink 12
            $g.DrawLine($pp, 512, 600, 512, 628)
            $g.DrawArc($pp, 472, 600, 40, 34, 10, 160); $g.DrawArc($pp, 512, 600, 40, 34, 10, 160)
            $pp.Dispose()
            Fill-Ell $g $ink 425 520 20 24; Fill-Ell $g $ink 599 520 20 24
            Fill-Ell $g (C 'FF9E9E' 140) 380 590 34 20; Fill-Ell $g (C 'FF9E9E' 140) 644 590 34 20
        } else {
            Fill-Ell $g $ink 512 600 40 30
            Fill-Ell $g $ink 420 515 32 38; Fill-Ell $g $ink 604 515 32 38
        }
        # dock bar in front (dog peeks over it)
        if ($level -eq 0) {
            Fill-RR $g (C 'E9845A') 160 690 704 160 60
            Fill-RR $g (C 'FFFFFF' 70) 160 690 704 34 17
            # paws on the edge
            Fill-Ell $g $fur 420 700 50 34; Fill-Ell $g $fur 604 700 50 34
            $pp = PenOf (C 'E0B89A') 8
            foreach ($cx in 420, 604) { $g.DrawLine($pp, $cx - 14, 690, $cx - 14, 712); $g.DrawLine($pp, $cx + 14, 690, $cx + 14, 712) }
            $pp.Dispose()
            # app dots
            foreach ($cx in 250, 774) { Fill-Ell $g (C 'FFFFFF' 200) $cx 770 26 26 }
        } else {
            Fill-RR $g (C 'E07A4F') 130 690 764 200 70
            Fill-Ell $g $fur 420 698 56 40; Fill-Ell $g $fur 604 698 56 40
        }
    }
    $g.ResetClip(); $plate.Dispose()
}

# ---------------------------------------------------------------- 3. marshmallow blob
function Draw-Blob($g, [int]$level) {
    $plate = New-Squircle 100 100 824 824 185
    Fill-Plate $g $plate (C 'A8F2D6') (C '7CC4F6') 70
    $g.SetClip($plate)
    $ink = C '2E4A5C'
    if ($level -eq 0) {
        Fill-RR $g (C 'FFFFFF' 105) 170 690 684 150 60
        # small apps (dots) in the other slots
        $cols = @('FFD27A', 'FF9DAE', 'B9A6FF')
        for ($i = 0; $i -lt 3; $i++) { Fill-RR $g (C $cols[$i]) (560 + $i * 92) 726 70 70 22 }
        # blob: dome sitting in slot 1, wider at the bottom
        $blob = New-Object System.Drawing.Drawing2D.GraphicsPath
        $blob.AddBezier(200, 775, 180, 600, 230, 470, 380, 470)
        $blob.AddBezier(380, 470, 530, 470, 580, 600, 560, 775)
        $blob.AddBezier(560, 775, 470, 805, 290, 805, 200, 775)
        $blob.CloseFigure()
        $m = New-Object System.Drawing.Drawing2D.Matrix; $m.Translate(0, 16); $sh = $blob.Clone(); $sh.Transform($m)
        $sb = Brush (C '3A8FB0' 55); $g.FillPath($sb, $sh); $sb.Dispose(); $sh.Dispose()
        $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.RectangleF(180, 460, 400, 350)), (C 'FFFFFF' 245), (C 'FFE3EE' 225), [single]90)
        $g.FillPath($lg, $blob); $lg.Dispose()
        $g.SetClip($blob, [System.Drawing.Drawing2D.CombineMode]::Intersect)
        Fill-Ell $g (C 'FFFFFF' 255) 290 540 30 40                     # jelly highlight
        Fill-Ell $g (C 'FFFFFF' 200) 322 510 11 11
        $g.SetClip($plate)
        Fill-Ell $g $ink 335 640 17 22; Fill-Ell $g $ink 425 640 17 22
        Fill-Ell $g (C 'FF9DB5' 130) 290 685 28 16; Fill-Ell $g (C 'FF9DB5' 130) 470 685 28 16
        $blob.Dispose()
    } else {
        $big = ($level -eq 2)
        Fill-RR $g (C 'FFFFFF' 150) 130 690 764 170 70
        if (-not $big) { Fill-Ell $g (C 'FFD27A') 640 775 44 44; Fill-Ell $g (C 'FF9DAE') 770 775 44 44 }
        $blob = New-Object System.Drawing.Drawing2D.GraphicsPath
        if ($big) {
            $blob.AddBezier(170, 800, 150, 560, 280, 360, 512, 360)
            $blob.AddBezier(512, 360, 744, 360, 874, 560, 854, 800)
        } else {
            $blob.AddBezier(160, 800, 140, 580, 230, 400, 390, 400)
            $blob.AddBezier(390, 400, 550, 400, 640, 580, 620, 800)
        }
        $blob.CloseFigure()
        $wb = Brush (C 'FFFFFF'); $g.FillPath($wb, $blob); $wb.Dispose(); $blob.Dispose()
        if ($big) { Fill-Ell $g $ink 400 600 52 62; Fill-Ell $g $ink 624 600 52 62 }
        else { Fill-Ell $g $ink 320 610 32 40; Fill-Ell $g $ink 460 610 32 40 }
    }
    $g.ResetClip(); $plate.Dispose()
}

# ---------------------------------------------------------------- 4. dream moon
function Star($g, $c, [float]$cx, [float]$cy, [float]$r) {
    # 4-point sparkle
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $k = $r * 0.28
    $p.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($cx, ($cy - $r))), (New-Object System.Drawing.PointF(($cx + $k), ($cy - $k))),
        (New-Object System.Drawing.PointF(($cx + $r), $cy)), (New-Object System.Drawing.PointF(($cx + $k), ($cy + $k))),
        (New-Object System.Drawing.PointF($cx, ($cy + $r))), (New-Object System.Drawing.PointF(($cx - $k), ($cy + $k))),
        (New-Object System.Drawing.PointF(($cx - $r), $cy)), (New-Object System.Drawing.PointF(($cx - $k), ($cy - $k)))))
    $b = Brush $c; $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()
}
function Draw-Moon($g, [int]$level) {
    $plate = New-Squircle 100 100 824 824 185
    Fill-Plate $g $plate (C '1E2A6E') (C '7A55C8') 75
    $g.SetClip($plate)
    $moonC = C 'FFE7A3'; $ink = C '5A3E7A'
    if ($level -eq 0) {
        $cres = New-Crescent 450 430 240 590 330 210
        $b = Brush $moonC; $g.FillPath($b, $cres); $b.Dispose(); $cres.Dispose()
        # sleeping face on the thick part: closed eyes + blush
        $pp = PenOf $ink 13
        $g.DrawArc($pp, 278, 470, 56, 40, 20, 140)
        $g.DrawArc($pp, 380, 548, 56, 40, 0, 140)
        $pp.Dispose()
        Fill-Ell $g (C 'FF9DB5' 150) 300 540 28 17
        Star $g (C 'FFFFFF') 700 270 46; Star $g (C 'FFFFFF' 210) 780 470 30; Star $g (C 'FFFFFF' 170) 820 600 20
        Fill-Ell $g (C 'FFFFFF' 160) 250 230 8 8; Fill-Ell $g (C 'FFFFFF' 140) 820 250 7 7
        Fill-RR $g (C 'FFFFFF' 60) 180 700 664 130 56
        foreach ($cx in 330, 450, 574, 694) { Fill-RR $g (C 'FFFFFF' 190) ($cx - 40) 725 80 80 24 }
    } elseif ($level -eq 1) {
        $cres = New-Crescent 450 450 270 620 340 230
        $b = Brush $moonC; $g.FillPath($b, $cres); $b.Dispose(); $cres.Dispose()
        $pp = PenOf $ink 30; $g.DrawArc($pp, 260, 470, 80, 56, 20, 140); $pp.Dispose()
        Star $g (C 'FFFFFF') 740 300 80
        Fill-RR $g (C 'FFFFFF' 110) 140 720 744 150 70
    } else {
        $cres = New-Crescent 470 440 300 660 320 260
        $b = Brush $moonC; $g.FillPath($b, $cres); $b.Dispose(); $cres.Dispose()
        Fill-RR $g (C 'FFFFFF' 150) 120 730 784 170 85
    }
    $g.ResetClip(); $plate.Dispose()
}

# ---------------------------------------------------------------- render
function Render([scriptblock]$draw, [int]$size, [int]$level, [bool]$shadow) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); Set-Quality $g
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($level -eq 0) {
        $g.ScaleTransform($size / 1024.0, $size / 1024.0)
        if ($shadow) {
            for ($i = 12; $i -ge 1; $i--) {
                $gr = $i * 2.2
                $sp = New-Squircle (100 - $gr) (114 - $gr + $i * 1.2) (824 + 2 * $gr) (824 + 2 * $gr) (185 + $gr)
                $sb = Brush (C '141432' 7); $g.FillPath($sb, $sp); $sb.Dispose(); $sp.Dispose()
            }
        }
    } else {
        # small sizes: plate fills the cell (1px margin at 32, none at 16)
        $m = if ($size -ge 32) { 1.0 } else { 0.0 }
        $k = ($size - 2 * $m) / 824.0
        $g.TranslateTransform($m, $m); $g.ScaleTransform($k, $k); $g.TranslateTransform(-100, -100)
    }
    & $draw $g $level
    $g.Dispose(); $bmp
}

function Downscale($src, [int]$size) {
    $cur = $src; $own = $false
    while ($cur.Width / 2 -ge $size * 2) {
        $half = [int]($cur.Width / 2)
        $nb = New-Object System.Drawing.Bitmap($half, $half, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($nb); Set-Quality $g; $g.DrawImage($cur, 0, 0, $half, $half); $g.Dispose()
        if ($own) { $cur.Dispose() }; $cur = $nb; $own = $true
    }
    $out = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($out); Set-Quality $g; $g.DrawImage($cur, 0, 0, $size, $size); $g.Dispose()
    if ($own) { $cur.Dispose() }; $out
}

$concepts = @(
    @{ N = 1; Name = '1  Mong cloud';     Draw = ${function:Draw-Cloud} },
    @{ N = 2; Name = '2  Mongdock puppy'; Draw = ${function:Draw-Dog} },
    @{ N = 3; Name = '3  Marshmallow';    Draw = ${function:Draw-Blob} },
    @{ N = 4; Name = '4  Dream moon';     Draw = ${function:Draw-Moon} }
)

$big = 256; $pad = 28; $gap = 22; $zoom = 4; $labelH = 34
$colW = $big + $gap + 64 + $gap + 32 + $gap + 16 + $gap + 32 * $zoom + $gap + 16 * $zoom
$rowH = $labelH + $big
$W = $pad * 2 + $colW
$H = ($pad * 2 + $concepts.Count * $rowH + ($concepts.Count - 1) * $gap) * 2
$sheet = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$font = New-Object System.Drawing.Font('Segoe UI Semibold', 13)
$half = $H / 2
$bgs = @((C 'F2F2F6'), (C '1E1E22')); $fgs = @((C '333338'), (C 'E6E6EA'))

$rendered = @()
foreach ($c in $concepts) {
    $master = Render $c.Draw 1024 0 $true
    $master.Save((Join-Path $PSScriptRoot "concept-$($c.N)-1024.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $rendered += , @{
        Name = $c.Name
        Big = Downscale $master $big
        S64 = Downscale $master 64
        S32 = Render $c.Draw 32 1 $false
        S16 = Render $c.Draw 16 2 $false
    }
    $master.Dispose()
}

for ($b = 0; $b -lt 2; $b++) {
    $top = $b * $half
    $bb = Brush $bgs[$b]; $sg.FillRectangle($bb, 0, $top, $W, $half); $bb.Dispose()
    $fb = Brush $fgs[$b]
    $y = $top + $pad
    foreach ($r in $rendered) {
        $sg.DrawString($r.Name + '   (big / 64 / 32 / 16 / 32x4 / 16x4)', $font, $fb, $pad, $y)
        $iy = $y + $labelH
        $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $x = $pad
        $sg.DrawImage($r.Big, $x, $iy, $big, $big); $x += $big + $gap
        $bottom = $iy + $big - 40
        $sg.DrawImage($r.S64, $x, ($bottom - 64), 64, 64); $x += 64 + $gap
        $sg.DrawImage($r.S32, $x, ($bottom - 32), 32, 32); $x += 32 + $gap
        $sg.DrawImage($r.S16, $x, ($bottom - 16), 16, 16); $x += 16 + $gap
        $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $sg.DrawImage($r.S32, $x, ($bottom - 32 * $zoom), 32 * $zoom, 32 * $zoom); $x += 32 * $zoom + $gap
        $sg.DrawImage($r.S16, $x, ($bottom - 16 * $zoom), 16 * $zoom, 16 * $zoom)
        $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Default
        $y += $rowH + $gap
    }
    $fb.Dispose()
}
$sg.Dispose()
$out = Join-Path $RepoRoot 'docs\icon-concepts.png'
$sheet.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $sheet.Dispose()
foreach ($r in $rendered) { $r.Big.Dispose(); $r.S64.Dispose(); $r.S32.Dispose(); $r.S16.Dispose() }
Write-Host "wrote $out"
