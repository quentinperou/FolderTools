<#
.SYNOPSIS
    Generates the application icon (.ico), the context menu icons (.ico) and the MSIX package logos (.png).
.DESCRIPTION
    The generated files are committed: run this script again only to change the design.
    Requires Windows PowerShell 5.1 (System.Drawing).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot

function New-RoundedRectPath([System.Drawing.RectangleF]$r, [float]$radius) {
    $d = $radius * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Rounded square in the yellow of File Explorer folders, with a white line drawing:
#   Folder: the app (a filled folder with its tab); Size: three bars (folder sizes); Clean: the folder with a cross;
#   Color: three dots (the folder color sub-menu).
# -Hinted (small sizes): lines of a whole number of pixels, snapped to the pixel grid, so that they stay sharp.
function New-IconBitmap([int]$size, [float]$padding = 0.04, [switch]$Hinted, [string]$Glyph = 'Folder') {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [float]($size * $padding)
    $s = [float]($size - 2 * $m)
    $rect = New-Object System.Drawing.RectangleF $m, $m, $s, $s
    $bg = New-RoundedRectPath $rect ([float]($s * 0.22))
    $c1 = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xC4, 0x3D)
    $c2 = [System.Drawing.Color]::FromArgb(255, 0xEC, 0x8C, 0x00)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, ([float]45)
    $g.FillPath($brush, $bg)

    $w = if ($Hinted) { [float][Math]::Max(2, [Math]::Round($s * 0.09)) } else { [float][Math]::Max(1.6, $s * 0.09) }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    # With PixelOffsetMode.HighQuality pixel i covers [i, i + 1]: an odd width is centered on .5, an even one on whole.
    $snap = if ([int]$w % 2) { 0.5 } else { 0.0 }
    function C([float]$v) {
        $c = $m + $v * $s
        if ($Hinted) { [float]([Math]::Round($c - $snap) + $snap) } else { [float]$c }
    }
    function P([float]$x, [float]$y) { New-Object System.Drawing.PointF (C $x), (C $y) }
    if ($Glyph -in 'Folder', 'Clean') {
        # Filled folder with its tab on the top left. Small sizes: edges on whole pixels, so that they stay sharp.
        function F([float]$v) { $c = $m + $v * $s; if ($Hinted) { [float][Math]::Round($c) } else { [float]$c } }
        $r = [float]([Math]::Max(1, 0.06 * $s)); $d = 2 * $r
        $folder = New-Object System.Drawing.Drawing2D.GraphicsPath
        $folder.AddArc((F 0.18), (F 0.25), $d, $d, 180, 90)
        $folder.AddLine((F 0.18) + $r, (F 0.25), (F 0.42), (F 0.25))
        $folder.AddLine((F 0.42), (F 0.25), (F 0.50), (F 0.33))
        $folder.AddArc((F 0.82) - $d, (F 0.33), $d, $d, 270, 90)
        $folder.AddArc((F 0.82) - $d, (F 0.75) - $d, $d, $d, 0, 90)
        $folder.AddArc((F 0.18), (F 0.75) - $d, $d, $d, 90, 90)
        $folder.CloseFigure()
        $g.FillPath([System.Drawing.Brushes]::White, $folder)
        $folder.Dispose()
    }
    switch ($Glyph) {
        'Size' {
            $g.DrawLine($pen, (P 0.30 0.76), (P 0.30 0.52))
            $g.DrawLine($pen, (P 0.50 0.76), (P 0.50 0.24))
            $g.DrawLine($pen, (P 0.70 0.76), (P 0.70 0.40))
        }
        'Color' {
            # Three dots, like a palette.
            foreach ($dot in @(@(0.36, 0.38), @(0.64, 0.38), @(0.50, 0.64))) {
                $dd = [float]($s * 0.22)
                $g.FillEllipse([System.Drawing.Brushes]::White, [float]($m + $dot[0] * $s - $dd / 2), [float]($m + $dot[1] * $s - $dd / 2), $dd, $dd)
            }
        }
        'Clean' {
            # A cross cut out of the folder, in the background gradient.
            # Up to 20 px: a bigger cross with a thinner line, otherwise it fills in and reads as a blob.
            $small = $size -le 20
            $cut = New-Object System.Drawing.Pen ($brush), $(if ($small) { [float]1.6 } else { $w })
            $cut.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $cut.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $a = if ($small) { 0.36 } else { 0.41 }; $b = 1 - $a; $t = if ($small) { 0.41 } else { 0.45 }; $u = $t + ($b - $a)
            function Q([float]$x, [float]$y) { New-Object System.Drawing.PointF ($m + $x * $s), ($m + $y * $s) }
            $g.DrawLine($cut, (Q $a $t), (Q $b $u))
            $g.DrawLine($cut, (Q $b $t), (Q $a $u))
            $cut.Dispose()
        }
    }
    $pen.Dispose(); $brush.Dispose(); $bg.Dispose(); $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

function Save-Png([int]$size, [string]$path, [float]$padding = 0.04) {
    $bmp = New-IconBitmap $size $padding
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  $path"
}

# ICO file made of PNG-compressed entries (supported since Windows Vista).
function Write-IcoFile([int[]]$sizes, [object[]]$images, [string]$path) {
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $bw.Write([byte[]]$img) }
    $bw.Dispose()
    Write-Host "  $path"
}

# App and menu icons. The small sizes (taskbar, Start menu, File Explorer) fill the whole square, like other
# Windows 11 app icons; from 48 px on, a 4 % margin is kept.
function Save-Ico([int[]]$sizes, [string]$path, [string]$Glyph = 'Folder') {
    $images = foreach ($size in $sizes) {
        $b = if ($size -le 40) { New-IconBitmap $size 0 -Hinted -Glyph $Glyph } else { New-IconBitmap $size -Glyph $Glyph }
        , (Get-PngBytes $b)
        $b.Dispose()
    }
    Write-IcoFile $sizes $images $path
}

# ---------------------------------------------------------------- Folder colors

# Colors of "FolderTools › Color ›", in the order of the menu (installer\FolderTools.iss and dllmain.cpp use the
# same names and order).
$FolderColors = [ordered]@{
    Red = 0xE5484D; Orange = 0xF5891F; Yellow = 0xF5C02B; Green = 0x3DAA5B
    Blue = 0x3B82F6; Purple = 0x8E5BD9; Pink = 0xE85A9E; Gray = 0x8A8F98
}

function Get-Shade([int]$rgb, [double]$k) {
    $r = ($rgb -shr 16) -band 255; $g = ($rgb -shr 8) -band 255; $b = $rgb -band 255
    if ($k -ge 0) { $r += (255 - $r) * $k; $g += (255 - $g) * $k; $b += (255 - $b) * $k }
    else { $r *= (1 + $k); $g *= (1 + $k); $b *= (1 + $k) }
    [System.Drawing.Color]::FromArgb(255, [int]$r, [int]$g, [int]$b)
}

# A folder in the flat style of Windows 11: back with its tab (darker), front panel with a light gradient.
# Up to 48 px the edges sit on whole pixels, so that they stay sharp in the Details and List views.
function New-ColorFolderBitmap([int]$size, [int]$rgb) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $hinted = $size -le 48
    function X([float]$v) { $c = $v * $size; if ($hinted) { [float][Math]::Round($c) } else { [float]$c } }
    $r = [float][Math]::Max(1, [Math]::Round(0.05 * $size)); $d = 2 * $r

    # Proportions of the Windows 11 folder icon (imageres.dll,-3), measured at each size: the colored folders must
    # take the same room as the ordinary ones next to them (Details view, tabs). 16 and 20 px have their own layout,
    # like Windows' own: the whole width at 16 px.
    $L = 0.06; $R = 0.94; $T = 0.135; $B = 0.82; $tab = 0.40; $slope = 0.47; $body = 0.21; $frontTop = 0.29
    if ($size -le 16) { $L = 0; $R = 1; $T = 0.0625; $B = 0.875; $tab = 0.375; $slope = 0.44; $body = 0.1875; $frontTop = 0.25 }
    elseif ($size -le 20) { $L = 0.05; $R = 0.95; $T = 0.15; $B = 0.85; $tab = 0.40; $slope = 0.47; $body = 0.20; $frontTop = 0.30 }

    $back = New-Object System.Drawing.Drawing2D.GraphicsPath
    $back.AddArc((X $L), (X $T), $d, $d, 180, 90)
    $back.AddLine((X $L) + $r, (X $T), (X $tab), (X $T))
    $back.AddLine((X $tab), (X $T), (X $slope), (X $body))
    $back.AddArc((X $R) - $d, (X $body), $d, $d, 270, 90)
    $back.AddArc((X $R) - $d, (X $B) - $d, $d, $d, 0, 90)
    $back.AddArc((X $L), (X $B) - $d, $d, $d, 90, 90)
    $back.CloseFigure()
    $backBrush = New-Object System.Drawing.SolidBrush (Get-Shade $rgb -0.18)
    $g.FillPath($backBrush, $back)

    $frontRect = New-Object System.Drawing.RectangleF (X $L), (X $frontTop), ((X $R) - (X $L)), ((X $B) - (X $frontTop))
    $front = New-RoundedRectPath $frontRect $r
    $frontBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $frontRect, (Get-Shade $rgb 0.22), (Get-Shade $rgb 0), ([float]90)
    $g.FillPath($frontBrush, $front)
    $frontBrush.Dispose(); $front.Dispose(); $backBrush.Dispose(); $back.Dispose(); $g.Dispose()
    return $bmp
}

function Save-ColorIco([string]$name, [int]$rgb, [string]$path) {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    $images = foreach ($size in $sizes) {
        $b = New-ColorFolderBitmap $size $rgb
        , (Get-PngBytes $b)
        $b.Dispose()
    }
    Write-IcoFile $sizes $images $path
}
Write-Host 'Generated icons:'
Save-Ico @(16, 20, 24, 32, 40, 48, 64, 256) (Join-Path $root 'src\FolderTools\Assets\FolderTools.ico')
# Sub-entries of "FolderTools ›" in the context menu (resources of FolderToolsMenu.dll).
Save-Ico @(16, 20, 24, 32, 40, 48) (Join-Path $root 'src\FolderTools.Shell\SizeMenu.ico') 'Size'
Save-Ico @(16, 20, 24, 32, 40, 48) (Join-Path $root 'src\FolderTools.Shell\CleanMenu.ico') 'Clean'
Save-Ico @(16, 20, 24, 32, 40, 48) (Join-Path $root 'src\FolderTools.Shell\ColorMenu.ico') 'Color'
# The colored folders, installed in <app>\Colors and referenced by the desktop.ini of the colored folders.
$colorDir = Join-Path $root 'src\FolderTools.Shell\Colors'
[System.IO.Directory]::CreateDirectory($colorDir) | Out-Null
foreach ($name in $FolderColors.Keys) { Save-ColorIco $name $FolderColors[$name] (Join-Path $colorDir "$name.ico") }
Save-Png 44 (Join-Path $root 'package\Assets\Square44x44Logo.png')
Save-Png 150 (Join-Path $root 'package\Assets\Square150x150Logo.png') 0.2
Save-Png 50 (Join-Path $root 'package\Assets\StoreLogo.png')
Save-Png 256 (Join-Path $root 'docs\icon-256.png')
# Shown inside the app (home, About): WPF's Image would take a small frame of the .ico and blow it up.
Save-Png 256 (Join-Path $root 'src\FolderTools\Assets\FolderTools.png')
