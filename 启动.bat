@echo off
rem DesktopDock launcher
rem Prefer the installed copy (%LOCALAPPDATA%\Programs\DesktopDock): it lives in a clean
rem folder and keeps autostart working even if this download folder gets cleaned up
rem or re-labelled by a sandbox tool.
setlocal

if exist "%LOCALAPPDATA%\Programs\DesktopDock\DesktopDock.exe" (
    start "" "%LOCALAPPDATA%\Programs\DesktopDock\DesktopDock.exe"
    exit /b 0
)

cd /d "%~dp0"

if exist "dist\DesktopDock.exe" (
    start "" "dist\DesktopDock.exe"
    exit /b 0
)

if exist "bin\Release\net10.0-windows\DesktopDock.exe" (
    start "" "bin\Release\net10.0-windows\DesktopDock.exe"
    exit /b 0
)

echo DesktopDock.exe not found. Please run: dotnet publish -c Release -o dist
pause
