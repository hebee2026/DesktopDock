param(
    [int]$X = 0,
    [int]$Y = 384,
    [int]$W = 630,
    [int]$H = 1320,
    [string]$Out = "shots\panel.png"
)

# 先声明 DPI 感知，否则 GDI 抓屏被系统缩放，量出来的坐标全是错的
$cs = 'using System;using System.Runtime.InteropServices;public class DpiFix{[DllImport("user32.dll")]public static extern bool SetProcessDpiAwarenessContext(IntPtr v);}'
Add-Type -TypeDefinition $cs -ErrorAction SilentlyContinue
try { [void][DpiFix]::SetProcessDpiAwarenessContext([IntPtr](-4)) } catch { }

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$target = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$dir = Split-Path -Parent $target
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen((New-Object System.Drawing.Point $X, $Y), [System.Drawing.Point]::Empty, (New-Object System.Drawing.Size $W, $H))
$g.Dispose()
$bmp.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output ("saved {0} ({1}x{2} from {3},{4})" -f $target, $W, $H, $X, $Y)
