#!/usr/bin/env bash
set -euo pipefail
if [[ $(id -u) -ne 0 ]]; then echo "Run as root"; exit 1; fi
if [[ ! -f /etc/os-release ]]; then echo "Linux required"; exit 1; fi
. /etc/os-release
if [[ "$ID" != ubuntu || "$VERSION_ID" != "24.04" ]]; then echo "Designed for Ubuntu 24.04; found $PRETTY_NAME"; exit 1; fi
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y ca-certificates curl git openssl python3
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  cat > /etc/apt/sources.list.d/docker.sources <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: noble
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF
  apt-get update
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi
systemctl enable --now docker
APP=/opt/inventory-manager
mkdir -p "$APP"
if [[ -d "$APP/.git" ]]; then
  cd "$APP"
  if [[ -n "$(git status --porcelain)" ]]; then echo "Local changes detected; refusing to overwrite."; exit 1; fi
  git pull --ff-only
else
  if [[ -n "$(ls -A "$APP")" ]]; then echo "$APP is not empty; refusing to overwrite."; exit 1; fi
  git clone https://github.com/moordyn/Inventory-Manager.git "$APP"
  cd "$APP"
fi
if [[ ! -f .env ]]; then
  umask 077
  printf 'POSTGRES_PASSWORD=%s\nSETUP_TOKEN=%s\n' "$(openssl rand -hex 32)" "$(openssl rand -hex 32)" > .env
fi
chmod 600 .env
# Persist the encryption key across deployments. Never silently rotate an existing key.
if ! grep -q '^GOOGLE_TOKEN_KEY=' .env; then
  printf 'GOOGLE_TOKEN_KEY=%s\n' "$(openssl rand -hex 32)" >> .env
fi
docker compose --env-file .env -f deploy/compose.yml up -d --build
install -m 0644 deploy/inventory-backup.service /etc/systemd/system/inventory-backup.service
install -m 0644 deploy/inventory-backup.timer /etc/systemd/system/inventory-backup.timer
systemctl daemon-reload
systemctl enable --now inventory-backup.timer
IP=$(hostname -I | awk '{print $1}')
echo ""
echo "Inventory Manager started at http://${IP}:18424"
echo "Open the site and create your 6-digit PIN."
echo "For FIRST SETUP only, enter this setup token when prompted:"
grep '^SETUP_TOKEN=' .env | cut -d= -f2-
echo ""
echo "Keep the setup token private. Do not expose the app to the internet until tested."
