# Installs the hook in %USERPROFILE%\.claude\hooks and registers it in %USERPROFILE%\.claude\settings.json.
# Can be re-run without duplicating entries. Backs up settings.json and keeps the other hooks.
#
# Usage: powershell -ExecutionPolicy Bypass -File .\install-hooks.ps1
$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'build\claude-activity-hook.exe'
if (-not (Test-Path $source)) {
    Write-Host "✗ $source not found: run .\build.ps1 first" -ForegroundColor Red
    exit 1
}

$claudeDir = Join-Path $env:USERPROFILE '.claude'
$hooksDir = Join-Path $claudeDir 'hooks'
$target = Join-Path $hooksDir 'claude-activity-hook.exe'
New-Item -ItemType Directory -Force -Path $hooksDir, (Join-Path $claudeDir 'activity') | Out-Null

# Open sessions launch the hook constantly: if the file is in use for a moment, retry.
for ($attempt = 1; ; $attempt++) {
    try {
        Copy-Item $source $target -Force
        break
    } catch {
        if ($attempt -ge 20) { throw }
        Start-Sleep -Milliseconds 250
    }
}
Write-Host "✓ Hook copied to $hooksDir" -ForegroundColor Green

# The hook registers itself in settings.json, with its own path.
# The pipe makes PowerShell wait, because the executable has no console.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
& $target --install | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "  Claude Code sessions that are already open will use them after a restart."
