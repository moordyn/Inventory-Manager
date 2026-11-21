# Inventory Manager — current system architecture

Use the [documentation index](COMPLETE_DEPLOYMENT_GUIDE.md) for focused deployment, accounts, operations and QA references. This page describes the implemented system rather than installation steps.

## Current stack

- **UI and API:** ASP.NET Core 10 web application serving the responsive browser UI and JSON endpoints from the same API container.
- **Database:** PostgreSQL 17, running as the internal Docker Compose service `db` with persistent named volume `inventory_data`. The current Compose mapping exposes the API on host TCP/18424 and does not publish PostgreSQL on the host.
- **Deployment:** Ubuntu 24.04 LXC on Proxmox, `deploy/install.sh` (supports `--domain`), Docker Compose at `deploy/compose.yml`.
- **Public domain:** **installer-supplied** `PUBLIC_ORIGIN`, e.g. `https://inventory.example.com` (placeholder only), reached through the chosen DNS, firewall/tunnel and HTTPS reverse proxy to the app's host port 18424. Cloudflare + OPNsense + Nginx Proxy Manager are supported documented examples, not requirements.
- **Google OAuth 2.0 callback:** `PUBLIC_ORIGIN + /api/google/callback`. `server/Inventory.Api/Program.cs` validates the configured external HTTPS origin and uses it for OAuth requests and JSON import validation; do not infer the domain from untrusted Host headers. Each customer creates their own Google Cloud Web OAuth client.
- **Email alerts:** One global Gmail API sender for all stores; per-store recipient lists and alert toggles. SMTP configuration is an optional alternate sender.
- **Backup:** Compressed `pg_dump` to `/var/backups/inventory-manager` at 03:15 local host time by systemd timer, with roughly 14-day local retention. An offsite copy and a tested restore are still operational requirements.

## Inventory and transaction consistency

Each store has separately scoped inventory records with SKU, barcode, name, optional category/description, minimum stock and optional unit cost. Stock changes create movements with a delta, before/after balances, employee ID, timestamp, note and optional idempotency key. The server uses database transactions/row locks for stock changes, rejects negative balances and supports idempotency on movement inserts.

An employee authorized in the selected store can update the **note on an earlier checkout**. The API changes only the checkout note and metadata, preserving the original quantity, timestamp, and recorded checkout employee. Each note change has a revision, editor and timestamp in `movement_note_edits`. Check-in movements cannot be edited by this endpoint. Client history views display up to 200 recent movements; this does not imply deletion of older rows.

The application provides a per-store reorder list, CSV exports, audit logging and a responsive desktop/mobile barcode scanner.

## Authentication and administration

- Initial setup uses the one-time server `SETUP_TOKEN` to create an OWNER administrator email/password; `OWNER` is seeded in the `DEFAULT` store. The setup-token owner may configure Gmail before verifying the bootstrap mailbox.
- Global admins and employees sign in using their employee ID and individual password. Global admins manage stores and the shared Gmail sender.
- Store-admin access is scoped to the current store. Employees have store grants and a primary store.
- **Known security limitation:** Employee accounts self-enroll using an unused employee ID, matching email/password fields, and a 24-hour single-use email verification link. Whoever claims an unactivated employee ID first can register their email address, so email verification proves mailbox access rather than employee identity. Restrict onboarding appropriately, especially for store admins.
- Per-user password hashes use salted PBKDF2-SHA256; one-time activation and reset tokens are stored as SHA256 hashes and expire after 24 hours and 30 minutes respectively. Password reset revokes old sessions; browser inactivity is limited to 15 minutes with matching server expiry and no visible countdown. The old PIN and ID-only sessions are invalidated on initial migration.
- Login/session and OAuth/token fields are protected within PostgreSQL to the extent specified by the current implementation. Protect the host's private `.env`, including `GOOGLE_TOKEN_KEY`, alongside database backups.

### Employee identity changes

Employees may change their own email only after confirming the new mailbox with a single-use, 30-minute link; existing sessions are revoked on success. Employee ID changes are server-authorized for **global administrators only** (including self-renaming), with a database transaction updating dependent references and revoking renamed sessions. The original setup owner's ID starts as `OWNER` but is persisted under `settings.owner_employee_id`, which remains authoritative after a rename. The original recovery administrator cannot be demoted, even after changing the display/login ID. All other users' IDs are immutable from their own account settings.

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
