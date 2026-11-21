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
# A new installation must supply its own public domain; this is used for OAuth's exact HTTPS callback.
# Run non-interactively with PUBLIC_ORIGIN=https://inventory.example.com in the environment.
if [[ ! -f .env ]]; then
  ORIGIN="${PUBLIC_ORIGIN:-}"
  if [[ -z "$ORIGIN" && -t 0 ]]; then
    read -r -p "Public HTTPS origin for this installation (e.g. https://inventory.example.com): " ORIGIN
  fi
  if [[ -z "$ORIGIN" ]]; then
    echo "A new installation needs PUBLIC_ORIGIN=https://your-domain.example (without trailing slash)." >&2
    echo "For a non-interactive install, run: sudo PUBLIC_ORIGIN=https://inventory.example.com bash deploy/install.sh" >&2
    exit 1
  fi
  python3 - "$ORIGIN" <<'PY'
import re
import sys
from urllib.parse import urlsplit
value = sys.argv[1]
try:
    url = urlsplit(value)
    if not (url.scheme == "https" and url.hostname and "." in url.hostname
            and url.netloc == url.hostname and not url.username and not url.password
            and not url.path and not url.query and not url.fragment
            and re.fullmatch(r"[a-z0-9.-]+", url.hostname)
            and all(label and len(label) <= 63 and not label.startswith("-") and not label.endswith("-") for label in url.hostname.split("."))):
        raise ValueError()
except ValueError:
    raise SystemExit("Invalid PUBLIC_ORIGIN: use https://inventory.example.com (no port, path or trailing slash)")
PY
  umask 077
  printf 'POSTGRES_PASSWORD=%s\nSETUP_TOKEN=%s\nPUBLIC_ORIGIN=%s\n' "$(openssl rand -hex 32)" "$(openssl rand -hex 32)" "$ORIGIN" > .env
  echo "Configured public origin: $ORIGIN"
elif ! grep -q '^PUBLIC_ORIGIN=' .env; then
  # Preserve the previously deployed hardcoded OAuth domain for existing installations.
  # Operators moving to another domain must edit PUBLIC_ORIGIN in .env intentionally.
  printf 'PUBLIC_ORIGIN=https://zagginventorymanager.com\n' >> .env
  echo "Existing installation: retained legacy public origin. Update PUBLIC_ORIGIN in .env if moving domains."
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
