# Operations, backup and recovery

[Documentation index](COMPLETE_DEPLOYMENT_GUIDE.md) · [Deployment](deployment.md) · [Accounts](accounts.md)

## Everyday health checks

```bash
cd /opt/inventory-manager
docker compose --env-file .env -f deploy/compose.yml ps
docker compose --env-file .env -f deploy/compose.yml logs --tail=100 api
docker compose --env-file .env -f deploy/compose.yml logs --tail=100 db
curl -f http://127.0.0.1:18424/health
sudo systemctl status inventory-backup.timer
sudo journalctl -u inventory-backup.service -n 100 --no-pager
```

The browser's public HTTPS `/health` endpoint also needs to pass; a local health response does not establish that DNS, certificates, or external routing work. Verify a real employee sign-in, store selection, scan, checkout, stock history, and alert email after major changes.

## Backup policy

The installer registers `inventory-backup.timer` for **03:15 host local time**; `deploy/backup.sh` writes gzip-compressed plain SQL (`pg_dump`) to `/var/backups/inventory-manager/inventory-YYYYMMDDTHHMMSSZ.sql.gz`. Local files older than **14 days** are removed on successful script runs. SQL backup files are permission-restricted. This is **not an offsite backup**.

```bash
cd /opt/inventory-manager
sudo systemctl list-timers --all | grep inventory
sudo bash deploy/backup.sh
sudo ls -lh /var/backups/inventory-manager/
sudo gzip -t /var/backups/inventory-manager/inventory-REPLACE-WITH-ACTUAL-TIMESTAMP.sql.gz
```

Back up **both** SQL dumps **and** `/opt/inventory-manager/.env`, encrypted and off-host. The latter contains `GOOGLE_TOKEN_KEY` needed to decrypt stored OAuth credentials, and database/bootstrap secrets. Also record your Git commit, server IP, DNS/DDNS configuration, proxy certificate/host, router/NAT/tunnel rules and Google OAuth client project. Do not put these secrets in Git or support messages. Proxmox snapshots are useful supplementary recovery points but not the only backup.

## Test a restore safely

Prefer an isolated staging host. The following creates a **disposable separate database inside the current PostgreSQL service**; it does **not** overwrite production, but should still be run carefully. Select a real backup filename, and check exit status.

```bash
set -o pipefail
cd /opt/inventory-manager
BACKUP=/var/backups/inventory-manager/inventory-REPLACE-WITH-ACTUAL-TIMESTAMP.sql.gz
sudo gzip -t "$BACKUP"
sudo docker compose --env-file .env -f deploy/compose.yml exec -T db \
  createdb -U inventory inventory_restore_test
sudo gzip -dc "$BACKUP" | sudo docker compose --env-file .env -f deploy/compose.yml exec -T db \
  psql -X -v ON_ERROR_STOP=1 -U inventory -d inventory_restore_test
sudo docker compose --env-file .env -f deploy/compose.yml exec -T db \
  psql -X -U inventory -d inventory_restore_test -c '\dt'
# Only when validated, delete the DISPOSABLE test database:
sudo docker compose --env-file .env -f deploy/compose.yml exec -T db \
  dropdb -U inventory inventory_restore_test
```

Check expected tables and a sample of restored stores/products/movements, not just that `gzip -t` passes. A failing pipeline means the test has **not** succeeded. Never point a restore test at the production `inventory` database.

## Full replacement-host recovery

1. Select a known-good SQL dump and **matching** secure `.env` backup; record expected data-loss window and downtime.
2. Provision Ubuntu 24.04, clone/install to `/opt/inventory-manager`; keep the public proxy disconnected.
3. Stop API writes and restore SQL into a **new, empty** PostgreSQL database under a reviewed maintenance procedure. Do not merge blindly into populated tables.
4. Restore the protected environment settings, especially `GOOGLE_TOKEN_KEY`, and correct DB role password configuration.
5. Start the API, check database and application logs, inventory totals, email sender and employee authentication.
6. Restore the public proxy/DNS/firewall path; test TLS, login, barcode scanner, stock movement, alert delivery and scheduled backups.

Do not copy raw Postgres volumes across incompatible major versions as a substitute for a logical backup. If the encryption key is gone, already-stored OAuth/Gmail tokens may be unusable and require reconnection.

## Updates and rollbacks

Before deploying: create **database plus .env backups**, check GitHub Actions for the intended SHA, and review schema changes. Normal update:

```bash
cd /opt/inventory-manager
sudo bash deploy/backup.sh
git status --short
sudo bash deploy/install.sh   # reuses current PUBLIC_ORIGIN
docker compose --env-file .env -f deploy/compose.yml ps
curl -f http://127.0.0.1:18424/health
```

The installer pulls fast-forward changes; it refuses local modifications. PostgreSQL persists in the Compose named volume. Reverting source code **does not revert database schema**. Before rolling back code, confirm compatibility; if not compatible, use a verified pre-upgrade backup and a reviewed recovery plan. Never use `docker compose down -v`, `docker volume prune`, or destructive SQL as an ordinary repair.

Changing domain requires coordinated DNS/TLS/Google changes, then `sudo bash deploy/install.sh --domain new.example.com`. See [Deployment §8](deployment.md#8-update-or-migrate-domains).

## Troubleshooting

| Symptom | Check / response |
| --- | --- |
| `PUBLIC_ORIGIN` interpolation error | Run updated `sudo bash deploy/install.sh --domain your-domain.com`; confirm `grep '^PUBLIC_ORIGIN=' .env`; don't expose other keys |
| Installer refuses update | Ubuntu version, root privileges, `git status --short` (dirty checkout), required domain, or Docker/LXC constraints |
| API 502 or down | Compose `ps`, `logs api`/`logs db`, `curl http://127.0.0.1:18424/health`, proxy upstream host:18424 |
| Works on LAN but not public hostname | DNS A/AAAA, WAN/NAT/CGNAT, trusted firewall, TLS proxy, tunnel, ISP restrictions |
| TLS errors or loops | Certificate hostname and renewals; Cloudflare Full (strict) when proxied; no Flexible mode |
| Wrong store/empty inventory | Verify selected store before assuming data loss; check service volume and app history |
| OAuth JSON rejected | Correct **Web application** client; exact `PUBLIC_ORIGIN` JavaScript origin and `/api/google/callback`; use new JSON after changing Google Console |
| Google redirect mismatch | Compare configured domain, protocol, path and callback; verify actual active Google client |
| Google connection doesn't send | Global sender connection status, Gmail API/consent, SMTP fallback, sender test, logs; OAuth External Testing refresh-token lifetime |
| Employee activation fails | Employee ID created, still unclaimed, global mail sender operational, verification link not expired |
| Email change doesn't appear | Old email stays active until new-address **30-minute** verification link confirmed |
| Forgot password never arrives | Verified email required, sender healthy, rate limits, spam folder; responses deliberately avoid user enumeration |
| OWNER renamed, setup appears | Check persisted owner identity settings/migration and server logs; do **not** rerun setup or hand-edit employee records without a backup |
| Account access unexpectedly rejected | 15-minute idle expiry, revoked session after email/ID change, lost store permission; sign in again |
| Scanner unavailable | HTTPS secure context, camera permission, device/browser compatibility, supported barcode format; try USB scanner |
| Backups absent | `systemctl status inventory-backup.timer`, service journal, Docker health, disk space and permissions |

## Security and handover

Before opening to the public: mitigate **unclaimed employee-ID first-claim** risk, restrict app/API and infrastructure management ports, test permission boundaries, use TLS, verify Gmail sender ownership, maintain encrypted offsite SQL + `.env` backups, monitor access/changes, remove test accounts, and check brand-asset usage authorization.

For handover, explicitly record who owns the domain, DNS, hosting/Proxmox, network firewall, certificates, Google project, sender mailbox, GitHub source access and backup destinations. Define responsibility for renewals, deployments, updates, incident response, retention, staff offboarding, and any commercial code/license agreement. Technical documentation itself does not transfer IP rights.

| Cadence | Operator checklist |
| --- | --- |
| Daily | Availability, transaction flow, backup status, outgoing mail |
| Weekly | API errors, storage, queued alerts, account/store permissions |
| Monthly | Certificate renewal, security patches, **offsite restore test** |
| Each release | SQL + `.env` backup, CI and change review, deployment smoke test |
| Staff departure | Revoke permissions, assess session/credential rotation and audit |
| DNS/Google changes | Re-test HTTPS, OAuth callback, verification/reset mail, alerts |

## Production acceptance

- [ ] HTTPS hostname, DNS and proxy work from a separate network; app port 18424 not publicly forwarded.
- [ ] New owner setup, verified email, real password sign-in and 15-minute idle logout work.
- [ ] Employee activation, change email, reset password, store permissions and owner ID rename are tested.
- [ ] Scanner works on actual target browsers/devices; check-in/out/history and reorder CSV agree with inventory.
- [ ] Low-stock alert reaches the correct store recipients.
- [ ] Daily backup exists, offsite copy succeeds, and a **test restore** has been verified.
- [ ] Security/first-claim risks have explicit mitigations; handover ownership is documented.
