# ZAGG Inventory Manager — Complete installation, deployment and operations guide

**Public site:** **bring your own domain** (example: `https://inventory.example.com`; replace with the hostname you control)  
**Source repository:** https://github.com/moordyn/Inventory-Manager  
**Baseline platform:** Ubuntu 24.04 in a Proxmox LXC (or compatible host), Docker Compose, PostgreSQL 17, ASP.NET Core 10. **Examples** use Cloudflare DNS, OPNsense and Nginx Proxy Manager; you may use equivalent DNS, firewall and HTTPS reverse-proxy services.  
**Guide updated:** November 2026; grounded in this repository's installer, Compose file, API, Google integration, database scripts, and vendor documentation. All example domains and IP labels are placeholders.  
**Audience:** The installer, store owner, network administrator, Google Cloud administrator, and anyone taking over long-term maintenance.

> **Important status:** This is a detailed deployment and recovery runbook, **not** proof of a production security review. The app's ordinary employee login currently requires only an employee ID. Certain store administrator sessions can also be reached through this login route. Protect the site with a stronger authentication/access-control layer or implement proper per-user authentication **before exposing sensitive business inventory data to the public internet**. Also verify the actual current GitHub Actions results, on-site scanner behavior, database backup/restore, and permissions. Do not mistake a successful Docker startup for successful end-to-end testing.
>
> **Scope:** This guide documents the current application. It does **not** claim that the Inventory Manager installer configures Proxmox, OPNsense, Cloudflare, public certificates, Nginx Proxy Manager, or Google Cloud for you. Those are separate manual steps.

### Bring your own services — quick start

**You do not need to own or use the original developer's domain or Google Cloud account.** Every installation must choose its own canonical public HTTPS origin. The application now reads `PUBLIC_ORIGIN` from its private host `.env` file. The first-time installer asks for it, or accepts it as an environment variable. The backend uses the same value to generate the Google OAuth callback and validate the OAuth Web client JSON.

For example, if the installer owns `example.com` and wants this application on its `inventory` subdomain:

| You supply | Illustrative value — replace it |
| --- | --- |
| DNS zone you control | `example.com` |
| Site hostname | `inventory.example.com` |
| **PUBLIC_ORIGIN** | **`https://inventory.example.com`** |
| Google OAuth authorized JavaScript origin | `https://inventory.example.com` |
| Google OAuth authorized redirect URI | **`https://inventory.example.com/api/google/callback`** |
| Google Auth Platform authorized domain | Usually `example.com` (the registrable domain) |
| Cloudflare API token | **Only if using Cloudflare DDNS**; create your own restricted token |
| Gmail API OAuth Client ID and Secret | Created in **your own Google Cloud project** and imported privately |
| Sending mailbox | Your business-controlled Gmail or supported Workspace account |

A root domain (e.g. `https://example.com`) is equally valid: set `PUBLIC_ORIGIN=https://example.com`, use DNS host `@`, and register `https://example.com/api/google/callback`. A `www` or deeper subdomain also works if it is the exact hostname configured in every layer. **Use one canonical URL consistently, no trailing slash, path or custom port.**

**Placeholder rule:** The example domains `example.com`, `inventory.example.com`, `YOUR_DOMAIN`, `INVENTORY_LXC_IP`, and `NPM_LAN_IP` are **not** live values and must not be copied unchanged into production. Never use `inventory.example.com` as the real production value.

**Provider-neutral choices:** You can use any DNS registrar/provider, any static public IPv4 or supported DDNS updater, any router that forwards HTTPS to a secure reverse proxy, and your own Google Cloud credentials. Cloudflare and OPNsense are **one illustrated network setup**, not required dependencies. Google OAuth/Gmail is optional if you do not use Gmail alerts; the core inventory functions do not require a Google account.

---

## Contents

1. [What this deployment consists of](#1-what-this-deployment-consists-of)
2. [Record installation information](#2-record-installation-information)
3. [Prerequisites and Ubuntu/Proxmox preparation](#3-prerequisites-and-ubuntuproxmox-preparation)
4. [Install Inventory Manager from scratch](#4-install-inventory-manager-from-scratch)
5. [Initial admin PIN and store onboarding](#5-initial-admin-pin-and-store-onboarding)
6. [Choose DNS and configure optional Cloudflare/OPNsense DDNS](#6-choose-dns-and-configure-optional-cloudflareopnsense-ddns)
7. [Router rules and an HTTPS reverse proxy](#7-router-rules-and-an-https-reverse-proxy)
8. [HTTPS, certificates and optional Cloudflare proxy](#8-https-certificates-and-optional-cloudflare-proxy)
9. [Google Cloud OAuth and Gmail API](#9-google-cloud-oauth-and-gmail-api)
10. [Connect the global Gmail sender and configure alerts](#10-connect-the-global-gmail-sender-and-configure-alerts)
11. [Optional SMTP fallback](#11-optional-smtp-fallback)
12. [Daily use and workflow validation](#12-daily-use-and-workflow-validation)
13. [Environment variables and sensitive data](#13-environment-variables-and-sensitive-data)
14. [Backups and tested disaster recovery](#14-backups-and-tested-disaster-recovery)
15. [Updates, rollbacks and migrations](#15-updates-rollbacks-and-migrations)
16. [Troubleshooting by symptom](#16-troubleshooting-by-symptom)
17. [Security and business handover](#17-security-and-business-handover)
18. [Final acceptance checklist](#18-final-acceptance-checklist)
19. [Authoritative references](#19-authoritative-references)

---

## 1. What this deployment consists of

### 1.1 Provider-neutral traffic flow

~~~text
Desktop/mobile browser -> HTTPS to YOUR_APP_HOSTNAME
     |
     v
YOUR DNS provider (static A/AAAA or DDNS-updated public IP)
     |
     v
Router/firewall (if self-hosting behind NAT) or public server
     |
     v
HTTPS reverse proxy + matching TLS certificate
     |
     v
Ubuntu application host: http://INVENTORY_LXC_IP:18424
     |
     v
ASP.NET Core 10 API/frontend container: 8080
     |
     v
PostgreSQL 17 container: db:5432 (Docker-only network)
     |
     +-- daily host-level SQL backups
     +-- optional outbound Google OAuth + Gmail API
~~~

**Example network variant:** Cloudflare DNS + OPNsense DDNS + WAN TCP/443 forwarding to Nginx Proxy Manager (NPM). Alternatives include a static public IP without DDNS, a different provider's DDNS service, a secure HTTPS reverse proxy such as Caddy or Traefik, or a supported tunnel/access gateway if public inbound IPv4 is unavailable. Instructions for your chosen alternative may differ, but your **canonical HTTPS hostname and Google OAuth callback must match exactly**.

### 1.2 What the repository installs

- **Inventory Manager app:** ASP.NET Core 10 API plus embedded responsive web interface.
- **PostgreSQL 17:** Docker service `db`, persisted in a named Docker volume.
- **Local port:** the supplied Compose file publishes API TCP/18424 on the Ubuntu host; it does not publish PostgreSQL.
- **Host secrets:** `/opt/inventory-manager/.env` with DB password, setup token, per-site `PUBLIC_ORIGIN` and Google encryption key.
- **Host backup timer:** daily compressed SQL backups at 03:15 local Ubuntu time, with about 14 days of local retention.
- **Not installed:** your DNS provider, domain registration, router firewall/NAT, reverse proxy, TLS certificate, Google project/credentials, mail account, offsite backup system, and strong identity-provider authentication.

### 1.3 Configuration mapping — replace these examples

| Component | Example/constant |
| --- | --- |
| Domain zone | `example.com` — yours may differ |
| Hostname for app | `inventory.example.com` **example only** |
| Host env setting | `PUBLIC_ORIGIN=https://inventory.example.com` |
| Google JavaScript origin | **same exact value as PUBLIC_ORIGIN** |
| Google redirect URI | `PUBLIC_ORIGIN + /api/google/callback` |
| Source repo | `https://github.com/moordyn/Inventory-Manager`, branch `main` |
| Host directory | `/opt/inventory-manager` |
| Compose file | `deploy/compose.yml` |
| LAN app URL | `http://INVENTORY_LXC_IP:18424` |
| DB | service `db`, database/user `inventory`, TCP/5432 inside Docker |
| First seeded store/admin | `DEFAULT` (“Original Store”), `OWNER` (“Original Administrator”) |
| Backup directory | `/var/backups/inventory-manager` |

**Do not point Google to your LAN HTTP address.** The OAuth callback must be externally reachable over HTTPS at your chosen `PUBLIC_ORIGIN`.

---

## 2. Record installation information

Before changing anything, make a **private** deployment worksheet for *this customer's/site's own* infrastructure:

| Field | Fill in privately |
| --- | --- |
| Business/site name and authorized software/brand use | [your value] |
| Your purchased/authorized DNS zone | [e.g., `yourcompany.com`] |
| Your desired canonical inventory site | [e.g., `inventory.yourcompany.com`] |
| **PUBLIC_ORIGIN** (`https://`, no trailing slash) | [your origin] |
| OAuth callback (`PUBLIC_ORIGIN/api/google/callback`) | [calculated URL] |
| Proxmox host/LXC ID or other Ubuntu host | [your values] |
| Inventory Ubuntu LAN IP | [your value] |
| Router/firewall type and network topology | [your values] |
| Public WAN IPv4, static/dynamic, CGNAT/double NAT status | [your value] |
| DNS provider and DDNS strategy, if needed | [your values] |
| Reverse proxy host and LAN IP | [your values] |
| TLS certificate provider and renewal owner | [your values] |
| Google Cloud project you own/control | [your values] |
| OAuth Web client ID (not secret) | [your value] |
| Google OAuth secret and DDNS token storage | [**password manager references only**] |
| Gmail account used as global sender | [your account] |
| Offsite database + `.env` backup destination | [your values] |
| Support contact and restoration approver | [your values] |

The sample domains in this guide belong to the **example namespace only**; they are not owned by this project or by an installer. Never commit domain transfer credentials, Cloudflare keys, private TLS keys, Google OAuth JSON, Gmail refresh tokens, `.env`, or administrator PINs.

### Decide on a deployment profile

Choose **one** route:
- **Profile A — static IP:** DNS A record manually points at your fixed public IP; no DDNS integration required.
- **Profile B — dynamic home/shop IP:** Use your DNS provider's updater or Cloudflare + OPNsense `os-ddclient` (detailed in §6); avoid CGNAT.
- **Profile C — inbound connections blocked/CGNAT:** Use a vetted HTTPS tunnel or other permitted gateway and verify callback support; conventional OPNsense port forwarding cannot fix CGNAT.
- **Profile D — cloud VPS:** Point DNS at your public server IP and use an HTTPS reverse proxy there; Proxmox/OPNsense are not required.

The **application** deployment remains Docker Compose on a supported Ubuntu host; only network/DNS instructions differ.

---

## 3. Prerequisites and Ubuntu/Proxmox preparation

### 3.1 What you need — select your own providers

Before installing, obtain access to:

1. A functioning **Ubuntu 24.04** host with administrator access (a Proxmox LXC, VM, or suitable dedicated host).
2. A domain that **you control or are authorized to use**. Register it yourself if you do not yet have one.
3. DNS management for that domain (Cloudflare **or another DNS provider**).
4. An external HTTPS traffic path (static public IP, DDNS + router port forwarding, VPS, or a supported tunnel); determine if CGNAT applies.
5. An HTTPS reverse proxy (examples use **Nginx Proxy Manager**; equivalent maintained proxies are possible).
6. **Optional for email alerts:** Your own Google Cloud project, Gmail API access, OAuth Web application client, and company-controlled sending Gmail/Workspace account.
7. GitHub/network access to this repository, an offsite backup destination, and an administrator responsible for the app.

**Cloudflare, OPNsense, and Proxmox are examples, not mandatory purchases.** The provided installer, however, specifically expects Ubuntu 24.04 and deploys Docker.

### 3.2 Prepare your Ubuntu host (example: Proxmox LXC)

Create an Ubuntu 24.04 container; suggested *starting* resources for a small business: **2 vCPU, 2–4 GB RAM, 20+ GB storage**, scaled to actual load. A full VM may be preferable if Docker-in-LXC feature/security limitations arise.

- Use a dedicated host/LXC with a stable LAN IPv4 or DHCP reservation. Record it as `INVENTORY_LXC_IP`.
- Keep Ubuntu's date/time and NTP accurate and install security updates.
- For Docker in a Proxmox LXC, configure supported nesting/keyctl features if required. Avoid unnecessary privileged containers.
- Permit only the HTTPS proxy/trusted admin network to reach the application's host port **18424**; never publicly forward it.
- Allow necessary outbound DNS, HTTPS, Docker image registries, GitHub, and Google API endpoints (if using Gmail).
- Check disk/RAM, power protection, snapshots **as a secondary safeguard**, and offsite backups.
- If the host is a VPS with a public IP, restrict **18424** in its cloud firewall and use a secure HTTPS proxy on an appropriate network interface.

~~~bash
cat /etc/os-release
hostname -I
date
df -h
free -h
ip route
getent hosts github.com
~~~

If the host is not Ubuntu 24.04, the installer exits by design. Do not bypass the version check without porting and testing the deployment scripts.

### 3.3 Security gate before Internet exposure

**Current ordinary employee login requires an employee ID only; it is not strong authentication.** A store administrator role can be reachable from such a login session. Put the installation behind a trusted VPN or identity-aware gateway with MFA and/or implement strong individual user credentials before exposing business data on a public domain. Test the OAuth callback, browser/mobile scanners, authorized customer devices and alert delivery with any gateway restrictions. Rebranding is also required when using the software outside the rights attached to its existing ZAGG visual assets.

---

## 4. Install Inventory Manager from scratch

### 4.1 Choose the final HTTPS hostname **before installing**

You bring your own public site name. Decide on the exact canonical URL **now**. Examples:

| Desired site | Enter when asked for PUBLIC_ORIGIN |
| --- | --- |
| Subdomain `inventory.example.com` | `https://inventory.example.com` |
| Root `example.com` | `https://example.com` |
| Another subdomain `stock.shop.example.com` | `https://stock.shop.example.com` |

**All are examples. Replace with a domain YOU control.** The accepted value must be HTTPS with a DNS hostname, no trailing slash, no path, no query, no custom port. The same exact value will later be the Google JavaScript origin; append `/api/google/callback` for the Google OAuth redirect URI.

The installer now prompts for `PUBLIC_ORIGIN` the **first time** it creates `.env`. A non-interactive installation must supply `PUBLIC_ORIGIN` explicitly. Installing with one hostname and later moving to another is supported, but requires editing `.env`, configuring DNS/TLS and updating Google Cloud (§15.4).

### 4.2 Connect to the correct Ubuntu server

SSH into the **inventory application Ubuntu machine** or open its Proxmox console and become root:

~~~bash
sudo -i
~~~

Do not run this script on your DNS provider, OPNsense, the Proxmox hypervisor or the NPM server unless that machine was intentionally provisioned as the Ubuntu app host.

### 4.3 Inspect the installer

~~~bash
curl -fsSL https://raw.githubusercontent.com/moordyn/Inventory-Manager/main/deploy/install.sh -o /tmp/inventory-install.sh
less /tmp/inventory-install.sh
~~~

The script [deploy/install.sh](../deploy/install.sh):
1. Requires root and Ubuntu 24.04.
2. Installs prerequisites, Docker Engine and Compose if necessary.
3. Clones/pulls the repository into `/opt/inventory-manager` (a dirty checkout stops the update).
4. On first installation, **prompts for your HTTPS PUBLIC_ORIGIN** or reads it from the shell environment; rejects invalid names.
5. Creates `/opt/inventory-manager/.env` containing your `PUBLIC_ORIGIN` plus random `POSTGRES_PASSWORD` and `SETUP_TOKEN`; creates a persistent `GOOGLE_TOKEN_KEY`.
6. Starts the PostgreSQL and API Docker services; installs and enables the 03:15 database backup timer.
7. Prints the LAN URL and one-time setup token. **Keep this token out of public logs/screenshots.**

### 4.4 Run a fresh install — interactive or unattended

**Interactive** (recommended for first installation):

~~~bash
bash /tmp/inventory-install.sh
~~~

When prompted, **type your own** `PUBLIC_ORIGIN`, such as `https://inventory.yourcompany.com`.

**Unattended/noninteractive** (example syntax; replace hostname!):

~~~bash
sudo PUBLIC_ORIGIN=https://inventory.example.com bash /tmp/inventory-install.sh
~~~

The command deliberately refuses an unattended **first install** when the domain has not been supplied. There is no default DNS zone you can assume belongs to the application developer.

**Existing server update:**

~~~bash
cd /opt/inventory-manager
sudo bash deploy/install.sh
~~~

The installer preserves existing secrets and domain settings. Legacy installations that were created before `PUBLIC_ORIGIN` existed receive the former production domain as a **compatibility value**; the operator must change it deliberately when migrating. This migration behavior is for existing installations only, **not** a domain everyone can use.

### 4.5 Verify actual configuration without printing secrets

~~~bash
cd /opt/inventory-manager
sudo grep '^PUBLIC_ORIGIN=' .env
sudo docker compose --env-file .env -f deploy/compose.yml ps
sudo docker compose --env-file .env -f deploy/compose.yml logs --tail 100 api
sudo docker compose --env-file .env -f deploy/compose.yml logs --tail 100 db
curl -fsS http://127.0.0.1:18424/health
curl -fsS http://127.0.0.1:18424/api/auth/status
~~~

The printed `PUBLIC_ORIGIN` must be **your** domain, not the example. Do **not** print the full `.env` for support or paste it into chat.

The API/database should be healthy and the LAN HTTP port should respond. A new database shows `initialized:false` until the PIN setup. `/health` tests API process status, **not** all workflows or database health.

The current Compose file maps host **18424** to API container **8080**, with PostgreSQL on the internal Docker network. Block unsolicited access to 18424 from the Internet.

### 4.6 Persistence and what updates don't erase

- PostgreSQL persists in a Docker named volume (`inventory_data`; actual volume name may have a Compose project prefix).
- Application source and images can be rebuilt; the data volume is not meant to be deleted.
- `.env` is private/ignored by Git; it holds the canonical site origin and the key protecting Google refresh tokens.
- Gmail client secrets imported through the dashboard and refresh tokens are encrypted in PostgreSQL, using the unchanged `GOOGLE_TOKEN_KEY`.
- SQL backups go to `/var/backups/inventory-manager` and **must** be copied to a separate protected location.
- Never run `docker compose down -v` or `docker volume prune` as a routine update.

**Next:** set up your DNS and HTTPS route using §6–§8, then complete the first-run PIN and optional Google authorization.

---

## 5. Initial admin PIN and store onboarding

### 5.1 First-run PIN

On a **brand-new database**, visit the local LAN URL or, once network and TLS are working, the public HTTPS URL. The first-time screen asks for:

1. **Installation token:** a generated secret printed by <code>deploy/install.sh</code>, saved in <code>.env</code>.
2. **New PIN:** a six-digit numeric administrator PIN.
3. **Confirm PIN:** repeat the six digits.

The PIN is stored as a salted PBKDF2 hash. **The setup token is not an administrator password** and cannot be used to replace an existing PIN through the setup screen. Keep both credentials private.

If the original terminal output is no longer available, a root administrator can inspect the local protected <code>.env</code> without disclosing it to others:

~~~bash
cd /opt/inventory-manager
sudo grep '^SETUP_TOKEN=' .env
~~~

Do **not** paste that output into logs, email, tickets or support chats. If <code>/api/auth/status</code> already shows initialized, do **not** expect the first-run screen or rerun the setup process.

### 5.2 Log in as global administrator

Use **Administrator access** on the sign-in screen with:

- Administrator employee ID: <code>OWNER</code> on a fresh install.
- Administrator PIN: the six-digit PIN enrolled above.

The initial database creates store <code>DEFAULT</code> (“Original Store”) and the global administrator <code>OWNER</code> (“Original Administrator”). The PIN-protected global administrator is distinct from a basic employee ID login.

### 5.3 Configure stores

Open **Administration**. Set a recognizable name for the default store and add additional stores as needed.

- Each store has a unique **Store ID** consisting of 2–24 letters, digits, hyphens or underscores.
- Each store also has a human-readable name. Record the ID-to-location mapping for staff and backups.
- Creating/managing locations is a **global administrator** activity.
- Be careful when renaming store IDs after the app is in use: it touches relationships across inventory, sessions and audit history. Take a backup first and perform a smoke test afterward.

### 5.4 Configure employees and access

In **Administration**, create employees with a unique employee ID and name, assign their primary store, and grant additional store access where appropriate.

- Employee IDs must be **unique across all stores** and 2–24 allowed characters (letters/digits/hyphens/underscores).
- Standard employees use **Employee login** with their employee ID.
- Store administrators can manage store-specific administrative features if granted that role.
- The global administrator controls company-wide Gmail sender settings and global-level store/role management.
- Remove access when an employee leaves; confirm their sessions and permissions are no longer valid.
- An employee's **primary** store access is not removable from the access-grants interface.
- Staff switch their active store using the store selector when they have permission for more than one store.

**Security reminder:** These store permissions are not a substitute for strong authentication; employee-ID-only login is weak, especially if the user is also a store admin. Verify your access policy before a public launch.

### 5.5 First inventory entries

For each store, create a representative product:
- SKU and product name (required).
- Optional barcode, category and description.
- Optional unit cost and minimum-stock threshold.
- Starting quantity.

Scan the item with a barcode gun or the device camera; check stock counts, add stock, check out one item with a required reason, and confirm the movement appears in History. Test a second store to confirm inventory does not leak between stores.

## 6. Choose DNS and configure optional Cloudflare/OPNsense DDNS

You **do not need Cloudflare or OPNsense** to run the application. What you need is a registered/authorized hostname that resolves to a reachable HTTPS endpoint. Choose the simplest network arrangement that works for your site.

### 6.1 Create a DNS record with **your** provider

In your registrar/DNS provider's DNS control panel, add a record for the **exact hostname you chose**:

| Hostname you choose | DNS zone | Record type | DNS Name/Host field | Points to |
| --- | --- | --- | --- | --- |
| `inventory.example.com` | `example.com` | `A` | `inventory` | Your **public** IPv4 |
| `example.com` (apex/root) | `example.com` | `A` | `@` or provider's apex convention | Your **public** IPv4 |

If your public IP is **static**, this may be all the DNS configuration you need. Never put `INVENTORY_LXC_IP` (a private LAN address) in public DNS. Only add `AAAA` if reachable IPv6 is correctly configured end-to-end and has its own update strategy. Remove stale/conflicting records for the chosen hostname.

For Cloudflare: first ensure your registered domain is delegated to the Cloudflare-assigned nameservers and zone is Active. Start with **DNS only** (grey-cloud) while verifying reachability, and optionally enable **Proxied** (orange cloud) after correct HTTPS configuration. With proxied DNS, `dig` returns Cloudflare edge IPs, **not** the origin WAN address; inspect the origin A-record content in Cloudflare's dashboard.

For other providers: use their equivalent DNS record settings and TTL; their authentication/API/DDNS setup may be entirely different.

### 6.2 When dynamic DNS is required

If your ISP changes the public IP, install a **DDNS updater supported by your provider** and point it at the exact A record for the inventory hostname. Options include your router's built-in DDNS feature, a provider-specific client, or OPNsense `os-ddclient`. Cloudflare does not automatically discover your new IP merely because the DNS zone is proxied.

DDNS is **not required** if the IP is static or if your tunnel provider handles hostname routing independently. It does not fix CGNAT.

### 6.3 Optional: Cloudflare + OPNsense DDNS worked example

If you own/control `example.com` at Cloudflare and choose `inventory.example.com` as your app hostname, create an **API token** at https://dash.cloudflare.com/profile/api-tokens:

- Template or custom scope: `Zone → DNS → Edit`.
- Resources: `Include → Specific zone → example.com`.
- Additional `Zone → Zone → Read` only if required by your particular client/provider; not blanket account permissions.
- Save the token securely; do **not** use or expose your Cloudflare Global API Key or put the DDNS token in app `.env`.

In OPNsense, install `os-ddclient` at **System → Firmware → Plugins**, then open **Services → Dynamic DNS → Settings**. Choose the supported Cloudflare backend (OPNsense documentation notes a newer *native* backend as well as `ddclient`).

| OPNsense account field | Example (replace it) |
| --- | --- |
| Service | Cloudflare |
| Description | Inventory site DDNS |
| Username | **`token`** literally (Cloudflare API-token mode) |
| Password | Your newly generated scoped Cloudflare API token |
| Zone | **`example.com`** (DNS zone name, not Zone ID) |
| Hostname(s) | **`inventory.example.com`** (complete FQDN, not merely `inventory`) |
| Check IP method | WAN interface IPv4 **only when WAN truly holds your public IP**, otherwise an external public IPv4 checker |
| Interface | WAN or appropriate outbound interface |
| Force SSL | Enable |
| Enabled | Enable, then save/apply |

For the **root** hostname `example.com`, set Hostname(s) to `example.com` instead. Check the OPNsense DDNS status/log and Cloudflare's A-record **content**; both should show your origin public IPv4. Reference: [OPNsense Dynamic DNS documentation](https://docs.opnsense.org/manual/dynamic_dns.html).

### 6.4 No Cloudflare? Provider-neutral alternatives

- **Static IP + ordinary registrar DNS:** configure the A/AAAA record, verify with `dig`, skip the Cloudflare API-token and OPNsense DDNS instructions.
- **Other dynamic DNS vendor:** configure that vendor's updater with the credentials/scopes it specifies; OPNsense may support it through `os-ddclient` or a separate client. Do not enter the literal Cloudflare `token` username for unrelated services.
- **Cloudflare Tunnel or alternative HTTPS tunnel:** follow your provider's secure tunnel and custom-hostname setup. Inbound WAN TCP/443 may not be required, but the public HTTPS hostname and OAuth callback must still match.
- **VPS:** configure public DNS to your server, reverse proxy there, and avoid home-router NAT entirely.

### 6.5 Verify the chosen hostname

For DNS-only A records:

~~~bash
# Example only: replace inventory.example.com with your real hostname
dig +short A inventory.example.com
~~~

Confirm the IP is correct and that external HTTPS works after §7–§8. If on a proxied CDN, validate both the browser URL and the origin value inside the provider's dashboard.

### 6.6 CGNAT, double NAT and stale IP detection

- Private upstream WAN IP (`10/8`, `172.16/12`, `192.168/16`) or `100.64.0.0/10` carrier-grade NAT can defeat normal inbound IPv4 port forwarding.
- With two routers, a separate upstream forward or bridge mode may be needed.
- Under ISP CGNAT, request a public address or use an approved tunnel/VPS; **DDNS itself does not establish inbound reachability**.
- If your firewall WAN IP is private, use **external IPv4 detection** for DDNS rather than writing the WAN interface's address into public DNS.

---

## 7. Router rules and an HTTPS reverse proxy

### 7.1 Choose the network entry point

This section illustrates an **on-premise router + Nginx Proxy Manager (NPM)**. If you use a public VPS, configure its host/cloud firewall instead of home-router NAT. If you use a properly configured HTTPS tunnel, conventional WAN port forwards may not be needed. In all cases, the final site address must be exactly your `PUBLIC_ORIGIN`, and its TLS certificate must match your chosen hostname.

### 7.2 Optional OPNsense port forwards

For on-premise hosting with a reachable public IPv4, use your firewall/router's NAT settings. On **OPNsense** the typical page is **Firewall → NAT → Port Forward**:

| Interface | Protocol | External destination/port | Forward destination | Internal port |
| --- | --- | --- | --- | --- |
| WAN | TCP | WAN address : **443** | `NPM_LAN_IP` | **443** |
| WAN | TCP | WAN address : **80**, **only if required** for ACME/redirects | `NPM_LAN_IP` | **80** |

Apply the associated firewall rules. Use the actual NPM LAN IP from your worksheet, not the app server's hostname or the example placeholder. If NPM already hosts other services, these rules may already exist; do not create duplicate/conflicting rules.

**Never publish** Inventory Manager host port **18424**, internal API port **8080**, PostgreSQL **5432**, NPM admin port **81**, or Proxmox/OPNsense management ports to the Internet.

Other routers use different menus but the same principle: Internet TLS traffic reaches the secured HTTPS proxy, **not** the app port directly. If your ISP uses CGNAT, a conventional WAN forward cannot work.

### 7.3 Nginx Proxy Manager — recommended example

NPM is an **external component**; the Inventory Manager installer does **not** install it. Install/manage NPM according to its [official setup guide](https://nginxproxymanager.com/setup/), keep its admin panel LAN/VPN-only, and give it access to the inventory host on LAN TCP/18424.

From the **NPM server**, verify backend connectivity:

~~~bash
# Replace INVENTORY_LXC_IP with your actual Ubuntu host IPv4:
curl -fsS http://INVENTORY_LXC_IP:18424/health
~~~

Then choose **Hosts → Proxy Hosts → Add Proxy Host** and use:

| NPM field | Value |
| --- | --- |
| Domain Names | **Your own chosen hostname**, e.g. `inventory.example.com` |
| Scheme (backend) | `http` |
| Forward Hostname/IP | Your Ubuntu host's **LAN IP** (`INVENTORY_LXC_IP`) |
| Forward Port | **18424** |
| Block Common Exploits | Enable |
| WebSockets Support | Optional; not required for regular inventory operations |
| Access List | Your approved security/access policy; still implement strong application authentication |

Don't put the *public* domain in the Forward Hostname field (may loop back) or use port 5432. The upstream HTTP connection is private; the browser-facing site is HTTPS.

### 7.4 Using another reverse proxy

A maintained alternative (Caddy, Traefik, Apache or nginx) can handle TLS and proxy requests to `http://INVENTORY_LXC_IP:18424`. Configure hostname routing, certificate issuance/renewal, client/body limits, forwarded headers and timeouts appropriately. The repository does **not** ship a configured proxy for these alternatives; follow that proxy's official documentation. Preserve `/api/google/callback` exactly and do **not** route it to a different application.

A separate identity-aware gateway or VPN is advised until employee authentication is hardened. Test any login gate's interaction with Google OAuth redirects.

### 7.5 External verification

Test your public HTTPS hostname over mobile data, **not only LAN Wi-Fi**, to catch split DNS/hairpin NAT differences. From the proxy host, verify upstream `/health`; from a public browser, verify the exact hostname, certificate, app login and API requests before proceeding to Google OAuth.

---

## 8. HTTPS, certificates and optional Cloudflare proxy

### 8.1 Match the actual hostname

Your public `PUBLIC_ORIGIN` must use **HTTPS** and your TLS certificate must cover its hostname. Example: a site at `inventory.example.com` needs a certificate covering `inventory.example.com` (a suitable wildcard may also cover it). An SSL certificate for `example.com` **does not necessarily** cover `inventory.example.com`.

**General option — public ACME/Let's Encrypt certificate:** In Nginx Proxy Manager, request/import an appropriate public CA certificate for your hostname and ensure automatic renewal. HTTP-01 ACME challenges generally need port 80 reachability; DNS-01 challenges use your supported DNS provider and scoped DNS API access. This option works whether your domain uses Cloudflare DNS or another provider.

**Cloudflare Proxied option only:** You may instead use a Cloudflare Origin CA certificate between Cloudflare and your proxy. This certificate is trusted by Cloudflare **but not typically directly by browsers**. Only use it when the public site always passes through Cloudflare's proxy, and keep its private key secret. If you switch to DNS-only later, install a publicly trusted certificate first.

### 8.2 NPM SSL settings

In your NPM Proxy Host for **your actual hostname**:
1. Select/request the matching certificate.
2. After confirming TLS and renewals, enable **Force SSL**.
3. Optionally enable HTTP/2 where supported.
4. Only enable strict HSTS after you know HTTPS is stable and recoverable.

Do not expose the NPM admin page publicly. If you use a different proxy, use the equivalent certificate/TLS settings.

### 8.3 Only when using Cloudflare orange-cloud proxy

After DNS-only/HTTPS diagnostics, enable **Proxied** for the relevant DNS record if you want Cloudflare edge proxy features. In **Cloudflare → SSL/TLS**, select **Full (strict)** with a matching, valid origin certificate on your reverse proxy. Do not use Flexible. Cloudflare uses browser-to-edge and edge-to-origin TLS, so the edge certificate **does not replace** the origin certificate. [Cloudflare Full (strict) reference](https://developers.cloudflare.com/ssl/origin-configuration/ssl-modes/full-strict/).

If using a DNS provider **without** Cloudflare's reverse proxy, ignore Cloudflare's SSL mode entirely; visitors connect directly to the public HTTPS reverse proxy, which needs a public-browser-trusted certificate.

### 8.4 Confirm HTTPS before adding OAuth

From an external device, open your own `PUBLIC_ORIGIN`:
- e.g. `https://inventory.example.com/` — app login screen
- e.g. `https://inventory.example.com/health` — app health JSON

Check the certificate, browser address, reverse proxy upstream and API connectivity. No certificate warning or wrong-site redirect should occur.

Common failures:
- 502 at the proxy: backend IP/port wrong, app container down, firewall blocks 18424.
- Cloudflare 522: origin unreachable; 525/526: origin TLS/certificate failure.
- Endless redirects: incorrect proxy/TLS scheme, Cloudflare Flexible mode, or conflicting Force SSL.
- Google redirects to an unrelated domain: inspect `PUBLIC_ORIGIN` and the OAuth Web client settings.

Do not cache authenticated API requests or sensitive HTML using CDN cache-everything rules. Test camera barcode scanning over HTTPS after login.

---

## 9. Google Cloud OAuth and Gmail API

### 9.1 Understand which Google credential is required

This application uses **Google OAuth 2.0 Authorization Code Flow** for a single global Gmail sending account. It is **not** a service-account Gmail sender and does **not** need a generic Google API key.

The server requests:
- <code>openid</code>
- <code>email</code>
- <code>https://www.googleapis.com/auth/gmail.send</code>

The <code>gmail.send</code> scope permits sending messages (not general inbox browsing). Google consent policies and verification requirements depend on audience, account ownership and publishing status.

**This is a bring-your-own-Google-Cloud integration.** The application reads `PUBLIC_ORIGIN` from the private server `.env` (passed into the API via Docker Compose). Google redirects to `PUBLIC_ORIGIN + /api/google/callback`. The OAuth Web client **must be created in your own Google Cloud project** and the uploaded JSON must contain **your** origin and callback. A change of domain needs an intentional `PUBLIC_ORIGIN` edit/restart, DNS/proxy/SSL updates and Google OAuth client updates; it does **not** require editing/recompiling the application code.

### 9.2 Create or choose a Google Cloud project

Open https://console.cloud.google.com/ with **your own Google account**, and create a project under your business or select a project you legitimately control. **You do not need — and must not rely upon — the original developer's Google Cloud project, OAuth Client ID, client secret, consent screen, or sending mailbox.** For an established installation, keep using the existing authorized client's credentials where possible, but new customers should create their own.

- Confirm the appropriate business/account administrator owns the project and will retain access during personnel changes.
- Search for and enable the **Gmail API** in **APIs & Services → Library** if not already enabled.
- In the new Google Auth Platform interface, review **Branding**, **Audience**, **Clients**, and **Data Access**. Exact navigation labels can evolve.
- Set application name and support contact appropriately. Use branding/domain assets only with permission to use the associated trademark.
- Add your **registrable/root domain** (for the example `inventory.example.com`, this is normally `example.com`) under **Authorized domains** where Google requires it; production/verification may require proof of ownership. This is **not** the same field as an OAuth client's full JavaScript origin.
- Select a suitable audience for your intended Gmail account(s). A company-managed Google Workspace internal app differs from an External app. Google's newer Personal mode may also be available to eligible new projects; it has restrictions and cannot simply be toggled on in an existing External project.

**Publishing/refresh-token caution:** For **External** projects in **Testing**, when requesting <code>gmail.send</code> and offline access, Google's refresh tokens normally expire after **seven days**. If alerts stop working after approximately a week, inspect the project's Audience/publishing status and token logs; repeatedly reconnecting is not a substitute for an appropriate production configuration. Publishing and sensitive-scope verification may apply; do not claim the project is verified merely because Google allowed a test login.

### 9.3 Configure the OAuth Web Application client

Go to https://console.cloud.google.com/auth/clients.

Create an OAuth **Web application** client in **your own Google Cloud project**. If you already operate a client that you legitimately control and intend to reuse, edit that client instead. Never import somebody else's OAuth client JSON.

Enter exactly:

| Google OAuth client field | Example if your PUBLIC_ORIGIN is `https://inventory.example.com` |
| --- | --- |
| Application type | **Web application** |
| Authorized JavaScript origin | **`https://inventory.example.com`** — replace with the exact URL in your `.env` |
| Authorized redirect URI | **`https://inventory.example.com/api/google/callback`** — `PUBLIC_ORIGIN + /api/google/callback` |

**Root-domain alternative:** `PUBLIC_ORIGIN=https://example.com` means JavaScript origin `https://example.com` and redirect URI `https://example.com/api/google/callback`. For any other host, calculate it the same way. **Do not put the example hostname into your Google project unless it is actually your authorized hostname.**

**Exact match matters:** HTTPS, hostname, callback path and any trailing slash. This callback ends with <code>/callback</code> and **has no trailing slash**. Do not enter the LAN HTTP address, another business's hostname, an unintended `www` variant, a trailing `/`, or the `/api/google/connect` endpoint. Use your chosen `PUBLIC_ORIGIN` precisely.

Click **Save** in Google Cloud. Download the Web OAuth client **JSON** **after** saving, so its <code>redirect_uris</code> and <code>javascript_origins</code> arrays reflect the new configuration. The dashboard import validates both values.

**Secret hygiene:** Client JSON includes a client secret. Store it only in an authorized password manager or temporary protected local file. Do not commit it, screenshot it, or send it over chat. Google displays a client ID which is not a password, but the client secret and refresh token must be protected.

### 9.4 Configure the consent screen and user access

- For **External → Testing**, explicitly add the intended sending Google account under **Audience → Test users**.
- Verify the scope under **Data Access**. Additional scopes are unnecessary for the current app.
- For an **Internal** app, the sending account must satisfy the Workspace organization policy.
- If Google's warning or restricted/sensitive-scope verification prevents a production connection, follow Google's Auth Platform verification requirements; do not bypass required verification or misrepresent the intended application.
- Consider a **dedicated company-controlled sending mailbox** rather than an individual's personal Gmail account, and document who controls it.

### 9.5 Import OAuth client JSON through the Inventory Manager dashboard — preferred

First verify the application itself works at **your own HTTPS domain**, configured by `PUBLIC_ORIGIN` (for example only, `https://inventory.example.com`).

1. Sign in with **Administrator access** using the **global administrator** account (<code>OWNER</code> by default) and the six-digit PIN.
2. Open **Administration**.
3. Locate the **Google OAuth configuration** section.
4. Choose the downloaded **Web application OAuth client JSON** (size must be under **24 KB**).
5. Enter/confirm the global administrator PIN.
6. Select **Import Google configuration**.
7. Confirm the UI reports configuration as ready.

On import, the API checks the client ID/secret format and both the authorized origin and exact callback. It stores an **encrypted client secret** in PostgreSQL using the persistent <code>GOOGLE_TOKEN_KEY</code>. You do **not** need to copy Google client secrets manually into <code>.env</code> for this preferred flow.

**If import says “redirect URI/origin not authorized”:** Save the new domain in Google Cloud and **download a new JSON file**. Editing the Google Cloud fields does not change an already-downloaded JSON file.

**If import says encryption key missing:** Re-run the normal installer and ensure the existing <code>GOOGLE_TOKEN_KEY</code> was preserved. Do not generate a replacement key for a server that already has encrypted tokens.

### 9.6 Alternative server-side OAuth client import

For an administrator who cannot use the dashboard import, copy the downloaded JSON to a **secure temporary location** on the Ubuntu LXC (never into the Git repository), then run from its app directory:

~~~bash
cd /opt/inventory-manager
sudo bash deploy/configure-google.sh /root/client_secret.json
~~~

The helper reads **the existing installation's `PUBLIC_ORIGIN` from `.env`**, requires that exact JavaScript origin **and** `PUBLIC_ORIGIN + /api/google/callback` in the Google Web client JSON, then writes `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` to `.env`. It rebuilds/restarts the API. It does **not** need credentials from the maintainer's Google project.

Only run this when you deliberately choose **server-side configuration** rather than the dashboard. A dashboard-imported OAuth config stored in PostgreSQL may take precedence over <code>.env</code> credentials when the API starts. **Delete the temporary JSON file securely after you verify setup**, and keep the authoritative password-manager copy. The optional helper does **not** authorize a Gmail sender on its own.

### 9.7 Google callback details

When the global admin clicks **Connect global Gmail**, the API issues a Google authorization URL using a 10-minute server-side state and PKCE challenge, then Google returns to:

**`PUBLIC_ORIGIN + /api/google/callback`** (example: `https://inventory.example.com/api/google/callback`).

The API exchanges the code for OAuth tokens, verifies a Google email identity, encrypts/stores the **refresh token** in PostgreSQL, and redirects back to the site's Administration flow. The browser should return to **your** domain. If it lands on an unexpected hostname, inspect your server `PUBLIC_ORIGIN`, any old client JSON and your actual deployed build.

**Common callback failures (Google's redirect URI must match exactly):**
- <code>redirect_uri_mismatch</code>: wrong Google Client / hostname / missing exact callback or old deployed code.
- Google sign-in canceled: retry and deliberately consent.
- “Not configured”: OAuth client absent or encryption key invalid.
- Expired state: callback took longer than the allowed time or authorization was opened twice.
- No refresh token: re-authorize and grant offline access through the normal Connect flow.
- Authorization stops after ~7 days: investigate External/Testing refresh-token expiration.
- Google API errors: check Gmail API enabled, scopes, account permissions and server logs.

## 10. Connect the global Gmail sender and configure alerts

### 10.1 One global sender for all stores

Only a **global administrator** can choose/connect/disconnect the sending Gmail account. There is **one shared sender across stores**; each location controls **only its own recipients and alert settings**.

From **Administration → Global email sender**:
1. Confirm OAuth Client configuration is ready.
2. Click **Connect global Gmail**.
3. Choose your intended company sending Gmail/Workspace account on Google's consent screen.
4. Review and grant the requested send-only permission.
5. Wait for the redirect to the Inventory Manager site and verify the sender's email appears as connected.
6. If needed, disconnect/reconnect from the same global admin section.

If an older deployment had separate per-store Google senders, that legacy configuration is not used by the current global sender worker. Connect a global sender once after upgrading.

### 10.2 Set each store's notification recipients

Switch to the target store and open **Administration → Low-stock email alerts**:

- Add **up to 10 valid email addresses** for that store, separated by commas (semicolons/newlines are also parsed).
- Save recipients **before** enabling notifications.
- Enable the alerts toggle and save.
- Click **Send test email**. Confirm actual inbox delivery (including spam/junk) and check any Gmail account security messages.
- Switch to each other store, configure *its own* recipients, save and test again. The sending account remains shared globally.

### 10.3 Alert behavior

- Products are low stock when <code>quantity <= min_stock</code>.
- An alert is queued for a low-stock cycle. Restocking **above** the minimum rearms the next cycle.
- The background worker checks approximately every **30 seconds** and retries failed mail using backoff (the current code calculates future attempts from failed-attempt count).
- Not receiving an email immediately after saving recipients is not necessarily a failure; test explicitly, and verify a real low-stock transition. Repeated notifications are intentionally avoided within one low-stock cycle.
- The **Reorder List** suggests enough units to bring an item to **one above** its configured minimum.

### 10.4 Diagnosing mail issues

On the Ubuntu LXC:

~~~bash
cd /opt/inventory-manager
docker compose --env-file .env -f deploy/compose.yml logs --tail 200 api
~~~

Look for token-exchange failures, Gmail API errors, unverified user, missing sender, credential decryption errors, and queued-email failures. Log contents might include staff, recipient or product information; redact sensitive material before sharing.

## 11. Optional SMTP fallback

The current Compose file supports two optional variables:

- <code>SMTP_USER</code>: the Gmail account used for SMTP.
- <code>SMTP_APP_PASSWORD</code>: an applicable Google-generated app password, **not** an ordinary Gmail password.

The application sends SMTP via <code>smtp.gmail.com</code> on port **587** with TLS when Gmail API is not selected/connected. Availability of app passwords depends on Google account policies and security configuration; do not assume every account permits them.

To configure intentionally, edit only the private host file:

~~~bash
sudo nano /opt/inventory-manager/.env
~~~

Add the two variables with real values (without publishing them), then recreate/rebuild the application:

~~~bash
cd /opt/inventory-manager
sudo docker compose --env-file .env -f deploy/compose.yml up -d --build
~~~

The global Gmail OAuth sender takes precedence when connected and functional. An SMTP fallback is **not** automatically guaranteed to take over every Gmail failure: check the current worker behavior and test the intended failover. Prefer a monitored and documented single mail strategy.

## 12. Daily use and workflow validation

### 12.1 Employee workflow

- Employee login: use the employee ID after your chosen network access controls. Select an authorized store if applicable.
- Inventory: search SKU, name, barcode or category; view stock and low-stock state.
- Scan: USB/Bluetooth scanners acting as keyboards, or an HTTPS mobile browser camera with permission.
- Checkout: choose a product and negative stock movement, provide the required reason, and **save**. The API refuses stock going below zero.
- Check-in/receiving: add positive stock movement and confirm new balance.
- History: movement rows show date, employee, balance and note.
- After a checkout: **History → Edit note** allows an authorized employee of that store to append context, for example **“Damaged during repair.”** The **note is replaced** with the newly saved text; previous versions are retained under **View changes**. Original checkout quantity/time/employee are unchanged.
- Product history: **Inventory → Edit product → View history** provides the same note editing controls for past checkout records.
- Inventory CSV and reorder CSV: exports are scoped to the currently selected store; spreadsheet formula-like fields are escaped.

### 12.2 Access and history limits

- Standard history and product-specific history endpoints currently return the **latest 200 movements**, not unlimited searchable history. Older rows remain in the database but may not be visible through the current browser lists once the 200-row limit is exceeded.
- Audited administrative activity also displays the most recent events; historic actions before audit logging was installed cannot be reconstructed automatically.
- Note edits are versioned: stale concurrent edits are rejected rather than silently overwritten.
- Staff can edit checkout notes for any checkout **in their currently authorized store**; the system records the note editor and revision history.

### 12.3 Device/browser validation

Test on representative company hardware:
- Windows desktop with keyboard and external scanner.
- Android Chrome if applicable.
- iPhone Safari camera with rear-camera permissions.
- Mobile slow connections and a real barcode under varying lighting.
- Device sleep/wake and session timeout.
- Normal login, logout, store switching, restricted-page access.
- Two employees editing notes concurrently, with proper revision-conflict message.
- Browser refresh after a deployment. If an old UI appears, try a cache-bypassing refresh.

Use [docs/ui-qa-checklist.md](ui-qa-checklist.md) for the more focused visual/UX test list.

## 13. Environment variables and sensitive data

The file <code>/opt/inventory-manager/.env</code> should be owned by an authorized administrator, restricted to mode **0600**, and stored in encrypted backups/password-manager procedures. A fresh installer creates it after receiving **the installer's own `PUBLIC_ORIGIN`**, then upgrades preserve existing values. Legacy installations that predate `PUBLIC_ORIGIN` receive their existing domain as a compatibility value instead of being silently migrated.

| Variable | Where used | Required? | Handling |
| --- | --- | --- | --- |
| <code>POSTGRES_PASSWORD</code> | Database and API connection string via Compose | **Yes** | Installer generates 32 random bytes as hex; retain for existing volume |
| <code>SETUP_TOKEN</code> | One-time admin PIN enrollment | **Yes** | Installer generates; printed by installer; don't publish |
| <code>PUBLIC_ORIGIN</code> | **Your own canonical public HTTPS URL; also used for OAuth redirect validation** | **Yes on new installs** | Enter when first prompted, e.g. `https://inventory.example.com` **placeholder only**; no slash, port, path, query, or fragment; must match DNS/TLS/Google client |
| <code>GOOGLE_TOKEN_KEY</code> | AES-GCM encryption/decryption of Google OAuth secrets and refresh tokens | **Yes for dashboard OAuth/Gmail** | 64 hex characters representing 32 bytes; **never rotate casually** |
| <code>GOOGLE_CLIENT_ID</code> | Optional server-side OAuth client config | Optional | Dashboard-imported client may override at startup |
| <code>GOOGLE_CLIENT_SECRET</code> | Optional server-side OAuth client config | Optional | Secret; never commit |
| <code>SMTP_USER</code> | Optional SMTP fallback | Optional | Mailbox identifier |
| <code>SMTP_APP_PASSWORD</code> | Optional SMTP fallback | Optional | Secret; never commit |

Other runtime settings are in [deploy/compose.yml](../deploy/compose.yml): <code>ConnectionStrings__Inventory</code> points to <code>Host=db;Port=5432;Database=inventory;Username=inventory</code>, and <code>ASPNETCORE_URLS</code> is <code>http://+:8080</code> inside the API container.

**Not in this file:** Your DNS-provider or DDNS API token (kept in the corresponding DNS updater, if used), reverse-proxy administrative credentials and TLS private key, Google Cloud Console login, and employee records/credentials (in PostgreSQL). Cloudflare and OPNsense are optional.

**To change `PUBLIC_ORIGIN`:** Back up `.env`, edit only the `PUBLIC_ORIGIN=https://your-real-domain` line, confirm corresponding DNS/HTTPS and Google client origin/redirect URI, then redeploy/restart the API via `sudo bash deploy/install.sh`. Never paste `.env` into support tickets.

**Do not change** <code>POSTGRES_PASSWORD</code> in <code>.env</code> alone against an already initialized PostgreSQL volume: the Postgres image does not automatically reset an existing database role password just because the environment variable changes. Follow a controlled rotation procedure.

**Do not change** <code>GOOGLE_TOKEN_KEY</code> without first implementing a supervised re-encryption process or accepting the loss of ability to decrypt already-stored credentials and Gmail tokens. Database and key must be backed up together.

## 14. Backups and tested disaster recovery

### 14.1 What is already automated

The installer installs:
- [deploy/inventory-backup.service](../deploy/inventory-backup.service)
- [deploy/inventory-backup.timer](../deploy/inventory-backup.timer)
- [deploy/backup.sh](../deploy/backup.sh)

The systemd timer runs **daily at 03:15** in the Ubuntu host's configured local timezone, including catch-up when applicable. The script runs <code>pg_dump</code> inside the running <code>db</code> container and writes a **gzip-compressed plain SQL dump** to:

<code>/var/backups/inventory-manager/inventory-YYYYMMDDTHHMMSSZ.sql.gz</code>

The script keeps the latest ~14 days based on file age (older files are deleted when the script runs). Files and backup directory are permissions-restricted. This **does not** make an offsite backup by itself.

### 14.2 Check timer, manual backup and files

~~~bash
sudo systemctl status inventory-backup.timer
sudo systemctl list-timers --all | grep inventory
sudo journalctl -u inventory-backup.service -n 100 --no-pager

# Run a one-off backup now:
sudo systemctl start inventory-backup.service

# Inspect the new files:
sudo ls -lh /var/backups/inventory-manager/
~~~

Or run the script directly:

~~~bash
sudo bash /opt/inventory-manager/deploy/backup.sh
~~~

Check the actual compressed archive (substitute a real filename):

~~~bash
sudo gzip -t /var/backups/inventory-manager/inventory-YYYYMMDDTHHMMSSZ.sql.gz
~~~

A healthy gzip file and a successful service run are **not** proof that the database can be restored. Perform a test restore.

### 14.3 Back up what the built-in timer does not cover

**Back up both:**

1. Compressed PostgreSQL SQL dumps.
2. The private <code>/opt/inventory-manager/.env</code> file (especially <code>GOOGLE_TOKEN_KEY</code>), with strict access controls.

Also preserve enough configuration information to recreate the NPM proxy host and certificate, Cloudflare zone/DDNS record, Google Cloud OAuth project/client, OPNsense NAT rules, LXC network setup and Git commit. Back up NPM's own data/certificate storage using NPM's deployment-specific backup process.

Use an encrypted **off-host/offsite** backup destination. The built-in 14-day local retention may be too short for your business; extend retention/offsite copy under an agreed policy. Record who checks backup success and restore tests.

### 14.4 Restore test in a separate disposable database — safer

**Run only after you have a known-good SQL dump and enough disk space.** The commands below are a manual **restore validation** using a new database named <code>inventory_restore_test</code> *inside the existing PostgreSQL instance*; they do not replace the production <code>inventory</code> database. It is still safer to do this in an isolated test LXC rather than touching production.

From the Ubuntu LXC, select an existing backup file and run:

~~~bash
cd /opt/inventory-manager

# Create a disposable test database:
sudo docker compose --env-file .env -f deploy/compose.yml exec -T db \
  createdb -U inventory inventory_restore_test

# Replace the archive path below with the ACTUAL backup filename:
sudo gzip -dc /var/backups/inventory-manager/inventory-YYYYMMDDTHHMMSSZ.sql.gz \
  | sudo docker compose --env-file .env -f deploy/compose.yml exec -T db \
      psql -X -v ON_ERROR_STOP=1 -U inventory -d inventory_restore_test

# Verify tables are present:
sudo docker compose --env-file .env -f deploy/compose.yml exec -T db \
  psql -U inventory -d inventory_restore_test -c '\dt'

# When fully verified, remove ONLY the disposable test database:
sudo docker compose --env-file .env -f deploy/compose.yml exec -T db \
  dropdb -U inventory inventory_restore_test
~~~

**If the pipe or restore fails, do not mark the test passed.** Re-run with appropriate <code>pipefail</code> handling, inspect the failure, and verify that the restored schema and sample rows match expectations. Never use the real database name as the test target.

### 14.5 Full disaster recovery to a replacement server

For a complete replacement host:
1. Identify the **last known good** database backup, its date and the matching private <code>.env</code> backup.
2. Provision an Ubuntu 24.04 host on the correct LAN and install the app **without exposing it publicly**.
3. Stop the API before changing production database contents. Never restore over an actively writing application.
4. Restore the plain SQL backup to a **new clean database** (or follow a reviewed drop/create procedure with a verified backup); do not blindly import SQL into populated tables.
5. Restore the matching private encryption key <code>GOOGLE_TOKEN_KEY</code> and required database password configuration.
6. Start the API, check the store/product/history data and Gmail connection, then restore proxy/NAT traffic.
7. Recheck scheduled backups, app authentication and one real alert delivery before declaring recovery complete.

**Do not blindly copy a Docker volume between unrelated PostgreSQL major versions** or rely on a Proxmox snapshot alone as the only backup. Use a tested SQL-based restore process and document downtime/expected data-loss window. If the encryption key is missing, old Gmail tokens/client secrets may not be recoverable; reconnecting Google may then be necessary.

## 15. Updates, rollbacks and migrations

### 15.1 Standard deployment/update procedure

1. Identify an approved maintenance window; tell store employees when writes will be interrupted.
2. Check the latest GitHub commit/CI; do not assume it passed.
3. Run a fresh manual database backup and protect a copy of <code>.env</code>.
4. Record the current deployed Git commit:

~~~bash
cd /opt/inventory-manager
git rev-parse HEAD
git status --short
~~~

5. If the checkout has **local modifications**, investigate them before updating; the installer refuses to overwrite them.
6. Run the supported installer:

~~~bash
cd /opt/inventory-manager
sudo bash deploy/install.sh
~~~

7. Verify containers, the LAN <code>/health</code> route, the public HTTPS homepage, employee login, store scopes, scanner, checkout, history, email and backup timer.
8. Check API logs for new errors.

### 15.2 What updates preserve

The installer does not intentionally delete the PostgreSQL named volume, re-enroll the PIN or rotate existing secrets. Database schema initialization/migration in <code>Program.cs</code> runs on API startup, so schema changes may execute during deployment. **Back up before updates**, especially ones introducing migrations.

### 15.3 Rollback considerations

**A Git rollback does not undo database schema changes or reverse data corruption.** Before using an older application image, confirm that it is compatible with the schema deployed by the newer version. Preserve the known-good image/commit when possible.

For a controlled code rollback, have an administrator check out a reviewed previous commit in the local working tree and rebuild Compose without deleting volumes. The checkout may then be detached from <code>main</code>; return to the main branch before later running the installer. Record every step. If a schema rollback is necessary, restore a verified pre-update SQL backup into a clean database under a formal maintenance procedure.

**Never run:** <code>docker compose down -v</code>, <code>docker volume prune</code>, or destructive SQL commands as an ordinary update/troubleshooting step.

### 15.4 Change **your** domain without editing source code

The application **no longer requires changing an API constant** to switch domains. The public URL comes from `PUBLIC_ORIGIN` in the installation's private `.env`. Plan the change as a coordinated migration:

1. Record the original `PUBLIC_ORIGIN`, current Google OAuth Client ID and current Git commit; protect a database and `.env` backup.
2. Create or update your new hostname's public DNS A/AAAA record or tunnel mapping. If dynamic, update the relevant DDNS zone and FQDN.
3. Add a matching HTTPS reverse proxy host/certificate and test reachability.
4. In the **existing Google OAuth Web client you control**, add the **new** JavaScript origin and exact redirect URI. Preserve the existing client ID/secret if you want the existing Gmail refresh token to remain potentially reusable.
5. Edit `/opt/inventory-manager/.env` with one new line like `PUBLIC_ORIGIN=https://stock.yourcompany.com` (use your real domain; never print the other secrets).
6. From the Ubuntu app directory, run `sudo bash deploy/install.sh` to recreate the API with its new origin, and confirm `docker compose ... ps`.
7. Test `https://YOUR_NEW_HOSTNAME/health` in a browser and reconnect Google if the existing sender fails; any newly downloaded OAuth Web client JSON must list the same newly configured origin/redirect.
8. Only after verification remove unused older DNS/proxy and Google authorized URLs. Keep a rollback plan and database/key backups.

**Changing `PUBLIC_ORIGIN` alone does not configure DNS, TLS or Google Cloud**. The API will reject a syntactically invalid origin at startup rather than infer one from an untrusted request Host header.

### 15.5 Existing installs from before PUBLIC_ORIGIN

If you are upgrading an earlier installation that had its origin embedded in code, the installer writes a **legacy compatibility value** into `.env` when it is missing. This prevents an existing Google integration from silently switching hosts. For a new business/customer, use a fresh installation and enter **your own hostname at setup**. An older deployment that is being transferred to a different company must explicitly update `.env`, DNS, proxy and Google OAuth as described above.

## 16. Troubleshooting by symptom

| Symptom | Likely causes | Check / action |
| --- | --- | --- |
| Installer refuses to run | Wrong Ubuntu version, not root, dirty checkout, nonempty path **or missing/invalid PUBLIC_ORIGIN on a new installation** | Check <code>cat /etc/os-release</code>, <code>id</code>, <code>git status --short</code>; for unattended installs set <code>PUBLIC_ORIGIN=https://YOUR_REAL_DOMAIN</code> |
| Docker/Compose unavailable | Docker-in-LXC limitations, apt/repo error, nesting issue | <code>docker info</code>; <code>docker compose version</code>; <code>journalctl -u docker</code>; review LXC features |
| API shows 502 or does not load | Container down, database unhealthy, wrong upstream IP/port | Compose <code>ps</code>, API/db logs, LAN <code>curl .../health</code>, reverse-proxy upstream |
| Local LAN works but public domain fails | DNS, optional DDNS, WAN NAT, double NAT, CGNAT, HTTPS proxy/firewall | Verify your chosen DNS provider's A/AAAA record or tunnel, firewall/NAT, proxy, external cellular network; Cloudflare/OPNsense logs only if used |
| Cloudflare 522 timeout | Origin unreachable/blocked/incorrect port forwarding | NPM reachability, firewall, WAN address, upstream NAT |
| Cloudflare 525 / 526 | TLS handshake failure or bad origin certificate | NPM SSL cert covers domain, Cloudflare Full (strict), renewed/not expired cert |
| Wrong app/site appears | Reverse proxy's hostname routed to wrong service | Verify the configured HTTPS proxy host and exact site FQDN |
| Site redirects repeatedly | Proxy HTTPS redirect conflict or incompatible CDN SSL mode | Verify matching certificate and HTTPS redirects; if Cloudflare proxied use Full (strict) |
| Cloudflare DNS shows another IP | Wrong DDNS zone/hostname/public-IP detection | OPNsense DDNS account and Cloudflare dashboard (not public dig for proxied records) |
| No initial PIN setup screen | Database already enrolled | <code>/api/auth/status</code>; existing PIN remains; installer is not PIN reset |
| Google JSON import rejected | Wrong client's JSON, invalid Web application type, mismatch with this installation's PUBLIC_ORIGIN/callback, or missing encryption key | Confirm <code>PUBLIC_ORIGIN</code> is **your real hostname**, configure **your own** Google OAuth Web client with matching origin/callback, re-download JSON; verify <code>GOOGLE_TOKEN_KEY</code> |
| Google <code>redirect_uri_mismatch</code> | `PUBLIC_ORIGIN` differs from OAuth client authorized redirect, extra `www`, old JSON or wrong host | Compare your server's `PUBLIC_ORIGIN` with Google `PUBLIC_ORIGIN + /api/google/callback`, verify installed client and restart |
| Google connection returns canceled/expired | Consent declined or state timed out | Retry promptly in same browser; verify server clock |
| Gmail stops after ~7 days | OAuth External app still in Testing | Review Audience/publishing, test users and Google requirements |
| Global Gmail connected, no alert | Recipients not saved/enabled, no low-stock transition, worker delay, Gmail token issue | Use **Send test email**, app API logs, store alert settings |
| Employee does not see Administration | Standard role, wrong store, stale login | Check employee permissions and selected store; **do not** broaden access casually |
| Employee does not see Edit note | Not a checkout, stale frontend, wrong store, old deployment | Only checkout rows get Edit note; verify current deployed build; hard refresh |
| Camera scan unavailable | No HTTPS, camera denied, unsupported browser/device | HTTPS, browser Settings permissions, rear camera, manual scanner fallback |
| Inventory data disappears | Wrong selected store, incorrect DB volume/project, accidental restore | Stop and investigate; check active store and volume, restore only from verified dump |
| Backups absent | Timer disabled, db unhealthy, full filesystem, permission failure | <code>systemctl list-timers</code>, journal, manual backup, <code>df -h</code> |
| API cannot decrypt Gmail token | <code>GOOGLE_TOKEN_KEY</code> missing/changed, mismatched DB/key restore | Restore matching key; avoid random rekeying |
| After deployment old UI remains | Browser/CDN cache or deployment did not pull latest commit | <code>git rev-parse HEAD</code>, Compose logs, hard refresh, CDN caching rules if used |

### 16.1 Handy commands

~~~bash
cd /opt/inventory-manager
git rev-parse --short HEAD
git status --short
sudo docker compose --env-file .env -f deploy/compose.yml ps
sudo docker compose --env-file .env -f deploy/compose.yml logs --tail 200 api
sudo docker compose --env-file .env -f deploy/compose.yml logs --tail 200 db
curl -fsS http://127.0.0.1:18424/health
curl -fsS http://127.0.0.1:18424/api/auth/status
sudo systemctl status inventory-backup.timer
sudo journalctl -u inventory-backup.service -n 100 --no-pager
sudo df -h
~~~

Do **not** include any environment secrets, bearer tokens, PINs, OAuth codes or customer data when sharing troubleshooting output.

## 17. Security and business handover

### 17.1 Minimum security work before public business use

- [ ] Replace or augment employee-ID-only authentication with real individual authentication, strong credentials/MFA and role enforcement. Audit **store-admin login by employee ID** specifically.
- [ ] Limit exposure using a VPN / trusted network / identity-aware gateway (e.g., Cloudflare Access where appropriate) until authentication is hardened.
- [ ] Restrict TCP/18424 to NPM/trusted management LAN; protect NPM admin port 81 and Proxmox/OPNsense management separately.
- [ ] Monitor TLS/certificate renewals; if using Cloudflare proxied mode, verify Full (strict).
- [ ] Protect the sending mailbox with strong authentication and keep Google Cloud project/OAuth credentials under the installing business's accountable administrators.
- [ ] Define access-offboarding, database retention, incident response, audit-log retention, and approved administrators.
- [ ] Store and test **encrypted offsite database + .env** backups.
- [ ] Review log contents, application updates/dependencies, and API authorization boundaries.
- [ ] Remove unused demo/test accounts and avoid using stock data from a real store in development tests.
- [ ] Obtain permission to use ZAGG branding if distributing outside the authorized business context.

### 17.2 Document ownership/handover without sending secrets

For a franchise handover, agree in writing:
- Whether the business receives a perpetual **license** or a full source-code/IP **assignment**, and exactly which code/brand assets are included.
- Who owns the chosen domain, registrar/DNS provider, router/firewall, application host, HTTPS proxy and the installer's own Google Cloud project.
- Who pays for hosting, domain renewal, email account, Internet access, offsite backup, SSL services and ongoing upgrades.
- Who is responsible for bug fixes, feature requests, outages, backups/restores, lost credentials and employee provisioning.
- Who may grant GitHub repository access and who can approve production deployments.
- Whether a setup/handover period and any subsequent support retainer are included.

The technical documentation does not by itself grant or transfer intellectual property ownership.

### 17.3 Recommended operating cadence

| Frequency | Responsibility |
| --- | --- |
| Daily | Confirm site reachable, transaction flow normal, backup timer success, email alerts functioning |
| Weekly | Review failed mail deliveries, disk/backup capacity, staff access changes, API errors |
| Monthly | Verify encrypted offsite backup copies and certificate renewals, review security updates, test restore in a safe environment |
| Before each update | Fresh backup, CI review, maintenance window, deploy and smoke test |
| After staff departure | Revoke access, rotate relevant shared secrets where justified, review audit history |
| After major network/Google changes | Retest external HTTPS, callback, store-specific alert recipients and mobile camera |

## 18. Final acceptance checklist

A person other than the original developer should be able to walk through this checklist using authorized credentials **without needing to ask where a secret lives**.

### Infrastructure
- [ ] Ubuntu 24.04 LXC has stable LAN IP and reliable power/network.
- [ ] Docker/Compose services are healthy and restart after a reboot.
- [ ] <code>/health</code> and <code>/api/auth/status</code> respond on LAN.
- [ ] Port 18424 cannot be reached from untrusted public networks.
- [ ] `PUBLIC_ORIGIN` contains **my actual domain**, not the example `inventory.example.com`.
- [ ] My DNS provider resolves **my chosen hostname** to the correct public IP or tunnel (static IP needs no DDNS).
- [ ] If using Cloudflare/OPNsense DDNS, the token is zone-scoped and the DNS zone and FQDN are correct.
- [ ] My reverse proxy routes **my hostname** to the correct app backend :18424 (or the reviewed alternative network).
- [ ] WAN/NAT, if used, forwards only TCP/443 and the needed HTTP/ACME traffic to the HTTPS proxy.
- [ ] Public HTTPS uses a valid matching certificate; Cloudflare Full (strict) applies **only if I use the Cloudflare proxy**.
- [ ] Another business's domain is not referenced in my active DNS, proxy, `.env`, or OAuth configuration.

### Application
- [ ] Global admin enrolls PIN once and logs in as OWNER.
- [ ] At least two stores and two employees demonstrate proper access isolation.
- [ ] Add/search/edit a product and scan barcode on desktop and mobile.
- [ ] Check-in/out changes quantity exactly once; checkout requires reason.
- [ ] Standard employee edits a coworker's checkout note; revision trail records editor and original transaction unchanged.
- [ ] Cross-store access is denied.
- [ ] Inventory/reorder CSV files download and contain current-store data.
- [ ] New role permissions, deletion confirmations, admin activity log and browser refresh operate correctly.

### Google and alerts
- [ ] Gmail API enabled in correct Google project; audience and scope appropriate.
- [ ] Google OAuth Web client is in **my own** Google Cloud project and its Client ID/Secret are mine.
- [ ] Authorized JavaScript origin equals **my actual `PUBLIC_ORIGIN`** (not the sample hostname).
- [ ] Authorized redirect URI equals **my actual `PUBLIC_ORIGIN + /api/google/callback`**.
- [ ] The site's authorized registrable domain is configured under Google's required Branding/Authorized domains settings.
- [ ] Latest OAuth Web client JSON imported by global admin; no secret committed.
- [ ] Dedicated global Gmail sender connected and accurately displayed.
- [ ] Each store has its own recipient list, alert toggle and successful **test email**.
- [ ] One actual low-stock cycle is tested; Gmail token longevity/verification understood.

### Operations and security
- [ ] GitHub Actions build and API smoke run **verified**, not merely configured.
- [ ] Database backup timer completed successfully.
- [ ] Compressed backup opened and a disposable **restore test passed**.
- [ ] Matching <code>.env</code>/encryption key is stored in an access-controlled offsite backup.
- [ ] Named owners, support responsibility, and emergency access documented.
- [ ] Employee authentication security risk is addressed before unrestricted public access.

---

## 19. Authoritative references

Application-specific instructions above were checked against the repository's **actual** <code>deploy/install.sh</code>, <code>deploy/compose.yml</code>, <code>deploy/backup.sh</code>, <code>deploy/configure-google.sh</code>, and <code>server/Inventory.Api/Program.cs</code>. Vendor navigation and requirements can change; verify these official sources before infrastructure changes:

- [OPNsense Dynamic DNS (os-ddclient / Cloudflare)](https://docs.opnsense.org/manual/dynamic_dns.html)
- [Cloudflare API token creation and least-privilege permissions](https://developers.cloudflare.com/fundamentals/api/get-started/create-token/)
- [Cloudflare proxied versus DNS-only records](https://developers.cloudflare.com/dns/proxy-status/)
- [Cloudflare Full (strict) SSL requirements](https://developers.cloudflare.com/ssl/origin-configuration/ssl-modes/full-strict/)
- [Nginx Proxy Manager guide](https://nginxproxymanager.com/guide/)
- [Nginx Proxy Manager installation](https://nginxproxymanager.com/setup/)
- [Google OAuth for server-side web applications](https://developers.google.com/identity/protocols/oauth2/web-server)
- [Google Gmail API server-side authorization](https://developers.google.com/workspace/gmail/api/auth/web-server)
- [Google Auth Platform — manage audience / Testing mode](https://support.google.com/cloud/answer/15549945)
- [Google OAuth application verification](https://support.google.com/cloud/answer/13463073/)
- [Microsoft ASP.NET Core behind reverse proxies](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)

**Support rule:** If a live result differs from this runbook, **do not guess or delete persistent data**. Capture non-secret logs, determine the current deployed Git revision, and diagnose one network/software layer at a time.
