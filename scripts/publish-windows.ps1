# Publish the cross-platform Avalonia desktop client for Windows (win-x64)
# and build the legacy WPF SocketDesktop (Windows only). Output goes under
# each project's publish/ folder (gitignored). Builds are unsigned - see
# the note below.
#
# Run from PowerShell:  ./scripts/publish-windows.ps1
#requires -Version 5
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "'dotnet' not found. Install the .NET 8 SDK."
}

Write-Host "==> Publishing SocketDesktop.Avalonia for win-x64"
dotnet publish SocketDesktop.Avalonia -c Release -r win-x64 --self-contained

Write-Host "==> Building legacy WPF SocketDesktop (Windows only)"
dotnet build SocketDesktop/SocketDesktop.csproj -c Release

Write-Host ""
Write-Host "Done. Avalonia output: SocketDesktop.Avalonia/bin/Release/net8.0/win-x64/publish/"
Write-Host "Note: these builds are UNSIGNED. Windows SmartScreen may warn on first run -"
Write-Host "      choose 'More info' > 'Run anyway'."
