# Inventory Manager — deployment quick start (bring your own domain)

**Start with the [complete bring-your-own-domain deployment guide](COMPLETE_DEPLOYMENT_GUIDE.md).** This quick reference assumes you own or control an HTTPS hostname and are using Ubuntu 24.04 with Docker Compose.

## New installation

1. Obtain your domain, select a hostname (root domain or subdomain), and document your desired HTTPS URL. Example: `https://inventory.example.com` is a **placeholder**, not a domain supplied by this project.
2. Provision an Ubuntu 24.04 server/LXC and install/access a compatible HTTPS reverse proxy and DNS service. Cloudflare, OPNsense, and Nginx Proxy Manager are documented **examples**, not requirements.
3. On the **Ubuntu application server**, download/review and run the installer:

~~~bash
curl -fsSL https://raw.githubusercontent.com/moordyn/Inventory-Manager/main/deploy/install.sh -o /tmp/inventory-install.sh
less /tmp/inventory-install.sh
sudo bash /tmp/inventory-install.sh
~~~

The first-run installer **prompts for your own canonical HTTPS `PUBLIC_ORIGIN`**, e.g. `https://inventory.yourcompany.com`. Unattended installs can set it explicitly:

~~~bash
# Replace the example hostname with your OWN real registered domain:
sudo PUBLIC_ORIGIN=https://inventory.example.com bash /tmp/inventory-install.sh
~~~

4. The installer creates a private `/opt/inventory-manager/.env`, random database/setup secrets, Docker containers, and a daily backup timer. It **does not** register the domain, create DNS records, configure firewall/proxy/SSL or create a Google OAuth project.
5. Configure DNS and HTTPS reverse proxy for **your exact hostname**, forwarding to the app host at `http://INVENTORY_LXC_IP:18424`. Keep app, database, and proxy admin ports off the public Internet.
6. Open the app, enter the one-time setup token and create the six-digit admin PIN. Log in as `OWNER` to configure stores and employees.

## Google OAuth and Gmail alerts (optional)

This is **bring your own Google Cloud**:
- Create/select a Google Cloud project **you control**; enable the Gmail API.
- Set up Google Auth Platform Branding/Audience/Data Access for your own use case.
- Create a **Web application** OAuth client.
- Authorized JavaScript origin = **your own `PUBLIC_ORIGIN`**.
- Authorized redirect URI = **`PUBLIC_ORIGIN + /api/google/callback`**.
- Download **your own** OAuth Web client JSON **after** saving.
- Sign in to your app as global admin, open **Administration → Google OAuth configuration**, import the file and confirm PIN, then click **Connect global Gmail**.
- Configure separate alert recipients and test mail per store.

You do **not** need the original developer's domain or Google Client ID/secret. Do not commit the OAuth JSON or private `.env` to GitHub.

## Updates on an existing Ubuntu app host

~~~bash
cd /opt/inventory-manager
sudo bash deploy/install.sh
sudo docker compose --env-file .env -f deploy/compose.yml ps
curl -fsS http://127.0.0.1:18424/health
sudo systemctl status inventory-backup.timer
~~~

`PUBLIC_ORIGIN`, database data and encryption secrets persist across normal updates. Legacy installations may have a compatibility origin; to move domains, update the `PUBLIC_ORIGIN` line in `.env`, DNS, TLS proxy and the Google OAuth client together.

**Security:** Employee ID-only login in the current app is not sufficient protection for sensitive Internet-exposed operations. Use stronger authentication/access controls before public production use. Read the [full security and backup/restore sections](COMPLETE_DEPLOYMENT_GUIDE.md).
