# Uninstalls Claude Guardian: closes the app, removes the autostart entry, the Start menu
# shortcut, the hook entries in settings.json (with backup) and the installed files.
#
# Usage: powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
$ErrorActionPreference = 'Stop'

Get-Process ClaudeGuardian -ErrorAction SilentlyContinue | Stop-Process -Force

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Claude Guardian' -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Claude Guardian' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude Guardian.lnk') -ErrorAction SilentlyContinue

$claudeDir = Join-Path $env:USERPROFILE '.claude'
$hook = Join-Path $claudeDir 'hooks\claude-activity-hook.exe'
if (Test-Path $hook) {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    & $hook --uninstall | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Start-Sleep -Milliseconds 300
    Remove-Item $hook -Force -ErrorAction SilentlyContinue
}

Remove-Item (Join-Path $claudeDir 'activity') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\Claude Guardian') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "✓ Claude Guardian uninstalled" -ForegroundColor Green
Write-Host "  Restart any Claude Code sessions that are already open; they still have the hooks in memory."
