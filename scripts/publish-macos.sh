#!/usr/bin/env bash
#
# Publish the cross-platform Avalonia desktop client as self-contained
# apps for macOS (Apple Silicon and Intel). Output goes under each RID's
# publish/ folder (gitignored). Builds are unsigned - see the note below.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [ -d "$HOME/.dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
fi

command -v dotnet >/dev/null 2>&1 || { echo "error: 'dotnet' not found. Install the .NET 8 SDK." >&2; exit 1; }

for rid in osx-arm64 osx-x64; do
  echo "==> Publishing SocketDesktop.Avalonia for ${rid}"
  dotnet publish SocketDesktop.Avalonia -c Release -r "${rid}" --self-contained
done

echo
echo "Done. Output: SocketDesktop.Avalonia/bin/Release/net8.0/<rid>/publish/"
echo "Note: these builds are UNSIGNED. macOS Gatekeeper may block them -"
echo "      right-click > Open, or:  xattr -dr com.apple.quarantine <app>"
