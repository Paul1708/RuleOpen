param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($Radius * 2.0, [Math]::Min($Width, $Height))
    if ($d -le 0.01) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $Width, $Height)))
        return $path
    }
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-RuleOpenSymbol {
    param(
        [System.Drawing.Graphics]$G,
        [float]$OriginX,
        [float]$OriginY,
        [float]$Size,
        [bool]$SmallMode = $false
    )

    $s = $Size / 256.0
    $px = { param([float]$v) return $OriginX + ($v * $s) }
    $py = { param([float]$v) return $OriginY + ($v * $s) }

    $G.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $G.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $G.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    # Soft document shadow (skip heavy shadow at <=24px for crispness)
    if (-not $SmallMode) {
        $shadowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = 20.0 * $s
        $shadowPath.AddArc((& $px 26), (& $py 33), $r*2, $r*2, 180, 90)
        $shadowPath.AddLine((& $px 46), (& $py 33), (& $px 110), (& $py 33))
        $shadowPath.AddLine((& $px 110), (& $py 33), (& $px 152), (& $py 75))
        $shadowPath.AddLine((& $px 152), (& $py 75), (& $px 152), (& $py 213))
        $shadowPath.AddArc((& $px 112), (& $py 193), $r*2, $r*2, 0, 90)
        $shadowPath.AddArc((& $px 26), (& $py 193), $r*2, $r*2, 90, 90)
        $shadowPath.CloseFigure()
        $shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(30, 15, 23, 42))
        $G.FillPath($shadowBrush, $shadowPath)
        $shadowBrush.Dispose()
        $shadowPath.Dispose()
    }

    # Document shape (with top-right dog-ear cut)
    $docPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 20.0 * $s
    $docPath.AddArc((& $px 26), (& $py 28), $r*2, $r*2, 180, 90)
    $docPath.AddLine((& $px 46), (& $py 28), (& $px 110), (& $py 28))
    $docPath.AddLine((& $px 110), (& $py 28), (& $px 152), (& $py 70))
    $docPath.AddLine((& $px 152), (& $py 70), (& $px 152), (& $py 208))
    $docPath.AddArc((& $px 112), (& $py 188), $r*2, $r*2, 0, 90)
    $docPath.AddArc((& $px 26), (& $py 188), $r*2, $r*2, 90, 90)
    $docPath.CloseFigure()

    $docGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF((& $px 26), (& $py 28))),
        (New-Object System.Drawing.PointF((& $px 152), (& $py 228))),
        [System.Drawing.Color]::FromArgb(255, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(255, 229, 236, 245)
    )
    $G.FillPath($docGrad, $docPath)
    $docGrad.Dispose()

    $borderWidth = if ($SmallMode) { [Math]::Max(1.0, 6.0 * $s) } else { [Math]::Max(1.2, 3.0 * $s) }
    $docPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 148, 166, 188), $borderWidth)
    $G.DrawPath($docPen, $docPath)
    $docPen.Dispose()
    $docPath.Dispose()

    # Folded dog-ear
    $foldPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fr = 14.0 * $s
    $foldPath.AddLine((& $px 110), (& $py 28), (& $px 110), (& $py 56))
    $foldPath.AddArc((& $px 110), (& $py 42), $fr*2, $fr*2, 180, -90)
    $foldPath.AddLine((& $px 124), (& $py 70), (& $px 152), (& $py 70))
    $foldPath.CloseFigure()

    $foldGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF((& $px 110), (& $py 28))),
        (New-Object System.Drawing.PointF((& $px 152), (& $py 70))),
        [System.Drawing.Color]::FromArgb(255, 212, 223, 236),
        [System.Drawing.Color]::FromArgb(255, 184, 200, 220)
    )
    $G.FillPath($foldGrad, $foldPath)
    $foldGrad.Dispose()
    $foldPath.Dispose()

    # Subtle header lines inside document (omit lowest detail at 16px)
    if (-not $SmallMode) {
        $line1 = New-RoundedRectPath (& $px 46) (& $py 64) (44 * $s) (10 * $s) (5 * $s)
        $line2 = New-RoundedRectPath (& $px 46) (& $py 86) (66 * $s) (10 * $s) (5 * $s)
        $b1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 184, 199, 217))
        $b2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 203, 215, 230))
        $G.FillPath($b1, $line1)
        $G.FillPath($b2, $line2)
        $b1.Dispose(); $b2.Dispose(); $line1.Dispose(); $line2.Dispose()
    }
    else {
        $line1 = New-RoundedRectPath (& $px 44) (& $py 64) (52 * $s) (14 * $s) (7 * $s)
        $b1 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 172, 188, 208))
        $G.FillPath($b1, $line1)
        $b1.Dispose(); $line1.Dispose()
    }

    # Secondary upper branch
    $secWidth = if ($SmallMode) { [Math]::Max(1.6, 15.0 * $s) } else { 11.0 * $s }
    $secPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $secPath.AddBezier(
        (& $px 108), (& $py 148),
        (& $px 134), (& $py 148),
        (& $px 134), (& $py 76),
        (& $px 178), (& $py 76)
    )
    $secPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 126, 192, 245), $secWidth)
    $secPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $secPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $G.DrawPath($secPen, $secPath)
    $secPen.Dispose()
    $secPath.Dispose()

    # Secondary app square
    $secTile = New-RoundedRectPath (& $px 176) (& $py 54) (44 * $s) (44 * $s) (12 * $s)
    $secBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF((& $px 176), (& $py 54))),
        (New-Object System.Drawing.PointF((& $px 220), (& $py 98))),
        [System.Drawing.Color]::FromArgb(255, 200, 227, 250),
        [System.Drawing.Color]::FromArgb(255, 139, 196, 245)
    )
    $G.FillPath($secBrush, $secTile)
    $secTilePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 98, 176, 239), [Math]::Max(1.0, 2.5 * $s))
    $G.DrawPath($secTilePen, $secTile)
    $secTilePen.Dispose(); $secBrush.Dispose(); $secTile.Dispose()

    # Origin white halo underneath line (only above SmallMode)
    if (-not $SmallMode) {
        $haloR = 18.0 * $s
        $haloBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $G.FillEllipse($haloBrush, (& $px 68) - $haloR, (& $py 148) - $haloR, $haloR * 2, $haloR * 2)
        $haloBrush.Dispose()
    }

    # Primary branch path
    $priWidth = if ($SmallMode) { [Math]::Max(2.0, 20.0 * $s) } else { 16.0 * $s }
    $priPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $priPath.AddLine((& $px 68), (& $py 148), (& $px 120), (& $py 148))
    $priPath.AddBezier(
        (& $px 120), (& $py 148),
        (& $px 138), (& $py 148),
        (& $px 142), (& $py 170),
        (& $px 174), (& $py 170)
    )
    $priGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF((& $px 68), (& $py 146))),
        (New-Object System.Drawing.PointF((& $px 228), (& $py 174))),
        [System.Drawing.Color]::FromArgb(255, 40, 153, 245),
        [System.Drawing.Color]::FromArgb(255, 0, 103, 192)
    )
    $priPen = New-Object System.Drawing.Pen($priGrad, $priWidth)
    $priPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $priPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $priPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $G.DrawPath($priPen, $priPath)
    $priPen.Dispose(); $priGrad.Dispose(); $priPath.Dispose()

    # Origin node inside document on top of path start
    $nodeR = if ($SmallMode) { 14.0 * $s } else { 13.0 * $s }
    $nodeFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 120, 212))
    $G.FillEllipse($nodeFill, (& $px 68) - $nodeR, (& $py 148) - $nodeR, $nodeR * 2, $nodeR * 2)
    $nodeFill.Dispose()

    # Target tile shadow
    if (-not $SmallMode) {
        $tShadow = New-RoundedRectPath (& $px 168) (& $py 142) (64 * $s) (64 * $s) (16 * $s)
        $tsBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(48, 0, 69, 138))
        $G.FillPath($tsBrush, $tShadow)
        $tsBrush.Dispose(); $tShadow.Dispose()
    }

    # Target app tile
    $targetTile = New-RoundedRectPath (& $px 168) (& $py 138) (64 * $s) (64 * $s) (16 * $s)
    $targetGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF((& $px 168), (& $py 138))),
        (New-Object System.Drawing.PointF((& $px 232), (& $py 202))),
        [System.Drawing.Color]::FromArgb(255, 56, 168, 255),
        [System.Drawing.Color]::FromArgb(255, 0, 95, 184)
    )
    $G.FillPath($targetGrad, $targetTile)
    $targetGrad.Dispose(); $targetTile.Dispose()

    # White crisp chevron inside target tile
    $chevWidth = if ($SmallMode) { [Math]::Max(1.6, 13.0 * $s) } else { 9.0 * $s }
    $chevPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $chevWidth)
    $chevPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $chevPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $chevPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $chevPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $chevPath.AddLines([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF((& $px 192), (& $py 156))),
        (New-Object System.Drawing.PointF((& $px 211), (& $py 170))),
        (New-Object System.Drawing.PointF((& $px 192), (& $py 184)))
    ))
    $G.DrawPath($chevPen, $chevPath)
    $chevPen.Dispose(); $chevPath.Dispose()
}

function New-IconBitmap {
    param(
        [int]$Width,
        [int]$Height,
        [float]$PaddingRatio = 0.0,
        [bool]$SmallMode = $false
    )
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $minDim = [Math]::Min($Width, $Height)
    $drawSize = $minDim * (1.0 - 2.0 * $PaddingRatio)
    $ox = ($Width - $drawSize) / 2.0
    $oy = ($Height - $drawSize) / 2.0
    Draw-RuleOpenSymbol -G $g -OriginX $ox -OriginY $oy -Size $drawSize -SmallMode $SmallMode
    $g.Dispose()
    return $bmp
}

function Save-Png {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Write-MultiIcon {
    param([string]$OutPath, [int[]]$Sizes)
    $entries = @()
    foreach ($sz in $Sizes) {
        $small = $sz -le 24
        $bmp = New-IconBitmap -Width $sz -Height $sz -SmallMode $small
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $entries += [PSCustomObject]@{
            Size = $sz
            Bytes = $ms.ToArray()
        }
        $ms.Dispose()
    }

    $fs = [System.IO.File]::Create($OutPath)
    $bw = New-Object System.IO.BinaryWriter($fs)
    # ICONDIR header: reserved(2)=0, type(2)=1, count(2)
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$entries.Count)

    $offset = 6 + (16 * $entries.Count)
    foreach ($e in $entries) {
        $dimByte = if ($e.Size -ge 256) { [byte]0 } else { [byte]$e.Size }
        $bw.Write($dimByte) # bWidth
        $bw.Write($dimByte) # bHeight
        $bw.Write([byte]0)  # bColorCount
        $bw.Write([byte]0)  # bReserved
        $bw.Write([uint16]1) # wPlanes
        $bw.Write([uint16]32) # wBitCount
        $bw.Write([uint32]$e.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $e.Bytes.Length
    }

    foreach ($e in $entries) {
        $bw.Write($e.Bytes)
    }

    $bw.Dispose()
    $fs.Dispose()
}

$brandingDir = Join-Path $Root "assets\branding"
$desktopAssets = Join-Path $Root "src\RuleOpen.Desktop\Assets"
$shellAssets = Join-Path $Root "src\RuleOpen.ShellExtension\Assets"

# Standard PNG icon resolutions
foreach ($sz in @(16, 20, 24, 32, 40, 44, 48, 50, 64, 71, 88, 96, 128, 150, 256, 310, 512)) {
    $bmp = New-IconBitmap -Width $sz -Height $sz -SmallMode ($sz -le 24)
    Save-Png $bmp (Join-Path $brandingDir "ruleopen-$sz.png")
    $bmp.Dispose()
}

# Multi-frame Windows .ico
Write-MultiIcon -OutPath (Join-Path $brandingDir "ruleopen.ico") -Sizes @(16, 24, 32, 48, 64, 128, 256)

# Packaging / MSIX assets (Desktop & ShellExtension)
$msixSpecs = @(
    @{ Name = "StoreLogo.png"; W = 50; H = 50; Pad = 0.08 },
    @{ Name = "Square44x44Logo.png"; W = 44; H = 44; Pad = 0.06 },
    @{ Name = "Square44x44Logo.targetsize-24_altform-unplated.png"; W = 24; H = 24; Pad = 0.04 },
    @{ Name = "Square150x150Logo.png"; W = 150; H = 150; Pad = 0.14 },
    @{ Name = "SmallTile.png"; W = 71; H = 71; Pad = 0.14 },
    @{ Name = "LargeTile.png"; W = 310; H = 310; Pad = 0.18 },
    @{ Name = "Wide310x150Logo.png"; W = 310; H = 150; Pad = 0.16 },
    @{ Name = "SplashScreen.png"; W = 620; H = 300; Pad = 0.22 }
)

foreach ($spec in $msixSpecs) {
    $bmp = New-IconBitmap -Width $spec.W -Height $spec.H -PaddingRatio $spec.Pad -SmallMode ($spec.W -le 24)
    Save-Png $bmp (Join-Path $desktopAssets $spec.Name)
    Save-Png $bmp (Join-Path $shellAssets $spec.Name)
    $bmp.Dispose()
}

Copy-Item (Join-Path $brandingDir "ruleopen.ico") (Join-Path $desktopAssets "RuleOpen.ico") -Force
Copy-Item (Join-Path $brandingDir "ruleopen.ico") (Join-Path $shellAssets "RuleOpen.ico") -Force
Copy-Item (Join-Path $brandingDir "ruleopen-256.png") (Join-Path $desktopAssets "RuleOpenLogo.png") -Force

# Banner PNG (960x240)
$bannerBmp = New-Object System.Drawing.Bitmap(960, 240, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bg = [System.Drawing.Graphics]::FromImage($bannerBmp)
$bg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$bg.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$bg.Clear([System.Drawing.Color]::Transparent)

$cardPath = New-RoundedRectPath 1 1 958 238 20
$cardGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(0, 0)),
    (New-Object System.Drawing.PointF(960, 240)),
    [System.Drawing.Color]::FromArgb(255, 246, 249, 253),
    [System.Drawing.Color]::FromArgb(255, 235, 242, 250)
)
$bg.FillPath($cardGrad, $cardPath)
$cardPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 214, 226, 240), 2)
$bg.DrawPath($cardPen, $cardPath)
$cardGrad.Dispose(); $cardPen.Dispose(); $cardPath.Dispose()

Draw-RuleOpenSymbol -G $bg -OriginX 76 -OriginY 30 -Size 180 -SmallMode $false

$titleFont = New-Object System.Drawing.Font("Segoe UI", 42, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$subFont = New-Object System.Drawing.Font("Segoe UI", 21, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$tagFont = New-Object System.Drawing.Font("Segoe UI", 15, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)

$titleBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 26, 36, 51))
$subBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 75, 91, 112))
$tagBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 103, 192))

$bg.DrawString("RuleOpen", $titleFont, $titleBrush, 290, 62)
$bg.DrawString("Context-aware file opening rules for Windows 11", $subFont, $subBrush, 293, 126)
$bg.DrawString("Configure once. Open naturally.", $tagFont, $tagBrush, 293, 162)

$titleFont.Dispose(); $subFont.Dispose(); $tagFont.Dispose()
$titleBrush.Dispose(); $subBrush.Dispose(); $tagBrush.Dispose()
$bg.Dispose()

Save-Png $bannerBmp (Join-Path $brandingDir "ruleopen-banner.png")
$bannerBmp.Dispose()

Write-Host "Branding assets generated successfully."
