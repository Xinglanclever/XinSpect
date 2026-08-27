# Generates the XinSpect app icon (app.ico) with System.Drawing.
# ASCII-only. Concept "曦覽 = dawn + survey": a dawn gauge -- a rising-sun disc
# cradled by an accent arc over a horizon, on a dark tile matching the app theme.
# Vector-rendered at each target size (crisper than downscaling), packed as a
# PNG-payload .ico (Vista+). NO third-party art; original geometry only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes   = @(256, 128, 64, 48, 32, 16)
$outDir  = 'C:\Users\Administrator\XinSpect\Assets'
$outIco  = Join-Path $outDir 'app.ico'
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

function New-IconBitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- rounded-square tile with a dark dawn-sky vertical gradient ---
    $pad    = [double]$S * 0.055
    $rectF  = New-Object System.Drawing.RectangleF($pad, $pad, ($S - 2*$pad), ($S - 2*$pad))
    $radius = [double]$S * 0.22
    $path   = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2.0 * $radius
    $path.AddArc([single]$rectF.Left,               [single]$rectF.Top,                  [single]$d, [single]$d, 180, 90)
    $path.AddArc([single]($rectF.Right - $d),       [single]$rectF.Top,                  [single]$d, [single]$d, 270, 90)
    $path.AddArc([single]($rectF.Right - $d),       [single]($rectF.Bottom - $d),        [single]$d, [single]$d,   0, 90)
    $path.AddArc([single]$rectF.Left,               [single]($rectF.Bottom - $d),        [single]$d, [single]$d,  90, 90)
    $path.CloseFigure()

    $tileTop = [System.Drawing.Color]::FromArgb(255, 0x1b, 0x27, 0x40)  # deep dawn indigo (theme card header)
    $tileBot = [System.Drawing.Color]::FromArgb(255, 0x0b, 0x0b, 0x0b)  # near-black base
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rectF, $tileTop, $tileBot, 90.0)
    $g.FillPath($lg, $path)

    # --- horizon baseline ---
    $cx = [double]$S / 2.0
    $horizonY = [double]$S * 0.62
    $inset = [double]$S * 0.20
    $penH = New-Object System.Drawing.Pen(([System.Drawing.Color]::FromArgb(150, 0x38, 0x38, 0x35)), [single]([math]::Max(1.0, $S*0.016)))
    $g.DrawLine($penH, [single]($inset), [single]$horizonY, [single]($S - $inset), [single]$horizonY)

    # --- accent gauge arc cradling the sun (semicircle opening downward) ---
    $arcR = [double]$S * 0.30
    $arcRect = New-Object System.Drawing.RectangleF([single]($cx - $arcR), [single]($horizonY - $arcR), [single]($arcR*2), [single]($arcR*2))
    $penW = [math]::Max(1.4, $S * 0.045)
    $accent   = [System.Drawing.Color]::FromArgb(255, 0x4c, 0x96, 0xf0)
    $accentDim= [System.Drawing.Color]::FromArgb(255, 0x1c, 0x5c, 0xab)
    $arcBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($arcRect, $accentDim, $accent, 0.0)
    $penArc = New-Object System.Drawing.Pen($arcBrush, [single]$penW)
    $penArc.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penArc.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($penArc, $arcRect, 180, 180)

    # --- rising sun disc (warm dawn gradient), sitting on the horizon ---
    $sunR = [double]$S * 0.155
    $sunRect = New-Object System.Drawing.RectangleF([single]($cx - $sunR), [single]($horizonY - $sunR), [single]($sunR*2), [single]($sunR*2))
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gp.AddEllipse($sunRect)
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($gp)
    $pgb.CenterColor    = [System.Drawing.Color]::FromArgb(255, 0xff, 0xe6, 0xb0)  # warm core
    $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 0xfa, 0xb2, 0x19)) # amber rim
    $pgb.CenterPoint = New-Object System.Drawing.PointF([single]$cx, [single]($horizonY - $sunR*0.35))
    # clip the sun to above the horizon so it "rises"
    $clip = New-Object System.Drawing.Drawing2D.GraphicsPath
    $clip.AddRectangle((New-Object System.Drawing.RectangleF(0, 0, [single]$S, [single]$horizonY)))
    $g.SetClip($clip)
    $g.FillEllipse($pgb, $sunRect)
    $g.ResetClip()

    # --- three short dawn rays above the sun ---
    $penRay = New-Object System.Drawing.Pen(([System.Drawing.Color]::FromArgb(230, 0xff, 0xd2, 0x76)), [single]([math]::Max(1.0, $S*0.02)))
    $penRay.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penRay.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    if ($S -ge 32) {
        foreach ($ang in @(250.0, 270.0, 290.0)) {
            $rad = $ang * [math]::PI / 180.0
            $r0 = $sunR * 1.25; $r1 = $sunR * 1.7
            $x0 = $cx + $r0 * [math]::Cos($rad); $y0 = $horizonY + $r0 * [math]::Sin($rad)
            $x1 = $cx + $r1 * [math]::Cos($rad); $y1 = $horizonY + $r1 * [math]::Sin($rad)
            $g.DrawLine($penRay, [single]$x0, [single]$y0, [single]$x1, [single]$y1)
        }
    }

    $g.Dispose()
    return $bmp
}

# render + collect PNG bytes
$pngList = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngList += ,($ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}

# --- assemble .ico (PNG payloads) ---
$fs = [System.IO.File]::Create($outIco)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type = icon
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bytes = $pngList[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }   # 0 means 256 in ICO dir
    $bw.Write([Byte]$dim)     # width
    $bw.Write([Byte]$dim)     # height
    $bw.Write([Byte]0)        # palette
    $bw.Write([Byte]0)        # reserved
    $bw.Write([UInt16]1)      # color planes
    $bw.Write([UInt16]32)     # bpp
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $pngList) { $bw.Write($bytes) }
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Output ("ICO_WRITTEN=" + $outIco + "  bytes=" + (Get-Item $outIco).Length)
