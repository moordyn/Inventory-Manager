# Deploy Inventory Manager

**Scope:** fresh installation, network exposure, Google integration, upgrades and domain changes. For account setup see [Accounts](accounts.md); for backups and restoration see [Operations](operations.md). All hostnames/IPs below are **examples**, not services supplied by this project.

## 1. Plan and prepare

| Item | Required choice |
| --- | --- |
| Application host | Ubuntu 24.04 (VM, physical host, or correctly configured Proxmox LXC with Docker/nesting support) |
| Domain | Your own hostname, e.g. `inventory.example.com` |
| DNS | Any registrar/DNS provider; optional DDNS if public IP changes |
| HTTPS ingress | Nginx Proxy Manager (example), Caddy, nginx, or other trusted reverse proxy/tunnel |
| Email | Google Cloud OAuth Web application + Gmail sender; optional Gmail SMTP app-password fallback |
| Backups | Database SQL + encrypted `.env` copy stored off-host |
| Access | SSH/root or sudo to application host; DNS/proxy/router access |

Topology: **Browser → HTTPS hostname (port 443) → reverse proxy → Ubuntu host TCP 18424 → API container TCP 8080 → internal PostgreSQL 17**. PostgreSQL has no host-published port. Restrict the API host port to the reverse proxy/trusted LAN; keep Proxmox, router, proxy administrator interfaces private.

On Proxmox LXC, enable appropriate container nesting and Docker prerequisites according to your host security policy; a VM is a valid alternative. Verify `cat /etc/os-release`, `ip addr`, `docker info`, disk capacity, local time, and host networking. Your server must be able to reach package repositories and Google services if Gmail integration is used.

## 2. Install

Inspect the install script before executing it. On an existing checkout:

```bash
cd /opt/inventory-manager
git status --short
sudo bash deploy/install.sh --domain inventory.example.com
```

On an empty host:

```bash
sudo apt-get update
sudo apt-get install -y git
sudo git clone https://github.com/moordyn/Inventory-Manager.git /opt/inventory-manager
cd /opt/inventory-manager
sudo bash deploy/install.sh --domain inventory.example.com
```

Use your real hostname in place of `inventory.example.com`. Accepted forms:

```bash
sudo bash deploy/install.sh --domain example.com
sudo bash deploy/install.sh --domain=https://example.com
sudo PUBLIC_ORIGIN=https://example.com bash deploy/install.sh
sudo bash deploy/install.sh        # reuse a configured .env origin, or prompt
sudo bash deploy/install.sh --help
```

The origin must be HTTPS with no port, trailing slash, path, query or fragment. `--domain` overrides `PUBLIC_ORIGIN`; otherwise the value already stored in `.env` is retained. The installer rejects a dirty Git checkout rather than overwriting local changes. It installs Docker/Compose when needed, creates/reuses `/opt/inventory-manager/.env`, builds services, and enables the database backup timer. It generates random `POSTGRES_PASSWORD`/`SETUP_TOKEN` on new installs and `GOOGLE_TOKEN_KEY` if absent. Existing values persist.

**Verify without revealing credentials:**

```bash
cd /opt/inventory-manager
grep '^PUBLIC_ORIGIN=' .env
stat -c '%a %n' .env                 # should be 600
docker compose --env-file .env -f deploy/compose.yml ps
curl -f http://127.0.0.1:18424/health
sudo systemctl status inventory-backup.timer
```

The installer prints a first-run setup token: secure it. Do not publish `.env`, terminal captures containing tokens, or OAuth JSON.

## 3. DNS, changing public IP, and the firewall

Create DNS A (and valid AAAA if IPv6 is configured) for the **exact** chosen hostname to the public address of the proxy/tunnel. Example: `inventory.example.com → YOUR_PUBLIC_IP`. For a residential/dynamic WAN IP, set up DDNS in your router or DNS provider; Cloudflare + OPNsense DDNS is one option, not a requirement.

If the proxy is on a LAN behind your router, forward inbound **TCP 80/443** to the proxy according to your chosen certificate/HTTP challenge design; do **not** forward the API's 18424, PostgreSQL 5432, Proxmox or reverse-proxy admin ports to the Internet. If behind CGNAT or double NAT, port forwarding may not work: use a supported tunnel, VPN, public ingress host, or correct upstream NAT configuration. Verify the effective WAN address and the authoritative DNS records (remember proxied Cloudflare records may resolve to Cloudflare addresses).

Examples for DNS diagnostics:

```bash
getent ahostsv4 inventory.example.com
dig +short inventory.example.com A
dig +short inventory.example.com AAAA
```

Replace the example hostname; `dig` may require `dnsutils`. Verify external reachability from a different network, such as cellular data.

## 4. HTTPS reverse proxy

In **Nginx Proxy Manager** (or equivalent), add a proxy host:

| Setting | Value |
| --- | --- |
| Public domain | `inventory.example.com` (your exact host) |
| Scheme to upstream | `http` |
| Forward host | Your Ubuntu host's LAN IPv4 (not `localhost` if NPM is on another machine) |
| Forward port | `18424` |
| Public certificate | Valid certificate covering that domain; enable HTTPS redirect |
| Proxy admin interface | Private/trusted management network only |

For a step-by-step Cloudflare, OPNsense DDNS and Nginx Proxy Manager configuration, see [Optional networking examples](networking-examples.md).

For Cloudflare orange-cloud proxy, use a valid origin certificate and **Full (strict)** SSL; avoid Flexible mode and conflicting redirect loops. Ensure DNS, TLS certificate SAN, proxy hostname, `.env` `PUBLIC_ORIGIN`, and Google OAuth URIs match **exactly** (including `www` vs non-`www`). Test:

```bash
curl -I https://inventory.example.com/
curl -f https://inventory.example.com/health
```

The installer neither provisions a certificate nor updates your DNS/firewall.

## 5. Owner and employee setup

Open the HTTPS site and complete first-run owner setup with the installer-generated `SETUP_TOKEN`, email + confirmation and password + confirmation. Use 8–128 characters including uppercase, lowercase, digit and symbol. Log in with the owner's employee ID and password. The initial owner starts as `OWNER`, but global administrators can later **rename the original owner ID**; the application retains its recovery identity in the database and must not recreate OWNER after a rename. Configure your company sender before verifying the owner's mailbox or activating employees.

Detailed workflow, roles, security and email changes: [Accounts](accounts.md).

## 6. Google Cloud OAuth and Gmail

Use **your own** Google Cloud project and credentials:

1. In Google Cloud Console, create/select a project; configure the OAuth consent screen/brand and audience, contact details and test users as required. Enable **Gmail API**.
2. Create an OAuth client of type **Web application** (not Desktop or Android).
3. Add **Authorized JavaScript origin**: `https://inventory.example.com`.
4. Add **Authorized redirect URI**: `https://inventory.example.com/api/google/callback` (exact path; no trailing slash).
5. Download the client's JSON. Keep the client secret private. Review Google verification/testing requirements if an External consent screen is used; testing-mode refresh tokens can expire early.
6. Sign in as a global administrator → **Administration → Global email sender**. Import the downloaded Web OAuth JSON and confirm your own account password. Connect the company Gmail sender through Google consent.
7. Per store, set low-stock notification recipients and enable alerts; send a test email. The one global sender sends notifications across all stores, but recipient lists are store-specific.

Alternatively, stage the JSON securely on the Ubuntu host and run:

```bash
cd /opt/inventory-manager
sudo bash deploy/configure-google.sh /secure/path/client_secret.json
# Remove the staged credential after verification.
```

The import script validates JSON redirect/origin against `PUBLIC_ORIGIN`; changed Google Console values require a freshly downloaded JSON. The same HTTPS callback must work from the browser. Gmail OAuth credentials and refresh tokens are encrypted using the persistent `GOOGLE_TOKEN_KEY`: **never rotate that key casually**. Google integration is not used as employee sign-in; employees authenticate by employee ID and password.

### Optional SMTP fallback

If you don't use the Google Gmail sender, configure `SMTP_USER` and `SMTP_APP_PASSWORD` in the private `.env` (Google Gmail SMTP on port 587). This requires an applicable mailbox/app-password configuration and is separate from Google OAuth. Rebuild/recreate API with:

```bash
cd /opt/inventory-manager
docker compose --env-file .env -f deploy/compose.yml up -d --build
```

Employee activation/reset/change-email verification needs a configured outgoing mail sender. Store low-stock emails need per-store enabled alerts and recipients.

## 7. Environment reference

| Key | Purpose | Important handling |
| --- | --- | --- |
| `PUBLIC_ORIGIN` | Canonical public HTTPS origin for email links and exact OAuth callback | Required, no trailing slash; use `--domain` to update |
| `POSTGRES_PASSWORD` | Postgres role and API connection | Generated at first install; editing `.env` alone does **not** rotate the DB password in an existing volume |
| `SETUP_TOKEN` | One-time initial recovery administrator enrollment | Generated; never share |
| `GOOGLE_TOKEN_KEY` | Encryption for stored OAuth credentials/tokens | Preserve 64 hex chars; losing it may require Google reconnection |
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | Optional server-side Google client config | Alternative to dashboard import; secrets stay private |
| `SMTP_USER`, `SMTP_APP_PASSWORD` | Optional account and alert email fallback | Secret; Gmail SMTP provider-specific requirements |

Compose supplies `ConnectionStrings__Inventory` for the internal database and sets API listener `ASPNETCORE_URLS=http://+:8080`. Do not put real tokens in documentation, Git, screenshots or issue reports.

## 8. Update or migrate domains

Before any upgrade: verify [backups](operations.md), check the target commit's GitHub Actions result, and plan a maintenance window. For normal updates:

```bash
cd /opt/inventory-manager
sudo bash deploy/backup.sh
git status --short
sudo bash deploy/install.sh            # preserves existing domain
docker compose --env-file .env -f deploy/compose.yml ps
curl -f http://127.0.0.1:18424/health
```

The installer performs a fast-forward pull. It preserves the database volume and existing secrets but **does run app startup schema changes**. It does not downgrade schemas during a Git rollback. Never run `docker compose down -v` or `docker volume prune` as an update.

For a new domain: prepare DNS/proxy/certificate **first**; add the new Google OAuth origin and exact callback in your own Google project; back up `.env` and database; run `sudo bash deploy/install.sh --domain new.example.com`; verify HTTPS/Gmail/email links and remove old DNS/OAuth entries only afterward. The change is not accomplished by `--domain` alone.

For failures, see [Operations troubleshooting](operations.md#troubleshooting).
