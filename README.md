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

## Gmail low-stock email alerts

A single Gmail sender account is configured on the **server**. Store administrators can independently enable alerts and set up to 10 recipients for **their currently selected store** in **Administration → Low-stock email alerts**, and send a test message. Global administrators can switch stores to manage each location.

1. Enable Google 2-Step Verification for the dedicated sender Gmail account, then generate an App Password at https://myaccount.google.com/apppasswords (where eligible).
2. On the Ubuntu host, edit `/opt/inventory-manager/.env` (never commit it) and add:
   ```dotenv
   SMTP_USER=your-inventory-sender@gmail.com
   SMTP_APP_PASSWORD=your16characterapppassword
   ```
3. Run `cd /opt/inventory-manager && bash deploy/install.sh`.
4. Sign in as a store administrator, select the store, enter notification recipients, enable alerts, save, then click **Send test email**.

Alerts are queued when a product's quantity is at or below its configured minimum. A product sends at most one alert per low-stock cycle; it rearms after its quantity rises above the minimum. Email delivery runs asynchronously and retries temporary failures; inventory transactions never depend on Gmail availability. Gmail's sending limits and App Password availability apply. Server logs show delivery errors. The existing 30-second UI inactivity timeout remains separate from the email worker.

**Security:** Only the server owner configures the sender credentials. Store administrators can edit their own store's recipient list and enabled status, not the sender password. The existing employee-ID-only login for store admins is not suitable for sensitive production administration; add stronger employee authentication before relying on this authorization model in production.
