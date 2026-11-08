param(
    [string]$Out = "assets\app.ico"
)

# Build the application icon: dark rounded square + four cream dots (same look as the tray icon).
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$target = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$dir = Split-Path -Parent $target
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$d = 116.0
$path.AddArc(16, 16, $d, $d, 180, 90)
$path.AddArc(124, 16, $d, $d, 270, 90)
$path.AddArc(124, 124, $d, $d, 0, 90)
$path.AddArc(16, 124, $d, $d, 90, 90)
$path.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF 16, 16),
    (New-Object System.Drawing.PointF 240, 240),
    [System.Drawing.Color]::FromArgb(255, 62, 74, 96),
    [System.Drawing.Color]::FromArgb(255, 20, 25, 33))
$g.FillPath($brush, $path)

$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(120, 232, 199, 155)), 7
$g.DrawPath($pen, $path)

$dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245, 232, 199, 155))
$g.FillEllipse($dot, 78, 70, 42, 42)
$g.FillEllipse($dot, 136, 70, 42, 42)
$g.FillEllipse($dot, 78, 128, 42, 42)
$g.FillEllipse($dot, 136, 128, 42, 42)

$g.Dispose()

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$ms.Dispose()
$bmp.Dispose()
$brush.Dispose()
$pen.Dispose()
$dot.Dispose()
$path.Dispose()

# Pack as a single 256x256 PNG-in-ICO (supported since Vista).
$fs = [System.IO.File]::Create($target)
$bw = New-Object System.IO.BinaryWriter -ArgumentList $fs
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]1)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([Byte]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]32)
$bw.Write([UInt32]$png.Length)
$bw.Write([UInt32]22)
$bw.Write($png)
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Write-Output ("icon written: {0} ({1} bytes)" -f $target, (Get-Item $target).Length)
