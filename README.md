# Inventory Manager

Self-hosted barcode inventory application for Windows browsers, mobile browsers, and a planned packaged Windows/iOS client.

## Current web build
- Consistent desktop/tablet/mobile design system: sticky sidebar, responsive KPI cards and record tables, compact mobile navigation, accessible dialogs and form labels
- Fast camera barcode scanning: native BarcodeDetector when supported, prewarmed ZXing fallback on Safari, one camera stream, retail-format hints, flashlight support where available, and clean camera shutdown
- Responsive charcoal dark dashboard with Overview, Inventory, Check In, Check Out, History, Settings
- Search by SKU, name, category, or barcode; keyboard-emulating USB/Bluetooth scanners
- Item creation/editing, categories, descriptions, costs, minimum-stock alerts
- Atomic check-in/out, nonnegative balances, transaction history, CSV export
- One-time installation-token enrollment for a shared six-digit PIN
- Persisted login sessions, PIN hashing, failure-only login lockouts (5 failed attempts per 5 minutes; success clears the counter)
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

## Visual and camera quality assurance
See [docs/ui-qa-checklist.md](docs/ui-qa-checklist.md) for the full mobile, desktop, accessibility, barcode, administrator and production acceptance checklist. The UI and scanner static regression checks run in GitHub Actions, but real-device camera speed and production readiness must be validated on the deployed server.

## Remaining work
The web app is an initial implementation, not a validated production release. GitHub Actions build verification, database migration tests, integration tests, robust concurrency/idempotency handling, restore tooling, camera-scanning fallback for iOS Safari, richer barcode formats, and packaged Tauri Windows / Capacitor iOS clients remain. Browser camera access requires HTTPS. A shared six-digit PIN is less secure than individual credentials; protect internet access with Cloudflare Access or another access control.

## Backup
Backups are saved to `/var/backups/inventory-manager` daily at 03:15 local time and retained for 14 days. Back up this directory outside the LXC and test restoring before relying on it.

No employee accounts, invoicing, accounting or offline edits in v1.

## Low-stock email alerts: one global sender, separate store recipients

**A single company-wide email account sends notifications for all stores.** Only global administrators can connect, change, or disconnect this account. Store administrators can enable or disable alerts and set up to 10 recipient addresses for their own location in **Administration → Low-stock email alerts**. Test emails use the same global sender.

Alerts are queued when a product is at or below its minimum stock threshold and sent once per low-stock cycle. Restocking above the minimum rearms the next alert. The background worker retries failed deliveries.

### Set up the global Gmail sender from the dashboard

1. Update the Ubuntu installation: `cd /opt/inventory-manager && bash deploy/install.sh`.
2. Log in as a PIN-authenticated global administrator. Open **Administration → Global email sender**.
3. If the Google OAuth application is not yet configured, import the Google Cloud **Web application OAuth Client JSON** and confirm your global administrator PIN. The JSON is read by the browser and sent only to the authorized API; the Client Secret is encrypted in PostgreSQL with the host's persistent `GOOGLE_TOKEN_KEY` and never stored in GitHub.
4. In that same global section, click **Connect global Gmail** and authorize the one Gmail account to send notifications for all stores.
5. Store administrators can now open **Administration → Low-stock email alerts**, type their own recipient addresses, enable alerts, save, and click **Send test email**. They have no access to the sending Gmail connection.

The Gmail authorization uses `https://www.googleapis.com/auth/gmail.send` and the OAuth Web application's redirect `https://inv.mrmschf.xyz/api/google/callback`. In Google Cloud, enable Gmail API, use an **External** audience if connecting ordinary Gmail accounts, and include any required test users. Google's **Testing** publishing status can result in refresh tokens expiring after seven days; production use may require verification.

**Upgrade note:** Prior per-store Google sender authorizations are retained as legacy data but are **no longer used** for outgoing mail. A global administrator must connect the global sender once after upgrading. Existing per-store recipient addresses and alert settings are preserved.

**Optional manual/server setup:** For global OAuth Client ID/Secret settings, the private `deploy/configure-google.sh` helper can import a Web application JSON file into the server's gitignored `.env`; the global administrator still must authorize a sending Gmail account once. A centrally configured `SMTP_USER` and `SMTP_APP_PASSWORD` may be used as a fallback sender if Gmail API is not connected. Never commit Google client secrets, SMTP passwords, OAuth refresh tokens, or the token encryption key to GitHub.

**Backup:** Keep a protected backup of both PostgreSQL and the private host `.env` encryption key; losing the latter prevents decrypting connected Gmail tokens.

**Security:** Employee ID-only sign-in for store administrators is not strong authentication. Use stronger sign-in before treating the role boundary as production-grade protection.
