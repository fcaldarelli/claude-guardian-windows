# Installs the app in %LOCALAPPDATA%\Programs\Claude Guardian, adds a shortcut
# to the Start menu and launches it. No administrator privileges required.
#
# Usage: powershell -ExecutionPolicy Bypass -File .\install-app.ps1
$ErrorActionPreference = 'Stop'

$build = Join-Path $PSScriptRoot 'build'
if (-not (Test-Path (Join-Path $build 'ClaudeGuardian.exe'))) {
    Write-Host "✗ build\ClaudeGuardian.exe not found: run .\build.ps1 first" -ForegroundColor Red
    exit 1
}

$dest = Join-Path $env:LOCALAPPDATA 'Programs\Claude Guardian'
$exe = Join-Path $dest 'ClaudeGuardian.exe'

# A running copy would lock the file: close it before replacing it.
Get-Process ClaudeGuardian -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $build 'ClaudeGuardian.exe*') $dest -Force

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Guardian.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.Description = 'Claude Code sessions in the notification area'
$link.Save()

Start-Process $exe
Write-Host "✓ App installed in $dest and started" -ForegroundColor Green
Write-Host "  Start menu shortcut: Claude Guardian"
Write-Host "  Tip: drag the icons out of the ^ menu in the notification area to keep them always visible."
