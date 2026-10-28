#!/usr/bin/env bash
# Desarrollo en Linux/macOS: PostgreSQL local + API (puerto 5080). Requiere .NET 8 y Node 22.
# Uso: scripts/run-dev.sh  → http://127.0.0.1:5080   (usuarios demo en docs/ESTADO.md)
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/dev-env.sh
(cd web && npm ci && npm run build)
cd server/src/Pos.Api && exec dotnet run --no-launch-profile
