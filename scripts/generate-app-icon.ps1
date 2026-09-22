param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\Sidetalk.App\Assets\Sidetalk.ico')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# SIDETALK's icon is the same two-bubble mark used in MainWindow.xaml. The
# PNG frames are rendered from those coordinates instead of copying a
# screenshot, so the transparent ICO stays sharp at every shell size.
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$accent = [System.Drawing.Color]::FromArgb(255, 213, 250, 97) # App.xaml AccentBrush #D5FA61
$canvas = [System.Drawing.Color]::FromArgb(255, 20, 22, 25) # App.xaml CanvasBrush #141619

function New-IconPng {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $bitmap.SetResolution(96, 96)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            # Keep the x40 outline stroke inside the transparent canvas with a 2px shell margin.
            $scale = [single](($Size - 4.0) / 41.0)
            $transform = [System.Drawing.Drawing2D.Matrix]::new($scale, 0, 0, $scale, 2, 2)
            $graphics.Transform = $transform

            $filled = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $filled.AddLine(3, 7, 26, 7)
            $filled.AddBezier(26, 7, 28.2, 7, 30, 8.8, 30, 11)
            $filled.AddLine(30, 11, 30, 23)
            $filled.AddBezier(30, 23, 30, 25.2, 28.2, 27, 26, 27)
            $filled.AddLine(26, 27, 13, 27)
            $filled.AddLine(13, 27, 6, 33)
            $filled.AddLine(6, 33, 6, 27)
            $filled.AddLine(6, 27, 3, 27)
            $filled.AddBezier(3, 27, 1.2, 27, 0, 25.2, 0, 23)
            $filled.AddLine(0, 23, 0, 11)
            $filled.AddBezier(0, 11, 0, 8.8, 1.2, 7, 3, 7)
            $filled.CloseFigure()
            $filledBrush = [System.Drawing.SolidBrush]::new($accent)
            try { $graphics.FillPath($filledBrush, $filled) } finally { $filledBrush.Dispose() }

            $outline = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $outline.AddLine(33, 14, 37, 14)
            $outline.AddBezier(37, 14, 38.8, 14, 40, 15.6, 40, 18)
            $outline.AddLine(40, 18, 40, 31)
            $outline.AddBezier(40, 31, 40, 32.8, 38.8, 34, 37, 34)
            $outline.AddLine(37, 34, 35, 34)
            $outline.AddLine(35, 34, 35, 39)
            $outline.AddLine(35, 39, 28, 34)
            $outline.AddLine(28, 34, 18, 34)
            $outlinePen = [System.Drawing.Pen]::new($accent, 2)
            try { $graphics.DrawPath($outlinePen, $outline) } finally { $outlinePen.Dispose() }

            $eyes = [System.Drawing.Pen]::new($canvas, 3)
            try {
                $eyes.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $eyes.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $graphics.DrawLine($eyes, 9, 15, 13, 15)
                $graphics.DrawLine($eyes, 18, 15, 22, 15)
            }
            finally { $eyes.Dispose() }

            $transform.Dispose()

            $stream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                return ,$stream.ToArray()
            }
            finally { $stream.Dispose() }
        }
        finally { $graphics.Dispose() }
    }
    finally { $bitmap.Dispose() }
}

$frames = @(
    foreach ($size in $sizes) {
        [pscustomobject]@{ Size = $size; Data = (New-IconPng -Size $size) }
    }
)

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$parent = [System.IO.Path]::GetDirectoryName($resolvedOutput)
[System.IO.Directory]::CreateDirectory($parent) | Out-Null
$stream = [System.IO.File]::Open($resolvedOutput, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0) # reserved
        $writer.Write([uint16]1) # icon type
        $writer.Write([uint16]$frames.Count)

        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0) # palette entries
            $writer.Write([byte]0) # reserved
            $writer.Write([uint16]1) # color planes
            $writer.Write([uint16]32) # bits per pixel
            $writer.Write([uint32]$frame.Data.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Data.Length
        }

        foreach ($frame in $frames) { $writer.Write($frame.Data) }
    }
    finally { $writer.Dispose() }
}
finally { $stream.Dispose() }

Write-Output "Generated $resolvedOutput ($($frames.Count) PNG frames: $($sizes -join ', '))."
