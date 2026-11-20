#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
command -v docker >/dev/null || { echo "Docker is required. Install Docker Engine and Compose plugin first."; exit 1; }
docker compose version >/dev/null || { echo "Docker Compose plugin is required."; exit 1; }
test -f .env || { echo "Create .env from .env.example and set strong secrets."; exit 1; }
echo "Starting Inventory Manager; do not expose publicly until security hardening is complete."
docker compose --env-file .env -f deploy/compose.yml up -d --build
echo "Docker restart policies will restart containers after Docker starts on boot."
