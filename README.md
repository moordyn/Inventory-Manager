# Inventory Manager

Self-hosted barcode inventory application for Windows browsers, mobile browsers, and a planned packaged Windows/iOS client.

## Current web build
- Consistent desktop/tablet/mobile design system: sticky sidebar, responsive KPI cards and record tables, compact mobile navigation, accessible dialogs and form labels
- Fast camera barcode scanning: native BarcodeDetector when supported, prewarmed ZXing fallback on Safari, one camera stream, retail-format hints, flashlight support where available, and clean camera shutdown
- Responsive charcoal dark dashboard with Overview, Inventory, Check In, Check Out, History, Settings
- Search by SKU, name, category, or barcode; keyboard-emulating USB/Bluetooth scanners
- Item creation/editing, categories, descriptions, costs, minimum-stock alerts
- Atomic check-in/out, nonnegative balances, transaction history, CSV export
- Full store inventory CSV export (all active products, not just the current table), with formula-injection-safe text fields
- Clickable low-stock dashboard metric with a reorder list, suggested order quantities and a reorder CSV export
- In-place product-specific transaction history (latest 200 stock movements per product)
- Quick stock adjustment presets (+1, +5, −1, −5), still requiring Save and a reason for checkout
- Administrator activity history (latest 200 actions): access grants/revocations, permissions, store changes, product deletion, email settings and global Gmail connection changes
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

## Reporting, reorder and administrator activity

- **Inventory → Export inventory CSV:** downloads all active products from the currently selected location, regardless of how many rows have loaded into the dashboard. Exports include SKU, product name, barcode, category, stock, minimum level, unit cost and description.
- **Overview → Low stock alerts** (select the count), or **Inventory → Reorder list:** shows every product at or below its configured minimum. The **Order Qty** column gives the quantity necessary to bring stock **one unit above** the minimum. Export the list as CSV.
- **Inventory → Edit → View history:** expands a per-product transaction list while leaving unsaved form fields intact. Shows up to the latest 200 movements with before/after quantities, employee, date, and reason.
- **Item stock dialog → +1/+5/−1/−5:** picks the action and quantity. Click **Save transaction** to commit it; checkout still requires a reason and prevents negative balances.
- **Administration → Administrator activity history:** global admins see changes across stores; store admins see only changes associated with their current store. Historical actions from before this feature was deployed cannot be reconstructed.

Reports are server-generated and store-scoped; the API never trusts a client-provided store ID for report access. The administrator event log excludes OAuth credentials and recipient address lists. Activity is recorded in the same database transaction as most administrative actions.

## Testing and release status

GitHub Actions now includes a .NET Release build, frontend/static regression checks, and a PostgreSQL-backed API smoke-test suite in `tests/api-smoke.mjs`. The smoke tests cover CSV export, formula escaping, low-stock thresholds, product history, transaction idempotency, cross-store authorization, activity scoping and deletion.

This repository is **not yet certified for production deployment**. Verify the CI run on your latest commit and test the live deployment, real mobile camera scanning, database backup/restore, and administrator authorization. Employee ID-only store-admin login is a weak authentication boundary; require stronger authentication before using it to protect sensitive or high-privilege operations. Automated testing is not a substitute for staging verification.

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
