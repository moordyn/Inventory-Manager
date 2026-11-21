# ZAGG Inventory Manager — Complete installation, deployment and operations guide

**Canonical site:** https://zagginventorymanager.com  
**Source repository:** https://github.com/moordyn/Inventory-Manager  
**Target platform:** Ubuntu 24.04 in a Proxmox LXC, Docker Compose, PostgreSQL 17, ASP.NET Core 10, Cloudflare DNS, OPNsense, Nginx Proxy Manager (NPM).  
**Guide updated:** November 2026; based on the current repository deployment scripts and backend, plus the linked vendor documentation.  
**Audience:** The installer, store owner, network administrator, Google Cloud administrator, and anyone taking over long-term maintenance.

> **Important status:** This is a detailed deployment and recovery runbook, **not** proof of a production security review. The app's ordinary employee login currently requires only an employee ID. Certain store administrator sessions can also be reached through this login route. Protect the site with a stronger authentication/access-control layer or implement proper per-user authentication **before exposing sensitive business inventory data to the public internet**. Also verify the actual current GitHub Actions results, on-site scanner behavior, database backup/restore, and permissions. Do not mistake a successful Docker startup for successful end-to-end testing.
>
> **Scope:** This guide documents the current application. It does **not** claim that the Inventory Manager installer configures Proxmox, OPNsense, Cloudflare, public certificates, Nginx Proxy Manager, or Google Cloud for you. Those are separate manual steps.

## Contents

1. [What this deployment consists of](#1-what-this-deployment-consists-of)
2. [Record installation information](#2-record-installation-information)
3. [Prerequisites and Proxmox preparation](#3-prerequisites-and-proxmox-preparation)
4. [Install Inventory Manager from scratch](#4-install-inventory-manager-from-scratch)
5. [Initial admin PIN and store onboarding](#5-initial-admin-pin-and-store-onboarding)
6. [Cloudflare DNS and OPNsense dynamic DNS](#6-cloudflare-dns-and-opnsense-dynamic-dns)
7. [Router rules and Nginx Proxy Manager](#7-router-rules-and-nginx-proxy-manager)
8. [HTTPS, certificates and Cloudflare proxy](#8-https-certificates-and-cloudflare-proxy)
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

### 1.1 Traffic flow

~~~text
Employee / administrator browser (desktop, iPhone, scanner)
        |
        | HTTPS to zagginventorymanager.com
        v
Cloudflare public DNS / optional orange-cloud HTTP proxy
        |
        | Public WAN IPv4 address (kept current by OPNsense DDNS)
        v
OPNsense firewall — WAN TCP/443 (and optionally TCP/80)
        |
        | NAT to the EXISTING Nginx Proxy Manager host
        v
Nginx Proxy Manager — HTTPS / TLS termination
        |
        | LAN HTTP to INVENTORY_LXC_IP:18424
        v
Ubuntu 24.04 Inventory Manager LXC
        |
        | Docker host port 18424 -> API container port 8080
        v
ASP.NET Core 10 Inventory.Api + responsive web frontend
        |
        | Docker internal network only, service name "db":5432
        v
PostgreSQL 17, named Docker volume "inventory_data"
        |
        +--- host systemd backup timer -> compressed pg_dump files
        |
        +--- outbound HTTPS -> Google OAuth / Gmail API
~~~

The database is not independently exposed by the provided Compose file. The API **is** bound to host port 18424 (all host interfaces by the current Compose port mapping), so **restrict this port using LAN firewall/host access rules**; do not forward it to the WAN. The Nginx Proxy Manager management page (typically port 81) is likewise LAN/admin-only.

### 1.2 What is and is not included

- **Included:** Inventory CRUD, barcode/SKU lookup and scanning, checkout/check-in, per-store stock, notes and note revisions, store/employee administration, low-stock reporting, CSV exports, a shared Gmail sender, store-specific notification recipients, API, Compose, and automated database backup timer.
- **Not included:** Cloudflare/OPNsense/NPM automatic configuration, a hosted database, a verified restore automation, enterprise SSO, offline editing, an invoicing system, or guaranteed production security hardening.
- **Web application:** Served by the same ASP.NET Core container as the API; there is no separate Node or frontend build required on the host.

### 1.3 Concrete identifiers

| Component | Current value |
| --- | --- |
| Public hostname | **zagginventorymanager.com** (root/apex, no <code>www</code>) |
| HTTPS website | **https://zagginventorymanager.com** |
| Google OAuth origin | **https://zagginventorymanager.com** |
| Google OAuth callback | **https://zagginventorymanager.com/api/google/callback** |
| GitHub branch for installer | <code>main</code> |
| Ubuntu deployment directory | <code>/opt/inventory-manager</code> |
| Ubuntu private environment file | <code>/opt/inventory-manager/.env</code> |
| Compose file | <code>deploy/compose.yml</code> |
| LAN app access | <code>http://INVENTORY_LXC_IP:18424</code> |
| API listening port in container | <code>8080</code> |
| PostgreSQL | <code>db:5432</code>, database/user <code>inventory</code> |
| Backup directory | <code>/var/backups/inventory-manager</code> |
| Backup schedule | Daily, **03:15 on the Ubuntu host's local timezone** |
| Backup retention | Files older than 14 days removed by backup script |
| Initial store | <code>DEFAULT</code> / “Original Store” |
| Initial global admin | <code>OWNER</code> / “Original Administrator” |

The old <code>inv.mrmschf.xyz</code> URL is **retired**. Do not use it for new DNS, Google OAuth, NPM, or marketing materials.

## 2. Record installation information

Write these values into your **private password manager or restricted operations inventory**, not in GitHub issues, screenshots, chat messages or commits.

| Value | Record privately |
| --- | --- |
| Proxmox host and CT/VM ID | [your values] |
| Inventory Ubuntu LXC static/reserved LAN IPv4 | [your value] |
| Nginx Proxy Manager host LAN IPv4 | [your value] |
| OPNsense LAN gateway / WAN interface | [your values] |
| Current public IPv4 and ISP CGNAT status | [your value] |
| Cloudflare account with zone ownership | [account identifier only] |
| Cloudflare DDNS token location | [password-manager item name only] |
| NPM administrative account | [password-manager item name only] |
| Google Cloud project name/ID | [your value] |
| Google OAuth client secret storage location | [password-manager item name only] |
| Global sending Gmail address | [your value] |
| Private backup destination and person responsible | [your value] |
| Person authorized to approve updates/restores | [your value] |

**No actual IP addresses, DNS record IDs, tokens, PINs, or passwords have been assumed in this document.** Substitute your real values for <code>INVENTORY_LXC_IP</code> and <code>NPM_LAN_IP</code> when performing the network steps.

## 3. Prerequisites and Proxmox preparation

### 3.1 Required access

Before beginning, ensure you can administer:
1. A Proxmox host with the ability to create/manage an Ubuntu 24.04 container.
2. The Ubuntu LXC as root (or a sudo-capable account).
3. OPNsense WAN/LAN/NAT/firewall.
4. Cloudflare DNS for <code>zagginventorymanager.com</code>.
5. The Nginx Proxy Manager instance that receives HTTPS traffic.
6. A Google Cloud project and the Gmail/Workspace account that will send alerts.
7. GitHub access to the code repository and a tested offsite backup location.

### 3.2 Proxmox LXC

Create an **Ubuntu 24.04** container (not an older Ubuntu release). Suggested *starting* resources for a small franchise are **2 vCPU, 2–4 GB RAM and 20+ GB disk**, adjusted after watching actual utilization. These are sizing suggestions rather than hard minimums.

- Set a strong root password or provision a secure administrative SSH key.
- Put the LXC on a network that can reach PostgreSQL inside Docker, the NPM proxy, GitHub/Docker registries, DNS, and Google HTTPS endpoints.
- Assign a stable IP via static configuration or DHCP reservation. Record this as <code>INVENTORY_LXC_IP</code>.
- Set correct clock/timezone and keep NTP working; incorrect time can break TLS and OAuth.
- For Docker inside LXC, configure appropriate Proxmox container features such as **nesting**, and **keyctl if required by your exact Docker/LXC configuration**. A VM is a valid alternative if Docker-in-LXC restrictions become problematic.
- Avoid privileged containers merely to make Docker work without understanding the added risk. Review your Proxmox security and backup policy.
- Ensure the LXC firewall allows **LAN/NPM to TCP/18424** and outbound DNS/HTTPS, but no inbound WAN access to TCP/18424.
- Confirm root has enough storage for the Docker images, database volume and logs; backup files are on host storage and also require room.

Inside Ubuntu, verify basic access:

~~~bash
cat /etc/os-release
hostname -I
date
df -h
free -h
ip route
getent hosts github.com
~~~

The installer intentionally aborts if <code>/etc/os-release</code> is not Ubuntu 24.04.

### 3.3 Production warning about credentials

The current ordinary employee login is **employee-ID-only**. Employee IDs are identifiers, **not passwords**. A store-admin role can be associated with this login path. Do not rely on that as your sole security control on a publicly reachable business system. Prefer a VPN/identity-aware access gateway with MFA for the intended users, together with implementing real per-user credentials in the application. Cloudflare Access is one possible additional control; if enabled, test the OAuth callback and mobile-browser flows carefully.

## 4. Install Inventory Manager from scratch

### 4.1 Connect to the correct Ubuntu LXC

SSH into the **Inventory Manager** LXC or open its Proxmox console, then become root:

~~~bash
sudo -i
~~~

Confirm you are in the correct machine using <code>hostname</code> and <code>hostname -I</code>. Do **not** run the installer on OPNsense, the Proxmox host, the NPM LXC, or an unrelated application host.

### 4.2 Inspect the installer before running it

From the Ubuntu LXC:

~~~bash
curl -fsSL https://raw.githubusercontent.com/moordyn/Inventory-Manager/main/deploy/install.sh -o /tmp/inventory-install.sh
less /tmp/inventory-install.sh
~~~

The script is in the repository at [deploy/install.sh](../deploy/install.sh). Its current behavior is to:

1. Require root and Ubuntu 24.04.
2. Install <code>ca-certificates</code>, <code>curl</code>, <code>git</code>, <code>openssl</code> and <code>python3</code>.
3. Install Docker Engine/Compose plugin from Docker's Ubuntu repository **if Docker/Compose is unavailable**.
4. Enable and start Docker.
5. Clone the main repository to <code>/opt/inventory-manager</code>, or <code>git pull --ff-only</code> an existing clean checkout. It **refuses a nonempty non-repository directory or uncommitted tracked changes**.
6. Create a private <code>.env</code> if missing, with randomly generated <code>POSTGRES_PASSWORD</code> and <code>SETUP_TOKEN</code>.
7. Preserve the existing <code>.env</code> and its secrets on upgrades; add a <code>GOOGLE_TOKEN_KEY</code> if absent. It does not silently rotate a key.
8. Build and start Docker Compose services <code>db</code> and <code>api</code>, with the database in a persistent named volume.
9. Install and enable the systemd daily backup timer.
10. Print the LAN URL and **the installation token**. Keep the terminal transcript private.

### 4.3 Run the install

~~~bash
bash /tmp/inventory-install.sh
~~~

If you have already installed the application and have a local checkout, the supported update command is instead:

~~~bash
cd /opt/inventory-manager
sudo bash deploy/install.sh
~~~

Do **not** create a new <code>.env</code> or use <code>docker compose down -v</code> to “fix” an existing installation. The latter can delete the database volume.

### 4.4 Check containers and health

~~~bash
cd /opt/inventory-manager
docker compose --env-file .env -f deploy/compose.yml ps
docker compose --env-file .env -f deploy/compose.yml logs --tail 100 api
docker compose --env-file .env -f deploy/compose.yml logs --tail 100 db
curl -fsS http://127.0.0.1:18424/health
curl -fsS http://127.0.0.1:18424/api/auth/status
~~~

Expected broad results:
- Both services running; the <code>db</code> service becomes healthy.
- <code>/health</code> responds with a JSON status such as <code>{"status":"ok"}</code>; this **does not** test full DB availability.
- <code>/api/auth/status</code> returns <code>initialized:false</code> on a never-enrolled database, and <code>true</code> after initial setup.
- From a different LAN computer, <code>http://INVENTORY_LXC_IP:18424</code> loads the page **if allowed by your LAN firewall**.

Because the Compose file forwards <code>18424:8080</code>, the app is accessible on the Ubuntu host's listening interfaces; prevent exposure by firewall and proxy design, not by assuming this port is localhost-only.

### 4.5 Understand data persistence

- PostgreSQL data: Docker named volume for the <code>db</code> service, declared as <code>inventory_data</code>. Docker Compose may prefix the actual volume name with the project name. Get the real name with <code>docker volume ls</code> or <code>docker compose ... config</code>.
- App code/images: replaceable by a new build; **not** where persistent inventory is stored.
- Secrets: <code>/opt/inventory-manager/.env</code>; this file is ignored by Git.
- Google OAuth client secret imported through the dashboard: encrypted and saved in PostgreSQL; decryption relies on the persistent <code>GOOGLE_TOKEN_KEY</code> in <code>.env</code>.
- Connected global Gmail refresh token: encrypted in PostgreSQL, also dependent on <code>GOOGLE_TOKEN_KEY</code>.
- SQL backups: <code>/var/backups/inventory-manager</code>; separate from the database volume.

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

## 6. Cloudflare DNS and OPNsense dynamic DNS

### 6.1 Confirm your domain is active

In Cloudflare:
1. Open the **zagginventorymanager.com** zone.
2. Confirm the registrar is using the Cloudflare-assigned nameservers and the zone is Active.
3. Decide whether the website will use Cloudflare's **orange-cloud proxy** (recommended for an external HTTPS site with the origin secured) or DNS only for initial diagnostics.
4. The current application is configured for the **apex/root domain**, not <code>www.zagginventorymanager.com</code>.

### 6.2 Create the A record

**Cloudflare → DNS → Records → Add record**:

| Setting | Value |
| --- | --- |
| Type | <code>A</code> |
| Name | <code>@</code> (the zone apex) |
| IPv4 | Your **current public WAN IPv4**, not the Ubuntu/NPM LAN address |
| TTL | Auto |
| Proxy status | **DNS only** while verifying DDNS, then optionally **Proxied** |

The resulting FQDN must be **zagginventorymanager.com**. Avoid stale competing A/AAAA records for the same public hostname. Add AAAA only if you have an actual reachable, correctly configured IPv6 service and update it separately.

When Cloudflare **Proxied** is enabled, public <code>dig</code>/<code>nslookup</code> answers will generally show **Cloudflare edge IPs** rather than your home public IPv4. In that case, check the **record value in the Cloudflare dashboard and OPNsense status**, not public DNS resolution, to determine whether DDNS correctly tracked the origin IP.

### 6.3 Create a narrowly scoped Cloudflare API token

Open https://dash.cloudflare.com/profile/api-tokens and select **Create Token**.

- Use the **Edit zone DNS** template (or create a custom token).
- Token name: e.g., “OPNsense DDNS — Inventory”.
- Permission: <code>Zone → DNS → Edit</code>.
- Zone resources: <code>Include → Specific zone → zagginventorymanager.com</code>.
- Grant <code>Zone → Zone → Read</code> only if your chosen integration/version requires zone lookup; the official OPNsense Cloudflare DDNS instructions identify DNS Edit as the recommended permission.
- **Do not** grant unrelated <code>DNS Settings → Edit</code>, account-wide privileges, or Global API Key access.
- Create the token and store the complete secret in a password manager. Never place it in the application <code>.env</code> or GitHub; OPNsense owns this secret.

### 6.4 Configure OPNsense os-ddclient

OPNsense UI:
1. Go to **System → Firmware → Plugins**; install <code>os-ddclient</code> if not installed.
2. Open **Services → Dynamic DNS → Settings**.
3. Enable Dynamic DNS; use the backend supported by your OPNsense build and the Cloudflare provider (<code>ddclient</code> is commonly used).
4. In **Accounts**, create or edit the Cloudflare account.

| OPNsense field | Value |
| --- | --- |
| Enabled | Checked |
| Description | <code>Cloudflare - ZAGG Inventory Manager</code> |
| Service | <code>Cloudflare</code> |
| Username | **<code>token</code>**, literally lowercase |
| Password | Cloudflare zone-scoped **API token** |
| Zone | **<code>zagginventorymanager.com</code>** (zone name, **not** zone ID) |
| Hostname(s) | **<code>zagginventorymanager.com</code>** (full hostname) |
| Check IP method | WAN interface IPv4 **only if WAN actually has the public IP** |
| Interface to monitor | WAN, or the intended outward-facing interface |
| Force SSL | Enabled |

If OPNsense is **behind another router** and WAN has a private address, use an external public IPv4 check service supported by your version (for example, an IPv4 ipify option), not the WAN interface's private IP. Save/apply, then inspect the account status/log.

**Troubleshooting:** A correct API token can still fail if the hostname/zone is wrong, the required A record does not exist, the service is disabled, or WAN is not truly public. Use the OPNsense log to distinguish authentication errors from record/IP detection errors.

### 6.5 Validate DDNS safely

1. Look at OPNsense **Services → Dynamic DNS → Log File / Accounts**. Confirm successful update/current status.
2. Look at the **Cloudflare DNS dashboard** A-record **content**; it should match your public IPv4.
3. For a DNS-only record, from a shell with <code>dig</code> installed:

~~~bash
dig +short A zagginventorymanager.com
~~~

4. After switching to Proxied, do not interpret Cloudflare edge-IP answers as a DDNS failure.
5. Verify that OPNsense updates the same record on a real public-IP change. Do not deliberately disrupt your ISP connection in working hours merely to force this test.

### 6.6 Public IP / CGNAT / double NAT

- If the WAN address falls in a private range (for example, <code>192.168.x.x</code>, <code>10.x.x.x</code>, <code>172.16–31.x.x</code>) or provider shared address range (<code>100.64.0.0/10</code>), port forwarding on OPNsense alone may be insufficient.
- With double NAT, your upstream router may also need to forward TCP/80 and TCP/443 to OPNsense.
- With ISP CGNAT, inbound IPv4 port forwarding generally cannot reach your firewall; request a public IP from the ISP or consider Cloudflare Tunnel/another supported network design instead. **DDNS cannot bypass CGNAT.**
- Do not publish private LAN addresses in an Internet-facing DNS record.

## 7. Router rules and Nginx Proxy Manager

### 7.1 OPNsense NAT/firewall

If using conventional inbound forwarding, create or verify **Firewall → NAT → Port Forward** rules:

| Interface | Protocol | Destination | External port | Internal target | Internal port |
| --- | --- | --- | --- | --- | --- |
| WAN | TCP | WAN address | **443** | <code>NPM_LAN_IP</code> | **443** |
| WAN | TCP | WAN address | **80** (only if needed for HTTP/ACME redirect/challenge) | <code>NPM_LAN_IP</code> | **80** |

Enable the associated WAN firewall rule or create an appropriately limited explicit rule. Use only the real NPM host IP. If you already host several domains behind NPM, these forwards may **already exist**—do not create competing routes or replace other hosts' working proxy configurations.

**Never forward these directly to the Internet:** PostgreSQL 5432, Inventory Manager 18424, NPM administration 81, or the application container's 8080.

Inside the LAN, NPM must be able to connect to <code>http://INVENTORY_LXC_IP:18424</code>. Confirm:

~~~bash
# Run from the NPM host, if curl is installed:
curl -fsS http://INVENTORY_LXC_IP:18424/health
~~~

If this fails, diagnose internal LXC firewall/network routing **before** debugging Cloudflare or Google OAuth.

### 7.2 Create the proxy host in NPM

Open the existing **Nginx Proxy Manager** admin UI from your admin LAN/VPN. The official project commonly uses TCP/81 for its management UI; do not expose it publicly.

Go to **Hosts → Proxy Hosts → Add Proxy Host** (labels may vary with NPM version):

| Field | Value |
| --- | --- |
| Domain names | **zagginventorymanager.com** |
| Scheme | **http** |
| Forward hostname/IP | **INVENTORY_LXC_IP** |
| Forward port | **18424** |
| Block Common Exploits | Enable |
| Websockets Support | Optional, safe to enable; this application does not require WebSockets for standard operation |
| Access List | Use your selected security policy; not a replacement for proper app authentication |

The proxy **upstream scheme is HTTP** because TLS is terminated at NPM; the public visitor still uses HTTPS. Do not point the proxy to the <code>db</code> service or to port 5432. Do not enter the public domain as the upstream target (that can create a loop).

Verify NPM can proxy a local request and that the Host header is correct. For WAN testing, use a phone on **cellular data**, not only Wi-Fi, to rule out split DNS and hairpin-NAT problems.

### 7.3 NPM installation if none exists

NPM is separate software, not deployed by this application's installer. Its official setup is at https://nginxproxymanager.com/setup/ and includes its own Docker container, persistent configuration/certificate storage and admin account. Install NPM on an appropriately secured host before creating the proxy host.

If other services already run on TCP/80 or TCP/443 on the NPM machine, resolve that port ownership; only one front-end listener can bind a given port on a given IP at a time. Preserve existing NPM proxy hosts and SSL certificates while adding this new one.

## 8. HTTPS, certificates and Cloudflare proxy

### 8.1 Select one origin certificate strategy

**Recommended for ordinary external-browser access:** Use an NPM-managed **Let's Encrypt** certificate covering **zagginventorymanager.com**. It must be valid and renewed. The standard HTTP challenge requires correct DNS/port-80 reachability; alternatively use a supported NPM DNS challenge with a separate, appropriately scoped DNS token.

**Cloudflare-proxied-only alternative:** Generate a **Cloudflare Origin CA** certificate in Cloudflare's SSL/TLS Origin Server area, covering the root domain, and import certificate **and private key** into NPM. The certificate is trusted by Cloudflare to connect to your origin but **not** generally by browsers accessing NPM directly while Cloudflare is bypassed. Never publish the private key or upload it to GitHub.

Both strategies require a certificate that matches **zagginventorymanager.com**; an old certificate issued only for <code>mrmschf.xyz</code> is not a substitute.

### 8.2 NPM SSL tab

On your Inventory Manager proxy host:
1. Choose or request the certificate for <code>zagginventorymanager.com</code>.
2. Enable **Force SSL** after confirming HTTPS works and no redirect loop occurs.
3. Enable HTTP/2 if your NPM build supports it.
4. Enable relevant HSTS controls **only after** HTTPS and renewal are thoroughly validated; HSTS mistakes can lock clients into a broken HTTPS configuration.

### 8.3 Cloudflare SSL/TLS

With the **Proxied** (orange cloud) A record, configure **Cloudflare → SSL/TLS → Overview → Full (strict)** and ensure NPM presents a matching, trusted origin certificate (public CA or Cloudflare Origin CA). Do **not** choose Flexible for this design.

The two encrypted hops are:
- Browser ↔ Cloudflare: Cloudflare edge certificate.
- Cloudflare ↔ NPM: NPM origin certificate checked under **Full (strict)**.

Cloudflare's edge certificate alone does not replace the NPM-origin TLS setup. If you use **DNS only** instead, the visitor connects directly to NPM and therefore needs a public-browser-trusted certificate such as Let's Encrypt.

### 8.4 Check the public HTTPS site

From a device outside your LAN, visit:

- https://zagginventorymanager.com/
- https://zagginventorymanager.com/health

Check:
- No browser certificate warning.
- No NPM default landing page.
- The inventory login interface loads.
- The page and API calls return normally, not a Cloudflare 522/525/526 or NPM 502.
- A known employee can sign in only after the chosen additional security controls are satisfied.
- Camera permissions can be requested from a mobile browser over HTTPS.

Avoid caching authenticated API responses in Cloudflare. **Do not** add cache-everything rules for <code>/api/*</code>, <code>/health</code> or signed-in HTML. If the UI appears stale after updates, clear the browser cache/hard refresh and check the deployed Git commit.

## 9. Google Cloud OAuth and Gmail API

### 9.1 Understand which Google credential is required

This application uses **Google OAuth 2.0 Authorization Code Flow** for a single global Gmail sending account. It is **not** a service-account Gmail sender and does **not** need a generic Google API key.

The server requests:
- <code>openid</code>
- <code>email</code>
- <code>https://www.googleapis.com/auth/gmail.send</code>

The <code>gmail.send</code> scope permits sending messages (not general inbox browsing). Google consent policies and verification requirements depend on audience, account ownership and publishing status.

**The configured origin/callback are hardcoded in the current API.** Changing the domain again requires changing the code/import validation and Google Cloud client entries, then redeploying. A DNS redirect by itself is not sufficient.

### 9.2 Create or choose a Google Cloud project

Open https://console.cloud.google.com/ and select the intended Google Cloud project. **Do not create another project if you already have an OAuth client you want to preserve.**

- Confirm the appropriate business/account administrator owns the project and will retain access during personnel changes.
- Search for and enable the **Gmail API** in **APIs & Services → Library** if not already enabled.
- In the new Google Auth Platform interface, review **Branding**, **Audience**, **Clients**, and **Data Access**. Exact navigation labels can evolve.
- Set application name and support contact appropriately. Use branding/domain assets only with permission to use the associated trademark.
- Add <code>zagginventorymanager.com</code> as an **Authorized domain** where Google requests it; Google may require domain ownership verification for production/verification.
- Select a suitable audience for your intended Gmail account(s). A company-managed Google Workspace internal app differs from an External app. Google's newer Personal mode may also be available to eligible new projects; it has restrictions and cannot simply be toggled on in an existing External project.

**Publishing/refresh-token caution:** For **External** projects in **Testing**, when requesting <code>gmail.send</code> and offline access, Google's refresh tokens normally expire after **seven days**. If alerts stop working after approximately a week, inspect the project's Audience/publishing status and token logs; repeatedly reconnecting is not a substitute for an appropriate production configuration. Publishing and sensitive-scope verification may apply; do not claim the project is verified merely because Google allowed a test login.

### 9.3 Configure the OAuth Web Application client

Go to https://console.cloud.google.com/auth/clients.

Create a **Web application** client if you do not already have one. Otherwise **edit the existing client** to preserve the existing client ID/secret and connected sender where possible.

Enter exactly:

| Google OAuth client field | Required value |
| --- | --- |
| Application type | **Web application** |
| Authorized JavaScript origin | **https://zagginventorymanager.com** |
| Authorized redirect URI | **https://zagginventorymanager.com/api/google/callback** |

**Exact match matters:** HTTPS, hostname, callback path and any trailing slash. This callback ends with <code>/callback</code> and **has no trailing slash**. Do not enter the LAN HTTP URL, old <code>inv.mrmschf.xyz</code>, <code>www</code>, or <code>/api/google/connect</code> as the primary callback.

Click **Save** in Google Cloud. Download the Web OAuth client **JSON** **after** saving, so its <code>redirect_uris</code> and <code>javascript_origins</code> arrays reflect the new configuration. The dashboard import validates both values.

**Secret hygiene:** Client JSON includes a client secret. Store it only in an authorized password manager or temporary protected local file. Do not commit it, screenshot it, or send it over chat. Google displays a client ID which is not a password, but the client secret and refresh token must be protected.

### 9.4 Configure the consent screen and user access

- For **External → Testing**, explicitly add the intended sending Google account under **Audience → Test users**.
- Verify the scope under **Data Access**. Additional scopes are unnecessary for the current app.
- For an **Internal** app, the sending account must satisfy the Workspace organization policy.
- If Google's warning or restricted/sensitive-scope verification prevents a production connection, follow Google's Auth Platform verification requirements; do not bypass required verification or misrepresent the intended application.
- Consider a **dedicated company-controlled sending mailbox** rather than an individual's personal Gmail account, and document who controls it.

### 9.5 Import OAuth client JSON through the Inventory Manager dashboard — preferred

First verify the application itself works at **https://zagginventorymanager.com**.

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

The helper validates the Web client JSON, requires <code>https://zagginventorymanager.com/api/google/callback</code> in redirect URIs, and writes <code>GOOGLE_CLIENT_ID</code> and <code>GOOGLE_CLIENT_SECRET</code> into the host's private <code>.env</code>. It rebuilds/restarts the API.

Only run this when you deliberately choose **server-side configuration** rather than the dashboard. A dashboard-imported OAuth config stored in PostgreSQL may take precedence over <code>.env</code> credentials when the API starts. **Delete the temporary JSON file securely after you verify setup**, and keep the authoritative password-manager copy. The optional helper does **not** authorize a Gmail sender on its own.

### 9.7 Google callback details

When the global admin clicks **Connect global Gmail**, the API issues a Google authorization URL using a 10-minute server-side state and PKCE challenge, then Google returns to:

**https://zagginventorymanager.com/api/google/callback**

The API exchanges the code for OAuth tokens, verifies a Google email identity, encrypts/stores the **refresh token** in PostgreSQL, and redirects back to the site's Administration flow. The browser should land back at the new domain; if it lands on the old domain, the server code you deployed is outdated.

**Common callback failures:**
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

The file <code>/opt/inventory-manager/.env</code> should be owned by an authorized administrator, restricted to mode **0600**, and stored in encrypted backups/password-manager procedures. The installer generates it only when missing; upgrades are designed to keep its values.

| Variable | Where used | Required? | Handling |
| --- | --- | --- | --- |
| <code>POSTGRES_PASSWORD</code> | Database and API connection string via Compose | **Yes** | Installer generates 32 random bytes as hex; retain for existing volume |
| <code>SETUP_TOKEN</code> | One-time admin PIN enrollment | **Yes** | Installer generates; printed by installer; don't publish |
| <code>GOOGLE_TOKEN_KEY</code> | AES-GCM encryption/decryption of Google OAuth secrets and refresh tokens | **Yes for dashboard OAuth/Gmail** | 64 hex characters representing 32 bytes; **never rotate casually** |
| <code>GOOGLE_CLIENT_ID</code> | Optional server-side OAuth client config | Optional | Dashboard-imported client may override at startup |
| <code>GOOGLE_CLIENT_SECRET</code> | Optional server-side OAuth client config | Optional | Secret; never commit |
| <code>SMTP_USER</code> | Optional SMTP fallback | Optional | Mailbox identifier |
| <code>SMTP_APP_PASSWORD</code> | Optional SMTP fallback | Optional | Secret; never commit |

Other runtime settings are in [deploy/compose.yml](../deploy/compose.yml): <code>ConnectionStrings__Inventory</code> points to <code>Host=db;Port=5432;Database=inventory;Username=inventory</code>, and <code>ASPNETCORE_URLS</code> is <code>http://+:8080</code> inside the API container.

**Not in this file:** Cloudflare DDNS API token (stored in OPNsense), NPM password/SSL private key (stored by NPM), Google Cloud Console login (external), and employees' inventory credentials (stored by the app in PostgreSQL).

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

### 15.4 Domain migration reminder

This installation deliberately uses <code>zagginventorymanager.com</code>. A future domain change must coordinate:
- Cloudflare DNS/DDNS record and OPNsense hostname.
- NPM proxy hostname and TLS certificate.
- Google Cloud client origin and callback.
- The API constants <code>PublicOrigin</code> and <code>GoogleRedirect</code> and JSON/client importer validations.
- Links in the runbooks and any printed materials.

Keep the existing Google Client ID/secret if possible to preserve the global sender token, but test consent/callback again. Don't delete the previous authorized redirect until the replacement flow is verified.

## 16. Troubleshooting by symptom

| Symptom | Likely causes | Check / action |
| --- | --- | --- |
| Installer refuses to run | Wrong Ubuntu version, not root, dirty Git working tree, nonempty target directory | <code>cat /etc/os-release</code>; <code>id</code>; <code>git status --short</code>; inspect before editing |
| Docker/Compose unavailable | Docker-in-LXC limitations, apt/repo error, nesting issue | <code>docker info</code>; <code>docker compose version</code>; <code>journalctl -u docker</code>; review LXC features |
| API shows 502 or does not load | Container down, database unhealthy, wrong upstream IP/port | Compose <code>ps</code>, API/db logs, LAN <code>curl .../health</code>, NPM target |
| Local LAN works but public domain fails | DNS, DDNS, WAN NAT, double NAT, CGNAT, NPM firewall | Cloudflare record content, OPNsense DDNS logs, WAN routing, external LTE test |
| Cloudflare 522 timeout | Origin unreachable/blocked/incorrect port forwarding | NPM reachability, firewall, WAN address, upstream NAT |
| Cloudflare 525 / 526 | TLS handshake failure or bad origin certificate | NPM SSL cert covers domain, Cloudflare Full (strict), renewed/not expired cert |
| Wrong app/site appears | NPM host points to other service or conflicting proxy host | Verify exact NPM proxy host and domain |
| Site redirects repeatedly | Incompatible SSL mode, proxy HTTPS redirect conflict | Full (strict) with valid origin cert; remove competing redirect rules |
| Cloudflare DNS shows another IP | Wrong DDNS zone/hostname/public-IP detection | OPNsense DDNS account and Cloudflare dashboard (not public dig for proxied records) |
| No initial PIN setup screen | Database already enrolled | <code>/api/auth/status</code>; existing PIN remains; installer is not PIN reset |
| Google JSON import rejected | Old downloaded JSON, wrong OAuth client type, mismatch origin/callback, missing key | Re-save Google origin/callback and re-download Web client JSON; confirm <code>GOOGLE_TOKEN_KEY</code> |
| Google <code>redirect_uri_mismatch</code> | Wrong domain/HTTPS/path, old application build | Check exact callback, Google client and deployed commit |
| Google connection returns canceled/expired | Consent declined or state timed out | Retry promptly in same browser; verify server clock |
| Gmail stops after ~7 days | OAuth External app still in Testing | Review Audience/publishing, test users and Google requirements |
| Global Gmail connected, no alert | Recipients not saved/enabled, no low-stock transition, worker delay, Gmail token issue | Use **Send test email**, app API logs, store alert settings |
| Employee does not see Administration | Standard role, wrong store, stale login | Check employee permissions and selected store; **do not** broaden access casually |
| Employee does not see Edit note | Not a checkout, stale frontend, wrong store, old deployment | Only checkout rows get Edit note; verify current deployed build; hard refresh |
| Camera scan unavailable | No HTTPS, camera denied, unsupported browser/device | HTTPS, browser Settings permissions, rear camera, manual scanner fallback |
| Inventory data disappears | Wrong selected store, incorrect DB volume/project, accidental restore | Stop and investigate; check active store and volume, restore only from verified dump |
| Backups absent | Timer disabled, db unhealthy, full filesystem, permission failure | <code>systemctl list-timers</code>, journal, manual backup, <code>df -h</code> |
| API cannot decrypt Gmail token | <code>GOOGLE_TOKEN_KEY</code> missing/changed, mismatched DB/key restore | Restore matching key; avoid random rekeying |
| After deployment old UI remains | Cache or deployment did not pull latest commit | <code>git rev-parse HEAD</code>, Compose logs, hard refresh, Cloudflare caching rules |

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
- [ ] Monitor TLS/certificate renewals and verify Cloudflare Full (strict).
- [ ] Protect the Gmail mailbox with strong authentication and transfer Google Cloud project ownership to an accountable business-controlled identity.
- [ ] Define access-offboarding, database retention, incident response, audit-log retention, and approved administrators.
- [ ] Store and test **encrypted offsite database + .env** backups.
- [ ] Review log contents, application updates/dependencies, and API authorization boundaries.
- [ ] Remove unused demo/test accounts and avoid using stock data from a real store in development tests.
- [ ] Obtain permission to use ZAGG branding if distributing outside the authorized business context.

### 17.2 Document ownership/handover without sending secrets

For a franchise handover, agree in writing:
- Whether the business receives a perpetual **license** or a full source-code/IP **assignment**, and exactly which code/brand assets are included.
- Who owns the domain, registrar, Cloudflare zone, OPNsense, Proxmox, NPM infrastructure and Google Cloud project.
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
- [ ] OPNsense DDNS uses Cloudflare scoped token; apex A record tracks public IPv4.
- [ ] NPM forwards hostname to correct LAN IP:18424.
- [ ] WAN/NAT forwards only required TCP/443 and TCP/80 to NPM, not to inventory/database/admin ports.
- [ ] External HTTPS works with Cloudflare Full (strict) and valid origin cert.
- [ ] No old domain remains in active Google callbacks or NPM Inventory host rules.

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
- [ ] Origin = <code>https://zagginventorymanager.com</code>.
- [ ] Callback = <code>https://zagginventorymanager.com/api/google/callback</code>.
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
