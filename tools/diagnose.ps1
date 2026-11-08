param(
    [string]$Exe
)

# DesktopDock diagnostics: where is it installed, what integrity label does the
# file/folder carry, which autostart channel is active, and what does the log say.
# Usage:  powershell -ExecutionPolicy Bypass -File tools\diagnose.ps1

$ErrorActionPreference = "Continue"

function Write-Section([string]$title) {
    Write-Host ""
    Write-Host "=== $title ===" -ForegroundColor Cyan
}

function Show-Label([string]$path) {
    if (-not (Test-Path $path)) {
        Write-Host "  (missing) $path" -ForegroundColor DarkGray
        return
    }

    Write-Host "  $path"
    $label = icacls $path 2>$null | Select-String "Mandatory Label"
    if ($label) {
        Write-Host ("    " + $label.Line.Trim())
    } else {
        Write-Host "    (no explicit label -> Medium by inheritance)"
    }
}

if (-not $Exe) {
    $Exe = Join-Path $env:LOCALAPPDATA "Programs\DesktopDock\DesktopDock.exe"
}

Write-Section "install"
Write-Host "  installed path : $Exe"
Write-Host "  exists         : $(Test-Path $Exe)"
if (Test-Path $Exe) {
    $item = Get-Item $Exe
    Write-Host "  version time   : $($item.LastWriteTime)"
    Write-Host "  size           : $($item.Length) bytes"
}

Write-Section "integrity labels (Low here = everything gets denied)"
Show-Label $Exe
Show-Label (Split-Path $Exe)

Write-Section "running processes"
$procs = Get-Process DesktopDock -ErrorAction SilentlyContinue
if ($procs) {
    $procs | ForEach-Object {
        Write-Host ("  pid {0}  started {1}  path {2}" -f $_.Id, $_.StartTime, $_.Path)
    }
} else {
    Write-Host "  (not running)"
}

Write-Section "autostart channels"
$run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).DesktopDock
if ($run) { Write-Host "  HKCU Run       : $run" } else { Write-Host "  HKCU Run       : (none)" }

$startup = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup"
$startupItems = Get-ChildItem $startup -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "DesktopDock|桌面收纳盒" }
if ($startupItems) {
    $startupItems | ForEach-Object { Write-Host "  Startup folder : $($_.Name)" }
} else {
    Write-Host "  Startup folder : (none)"
}

$taskXml = schtasks /query /tn "DesktopDock_AutoStart" /xml 2>$null
if ($LASTEXITCODE -eq 0 -and $taskXml) {
    try {
        [xml]$task = ($taskXml -join "`n")
        $ns = New-Object System.Xml.XmlNamespaceManager($task.NameTable)
        $ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task")
        $command = $task.SelectSingleNode("//t:Actions/t:Exec/t:Command", $ns)
        $level = $task.SelectSingleNode("//t:Principals/t:Principal/t:RunLevel", $ns)
        $limit = $task.SelectSingleNode("//t:Settings/t:ExecutionTimeLimit", $ns)
        $batt = $task.SelectSingleNode("//t:Settings/t:DisallowStartIfOnBatteries", $ns)
        Write-Host "  Scheduled task : DesktopDock_AutoStart"
        Write-Host "    command      : $($command.InnerText)"
        Write-Host "    run level    : $($level.InnerText)"
        Write-Host "    time limit   : $($limit.InnerText)"
        Write-Host "    on battery   : $(if ($batt.InnerText -eq 'false') { 'allowed' } else { 'blocked (bad)' })"
    } catch {
        Write-Host "  Scheduled task : exists (xml parse failed)"
    }
} else {
    Write-Host "  Scheduled task : (none)"
}

Write-Section "recent log"
$log = Join-Path $env:APPDATA "DesktopDock\log.txt"
if (Test-Path $log) {
    Get-Content $log -Tail 15 -Encoding UTF8 -ErrorAction SilentlyContinue
} else {
    $fallback = Join-Path $env:USERPROFILE "AppData\LocalLow\DesktopDock\log.txt"
    if (Test-Path $fallback) {
        Write-Host "  (low-integrity fallback log)"
        Get-Content $fallback -Tail 15 -Encoding UTF8
    } else {
        Write-Host "  (no log yet)"
    }
}

Write-Host ""
Write-Host "Tip: Low label + Low process = run 修复权限.bat or use Settings -> 安装到本机并重启." -ForegroundColor Yellow
