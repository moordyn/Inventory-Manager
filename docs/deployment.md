# ZAGG Inventory Manager — deployment documentation

**The authoritative installation and deployment runbook is now:**

**[COMPLETE_DEPLOYMENT_GUIDE.md](COMPLETE_DEPLOYMENT_GUIDE.md)**

That guide contains the complete first-install and migration procedure for Ubuntu 24.04 in Proxmox LXC, Docker Compose, PostgreSQL 17, Cloudflare and OPNsense DDNS, public DNS, Nginx Proxy Manager, HTTPS certificates, Google OAuth/Gmail API, application onboarding, backups, recovery, troubleshooting, security, and operational handover.

The production public hostname is **https://zagginventorymanager.com** with Google callback **https://zagginventorymanager.com/api/google/callback**. Previous directions referencing the retired hostname are superseded.

## Quick reference for an already-configured Ubuntu server

From the Ubuntu Inventory Manager LXC (not the Proxmox host):

~~~bash
cd /opt/inventory-manager
sudo bash deploy/install.sh
sudo docker compose --env-file .env -f deploy/compose.yml ps
curl -fsS http://127.0.0.1:18424/health
sudo systemctl status inventory-backup.timer
~~~

**Read the full guide before a first installation or network/security change.** The install command does **not** configure DNS, NAT, SSL or Google Cloud. It also does **not** establish that the system is safe for public access: employee-ID-only login currently needs stronger protection.
