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
#   Folder: the app (a filled folder with its tab); Size: three bars (folder sizes); Clean: a recycle bin.
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
    switch ($Glyph) {
        'Folder' {
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
        'Size' {
            $g.DrawLine($pen, (P 0.30 0.76), (P 0.30 0.52))
            $g.DrawLine($pen, (P 0.50 0.76), (P 0.50 0.24))
            $g.DrawLine($pen, (P 0.70 0.76), (P 0.70 0.40))
        }
        'Clean' {
            $g.DrawLine($pen, (P 0.22 0.30), (P 0.78 0.30))
            $g.DrawLines($pen, [System.Drawing.PointF[]]@((P 0.40 0.30), (P 0.40 0.20), (P 0.60 0.20), (P 0.60 0.30)))
            $g.DrawLines($pen, [System.Drawing.PointF[]]@((P 0.30 0.30), (P 0.34 0.80), (P 0.66 0.80), (P 0.70 0.30)))
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

# ICO file made of PNG-compressed entries (supported since Windows Vista). The small sizes (taskbar, Start menu,
# File Explorer) fill the whole square, like other Windows 11 app icons; from 48 px on, a 4 % margin is kept.
function Save-Ico([int[]]$sizes, [string]$path, [string]$Glyph = 'Folder') {
    $images = foreach ($size in $sizes) {
        $b = if ($size -le 40) { New-IconBitmap $size 0 -Hinted -Glyph $Glyph } else { New-IconBitmap $size -Glyph $Glyph }
        , (Get-PngBytes $b)
        $b.Dispose()
    }
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

Write-Host 'Generated icons:'
Save-Ico @(16, 20, 24, 32, 40, 48, 64, 256) (Join-Path $root 'src\FolderTools\Assets\FolderTools.ico')
# Sub-entries of "FolderTools ›" in the context menu (resources of FolderToolsMenu.dll).
Save-Ico @(16, 20, 24, 32, 40, 48) (Join-Path $root 'src\FolderTools.Shell\SizeMenu.ico') 'Size'
Save-Ico @(16, 20, 24, 32, 40, 48) (Join-Path $root 'src\FolderTools.Shell\CleanMenu.ico') 'Clean'
Save-Png 44 (Join-Path $root 'package\Assets\Square44x44Logo.png')
Save-Png 150 (Join-Path $root 'package\Assets\Square150x150Logo.png') 0.2
Save-Png 50 (Join-Path $root 'package\Assets\StoreLogo.png')
Save-Png 256 (Join-Path $root 'docs\icon-256.png')
