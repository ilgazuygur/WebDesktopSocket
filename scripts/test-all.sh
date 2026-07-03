#!/usr/bin/env bash
#
# Restore, build and run the whole test suite (Socket.Tests +
# SocketDesktop.Avalonia.Tests, including the Avalonia headless UI tests).
# No secrets, no database, no real AI provider required.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# Prefer a local .NET install if present (this repo was developed with ~/.dotnet).
if [ -d "$HOME/.dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
fi

command -v dotnet >/dev/null 2>&1 || { echo "error: 'dotnet' not found. Install the .NET 8 SDK." >&2; exit 1; }

echo "==> Restoring"
dotnet restore WebDesktopSocket.sln

echo "==> Building (Release)"
dotnet build WebDesktopSocket.sln -c Release --no-restore

echo "==> Testing (whole solution)"
dotnet test WebDesktopSocket.sln -c Release --no-build
