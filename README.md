# Inventory Manager

Self-hosted barcode inventory application for Windows browsers, mobile browsers, and a planned packaged Windows/iOS client.

## Current web build
- Responsive charcoal dark dashboard with Overview, Inventory, Check In, Check Out, History, Settings
- Search by SKU, name, category, or barcode; keyboard-emulating USB/Bluetooth scanners
- Item creation/editing, categories, descriptions, costs, minimum-stock alerts
- Atomic check-in/out, nonnegative balances, transaction history, CSV export
- One-time installation-token enrollment for a shared six-digit PIN
- Persisted login sessions, PIN hashing, login rate limiting
- Camera scanning through native BarcodeDetector where browser supports it
- Online-only PostgreSQL backend; 20-second client refresh
- Daily compressed PostgreSQL backup script and systemd timer

## Install / update on Ubuntu 24.04 Proxmox LXC
As root:
```bash
curl -fsSL https://raw.githubusercontent.com/moordyn/Inventory-Manager/main/deploy/install.sh -o /tmp/inventory-install.sh
bash /tmp/inventory-install.sh
```
The installer retains the database volume and PIN and generates secrets only on first installation. Initial setup requires the one-time token printed by the installer.

Nginx Proxy Manager forwards `https://inv.mrmschf.xyz` to `http://INVENTORY_LXC_IP:18424`. Only ports 80/443 should be forwarded to Nginx; never forward 18424, 81, or 5432 publicly.

## Project layout
- `server/Inventory.Api`: ASP.NET Core 10 API and web interface
- `deploy`: Docker Compose, Ubuntu installer, PostgreSQL backup timer
- `docs`: architecture and deployment notes

## Remaining work
The web app is an initial implementation, not a validated production release. GitHub Actions build verification, database migration tests, integration tests, robust concurrency/idempotency handling, restore tooling, camera-scanning fallback for iOS Safari, richer barcode formats, and packaged Tauri Windows / Capacitor iOS clients remain. Browser camera access requires HTTPS. A shared six-digit PIN is less secure than individual credentials; protect internet access with Cloudflare Access or another access control.

## Backup
Backups are saved to `/var/backups/inventory-manager` daily at 03:15 local time and retained for 14 days. Back up this directory outside the LXC and test restoring before relying on it.

No employee accounts, invoicing, accounting or offline edits in v1.
