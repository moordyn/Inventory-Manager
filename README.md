# Inventory Manager

Self-hosted, multi-store barcode inventory management for desktop and mobile browsers. Built on ASP.NET Core 10, PostgreSQL 17, and Docker Compose; designed for Ubuntu 24.04. The repository currently contains a web application; packaged native Windows/iOS clients are not shipped.

## Get started

**You bring your own domain, server, and email integration.** The installer does not provision DNS, HTTPS, a reverse proxy, a Google Cloud project, or a firewall.

```bash
# On the Ubuntu 24.04 application host
git clone https://github.com/moordyn/Inventory-Manager.git /opt/inventory-manager
cd /opt/inventory-manager
sudo bash deploy/install.sh --domain inventory.example.com
```

Replace `inventory.example.com` with a hostname **you control** (root domain or subdomain). The installer accepts `--domain example.com`, `--domain=https://example.com`, or an existing `PUBLIC_ORIGIN`. It generates a private `.env`, starts Docker services, and installs a daily database-backup timer. Point your HTTPS reverse proxy at `http://APP_HOST_IP:18424`, then complete first-run setup.

**Start here:** [Deployment / DNS / HTTPS / Google OAuth](docs/deployment.md).

## Capabilities

- Inventory isolated by store; SKU/barcode search, categories, descriptions, cost, minimum stock, per-store CSV export.
- Check-in/out with transaction history, notes, atomic stock updates and idempotency protection.
- **Repair Queue:** oldest-first device repair tickets, employee attribution, quotes/upfront payments and balances, technician assignment, status/history tracking, camera/SKU parts scanning, atomic inventory deductions, and auditable returns. [Repair Queue guide](docs/repairs.md).
- Camera barcode scanning with native BarcodeDetector where supported and ZXing fallback; USB/Bluetooth keyboard-style scanners.
- Reorder lists/CSV, low-stock thresholds, inventory metrics, and quick stock adjustments.
- Store access assignment, store/global administrative roles, auditable management operations; store admins can rename themselves and ordinary staff in their store, global admins can rename anyone.
- Individual employee passwords, email verification, password recovery, 15-minute idle expiration.
- Employee self-service email changes (new-address confirmation); **only global admins** may change employee IDs, including their own. Renames revoke active sessions; the original recovery administrator retains global privileges if renamed.
- One global Gmail API sender for store-specific low-stock recipients; optional SMTP fallback.
- Ten coordinated themes (nine dark plus Glacier Light): Evergreen, Deep Purple, Midnight Ocean, Copper Ember, Rose Noir, Arctic Slate, Forest Night, Champagne Gold, Crimson Night, and Glacier Light. Each employee saves a personal dashboard theme; only global admins set the public login theme. Theme-matched wordmarks and favicons are included.

## Documentation

| Need | Guide |
| --- | --- |
| Install, upgrade, domain, DNS, TLS, Google OAuth, SMTP | [Deployment](docs/deployment.md) |
| Optional Cloudflare DNS, OPNsense DDNS and Nginx Proxy Manager detail | [Networking examples](docs/networking-examples.md) |
| First-run owner, employees, roles, email, ID changes, security model | [Accounts and permissions](docs/accounts.md) |
| Backups, restore, troubleshooting, monitoring, disaster recovery | [Operations](docs/operations.md) |
| Personal themes, login appearance and matching logo variants | [Themes](docs/themes.md) |
| Service/data design and source locations | [Architecture](docs/architecture.md) |
| Desktop, mobile, camera and release acceptance tests | [QA checklist](docs/ui-qa-checklist.md) |
| Changes by version | [Changelog](CHANGELOG.md), [v1.2.0 notes](docs/releases/v1.2.0.md), and [releases](docs/releases/) |
| Legacy all-in-one link | [Documentation index](docs/COMPLETE_DEPLOYMENT_GUIDE.md) |

## Update an installation

```bash
cd /opt/inventory-manager
sudo bash deploy/backup.sh
sudo bash deploy/install.sh --domain inventory.example.com
docker compose --env-file .env -f deploy/compose.yml ps
curl -f http://127.0.0.1:18424/health
```

Use your actual domain; omit `--domain` to keep an existing `PUBLIC_ORIGIN`. The installer pulls fast-forward changes and builds containers. **Back up the database and private `.env` before upgrades.** Do not use `docker compose down -v` or volume pruning on a live installation. Review GitHub Actions results for the exact commit before deploying.

## Security notes

New employees activate their own email/password after an administrator creates an ID. **An unclaimed ID is first-claimable**: protect first-time activation behind trusted network access or an identity-aware gateway and verify staff onboarding. Do not expose port 18424 or the database directly to the Internet. Preserve `POSTGRES_PASSWORD`, `SETUP_TOKEN`, and `GOOGLE_TOKEN_KEY` securely, test offsite restores, and audit administrator access.

Source: [moordyn/Inventory-Manager](https://github.com/moordyn/Inventory-Manager). See [Changelog](CHANGELOG.md) for tagged releases versus changes on `main`.
