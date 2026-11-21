# Optional networking examples: Cloudflare, OPNsense and Nginx Proxy Manager

These instructions are **examples** retained for operators who use this stack. They are not required for Inventory Manager; equivalent DNS, firewall, proxy, certificate and DDNS systems are supported. Replace all example hostnames, IP addresses and tokens. For the canonical app setup and `--domain` syntax, see [Deployment](deployment.md). For troubleshooting, see [Operations](operations.md).

**Security boundary:** publish only HTTPS to the reverse proxy; do not publicly expose the API's TCP 18424, PostgreSQL, Nginx Proxy Manager administration, OPNsense or Proxmox administration.

## Choose DNS and configure optional Cloudflare/OPNsense DDNS

You **do not need Cloudflare or OPNsense** to run the application. What you need is a registered/authorized hostname that resolves to a reachable HTTPS endpoint. Choose the simplest network arrangement that works for your site.

### Create a DNS record with **your** provider

In your registrar/DNS provider's DNS control panel, add a record for the **exact hostname you chose**:

| Hostname you choose | DNS zone | Record type | DNS Name/Host field | Points to |
| --- | --- | --- | --- | --- |
| `inventory.example.com` | `example.com` | `A` | `inventory` | Your **public** IPv4 |
| `example.com` (apex/root) | `example.com` | `A` | `@` or provider's apex convention | Your **public** IPv4 |

If your public IP is **static**, this may be all the DNS configuration you need. Never put `INVENTORY_LXC_IP` (a private LAN address) in public DNS. Only add `AAAA` if reachable IPv6 is correctly configured end-to-end and has its own update strategy. Remove stale/conflicting records for the chosen hostname.

For Cloudflare: first ensure your registered domain is delegated to the Cloudflare-assigned nameservers and zone is Active. Start with **DNS only** (grey-cloud) while verifying reachability, and optionally enable **Proxied** (orange cloud) after correct HTTPS configuration. With proxied DNS, `dig` returns Cloudflare edge IPs, **not** the origin WAN address; inspect the origin A-record content in Cloudflare's dashboard.

For other providers: use their equivalent DNS record settings and TTL; their authentication/API/DDNS setup may be entirely different.

### When dynamic DNS is required

If your ISP changes the public IP, install a **DDNS updater supported by your provider** and point it at the exact A record for the inventory hostname. Options include your router's built-in DDNS feature, a provider-specific client, or OPNsense `os-ddclient`. Cloudflare does not automatically discover your new IP merely because the DNS zone is proxied.

DDNS is **not required** if the IP is static or if your tunnel provider handles hostname routing independently. It does not fix CGNAT.

### Optional: Cloudflare + OPNsense DDNS worked example

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

### No Cloudflare? Provider-neutral alternatives

- **Static IP + ordinary registrar DNS:** configure the A/AAAA record, verify with `dig`, skip the Cloudflare API-token and OPNsense DDNS instructions.
- **Other dynamic DNS vendor:** configure that vendor's updater with the credentials/scopes it specifies; OPNsense may support it through `os-ddclient` or a separate client. Do not enter the literal Cloudflare `token` username for unrelated services.
- **Cloudflare Tunnel or alternative HTTPS tunnel:** follow your provider's secure tunnel and custom-hostname setup. Inbound WAN TCP/443 may not be required, but the public HTTPS hostname and OAuth callback must still match.
- **VPS:** configure public DNS to your server, reverse proxy there, and avoid home-router NAT entirely.

### Verify the chosen hostname

For DNS-only A records:

~~~bash
# Example only: replace inventory.example.com with your real hostname
dig +short A inventory.example.com
~~~

Confirm the IP is correct and that external HTTPS works after §7–§8. If on a proxied CDN, validate both the browser URL and the origin value inside the provider's dashboard.

### CGNAT, double NAT and stale IP detection

- Private upstream WAN IP (`10/8`, `172.16/12`, `192.168/16`) or `100.64.0.0/10` carrier-grade NAT can defeat normal inbound IPv4 port forwarding.
- With two routers, a separate upstream forward or bridge mode may be needed.
- Under ISP CGNAT, request a public address or use an approved tunnel/VPS; **DDNS itself does not establish inbound reachability**.
- If your firewall WAN IP is private, use **external IPv4 detection** for DDNS rather than writing the WAN interface's address into public DNS.

---

## Router rules and an HTTPS reverse proxy

### Choose the network entry point

This section illustrates an **on-premise router + Nginx Proxy Manager (NPM)**. If you use a public VPS, configure its host/cloud firewall instead of home-router NAT. If you use a properly configured HTTPS tunnel, conventional WAN port forwards may not be needed. In all cases, the final site address must be exactly your `PUBLIC_ORIGIN`, and its TLS certificate must match your chosen hostname.

### Optional OPNsense port forwards

For on-premise hosting with a reachable public IPv4, use your firewall/router's NAT settings. On **OPNsense** the typical page is **Firewall → NAT → Port Forward**:

| Interface | Protocol | External destination/port | Forward destination | Internal port |
| --- | --- | --- | --- | --- |
| WAN | TCP | WAN address : **443** | `NPM_LAN_IP` | **443** |
| WAN | TCP | WAN address : **80**, **only if required** for ACME/redirects | `NPM_LAN_IP` | **80** |

Apply the associated firewall rules. Use the actual NPM LAN IP from your worksheet, not the app server's hostname or the example placeholder. If NPM already hosts other services, these rules may already exist; do not create duplicate/conflicting rules.

**Never publish** Inventory Manager host port **18424**, internal API port **8080**, PostgreSQL **5432**, NPM admin port **81**, or Proxmox/OPNsense management ports to the Internet.

Other routers use different menus but the same principle: Internet TLS traffic reaches the secured HTTPS proxy, **not** the app port directly. If your ISP uses CGNAT, a conventional WAN forward cannot work.

### Nginx Proxy Manager — recommended example

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

### Using another reverse proxy

A maintained alternative (Caddy, Traefik, Apache or nginx) can handle TLS and proxy requests to `http://INVENTORY_LXC_IP:18424`. Configure hostname routing, certificate issuance/renewal, client/body limits, forwarded headers and timeouts appropriately. The repository does **not** ship a configured proxy for these alternatives; follow that proxy's official documentation. Preserve `/api/google/callback` exactly and do **not** route it to a different application.

A separate identity-aware gateway or VPN is advised until employee authentication is hardened. Test any login gate's interaction with Google OAuth redirects.

### External verification

Test your public HTTPS hostname over mobile data, **not only LAN Wi-Fi**, to catch split DNS/hairpin NAT differences. From the proxy host, verify upstream `/health`; from a public browser, verify the exact hostname, certificate, app login and API requests before proceeding to Google OAuth.

---

## HTTPS, certificates and optional Cloudflare proxy

### Match the actual hostname

Your public `PUBLIC_ORIGIN` must use **HTTPS** and your TLS certificate must cover its hostname. Example: a site at `inventory.example.com` needs a certificate covering `inventory.example.com` (a suitable wildcard may also cover it). An SSL certificate for `example.com` **does not necessarily** cover `inventory.example.com`.

**General option — public ACME/Let's Encrypt certificate:** In Nginx Proxy Manager, request/import an appropriate public CA certificate for your hostname and ensure automatic renewal. HTTP-01 ACME challenges generally need port 80 reachability; DNS-01 challenges use your supported DNS provider and scoped DNS API access. This option works whether your domain uses Cloudflare DNS or another provider.

**Cloudflare Proxied option only:** You may instead use a Cloudflare Origin CA certificate between Cloudflare and your proxy. This certificate is trusted by Cloudflare **but not typically directly by browsers**. Only use it when the public site always passes through Cloudflare's proxy, and keep its private key secret. If you switch to DNS-only later, install a publicly trusted certificate first.

### NPM SSL settings

In your NPM Proxy Host for **your actual hostname**:
1. Select/request the matching certificate.
2. After confirming TLS and renewals, enable **Force SSL**.
3. Optionally enable HTTP/2 where supported.
4. Only enable strict HSTS after you know HTTPS is stable and recoverable.

Do not expose the NPM admin page publicly. If you use a different proxy, use the equivalent certificate/TLS settings.

### Only when using Cloudflare orange-cloud proxy

After DNS-only/HTTPS diagnostics, enable **Proxied** for the relevant DNS record if you want Cloudflare edge proxy features. In **Cloudflare → SSL/TLS**, select **Full (strict)** with a matching, valid origin certificate on your reverse proxy. Do not use Flexible. Cloudflare uses browser-to-edge and edge-to-origin TLS, so the edge certificate **does not replace** the origin certificate. [Cloudflare Full (strict) reference](https://developers.cloudflare.com/ssl/origin-configuration/ssl-modes/full-strict/).

If using a DNS provider **without** Cloudflare's reverse proxy, ignore Cloudflare's SSL mode entirely; visitors connect directly to the public HTTPS reverse proxy, which needs a public-browser-trusted certificate.

### Confirm HTTPS before adding OAuth

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

