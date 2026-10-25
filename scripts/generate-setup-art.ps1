[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\assets\installer'),
    # Compare freshly rendered pixels with the committed images instead of writing them.
    [switch]$Check
)

# Renders the installer window art that installer\SetupTheme.iss places behind Inno
# Setup's own controls: the glowing 600x340 backdrop, the rounded panels, the progress
# track, the status icons and the frameless caption glyphs. The geometry and palette
# are WebPier's scripts/icongen/setupart.go (itself CycleArc's SetupPalette.Dark),
# ported to .NET so SIDETALK needs no Go toolchain. The brand mark and logo are
# SIDETALK's own two-bubble icon, scaled from the 256px frame of Sidetalk.ico, so the
# installer follows scripts\generate-app-icon.ps1 instead of keeping a second copy.
#
# Every pixel comes from the numbers below. The committed PNGs are regenerated with
#   pwsh -File scripts/generate-setup-art.ps1
# and tests\Sidetalk.InstallerChecks runs it with -Check.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('SidetalkSetupArt' -as [type])) {
    Add-Type -TypeDefinition @'
using System;

public static class SidetalkSetupArt {
    // Overlays are drawn once at 3x; Inno shrinks them into the 96-DPI box the
    // script asks for, which keeps edges smooth from 100% to 300%.
    public const int Scale = 3;
    public const int Width = 600, Height = 340;

    struct Rgb { public double R, G, B; public Rgb(int v) { R = (v >> 16) & 0xFF; G = (v >> 8) & 0xFF; B = v & 0xFF; } }

    static readonly Rgb Surface = new Rgb(0x141416);
    static readonly Rgb SurfaceBottom = new Rgb(0x0E0E10);
    static readonly Rgb TextSecondary = new Rgb(0xA9A9B3);
    static readonly Rgb Accent = new Rgb(0x8DBBFF);
    static readonly Rgb Overlay = new Rgb(0xFFFFFF);
    static readonly Rgb Track = new Rgb(0x2A2A30);
    static readonly Rgb Panel = new Rgb(0x1B1B1F);
    static readonly Rgb Danger = new Rgb(0xFF7B7B);

    // Light pools rising from the bottom edge: amber, magenta, teal. Centre and
    // radii are fractions of the window.
    static readonly double[][] Glows = {
        new double[] { 0.15, 1.10, 0.42, 0.62, 0xE7, 0x9B, 0x3A, 0.30 },
        new double[] { 0.50, 1.16, 0.38, 0.60, 0xD9, 0x34, 0x6C, 0.26 },
        new double[] { 0.86, 1.10, 0.42, 0.62, 0x1F, 0xB3, 0xD6, 0.30 },
    };

    // Ordered dither so the dark gradient never bands on an 8-bit display.
    static readonly double[] Bayer4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

    static double Clamp01(double v) { return Math.Max(0, Math.Min(1, v)); }
    static byte ClampByte(double v) { return (byte)Math.Max(0, Math.Min(255, Math.Round(v, MidpointRounding.AwayFromZero))); }

    // Straight-alpha BGRA, the byte order of a 32bpp ARGB GDI+ bitmap.
    public static byte[] Backdrop(int scale) {
        int w = Width * scale, h = Height * scale;
        var pix = new byte[w * h * 4];
        for (int py = 0; py < h; py++) {
            double fy = (py + 0.5) / h;
            double br = Math.Round(Surface.R + (SurfaceBottom.R - Surface.R) * fy, MidpointRounding.AwayFromZero);
            double bg = Math.Round(Surface.G + (SurfaceBottom.G - Surface.G) * fy, MidpointRounding.AwayFromZero);
            double bb = Math.Round(Surface.B + (SurfaceBottom.B - Surface.B) * fy, MidpointRounding.AwayFromZero);
            for (int px = 0; px < w; px++) {
                double fx = (px + 0.5) / w;
                double r = br, g = bg, b = bb;
                foreach (var glow in Glows) {
                    double dx = (fx - glow[0]) / glow[2];
                    double dy = (fy - glow[1]) / glow[3];
                    double s = Math.Exp(-2.4 * (dx * dx + dy * dy)) * glow[7];
                    if (s < 0.0005) continue;
                    // Screen blend: light adds without clipping to a flat colour.
                    r = 255 - (255 - r) * (1 - s * glow[4] / 255);
                    g = 255 - (255 - g) * (1 - s * glow[5] / 255);
                    b = 255 - (255 - b) * (1 - s * glow[6] / 255);
                }
                double dither = (Bayer4[(py & 3) * 4 + (px & 3)] + 0.5) / 16 - 0.5;
                int i = (py * w + px) * 4;
                pix[i] = ClampByte(b + dither);
                pix[i + 1] = ClampByte(g + dither);
                pix[i + 2] = ClampByte(r + dither);
                pix[i + 3] = 255;
            }
        }
        return pix;
    }

    // Premultiplied accumulation so shapes layer with correct antialiased edges.
    sealed class Canvas {
        public readonly int W, H;
        readonly double[] r, g, b, a;
        public Canvas(int w, int h) { W = w; H = h; r = new double[w * h]; g = new double[w * h]; b = new double[w * h]; a = new double[w * h]; }

        // Coverage from a signed distance at the pixel centre: a one-pixel
        // antialiased edge with no supersampling noise.
        public void Paint(Rgb col, double opacity, Func<double, double, double> distance) {
            for (int py = 0; py < H; py++) {
                double y = py + 0.5;
                for (int px = 0; px < W; px++) {
                    double coverage = Clamp01(0.5 - distance(px + 0.5, y));
                    if (coverage <= 0) continue;
                    double alpha = coverage * opacity;
                    int i = py * W + px;
                    r[i] = col.R / 255 * alpha + r[i] * (1 - alpha);
                    g[i] = col.G / 255 * alpha + g[i] * (1 - alpha);
                    b[i] = col.B / 255 * alpha + b[i] * (1 - alpha);
                    a[i] = alpha + a[i] * (1 - alpha);
                }
            }
        }

        public byte[] Pixels() {
            var pix = new byte[W * H * 4];
            for (int i = 0; i < a.Length; i++) {
                if (a[i] <= 0) continue;
                pix[i * 4] = ClampByte(b[i] / a[i] * 255);
                pix[i * 4 + 1] = ClampByte(g[i] / a[i] * 255);
                pix[i * 4 + 2] = ClampByte(r[i] / a[i] * 255);
                pix[i * 4 + 3] = ClampByte(a[i] * 255);
            }
            return pix;
        }
    }

    static Func<double, double, double> RoundedBox(double x0, double y0, double x1, double y1, double radius) {
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, hx = (x1 - x0) / 2 - radius, hy = (y1 - y0) / 2 - radius;
        return (x, y) => {
            double qx = Math.Abs(x - cx) - hx, qy = Math.Abs(y - cy) - hy;
            double outside = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2));
            return outside + Math.Min(Math.Max(qx, qy), 0) - radius;
        };
    }

    static Func<double, double, double> Ring(Func<double, double, double> shape, double width) {
        return (x, y) => Math.Abs(shape(x, y)) - width / 2;
    }

    static Func<double, double, double> Circle(double cx, double cy, double radius) {
        return (x, y) => Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius;
    }

    // A capsule: a line with round caps.
    static Func<double, double, double> Segment(double ax, double ay, double bx, double by, double width) {
        return (x, y) => {
            double px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
            double t = Clamp01((px * dx + py * dy) / (dx * dx + dy * dy));
            return Math.Sqrt(Math.Pow(px - dx * t, 2) + Math.Pow(py - dy * t, 2)) - width / 2;
        };
    }

    // kind: "location" (faint translucent veil), "detail" (opaque, the read-only
    // text box inside uses the same colour) or "track".
    public static byte[] PanelArt(string kind, double width, double height, double radius) {
        double s = Scale;
        var c = new Canvas((int)(width * s), (int)(height * s));
        Rgb fill; double fillOpacity, strokeOpacity;
        switch (kind) {
            case "location": fill = Overlay; fillOpacity = 0.045; strokeOpacity = 0.09; break;
            case "detail": fill = Panel; fillOpacity = 1; strokeOpacity = 0.07; break;
            case "track": fill = Track; fillOpacity = 1; strokeOpacity = 0; break;
            default: throw new ArgumentException(kind);
        }
        c.Paint(fill, fillOpacity, RoundedBox(0, 0, width * s, height * s, radius * s));
        // The stroke sits just inside the edge so it is never clipped.
        var inner = RoundedBox(0.5 * s, 0.5 * s, (width - 0.5) * s, (height - 0.5) * s, (radius - 0.5) * s);
        if (strokeOpacity > 0) c.Paint(Overlay, strokeOpacity, Ring(inner, s));
        return c.Pixels();
    }

    // Completion check or failure mark inside a 44-unit circle.
    public static byte[] StatusIcon(bool failed) {
        const double box = 44.0;
        double s = Scale;
        var c = new Canvas((int)(box * s), (int)(box * s));
        double center = box / 2 * s, radius = (box / 2 - 1) * s;
        Rgb accent = failed ? Danger : Accent;
        c.Paint(accent, 0.13, Circle(center, center, radius));
        c.Paint(accent, 0.85, Ring(Circle(center, center, radius - 0.75 * s), 1.5 * s));
        double stroke = 2.4 * s;
        if (failed) {
            c.Paint(accent, 1, Segment(center, center - 0.42 * radius, center, center + 0.10 * radius, stroke));
            c.Paint(accent, 1, Circle(center, center + 0.38 * radius, 1.6 * s));
        } else {
            c.Paint(accent, 1, Segment(center - 0.36 * radius, center + 0.02 * radius, center - 0.10 * radius, center + 0.28 * radius, stroke));
            c.Paint(accent, 1, Segment(center - 0.10 * radius, center + 0.28 * radius, center + 0.38 * radius, center - 0.24 * radius, stroke));
        }
        return c.Pixels();
    }

    // Minimise or close glyph inside its 36x30 hit box.
    public static byte[] CaptionGlyph(bool close) {
        const double width = 36.0, height = 30.0;
        double s = Scale;
        var c = new Canvas((int)(width * s), (int)(height * s));
        double cx = width / 2 * s, cy = height / 2 * s, half = 5 * s, stroke = 1.1 * s;
        if (close) {
            c.Paint(TextSecondary, 1, Segment(cx - half, cy - half, cx + half, cy + half, stroke));
            c.Paint(TextSecondary, 1, Segment(cx - half, cy + half, cx + half, cy - half, stroke));
        } else {
            c.Paint(TextSecondary, 1, Segment(cx - half, cy, cx + half, cy, stroke));
        }
        return c.Pixels();
    }
}
'@
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$iconPath = Join-Path $repo 'src\Sidetalk.App\Assets\Sidetalk.ico'
$outputFull = [IO.Path]::GetFullPath($OutputDir)
$scale = [SidetalkSetupArt]::Scale

function New-Bitmap([int]$Width, [int]$Height, [byte[]]$Pixels) {
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $data = $bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $Width, $Height), [Drawing.Imaging.ImageLockMode]::WriteOnly, $bitmap.PixelFormat)
    try {
        if ($data.Stride -ne $Width * 4) { throw 'Unexpected bitmap stride.' }
        [Runtime.InteropServices.Marshal]::Copy($Pixels, 0, $data.Scan0, $Pixels.Length)
    } finally { $bitmap.UnlockBits($data) }
    return $bitmap
}

function Get-Pixels([Drawing.Bitmap]$Bitmap) {
    $rect = [Drawing.Rectangle]::new(0, 0, $Bitmap.Width, $Bitmap.Height)
    $data = $Bitmap.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $pixels = [byte[]]::new($Bitmap.Width * $Bitmap.Height * 4)
        if ($data.Stride -ne $Bitmap.Width * 4) { throw 'Unexpected bitmap stride.' }
        [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
        return ,$pixels
    } finally { $Bitmap.UnlockBits($data) }
}

# The largest PNG frame of Sidetalk.ico, the source of the brand mark and logo.
function Get-IconSource {
    $bytes = [IO.File]::ReadAllBytes($iconPath)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = $null
    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + 16 * $i
        $size = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
        if ($null -eq $best -or $size -gt $best.Size) {
            $best = @{ Size = $size; Length = [BitConverter]::ToInt32($bytes, $entry + 8); Offset = [BitConverter]::ToInt32($bytes, $entry + 12) }
        }
    }
    $stream = [IO.MemoryStream]::new($bytes, $best.Offset, $best.Length)
    return [Drawing.Bitmap]::new($stream)
}

function New-BrandBitmap([Drawing.Bitmap]$Source, [int]$Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        try {
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($Source, [Drawing.Rectangle]::new(0, 0, $Size, $Size), 0, 0, $Source.Width, $Source.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
        } finally { $attributes.Dispose() }
    } finally { $graphics.Dispose() }
    return $bitmap
}

# File name, 96-DPI size of the box SetupTheme.iss stretches it into, and renderer.
# Names and pixel sizes are the contract with installer\SetupTheme.iss.
$icon = Get-IconSource
try {
    $art = [ordered]@{
        'setup-brand.png'          = { New-BrandBitmap $icon (18 * $scale) }
        'setup-logo.png'           = { New-BrandBitmap $icon (44 * $scale) }
        'setup-done.png'           = { New-Bitmap (44 * $scale) (44 * $scale) ([SidetalkSetupArt]::StatusIcon($false)) }
        'setup-failed.png'         = { New-Bitmap (44 * $scale) (44 * $scale) ([SidetalkSetupArt]::StatusIcon($true)) }
        'setup-panel-location.png' = { New-Bitmap (440 * $scale) (38 * $scale) ([SidetalkSetupArt]::PanelArt('location', 440, 38, 9)) }
        'setup-panel-detail.png'   = { New-Bitmap (520 * $scale) (140 * $scale) ([SidetalkSetupArt]::PanelArt('detail', 520, 140, 10)) }
        'setup-track.png'          = { New-Bitmap (440 * $scale) (4 * $scale) ([SidetalkSetupArt]::PanelArt('track', 440, 4, 2)) }
        'setup-minimize.png'       = { New-Bitmap (36 * $scale) (30 * $scale) ([SidetalkSetupArt]::CaptionGlyph($false)) }
        'setup-close.png'          = { New-Bitmap (36 * $scale) (30 * $scale) ([SidetalkSetupArt]::CaptionGlyph($true)) }
        'setup-backdrop-1x.png'    = { New-Bitmap 600 340 ([SidetalkSetupArt]::Backdrop(1)) }
        'setup-backdrop-2x.png'    = { New-Bitmap 1200 680 ([SidetalkSetupArt]::Backdrop(2)) }
    }

    $stale = [Collections.Generic.List[string]]::new()
    if (-not $Check) { New-Item -ItemType Directory -Path $outputFull -Force | Out-Null }
    foreach ($entry in $art.GetEnumerator()) {
        $path = Join-Path $outputFull $entry.Key
        $bitmap = & $entry.Value
        try {
            if ($Check) {
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $stale.Add("$($entry.Key) is missing"); continue }
                $committed = [Drawing.Bitmap]::new($path)
                try {
                    if ($committed.Width -ne $bitmap.Width -or $committed.Height -ne $bitmap.Height) {
                        $stale.Add("$($entry.Key) is $($committed.Width)x$($committed.Height), want $($bitmap.Width)x$($bitmap.Height)")
                        continue
                    }
                    $want = Get-Pixels $bitmap
                    $have = Get-Pixels $committed
                    # GDI+ resampling of the brand mark may differ by a rounding step between Windows builds.
                    $tolerance = if ($entry.Key -like 'setup-brand.png' -or $entry.Key -like 'setup-logo.png') { 2 } else { 0 }
                    for ($i = 0; $i -lt $want.Length; $i++) {
                        if ([Math]::Abs([int]$want[$i] - [int]$have[$i]) -gt $tolerance) { $stale.Add("$($entry.Key) differs from the generator at byte $i"); break }
                    }
                } finally { $committed.Dispose() }
            } else {
                $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
                Write-Host "Wrote $path ($($bitmap.Width)x$($bitmap.Height))"
            }
        } finally { $bitmap.Dispose() }
    }
    if ($Check) {
        $extra = @(Get-ChildItem -LiteralPath $outputFull -File -Filter 'setup-*.png' -ErrorAction SilentlyContinue | Where-Object { -not $art.Contains($_.Name) })
        foreach ($file in $extra) { $stale.Add("$($file.Name) is not part of the theme contract") }
        if ($stale.Count) { throw ("Installer art is stale; run pwsh -File scripts/generate-setup-art.ps1:`n  " + ($stale -join "`n  ")) }
        Write-Host "Installer art matches the generator ($($art.Count) images)."
    }
} finally { $icon.Dispose() }
