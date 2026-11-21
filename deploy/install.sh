#!/usr/bin/env bash
set -euo pipefail
if [[ $(id -u) -ne 0 ]]; then echo "Run as root"; exit 1; fi
if [[ ! -f /etc/os-release ]]; then echo "Linux required"; exit 1; fi
. /etc/os-release
if [[ "$ID" != ubuntu || "$VERSION_ID" != "24.04" ]]; then echo "Designed for Ubuntu 24.04; found $PRETTY_NAME"; exit 1; fi
# Accept a bare domain or HTTPS origin, with optional --domain= syntax.
DOMAIN_ARG=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --domain)
      if [[ $# -lt 2 || -z "$2" ]]; then echo "Missing value for --domain" >&2; exit 2; fi
      DOMAIN_ARG="$2"; shift 2 ;;
    --domain=*)
      DOMAIN_ARG="${1#--domain=}"; shift ;;
    -h|--help)
      echo "Usage: sudo bash deploy/install.sh [--domain example.com]"
      echo "Accepts example.com or https://example.com; updates PUBLIC_ORIGIN in .env."
      exit 0 ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2 ;;
  esac
done
if [[ -n "$DOMAIN_ARG" ]]; then
  if [[ "$DOMAIN_ARG" != https://* ]]; then DOMAIN_ARG="https://$DOMAIN_ARG"; fi
fi
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
# The --domain argument takes precedence, followed by PUBLIC_ORIGIN from the environment.
# Existing PUBLIC_ORIGIN is preserved when no override is supplied.
ORIGIN="${DOMAIN_ARG:-${PUBLIC_ORIGIN:-}}"
if [[ -z "$ORIGIN" && -f .env ]]; then
  ORIGIN="$(sed -n 's/^PUBLIC_ORIGIN=//p' .env | tail -n 1)"
fi
if [[ -z "$ORIGIN" && -t 0 ]]; then
  read -r -p "Public HTTPS origin (e.g. https://inventory.example.com): " ORIGIN
fi
if [[ -z "$ORIGIN" ]]; then
  echo "Missing domain. Run: sudo bash deploy/install.sh --domain inventory.example.com" >&2
  exit 1
fi
if [[ "$ORIGIN" != https://* ]]; then ORIGIN="https://$ORIGIN"; fi
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
            and len(url.hostname) <= 253
            and re.fullmatch(r"[a-z0-9.-]+", url.hostname)
            and all(label and len(label) <= 63 and not label.startswith("-") and not label.endswith("-") for label in url.hostname.split("."))):
        raise ValueError()
except ValueError:
    raise SystemExit("Invalid domain: use example.com or https://example.com (no port, path or trailing slash)")
PY
if [[ ! -f .env ]]; then
  umask 077
  printf 'POSTGRES_PASSWORD=%s\nSETUP_TOKEN=%s\nPUBLIC_ORIGIN=%s\n' "$(openssl rand -hex 32)" "$(openssl rand -hex 32)" "$ORIGIN" > .env
else
  # Update only PUBLIC_ORIGIN; preserve database credentials, tokens and all other settings.
  python3 - "$ORIGIN" <<'PY'
import os, sys, tempfile
from pathlib import Path
path = Path(".env")
origin = sys.argv[1]
lines = path.read_text(encoding="utf-8").splitlines()
updated = []
seen = False
for line in lines:
    if line.startswith("PUBLIC_ORIGIN="):
        if not seen:
            updated.append("PUBLIC_ORIGIN=" + origin)
            seen = True
    else:
        updated.append(line)
if not seen:
    updated.append("PUBLIC_ORIGIN=" + origin)
fd, name = tempfile.mkstemp(prefix=".env-", dir=".")
try:
    os.fchmod(fd, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as out:
        out.write("\n".join(updated) + "\n")
    os.replace(name, path)
finally:
    if os.path.exists(name):
        os.unlink(name)
PY
fi
echo "Configured public origin: $ORIGIN"
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
echo "Open the site and complete the administrator setup."
echo "For FIRST SETUP only, enter this setup token when prompted:"
grep '^SETUP_TOKEN=' .env | cut -d= -f2-
echo ""
echo "Keep the setup token private. Do not expose the app to the internet until tested."
