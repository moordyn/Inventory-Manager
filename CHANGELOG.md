# Changelog

All notable changes to Inventory Manager are recorded here.

This project uses [Semantic Versioning](https://semver.org/). Repository tags and published GitHub Releases identify immutable source snapshots; deploying a tagged version does **not** automatically update a running server.

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
