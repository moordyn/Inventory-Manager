# Ubuntu 24.04 Proxmox LXC installation

Prerequisite: Ubuntu 24.04 LXC with Docker-compatible nesting/keyctl configuration and working LAN networking.

## One-command installer
Run as root in the **Inventory** LXC:

```bash
curl -fsSL https://raw.githubusercontent.com/moordyn/Inventory-Manager/main/deploy/install.sh -o /tmp/inventory-install.sh
bash /tmp/inventory-install.sh
```

The script installs Docker if missing, clones or fast-forwards the repository into `/opt/inventory-manager`, generates strong database and one-time setup secrets in `.env`, and builds/starts PostgreSQL and the API/web UI. It is designed to be rerunnable without resetting database volumes or PIN.

Open `http://INVENTORY_LXC_IP:18424` locally. The first-run setup screen requests the one-time setup token printed by the installer and a new six-digit PIN (with confirmation). Keep the setup token secret. It is stored in `/opt/inventory-manager/.env` (permissions 0600) for future recovery, but cannot be reused to change a PIN once enrolled.

## Nginx Proxy Manager
Once tested and hardened, add proxy host:
- Hostname: `inv.mrmschf.xyz`
- Forward scheme: HTTP
- Forward IP: Inventory LXC LAN IP
- Forward port: `18424`
- SSL: Existing Cloudflare Origin CA wildcard certificate
- Cloudflare SSL mode: Full (strict)

Only router ports 80 and 443 should forward to the Nginx LXC. Never forward 81, 18424 or 5432 publicly.

## Operational notes
`docker compose --env-file .env -f deploy/compose.yml ps` checks status.
`docker compose --env-file .env -f deploy/compose.yml logs -f api` shows server logs.
`bash deploy/install.sh` updates from GitHub and rebuilds.

**Status:** Initial working prototype; builds, authentication hardening, backups, restore, mobile camera and full scanner functionality still require validation. Do not assume internet-facing production readiness merely because the containers start.
