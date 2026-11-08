@echo off
setlocal
set "DIR=%~dp0"

echo ============================================
echo   桌面收纳盒 - 修复权限
echo ============================================
echo.
echo 目标目录: %DIR%
echo.
echo 正在把该目录及其中文件的完整性标签改成 Medium
echo (Low 标签会让程序被系统降级成低权限：托盘、开机自启都会失效)
echo.

icacls "%DIR%" /T /C /Q /setintegritylevel Medium
if errorlevel 1 (
    echo.
    echo [!] 修改失败。请右键本文件 - 以管理员身份运行后再试。
    echo.
    pause
    exit /b 1
)

echo.
echo 完成。正在启动程序 ...
if exist "%LOCALAPPDATA%\Programs\DesktopDock\DesktopDock.exe" (
    start "" "%LOCALAPPDATA%\Programs\DesktopDock\DesktopDock.exe"
) else if exist "%DIR%dist\DesktopDock.exe" (
    start "" "%DIR%dist\DesktopDock.exe"
) else (
    echo [!] 没找到 DesktopDock.exe
)

timeout /t 3 >nul