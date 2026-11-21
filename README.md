# Inventory Manager

Self-hosted barcode inventory application for Windows browsers, mobile browsers, and a planned packaged Windows/iOS client.

## Current web build
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

## Remaining work
The web app is an initial implementation, not a validated production release. GitHub Actions build verification, database migration tests, integration tests, robust concurrency/idempotency handling, restore tooling, camera-scanning fallback for iOS Safari, richer barcode formats, and packaged Tauri Windows / Capacitor iOS clients remain. Browser camera access requires HTTPS. A shared six-digit PIN is less secure than individual credentials; protect internet access with Cloudflare Access or another access control.

## Backup
Backups are saved to `/var/backups/inventory-manager` daily at 03:15 local time and retained for 14 days. Back up this directory outside the LXC and test restoring before relying on it.

No employee accounts, invoicing, accounting or offline edits in v1.

## Gmail and Google Sign-In for low-stock email alerts

Store administrators can configure email alerts for **their own store** in **Administration → Low-stock email alerts**. Each store has its own connected Google sending account and up to 10 recipient addresses. Global admins can switch to another store and manage its alerts. Notifications are generated on stock changes at or below the product's minimum and sent once per low-stock cycle; a restock above the minimum rearms the next alert.

### Dashboard JSON import (recommended for global administrators)

After deploying the latest version using `cd /opt/inventory-manager && bash deploy/install.sh`, sign in with the global administrator PIN and open **Administration → Google OAuth configuration**. Upload the **Web application OAuth Client JSON** downloaded from Google Cloud, enter the global administrator PIN again, and click **Import Google configuration**.

The API checks the client type, Client ID, Client Secret, authorized origin (`https://inv.mrmschf.xyz`) and callback URL (`https://inv.mrmschf.xyz/api/google/callback`). The secret is encrypted using the persistent `GOOGLE_TOKEN_KEY` on the Ubuntu server, stored in PostgreSQL, and activated immediately **without a restart**. The interface shows the Client ID and configuration status, never the Client Secret. Only PIN-authenticated global administrators can import or replace this application-wide configuration. Store administrators retain their own per-store Gmail connections and recipient settings.

**No manual copying of JSON or OAuth credentials to the server is needed for this method.** The installer must be run at least once to create and preserve the server encryption key. Back up both the database and the private `.env` encryption key so restored Gmail connections remain usable. If changing to a different OAuth Client ID, existing per-store Google senders must be disconnected first.

### Google OAuth setup (recommended)

1. In Google Cloud, enable **Gmail API** and set up an OAuth **Web application** with the **Gmail Send** scope (`https://www.googleapis.com/auth/gmail.send`). Use the authorized origin `https://inv.mrmschf.xyz` and redirect URI `https://inv.mrmschf.xyz/api/google/callback`.
2. **Alternative manual setup only:** Keep the downloaded Google OAuth client JSON **private**. Transfer it securely to your Ubuntu server (e.g. root's home directory); **never put it in GitHub, upload it to the publicly served app, or copy it to the Docker image**.
3. On the Ubuntu server, run the installer/update first and then import that JSON:

   ```bash
   cd /opt/inventory-manager
   bash deploy/install.sh
   sudo bash deploy/configure-google.sh /root/google-oauth-client.json
   ```

   Replace the JSON filename with the actual private file path. The import script restarts the API, validates the redirect URL, and securely adds `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, and a persistent `GOOGLE_TOKEN_KEY` to the **gitignored** `.env` with file mode 600. The installer can generate a key as well; never overwrite the existing encryption key or connected accounts' refresh tokens will become unusable. Remove the imported source JSON from the server when no longer needed.
4. Visit the website, log in as an authorized store administrator, open **Administration → Low-stock email alerts**, and click **Connect Google Account**. Grant access to **send emails**; Google returns to the app. Fill in recipients, enable alerts, **Save email settings**, and **Send test email**.

Google access tokens are refreshed using an encrypted, per-store refresh token in PostgreSQL; the encrypted value can only be decrypted using the persistent server key. The server does not need each store's Gmail password. Store admins can change or disconnect their own Google sender.

**Important:** If the OAuth app's publishing status is **Testing** with External audience, refresh tokens can expire after 7 days. Google might require app verification for wider production use of the sensitive `gmail.send` scope. Google accounts not listed as test users might be blocked. Back up both the database and the private server `.env` (with tightly controlled access) to preserve connections.

### Optional SMTP fallback

A server operator may configure `SMTP_USER` and `SMTP_APP_PASSWORD` in the host `.env`. When a store has connected a Google account, Gmail API is preferred for that store. Otherwise SMTP is used if configured. SMTP passwords and OAuth client secrets must never be stored in the repository.

**Security:** Existing Employee ID-only store-admin login is a weak authentication boundary. Stronger admin authentication should be added before treating this as production-grade access control.
