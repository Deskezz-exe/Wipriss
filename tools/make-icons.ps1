# Builds the .ico files from the logo geometry (assets/logo/wipriss.svg).
#   wipriss.ico     app icon: white mark on a dark rounded square
#   tray-light.ico  white mark for a dark taskbar
#   tray-dark.ico   dark mark for a light taskbar
# Run with Windows PowerShell 5.1:  powershell -ExecutionPolicy Bypass -File tools\make-icons.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Wipriss.App\Assets'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

# x, y, w, h, corner radius - in the 1024x1024 logo space; the mark spans 112..912
$shapes = @(
    @(112, 112, 288, 288, 30), @(624, 112, 288, 288, 30),
    @(112, 624, 288, 288, 30), @(624, 624, 288, 288, 30),
    @(492, 306, 40, 412, 0),   @(306, 492, 412, 40, 0),
    @(402, 402, 220, 220, 34)
)

function New-RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0.01) { $p.AddRectangle((New-Object System.Drawing.RectangleF ([single]$x), ([single]$y), ([single]$w), ([single]$h))); return $p }
    $d = [single](2 * $r)
    $p.AddArc([single]$x, [single]$y, $d, $d, 180, 90)
    $p.AddArc([single]($x + $w - $d), [single]$y, $d, $d, 270, 90)
    $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), $d, $d, 0, 90)
    $p.AddArc([single]$x, [single]($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Png([int]$size, [System.Drawing.Color]$fg, [bool]$withBackground) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $markShare = 0.94
    if ($withBackground) {
        $bg = New-RoundRect 0 0 $size $size ($size * 0.22)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 17, 17, 19))), $bg)
        $markShare = 0.62
    }
    $scale = $size * $markShare / 800.0
    $offset = ($size - 800.0 * $scale) / 2.0 - 112.0 * $scale
    $brush = New-Object System.Drawing.SolidBrush $fg

    foreach ($s in $shapes) {
        $x = $offset + $s[0] * $scale; $y = $offset + $s[1] * $scale
        $w = $s[2] * $scale; $h = $s[3] * $scale; $r = $s[4] * $scale
        # keep thin bars at least 1 px so they don't vanish at 16-24 px
        if ($w -lt 1.0) { $x -= (1.0 - $w) / 2; $w = 1.0 }
        if ($h -lt 1.0) { $y -= (1.0 - $h) / 2; $h = 1.0 }
        $g.FillPath($brush, (New-RoundRect $x $y $w $h $r))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes, [System.Drawing.Color]$fg, [bool]$withBackground) {
    $images = foreach ($s in $sizes) { , (New-Png $s $fg $withBackground) }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$images[$i].Length); $bw.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $bw.Write([byte[]]$img) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    "{0}  {1} sizes  {2:N1} KB" -f (Split-Path $path -Leaf), $sizes.Count, ($ms.Length / 1KB)
}

$white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$dark  = [System.Drawing.Color]::FromArgb(255, 26, 26, 28)
Write-Ico (Join-Path $OutDir 'wipriss.ico')    @(16, 20, 24, 32, 40, 48, 64, 128, 256) $white $true
Write-Ico (Join-Path $OutDir 'tray-light.ico') @(16, 20, 24, 32, 40, 48, 64) $white $false
Write-Ico (Join-Path $OutDir 'tray-dark.ico')  @(16, 20, 24, 32, 40, 48, 64) $dark  $false
