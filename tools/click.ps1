param(
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [int]$DelayMs = 250
)

# 关键：先切到 PerMonitorV2，否则 SetCursorPos/mouse_event 的坐标会被系统按 DPI 缩放
$cs = 'using System;using System.Runtime.InteropServices;public class ClickApi{[DllImport("user32.dll")]public static extern bool SetProcessDpiAwarenessContext(IntPtr v);[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint dx,uint dy,uint d,IntPtr e);}'
Add-Type -TypeDefinition $cs
try { [void][ClickApi]::SetProcessDpiAwarenessContext([IntPtr](-4)) } catch { }

[void][ClickApi]::SetCursorPos($X, $Y)
Start-Sleep -Milliseconds $DelayMs
[ClickApi]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 60
[ClickApi]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Write-Output ("clicked {0},{1}" -f $X, $Y)
