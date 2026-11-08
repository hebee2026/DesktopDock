param(
    [switch]$Build,
    [string]$AppArgs = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root "bin\Release\net10.0-windows\DesktopDock.exe"

Get-Process DesktopDock -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Milliseconds 900

if ($Build -or -not (Test-Path $exe)) {
    Push-Location $root
    dotnet build DesktopDock.csproj -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "build failed" }
    Pop-Location
}

Remove-Item (Join-Path $root "bin\Release\net10.0-windows\debug.log") -ErrorAction SilentlyContinue

# 通过计划任务启动：拿到当前用户令牌与完整环境变量
# （直接从 PowerShell Start-Process 启动会继承管道句柄，导致调用方挂起）
$task = "DesktopDock_Run"
$cmd = if ([string]::IsNullOrWhiteSpace($AppArgs)) { "`"$exe`"" } else { "`"$exe`" $AppArgs" }
& schtasks /create /tn $task /tr $cmd /sc once /st 23:59 /f *> $null
& schtasks /run /tn $task *> $null

Start-Sleep -Seconds 3
$p = Get-Process DesktopDock -ErrorAction SilentlyContinue
if ($p) { Write-Output "DesktopDock started pid=$($p.Id) args='$AppArgs'" } else { Write-Output "start failed" }
