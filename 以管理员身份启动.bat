@echo off
rem Launch DesktopDock elevated (UAC prompt). Double-click this file.
rem Only needed for the optional "scheduled task" autostart / task repair.
setlocal

set "EXE=%LOCALAPPDATA%\Programs\DesktopDock\DesktopDock.exe"
if not exist "%EXE%" set "EXE=%~dp0dist\DesktopDock.exe"
if not exist "%EXE%" set "EXE=%~dp0bin\Release\net10.0-windows\DesktopDock.exe"
if not exist "%EXE%" (
    echo DesktopDock.exe not found. Run: dotnet publish -c Release -o dist
    pause
    exit /b 1
)

powershell -NoProfile -Command "Start-Process -FilePath '%EXE%' -Verb RunAs"
