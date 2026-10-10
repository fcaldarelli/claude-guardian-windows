# Builds the app and the hook into "build\".
# Requirements: Windows 10/11 and the .NET SDK 8 or later (winget install Microsoft.DotNet.SDK.8).
# The resulting executables use .NET Framework 4.8, already included in Windows: users need to install nothing.
#
# Usage: powershell -ExecutionPolicy Bypass -File .\build.ps1 [-SingleIcon] [-Package]
#   -SingleIcon   one notification area icon with the most urgent state, instead of one icon
#                 per state (the default is set by TrayIcons in src\Directory.Build.props)
#   -Package      also create dist\ClaudeGuardian-<version>-windows.zip, ready to attach to a
#                 GitHub release: the executables in build\ plus the install scripts and README
param(
    [switch]$SingleIcon,
    [switch]$Package
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Right after "winget install" the PATH of an already open terminal does not contain dotnet yet:
# in that case look for it in the standard installation folder.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) {
        Write-Host "✗ .NET SDK not found. Install it with: winget install Microsoft.DotNet.SDK.8" -ForegroundColor Red
        exit 1
    }
}

# The version is defined in src\Directory.Build.props: here it is only read, for the final message.
[xml]$props = Get-Content 'src\Directory.Build.props'
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) {
    Write-Host "✗ Version not found in src\Directory.Build.props" -ForegroundColor Red
    exit 1
}

$out = Join-Path $PSScriptRoot 'build'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$extra = @()
if ($SingleIcon) { $extra += '-p:TrayIcons=single' }

foreach ($project in 'src\App\ClaudeGuardian.csproj', 'src\Hook\ClaudeActivityHook.csproj') {
    & $dotnet build $project -c Release -o $out --nologo -v quiet @extra
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Keep only the required files.
Get-ChildItem $out -File | Where-Object { $_.Extension -notin '.exe', '.config' } | Remove-Item -Force

$icons = if ($SingleIcon) { 'single' } else { $props.SelectSingleNode('//TrayIcons').InnerText }
Write-Host "✓ Build ready: $out (version $version, tray icons: $icons)" -ForegroundColor Green

if (-not $Package) {
    Write-Host "  Next: .\install-hooks.ps1 and .\install-app.ps1"
    exit 0
}

# The zip has the same layout as the repository, because the install scripts look for the
# executables in build\ next to themselves.
$name = "ClaudeGuardian-$version-windows" + $(if ($icons -eq 'single') { '-single-icon' } else { '' })
$dist = Join-Path $PSScriptRoot 'dist'
$staging = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $staging 'build') | Out-Null

Copy-Item 'install-hooks.ps1', 'install-app.ps1', 'uninstall.ps1', 'README.md' $staging
if (Test-Path 'LICENSE') { Copy-Item 'LICENSE' $staging }
Copy-Item (Join-Path $out '*') (Join-Path $staging 'build')

# ZipFile rather than Compress-Archive: in Windows PowerShell 5.1 the latter writes paths
# with backslashes, which some unzip tools do not handle.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Remove-Item $staging -Recurse -Force

Write-Host "✓ Package ready: $zip" -ForegroundColor Green
Write-Host "  Attach it to a GitHub release."
