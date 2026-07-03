#!/usr/bin/env bash
#
# Start the whole stack locally for a manual end-to-end run:
#   MockAiServer (deterministic AI)  +  SocketWeb (real MySQL)  +  Avalonia client
#
# Works on macOS and Linux. No secrets live in this file - the MySQL
# credentials are read from the gitignored .env at runtime, and the AI is
# the local deterministic mock (no real key). Press Ctrl+C to stop the
# processes this script started (the MySQL container is left running).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [ -d "$HOME/.dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
fi

command -v dotnet >/dev/null 2>&1 || { echo "error: 'dotnet' not found. Install the .NET 8 SDK." >&2; exit 1; }
command -v docker >/dev/null 2>&1 || { echo "error: 'docker' not found. Install/start Docker Desktop." >&2; exit 1; }

if [ ! -f .env ]; then
  echo "error: .env not found. Copy .env.example to .env and fill in local values." >&2
  exit 1
fi

CONN="$(grep -E '^ConnectionStrings__ChatDb=' .env | cut -d= -f2- || true)"
if [ -z "${CONN:-}" ]; then
  echo "error: ConnectionStrings__ChatDb is missing from .env" >&2
  exit 1
fi

MOCK_PORT="${MOCK_PORT:-5099}"

echo "==> Starting MySQL (docker compose up -d --wait)"
docker compose up -d --wait

echo "==> Applying EF Core migrations"
dotnet tool restore >/dev/null
if ! ConnectionStrings__ChatDb="$CONN" dotnet dotnet-ef database update \
      --project SocketWeb --startup-project SocketWeb; then
  echo "warning: migration step failed (schema may already be up to date); continuing." >&2
fi

PIDS=()
cleanup() {
  echo
  echo "==> Stopping processes started by this script"
  for pid in "${PIDS[@]:-}"; do kill "$pid" >/dev/null 2>&1 || true; done
  echo "    MySQL container left running. Stop it with:  docker compose down"
}
trap cleanup EXIT INT TERM

echo "==> Starting MockAiServer on port ${MOCK_PORT}"
dotnet run --project MockAiServer -c Release -- "${MOCK_PORT}" &
PIDS+=($!)

echo "==> Starting SocketWeb on http://localhost:5080"
ConnectionStrings__ChatDb="$CONN" ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project SocketWeb -c Release &
PIDS+=($!)

# Give SocketWeb a moment to bind before the desktop client tries to connect.
sleep 4

echo "==> Starting SocketDesktop.Avalonia (pointed at the mock AI)"
Ai__BaseUrl="http://localhost:${MOCK_PORT}/v1" \
Ai__Model="mock-model" \
AI_API_KEY="local-dummy-key-not-secret" \
Socket__Url="ws://localhost:5080/ws" \
  dotnet run --project SocketDesktop.Avalonia -c Release &
PIDS+=($!)

echo
echo "All started. Open  http://localhost:5080  in your browser."
echo "You should see WebSocket 'Connected' and 'AI: Online'."
echo "Press Ctrl+C to stop MockAiServer, SocketWeb and the desktop app."
wait
