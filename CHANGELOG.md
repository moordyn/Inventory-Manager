# Changelog

All notable changes to Inventory Manager are recorded here.

This project uses [Semantic Versioning](https://semver.org/). Repository tags and published GitHub Releases identify immutable source snapshots; deploying a tagged version does **not** automatically update a running server.

## Unreleased — next feature release

- Fixed mobile inventory, recent activity, checkout/product history, reorder, audit and bottom-navigation surfaces retaining hardcoded navy/green colors after changing themes. Mobile cards now inherit the active theme's raised surface color.


- Added a separate passive signed-in dashboard background with more prominent, overlapping drifting theme-colored light clouds, slightly translucent/blurred surfaces, fixed-size viewport rendering, capped frame rate, reduced-motion fallback and no pointer interaction.
- Replaced the login screen's glow-trail aurora with a WebGL2 fluid-smoke simulation: drag-injected colored dye and momentum, vorticity, pressure projection, autonomous drift, theme-matched backgrounds (including Glacier Light), and a static reduced-motion/WebGL fallback.
- Added ten cohesive palettes: Evergreen (mint), Deep Purple, Midnight Ocean, Copper Ember, Rose Noir, Arctic Slate, Forest Night, Champagne Gold, Crimson Night, and Glacier Light (**light mode**).
- Employees choose their own persistent dashboard palette from **Settings → Appearance**, maintained by server-side account preferences and applied across devices.
- Global administrators select the public sign-in/activation theme, independently of individual employee preferences; changes are audited and protected by a role-checked API.
- Added color-matched ZAGG wordmarks and favicons for every theme, coordinated UI surfaces and accent/focus colors, a light browser color scheme with dedicated contrast overrides, updated theme documentation and automated coverage.
- Full behavior and palette reference: [Themes](docs/themes.md). These commits are **not** included in the v1.2.0 release tag.

## [1.2.0] — Account administration, verified email changes and deploy improvements

- Installer accepts `--domain example.com` / `--domain=https://example.com`; repairs missing or empty `PUBLIC_ORIGIN` while preserving existing secrets.
- Role-based employee display-name changes: store admins can rename themselves and non-admin employees assigned to their current store; global admins can rename anyone. Employee IDs are unaffected.
- Employees may request a verified change to their own email (current password + matching new address fields + one-use 30-minute link); confirmation revokes sessions.
- Global administrators may rename employee IDs (including their own original `OWNER` account); related records are migrated atomically and sessions revoked. Owner recovery identity follows its new ID.
- Documentation reorganized into task-focused deployment, accounts, operations, networking examples and QA references. The old comprehensive-guide path is retained as an index.
- The full upgrade and acceptance checklist is in [v1.2.0 release notes](docs/releases/v1.2.0.md). Publishing does not automatically update installed servers.

## [1.1.0] — Employee email activation and password recovery

### Added
- Individual employee ID + password login for all users, including store administrators and the global OWNER administrator.
- Administrator remains responsible for creating only employee ID, name and store assignment; employees enter matching email and password twice during first-time self-enrollment.
- Strong password policy (8–128 characters, uppercase, lowercase, number, symbol), with server-side validation and salted 600,000-iteration PBKDF2-SHA256 password hashes.
- Single-use 24-hour email verification links; pending account credentials become active only after verification.
- Forgot-password button with 30-minute single-use reset links, sent only to previously verified email addresses; password change revokes old sessions.
- Server-enforced **15-minute inactivity expiry** renewed only by real user activity, not by automatic inventory polling.
- No visible auto-sign-out countdown on desktop or mobile.
- Per-installation Gmail API or SMTP delivery for authentication emails, independent of store low-stock recipient lists.
- Test-only mail capture for complete PostgreSQL-backed integration coverage without sending real messages.

### Changed
- Legacy six-digit administrator PIN endpoint is retired (HTTP 410) and employee-ID-only access is disabled.
- OWNER initial bootstrap now requires the original secret `SETUP_TOKEN` once to choose its email/password; OWNER can configure Gmail before verifying its bootstrap mailbox.
- Google OAuth client JSON import now confirms the signed-in global administrator's account password rather than the legacy shared PIN.
- One-time migration invalidates all old PIN/passwordless sessions, while preserving employee identities, inventory records and checkout history.
- Account-specific session renewal uses an explicit `/api/auth/activity` endpoint.

### Operational caveats
- **First-claim enrollment risk:** Anyone who knows an unactivated employee ID can try to register their own mailbox before the real employee. Email verification proves mailbox access, not employment identity; restrict enrollment to trusted staff or use additional access controls.
- **Setup mail dependency:** Employees cannot finish activation and cannot receive password-reset emails until a company Gmail API sender or SMTP sender is configured. The setup-token OWNER bootstrap is designed to break that first-install dependency; verify its mailbox once mail is configured.
- **Upgrade caution:** Save your database and original `.env`, including `SETUP_TOKEN` and `GOOGLE_TOKEN_KEY`. The OWNER must bootstrap a new password after upgrading from v1.0.0; old PIN/ID-only sessions are revoked.
- Browser, proxy, Gmail and backup-recovery behavior still require production-site acceptance testing.

[1.1.0]: https://github.com/moordyn/Inventory-Manager/releases/tag/v1.1.0

## [1.0.0] — Initial release

### Features

- Self-hosted responsive inventory management dashboard for desktop and mobile browsers.
- Barcode lookup and camera scanning, with support for compatible keyboard-emulating scanners.
- Per-store products, quantities, check-in/checkout, low-stock thresholds and reorder exports.
- Store employee management, roles, access grants and administrator activity history.
- Historical stock movement records with auditable, editable checkout notes.
- Inventory and reorder CSV exports.
- Global Gmail API notification sender with per-store recipient lists and low-stock alert scheduling.
- Ubuntu 24.04 installer using Docker Compose, ASP.NET Core 10 and PostgreSQL 17.
- Automated daily SQL backups with systemd timers and documented restoration testing.
- Bring-your-own-domain support through `PUBLIC_ORIGIN`; each business uses its own Google OAuth Web client.
- Comprehensive 19-chapter deployment, networking, OAuth, recovery and operations guide.

### Corrections made before release

- Permission-denied API responses now return HTTP 403 rather than an unhandled authentication-service exception/HTTP 500.
- Domain/OAuth callback is configurable rather than hardcoded to the original installation's hostname.
- New installations require an explicit public HTTPS hostname; existing installations retain compatibility with the original configured hostname.

### Important limitations

- **Security:** Standard employee sign-in currently accepts employee ID only. Protect public access with an approved identity-aware gateway/VPN and implement stronger per-user authentication before exposing sensitive data to untrusted users.
- **Operational verification:** A passing build and API integration tests do not replace on-site browser/scanner, reverse-proxy, email, security, and backup restoration testing.
- **Google OAuth:** Gmail sending requires the operator's own Google Cloud project, authorized origin and redirect URI. An External testing consent screen may issue short-lived refresh tokens.
- **Online-only:** Inventory editing requires a functioning backend; offline synchronization and invoicing are not provided.
- **Historical UI limit:** The client shows the most recent 200 movements in its history endpoints, even though older records remain in PostgreSQL.

### Install and upgrade

**Read [the complete deployment guide](docs/COMPLETE_DEPLOYMENT_GUIDE.md) before exposing the application publicly.** On an Ubuntu 24.04 host:

```bash
curl -fsSL https://raw.githubusercontent.com/moordyn/Inventory-Manager/v1.0.0/deploy/install.sh -o /tmp/inventory-install.sh
less /tmp/inventory-install.sh
sudo bash /tmp/inventory-install.sh
```

The installer uses the repository's default branch to clone its initial checkout; the release source archive and GitHub tag are the immutable source reference. For exact tagged-code deployments, follow the release documentation and account for that clone behavior.

If updating an existing installation, back up PostgreSQL and the matching protected `.env` file first, then use its normal installer. Updates pull `main` by design; a pinned release needs an explicitly controlled checkout and deployment procedure.

[1.0.0]: https://github.com/moordyn/Inventory-Manager/releases/tag/v1.0.0
