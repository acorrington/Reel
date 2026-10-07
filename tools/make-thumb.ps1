<#
.SYNOPSIS
    Generates the Reel plugin thumbnail (flat, two-tone, 600x338).

.DESCRIPTION
    Original artwork for Reel: a film reel with a play-triangle hub on the left,
    wordmark on the right — cream (#F2EDE4) on deep indigo (#171B33), legible
    down to card size. Output: src/Reel/thumb.jpg (embedded as Reel.thumb.jpg).

.EXAMPLE
    powershell -File tools/make-thumb.ps1
#>
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$w = 600
$h = 338
$outDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Reel'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$bg = [System.Drawing.Color]::FromArgb(23, 27, 51)     # #171B33 deep indigo
$fg = [System.Drawing.Color]::FromArgb(242, 237, 228)   # #F2EDE4 cream
$ac = [System.Drawing.Color]::FromArgb(232, 134, 43)    # #E8862B warm accent

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

$bgBrush = New-Object System.Drawing.SolidBrush($bg)
$fgBrush = New-Object System.Drawing.SolidBrush($fg)
$acBrush = New-Object System.Drawing.SolidBrush($ac)
$penFg12 = New-Object System.Drawing.Pen($fg, 12)

try {
    $g.Clear($bg)

    # ---- film reel: ring + spoke holes + play-triangle hub (left side) ----
    $cx = 160; $cy = 169; $r = 116
    $g.DrawEllipse($penFg12, $cx - $r, $cy - $r, 2 * $r, 2 * $r)

    # spoke holes around the reel
    foreach ($i in 0..4) {
        $a = ($i * 72 - 90) * [Math]::PI / 180
        $hx = $cx + [Math]::Cos($a) * 74
        $hy = $cy + [Math]::Sin($a) * 74
        $g.FillEllipse($fgBrush, $hx - 17, $hy - 17, 34, 34)
    }

    # hub: accent disc with a play triangle cut through it
    $g.FillEllipse($acBrush, $cx - 40, $cy - 40, 80, 80)
    $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tri.AddPolygon([System.Drawing.Point[]]@(
        (New-Object System.Drawing.Point(($cx - 14), ($cy - 24))),
        (New-Object System.Drawing.Point(($cx - 14), ($cy + 24))),
        (New-Object System.Drawing.Point(($cx + 26), $cy))
    ))
    $g.FillPath($fgBrush, $tri)

    # ---- wordmark (right side) ----
    $titleFont = New-Object System.Drawing.Font('Segoe UI', 64, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $subFont = New-Object System.Drawing.Font('Segoe UI', 22, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $g.DrawString('Reel', $titleFont, $fgBrush, 320, 92)
        $g.DrawString('MUSIC VIDEOS', $subFont, $acBrush, 324, 200)
        $g.FillRectangle($acBrush, 324, 244, 210, 5)
    }
    finally {
        $titleFont.Dispose()
        $subFont.Dispose()
    }

    # save as JPEG (plugin embeds thumb.jpg)
    $jpgCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
    $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]90)
    $bmp.Save((Join-Path $outDir 'thumb.jpg'), $jpgCodec, $ep)

    Write-Host "wrote thumb.jpg ($w x $h) to $outDir"
}
finally {
    $g.Dispose()
    $bmp.Dispose()
    $bgBrush.Dispose()
    $fgBrush.Dispose()
    $acBrush.Dispose()
    $penFg12.Dispose()
}
