# Generates app.ico for ControlPlan: a stylized golden bell with ringing
# waves on a dark circular background. Run once; commit the resulting .ico.
Add-Type -AssemblyName System.Drawing

function New-BellBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size

    # Dark rounded-square background with subtle gradient.
    $bgRect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $bgRect,
        [System.Drawing.Color]::FromArgb(255, 30, 41, 59),    # slate-800
        [System.Drawing.Color]::FromArgb(255, 15, 23, 42),    # slate-900
        90.0)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [single]($s * 0.22)
    $path.AddArc(0,           0,           $r*2, $r*2, 180, 90)
    $path.AddArc($s - $r*2,   0,           $r*2, $r*2, 270, 90)
    $path.AddArc($s - $r*2,   $s - $r*2,   $r*2, $r*2,   0, 90)
    $path.AddArc(0,           $s - $r*2,   $r*2, $r*2,  90, 90)
    $path.CloseFigure()
    $g.FillPath($bgBrush, $path)

    # Ringing waves (cyan arcs) on either side of the bell.
    $waveColor = [System.Drawing.Color]::FromArgb(220, 56, 189, 248)  # sky-400
    $wavePen = New-Object System.Drawing.Pen($waveColor, [single]($s * 0.045))
    $wavePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wavePen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $cx = $s * 0.5
    $cy = $s * 0.50
    foreach ($k in 1,2) {
        $rad = $s * (0.18 + 0.10 * $k)
        $rect = New-Object System.Drawing.RectangleF(
            [single]($cx - $rad), [single]($cy - $rad),
            [single]($rad * 2),   [single]($rad * 2))
        $g.DrawArc($wavePen, $rect, 210, 40)  # upper-left wave
        $g.DrawArc($wavePen, $rect, -70, 40)  # upper-right wave
    }
    $wavePen.Dispose()

    # Bell body (golden gradient).
    $bellW = $s * 0.46
    $bellH = $s * 0.46
    $bellX = $cx - $bellW / 2
    $bellY = $cy - $bellH * 0.45
    $bellRect = New-Object System.Drawing.RectangleF(
        [single]$bellX, [single]$bellY, [single]$bellW, [single]$bellH)
    $bellBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $bellRect,
        [System.Drawing.Color]::FromArgb(255, 253, 224, 71),  # yellow-300
        [System.Drawing.Color]::FromArgb(255, 217, 119, 6),   # amber-600
        90.0)

    $bell = New-Object System.Drawing.Drawing2D.GraphicsPath
    # Bell silhouette: rounded dome with flared rim.
    $topW   = $bellW * 0.55
    $topX   = $cx - $topW / 2
    $topY   = $bellY
    $rimY   = $bellY + $bellH * 0.78
    $rimW   = $bellW
    $rimX   = $cx - $rimW / 2
    $bell.AddBezier(
        [single]$topX,          [single]$topY,
        [single]($topX - $bellW*0.18), [single]($topY + $bellH*0.35),
        [single]$rimX,          [single]($rimY - $bellH*0.10),
        [single]$rimX,          [single]$rimY)
    $bell.AddLine([single]$rimX, [single]$rimY, [single]($rimX + $rimW), [single]$rimY)
    $bell.AddBezier(
        [single]($rimX + $rimW), [single]$rimY,
        [single]($rimX + $rimW), [single]($rimY - $bellH*0.10),
        [single]($topX + $topW + $bellW*0.18), [single]($topY + $bellH*0.35),
        [single]($topX + $topW), [single]$topY)
    $bell.AddBezier(
        [single]($topX + $topW), [single]$topY,
        [single]($topX + $topW*0.75), [single]($topY - $bellH*0.05),
        [single]($topX + $topW*0.25), [single]($topY - $bellH*0.05),
        [single]$topX,          [single]$topY)
    $bell.CloseFigure()
    $g.FillPath($bellBrush, $bell)

    # Bell highlight (subtle white sheen on the left).
    $sheen = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sheen.AddEllipse(
        [single]($bellX + $bellW*0.12),
        [single]($bellY + $bellH*0.12),
        [single]($bellW * 0.18),
        [single]($bellH * 0.55))
    $sheenBrush = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(80, 255, 255, 255))
    $g.FillPath($sheenBrush, $sheen)

    # Bell rim base bar.
    $barH = $s * 0.06
    $barRect = New-Object System.Drawing.RectangleF(
        [single]($rimX - $bellW*0.05),
        [single]$rimY,
        [single]($rimW + $bellW*0.10),
        [single]$barH)
    $barBrush = New-Object System.Drawing.SolidBrush(
        [System.Drawing.Color]::FromArgb(255, 180, 83, 9))   # amber-700
    $barPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $br = [single]($barH * 0.5)
    $barPath.AddArc([single]$barRect.X, [single]$barRect.Y, $br*2, $br*2, 180, 90)
    $barPath.AddArc([single]($barRect.Right - $br*2), [single]$barRect.Y, $br*2, $br*2, 270, 90)
    $barPath.AddArc([single]($barRect.Right - $br*2), [single]($barRect.Bottom - $br*2), $br*2, $br*2, 0, 90)
    $barPath.AddArc([single]$barRect.X, [single]($barRect.Bottom - $br*2), $br*2, $br*2, 90, 90)
    $barPath.CloseFigure()
    $g.FillPath($barBrush, $barPath)

    # Clapper (dangling ball below the bell).
    $clapperD = $s * 0.12
    $clapperRect = New-Object System.Drawing.RectangleF(
        [single]($cx - $clapperD/2),
        [single]($barRect.Bottom + $s*0.005),
        [single]$clapperD,
        [single]$clapperD)
    $clapperBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $clapperRect,
        [System.Drawing.Color]::FromArgb(255, 250, 204, 21),
        [System.Drawing.Color]::FromArgb(255, 161, 98, 7),
        90.0)
    $g.FillEllipse($clapperBrush, $clapperRect)

    # Small loop/handle at the top of the bell.
    $loopW = $s * 0.10
    $loopH = $s * 0.07
    $loopRect = New-Object System.Drawing.RectangleF(
        [single]($cx - $loopW/2),
        [single]($bellY - $loopH*0.65),
        [single]$loopW,
        [single]$loopH)
    $loopPen = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(255, 180, 83, 9),
        [single]($s * 0.045))
    $g.DrawArc($loopPen, $loopRect, 180, 180)
    $loopPen.Dispose()

    $g.Dispose()
    return $bmp
}

function Save-Ico([string]$path, [int[]]$sizes) {
    $pngs = @()
    foreach ($sz in $sizes) {
        $bmp = New-BellBitmap $sz
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += ,$ms.ToArray()
        $bmp.Dispose()
        $ms.Dispose()
    }

    $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Create)
    $bw = New-Object System.IO.BinaryWriter($fs)
    # ICONDIR
    $bw.Write([uint16]0)           # reserved
    $bw.Write([uint16]1)           # type = icon
    $bw.Write([uint16]$sizes.Count)

    # Each ICONDIRENTRY is 16 bytes; image data follows after all entries.
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]
        $bytes = $pngs[$i]
        $w = if ($sz -ge 256) { 0 } else { $sz }   # 0 means 256 in ICO spec
        $h = $w
        $bw.Write([byte]$w)
        $bw.Write([byte]$h)
        $bw.Write([byte]0)         # palette
        $bw.Write([byte]0)         # reserved
        $bw.Write([uint16]1)       # planes
        $bw.Write([uint16]32)      # bpp
        $bw.Write([uint32]$bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $bytes.Length
    }
    foreach ($bytes in $pngs) {
        $bw.Write($bytes)
    }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

$out = Join-Path $PSScriptRoot 'app.ico'
Save-Ico -path $out -sizes @(16, 32, 48, 64, 128, 256)
Write-Host "Wrote $out"
