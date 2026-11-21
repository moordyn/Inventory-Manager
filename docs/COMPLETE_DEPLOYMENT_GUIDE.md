# Inventory Manager documentation index

The former 1,100-line all-in-one deployment manual has been reorganized into focused guides. **Start with the guide matching your task**; commands and cautions have been consolidated, not intentionally dropped.

1. [Deployment](deployment.md) — Ubuntu 24.04 / Proxmox, `--domain` installation, Docker, DNS/DDNS, firewalls, HTTPS proxy, Google Cloud OAuth, Gmail and SMTP, upgrades.
2. [Accounts and permissions](accounts.md) — OWNER/bootstrap, employee onboarding, password and email verification, account email changes, employee ID changes, store roles, security limits.
3. [Operations](operations.md) — backups/restore, recovery, health checks, incident troubleshooting, domain migrations, handover, maintenance cadence.
4. [Optional networking examples](networking-examples.md) — retained step-by-step Cloudflare DNS, OPNsense DDNS/port-forwarding, Nginx Proxy Manager and TLS instructions.
5. [Architecture](architecture.md) — services, data integrity and implementation pointers.
6. [UI and camera acceptance](ui-qa-checklist.md) — manual and automated quality gates.
7. [Changelog](../CHANGELOG.md) and [release notes](releases/) — version-specific history.

**Example hostname:** `inventory.example.com` is illustrative. Every deployment must supply its own domain, OAuth application, DNS, certificates, and SMTP/Gmail credentials.

For a clean installation:

```bash
cd /opt/inventory-manager
sudo bash deploy/install.sh --domain inventory.example.com
```

The installer does **not** configure the reverse proxy, DNS or Google Cloud. Follow [Deployment](deployment.md) for those steps. Never paste `.env` or OAuth secrets into issues or documentation.
