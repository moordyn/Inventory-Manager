# Inventory Manager — current system architecture

For installation, operations, cloud integrations, security cautions and recovery, use the [complete deployment guide](COMPLETE_DEPLOYMENT_GUIDE.md). That guide supersedes all historical references to the previous public hostname.

## Current stack

- **UI and API:** ASP.NET Core 10 web application serving the responsive browser UI and JSON endpoints from the same API container.
- **Database:** PostgreSQL 17, running as the internal Docker Compose service `db` with persistent named volume `inventory_data`. The current Compose mapping exposes the API on host TCP/18424 and does not publish PostgreSQL on the host.
- **Deployment:** Ubuntu 24.04 LXC on Proxmox, `deploy/install.sh`, Docker Compose at `deploy/compose.yml`.
- **Public domain:** **installer-supplied** `PUBLIC_ORIGIN`, e.g. `https://inventory.example.com` (placeholder only), reached through the chosen DNS, firewall/tunnel and HTTPS reverse proxy to the app's host port 18424. Cloudflare + OPNsense + Nginx Proxy Manager are supported documented examples, not requirements.
- **Google OAuth 2.0 callback:** `PUBLIC_ORIGIN + /api/google/callback`. `server/Inventory.Api/Program.cs` validates the configured external HTTPS origin and uses it for OAuth requests and JSON import validation; do not infer the domain from untrusted Host headers. Each customer creates their own Google Cloud Web OAuth client.
- **Email alerts:** One global Gmail API sender for all stores; per-store recipient lists and alert toggles. SMTP configuration is an optional alternate sender.
- **Backup:** Compressed `pg_dump` to `/var/backups/inventory-manager` at 03:15 local host time by systemd timer, with roughly 14-day local retention. An offsite copy and a tested restore are still operational requirements.

## Inventory and transaction consistency

Each store has separately scoped inventory records with SKU, barcode, name, optional category/description, minimum stock and optional unit cost. Stock changes create movements with a delta, before/after balances, employee ID, timestamp, note and optional idempotency key. The server uses database transactions/row locks for stock changes, rejects negative balances and supports idempotency on movement inserts.

An employee authorized in the selected store can update the **note on an earlier checkout**. The API changes only the checkout note and metadata, preserving the original quantity, timestamp, and recorded checkout employee. Each note change has a revision, editor and timestamp in `movement_note_edits`. Check-in movements cannot be edited by this endpoint. Client history views display up to 200 recent movements; this does not imply deletion of older rows.

The application provides a per-store reorder list, CSV exports, audit logging and a responsive desktop/mobile barcode scanner.

## Authentication and administration

- First setup creates a shared six-digit administrator PIN using the setup token. An `OWNER` global admin is seeded in the default `DEFAULT` store.
- Global admins log in with their employee ID and PIN. Global admins control store management and the shared Gmail sender.
- Store-admin access is scoped to the current store. Employees have store grants and a primary store.
- **Known significant security limitation:** Ordinary employee sign-in currently requires an employee ID only, and store-admin privileges can be associated with these sessions. Employee IDs are not secure credentials. Do not expose sensitive inventory operations to untrusted networks without an additional security layer and/or improving user authentication. See the security section of the deployment guide.
- Login/session and OAuth/token fields are protected within PostgreSQL to the extent specified by the current implementation. Protect the host's private `.env`, including `GOOGLE_TOKEN_KEY`, alongside database backups.

## Scope and limitations

The application has employee accounts and multi-store management; earlier prototypes claiming otherwise are obsolete. Current deployment remains online-only. Offline inventory reconciliation, invoicing, full enterprise SSO, guaranteed app-store packages and production-certified security are not provided by the current codebase. Validate real devices, permission boundaries, backups, and Google authorization before commercial use.

## Code references

- [API and database initialization](../server/Inventory.Api/Program.cs)
- [Web UI](../server/Inventory.Api/wwwroot/index.html)
- [Compose](../deploy/compose.yml)
- [Installer](../deploy/install.sh)
- [Database backup](../deploy/backup.sh)
- [Automated API smoke tests](../tests/api-smoke.mjs)
- [Manual UI QA checklist](ui-qa-checklist.md)
