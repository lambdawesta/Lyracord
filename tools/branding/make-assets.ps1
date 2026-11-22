# Generates Lyracord's own logo assets (no Discord or Unicord artwork).
# Metro style: square, full-bleed flat dark red (#7A0E14) background,
# a sharp geometric white "L" and two crisp flat-ended sound arcs.
# Usage: powershell -ExecutionPolicy Bypass -File tools\branding\make-assets.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$assets = Join-Path $root "Lyracord\Assets"
$store = Join-Path $assets "Store"

$red = [System.Drawing.Color]::FromArgb(255, 122, 14, 20)   # #7A0E14

# Background: one flat dark red (a gradient drew too much attention on the Start screen).
function Fill-Gradient($g, [float]$x, [float]$y, [float]$w, [float]$h) {
    $brush = New-Object System.Drawing.SolidBrush($red)
    $g.FillRectangle($brush, $x, $y, $w, $h)
}

# Draws the sharp Metro mark (no background) into the square (x, y, size).
function Draw-Mark($g, [float]$x, [float]$y, [float]$size, [System.Drawing.Color]$color) {
    $u = $size / 64.0
    $brush = New-Object System.Drawing.SolidBrush($color)
    # Sharp geometric block "L": stem x=14..24 y=14..50, foot x=14..38 y=40..50
    $g.FillPolygon($brush, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(($x + 14 * $u), ($y + 14 * $u))),
        (New-Object System.Drawing.PointF(($x + 24 * $u), ($y + 14 * $u))),
        (New-Object System.Drawing.PointF(($x + 24 * $u), ($y + 40 * $u))),
        (New-Object System.Drawing.PointF(($x + 38 * $u), ($y + 40 * $u))),
        (New-Object System.Drawing.PointF(($x + 38 * $u), ($y + 50 * $u))),
        (New-Object System.Drawing.PointF(($x + 14 * $u), ($y + 50 * $u)))))
    $brush.Dispose()

    # Crisp flat-ended soundwave arcs centred at (30.5, 26.5), radii 9 and 17, sweep -48..+48 deg
    $pen = New-Object System.Drawing.Pen($color, (4.6 * $u))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $g.DrawArc($pen, ($x + 21.5 * $u), ($y + 17.5 * $u), (18 * $u), (18 * $u), -48, 96)
    $g.DrawArc($pen, ($x + 13.5 * $u), ($y + 9.5 * $u), (34 * $u), (34 * $u), -48, 96)
    $pen.Dispose()
}

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)
    return @($bmp, $g)
}

# Tile / icon: gradient over the whole image (Metro tiles are full-bleed squares), mark in the middle.
# $fill = mark size relative to the shorter side. $plate = $false leaves the background transparent.
function Save-Tile([string]$file, [int]$w, [int]$h, [float]$fill, [bool]$plate, [System.Drawing.Color]$color) {
    $c = New-Canvas $w $h; $bmp = $c[0]; $g = $c[1]
    if ($plate) { Fill-Gradient $g 0 0 $w $h }
    $s = [Math]::Min($w, $h) * $fill
    Draw-Mark $g (($w - $s) / 2) (($h - $s) / 2) $s $color
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
}

# Square plate with the mark, followed by the name (splash screen, in-app word mark).
function Save-WithText([string]$file, [int]$w, [int]$h, [System.Drawing.Color]$textColor) {
    $c = New-Canvas $w $h; $bmp = $c[0]; $g = $c[1]
    $s = $h * 0.42
    $font = New-Object System.Drawing.Font("Segoe UI Light", ($s * 0.62), [System.Drawing.GraphicsUnit]::Pixel)
    $text = "lyracord"
    $tw = $g.MeasureString($text, $font).Width
    $total = $s + $s * 0.25 + $tw
    $x = ($w - $total) / 2
    $y = ($h - $s) / 2
    Fill-Gradient $g $x $y $s $s
    Draw-Mark $g $x $y $s ([System.Drawing.Color]::White)
    $brush = New-Object System.Drawing.SolidBrush($textColor)
    $th = $g.MeasureString($text, $font).Height
    $g.DrawString($text, $font, $brush, ($x + $s * 1.25), (($h - $th) / 2))
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
}

$white = [System.Drawing.Color]::White
$dark = [System.Drawing.Color]::FromArgb(255, 32, 34, 37)

# tile kinds: width, height, mark size
$tiles = @{ "LargeTile" = @(310, 310, 0.42); "SmallTile" = @(71, 71, 0.62); "Square150x150Logo" = @(150, 150, 0.5);
            "Wide310x150Logo" = @(310, 150, 0.5); "Square44x44Logo" = @(44, 44, 0.9); "StoreLogo" = @(50, 50, 0.9) }

foreach ($f in Get-ChildItem $store -Filter *.png) {
    $name = $f.BaseName
    $kind = $name.Split('.')[0]
    $scale = 1.0
    if ($name -match 'scale-(\d+)') { $scale = [int]$Matches[1] / 100.0 }

    if ($kind -eq "BadgeLogo") {
        # badge logos must be a white silhouette on transparent
        $px = [int][Math]::Ceiling(24 * $scale)
        Save-Tile $f.FullName $px $px 1.0 $false $white
    }
    elseif ($kind -eq "SplashScreen") {
        # system splash: dark red background (manifest), white name. The ".light" copy is the in-app
        # extended splash for the light theme, so it keeps a dark name.
        $textColor = if ($name -match '\.light\.') { $dark } else { $white }
        Save-WithText $f.FullName ([int][Math]::Ceiling(620 * $scale)) ([int][Math]::Ceiling(300 * $scale)) $textColor
    }
    elseif ($name -match 'targetsize-(\d+)') {
        # app list / taskbar icons: always on the plate, so the white mark is visible on light backgrounds too
        $px = [int]$Matches[1]
        Save-Tile $f.FullName $px $px 0.9 $true $white
    }
    elseif ($tiles.ContainsKey($kind)) {
        $b = $tiles[$kind]
        Save-Tile $f.FullName ([int][Math]::Ceiling($b[0] * $scale)) ([int][Math]::Ceiling($b[1] * $scale)) $b[2] $true $white
    }
    else { Write-Warning "skipped $($f.Name)" }
}

# In-app images (keys DiscordWordMark / DiscordClyde / DiscordLogo in App.xaml point here)
Save-WithText (Join-Path $assets "LyracordWordMark.Dark.png") 640 160 $white
Save-WithText (Join-Path $assets "LyracordWordMark.Light.png") 640 160 $dark
Save-Tile (Join-Path $assets "LyracordLogo.png") 256 256 0.9 $true $white

$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" fill="#7A0E14"/>
  <path d="M14 14 H24 V40 H38 V50 H14 Z" fill="#FFFFFF"/>
  <g fill="none" stroke="#FFFFFF" stroke-width="4.6" stroke-linecap="butt">
    <path d="M36.52 19.81 A9 9 0 0 1 36.52 33.19"/>
    <path d="M41.88 13.87 A17 17 0 0 1 41.88 39.13"/>
  </g>
</svg>
'@
[System.IO.File]::WriteAllText((Join-Path $assets "LyracordLogo.svg"), $svg, (New-Object System.Text.UTF8Encoding($false)))
"done"
