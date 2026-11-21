#!/usr/bin/env bash
set -euo pipefail
if [[ $(id -u) -ne 0 ]]; then echo "Run as root: sudo bash deploy/configure-google.sh /path/to/client_secret.json"; exit 1; fi
if [[ $# -ne 1 || ! -f "$1" ]]; then echo "Usage: sudo bash deploy/configure-google.sh /path/to/client_secret.json"; exit 1; fi
APP=/opt/inventory-manager
if [[ ! -f "$APP/.env" ]]; then echo "Missing $APP/.env. Run deploy/install.sh first."; exit 1; fi
if ! command -v python3 >/dev/null; then echo "python3 is required"; exit 1; fi
python3 - "$1" "$APP/.env" <<'PY'
import json, os, re, secrets, sys, tempfile
from pathlib import Path
client_file, env_file = map(Path, sys.argv[1:])
with client_file.open(encoding="utf-8") as f:
    web = json.load(f).get("web", {})
client_id = web.get("client_id", "")
client_secret = web.get("client_secret", "")
redirects = web.get("redirect_uris", [])
origins = web.get("javascript_origins", [])
# Get the domain from the installed host, not an unrelated developer's OAuth configuration.
values_from_env = {}
for line in env_file.read_text(encoding="utf-8").splitlines():
    if "=" in line and not line.lstrip().startswith("#"):
        key, value = line.split("=", 1)
        values_from_env[key] = value
public_origin = values_from_env.get("PUBLIC_ORIGIN", "")
if not re.fullmatch(r"https://[a-z0-9-]+(?:\.[a-z0-9-]+)+", public_origin):
    raise SystemExit("PUBLIC_ORIGIN in .env must be https://your-domain.example without a trailing slash.")
expected_callback = public_origin + "/api/google/callback"
if not isinstance(client_id, str) or not re.fullmatch(r"[A-Za-z0-9._-]+\.apps\.googleusercontent\.com", client_id):
    raise SystemExit("Invalid Google OAuth Client ID in JSON file")
if not isinstance(client_secret, str) or not re.fullmatch(r"[A-Za-z0-9._~-]{10,256}", client_secret):
    raise SystemExit("Invalid Google OAuth Client Secret in JSON file")
if not isinstance(redirects, list) or expected_callback not in redirects:
    raise SystemExit("Google OAuth JSON must list redirect URI " + expected_callback + "; re-download the JSON after updating Google Cloud.")
if not isinstance(origins, list) or public_origin not in origins:
    raise SystemExit("Google OAuth JSON must list JavaScript origin " + public_origin + "; re-download the JSON after updating Google Cloud.")
lines = env_file.read_text(encoding="utf-8").splitlines()
values = {"GOOGLE_CLIENT_ID": client_id, "GOOGLE_CLIENT_SECRET": client_secret}
present = {}
for line in lines:
    if "=" in line and not line.lstrip().startswith("#"):
        key, value = line.split("=", 1)
        present[key] = value
if not re.fullmatch(r"[a-fA-F0-9]{64}", present.get("GOOGLE_TOKEN_KEY", "")):
    if "GOOGLE_TOKEN_KEY" in present and present["GOOGLE_TOKEN_KEY"]:
        raise SystemExit("Existing GOOGLE_TOKEN_KEY is invalid. Fix the server .env manually; token keys must never be silently rotated.")
    values["GOOGLE_TOKEN_KEY"] = secrets.token_hex(32)
updated = []
written = set()
for line in lines:
    if "=" in line:
        key = line.split("=", 1)[0]
        if key in values:
            if key not in written:
                updated.append(key + "=" + values[key])
                written.add(key)
            continue
    updated.append(line)
for key, value in values.items():
    if key not in written:
        updated.append(key + "=" + value)
fd, temp = tempfile.mkstemp(prefix=".env-google-", dir=str(env_file.parent))
try:
    os.fchmod(fd, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as out:
        out.write("\n".join(updated) + "\n")
    os.replace(temp, env_file)
finally:
    if os.path.exists(temp):
        os.unlink(temp)
print("Google OAuth credentials written to the private server .env (values hidden).")
print("Google OAuth configuration saved. Restarting the API will activate sign-in.")
print("Remove the original OAuth JSON file from the server after verification.")
PY

cd "$APP"
docker compose --env-file .env -f deploy/compose.yml up -d --build
echo "Google OAuth setup applied. Log in as a global administrator, then open Administration > Global email sender and connect the shared company Gmail account."
